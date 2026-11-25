using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RcloneMount.Services
{
    public enum TransferOperation { Copy, Sync, Move, Check, CryptCheck, Bisync }

    public sealed class JobSpec
    {
        public TransferOperation Operation { get; init; }
        public string SourceRemote { get; init; } = "";
        public string SourcePath { get; init; } = "";
        public string DestRemote { get; init; } = "";
        public string DestPath { get; init; } = "";
        public bool DryRun { get; init; }
        // Ask rclone to try server-side transfer when source and destination are on
        // different remotes. Silently ignored by backends that can't do it, so it's
        // safe to leave on when the user opts in.
        public bool ServerSideAcrossConfigs { get; init; }
        // Bandwidth cap in rclone's own format (e.g. "10M", "500K", or a schedule
        // like "08:00,512 12:00,10M 23:00,off"). Empty = uncapped.
        public string BwLimit { get; init; } = "";
        // Extra CLI args appended verbatim after the built-in ones. Used today by
        // the filter editor (--filter-from / --min-size / …). Kept as a raw list so
        // future features (bandwidth cap, custom flags) can pile on without changing
        // the shape of this record.
        public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
        // Optional temp file the job service should delete once the job ends —
        // used by the filter editor when it materialises the pattern list to disk.
        public string? CleanupTempFile { get; init; }

        // Check / cryptcheck only. --one-way skips reporting files present on the
        // destination but missing from source (useful for backup verification).
        public bool OneWay { get; init; }
        // Check / cryptcheck only. --size-only skips hashing so large trees finish
        // in minutes rather than hours; at the cost of missing same-size differing
        // content.
        public bool SizeOnly { get; init; }

        // Bisync only. See rclone bisync docs — --conflict-resolve values are one
        // of: none | newer | older | larger | smaller (empty string = don't pass
        // the flag, uses rclone's default of "none").
        public string ConflictResolve { get; init; } = "";
        // Bisync only. --check-access requires RCLONE_TEST marker files on both
        // sides — refuses to sync if they've gone missing (protects against a
        // remote-side wipe being propagated to the other side).
        public bool CheckAccess { get; init; }
        // Bisync only. --max-delete N (percent). 0 = don't pass the flag (rclone
        // default is 50).
        public int MaxDeletePct { get; init; }
        // Bisync only. Set true by the Initialise button — adds --resync.
        // Destructive; the dialog gates it behind an explicit confirmation.
        public bool Resync { get; init; }

        public bool IsCheck => Operation is TransferOperation.Check or TransferOperation.CryptCheck;
        public bool IsBisync => Operation is TransferOperation.Bisync;

        // Empty remote name means "local disk" — rclone reads a bare path with no
        // "name:" prefix as a local filesystem path. TrimStart('/') is only safe
        // for remote paths; a Windows local path can start with a drive letter and
        // must be handed through verbatim.
        public string SourceSpec =>
            string.IsNullOrEmpty(SourceRemote)
                ? (SourcePath ?? "")
                : $"{SourceRemote}:{(SourcePath ?? "").TrimStart('/')}";
        public string DestSpec =>
            string.IsNullOrEmpty(DestRemote)
                ? (DestPath ?? "")
                : $"{DestRemote}:{(DestPath ?? "").TrimStart('/')}";

        public string OperationVerb => Operation switch
        {
            TransferOperation.Copy => "copy",
            TransferOperation.Sync => "sync",
            TransferOperation.Move => "move",
            TransferOperation.Check => "check",
            TransferOperation.CryptCheck => "cryptcheck",
            TransferOperation.Bisync => "bisync",
            _ => "copy",
        };
    }

    // Populated by RcloneJobService when a check / cryptcheck run completes.
    // Counts are line counts of the corresponding rclone output files —
    // rclone writes one path per line to each. `Dir` is where the raw files
    // live so the caller can offer "Open report folder".
    public sealed class CheckReport
    {
        public string Dir { get; init; } = "";
        public int Match { get; init; }
        public int Differ { get; init; }
        public int MissingOnSrc { get; init; }
        public int MissingOnDst { get; init; }
        public int Errors { get; init; }
    }

    // One would-be action parsed from a --dry-run's stderr. Verb is rclone's
    // own word ("copy", "delete", "move", "update modification time", …).
    // Detail carries whatever was in the trailing parenthetical (e.g. size).
    public sealed record DryRunAction(string Verb, string Path, string Detail);

    public sealed class JobProgress
    {
        public long BytesTransferred;
        public long TotalBytes;
        public int TransfersDone;
        public int TotalTransfers;
        public double SpeedBytesPerSecond;
        public double? EtaSeconds;
        public int Errors;
        public string? LastError;
        public string? CurrentFile;
        public bool IsRunning;
        public int? ExitCode;
        // Used by the completion detector — a run is "idle" when there's
        // nothing in these arrays and the counters have gone quiet.
        public int Checks;
        public double ElapsedSeconds;
        public int TransferringCount;
    }

    // Spawns a one-shot rclone job (copy/sync/move) with --rc so we can poll
    // /core/stats for live progress. Same shape as RcloneMountService but without
    // WinFsp / drive-letter concerns — the child process just moves bytes.
    public sealed class RcloneJobService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3), DefaultRequestHeaders = { Authorization = RcAuth.Header } };
        // App-lifetime kill-on-close job: if the app exits or crashes, running transfers die
        // with it instead of carrying on unseen (Resume picks them up on next launch).
        private static readonly ChildProcessJob ChildJob = new();

        private readonly string exePath;

        public RcloneJobService(string exePath)
        {
            this.exePath = exePath;
        }

        // Runs the job to completion (or cancellation). Fires `progress` roughly once
        // per second with the current stats. Returns the process exit code (0 = success)
        // plus a short tail of any stderr for the "job failed" toast.
        //
        // `jobName` is optional — when non-empty, each newly-completed file the job's
        // rclone reports via /core/transferred is streamed into transfers.jsonl with
        // that name in the Job field. Ad-hoc runs pass empty and the per-file rows
        // still land in history, just without a job stamp.
        public async Task<JobResult> RunAsync(JobSpec spec, IProgress<JobProgress> progress, CancellationToken ct, string jobName = "")
        {
            var rcPort = ReserveFreeLoopbackPort();

            // Check / cryptcheck: allocate a per-run report dir so we can point
            // --differ / --missing-on-* / --error / --match at real files and
            // count the lines after the process exits.
            string? reportDir = null;
            if (spec.IsCheck)
            {
                reportDir = Path.Combine(Path.GetTempPath(), "RcloneMount", "check-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(reportDir);
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(spec.OperationVerb);
            psi.ArgumentList.Add(spec.SourceSpec);
            psi.ArgumentList.Add(spec.DestSpec);
            psi.ArgumentList.Add("--rc");
            psi.ArgumentList.Add("--rc-addr");
            psi.ArgumentList.Add($"127.0.0.1:{rcPort}");
            RcAuth.AddServerArgs(psi.ArgumentList);
            // 1s stat window matches what our poller is doing anyway; makes the numbers
            // in /core/stats reflect near-current throughput instead of a rolling average.
            psi.ArgumentList.Add("--stats");
            psi.ArgumentList.Add("1s");
            // -v so rclone prints per-file INFO lines ("filename: Copied (new)",
            // "filename: Deleted", etc.) that we parse into transfers.jsonl. This
            // is more reliable than scraping /core/transferred, which returned
            // empty for SFTP transfers in v1.75 testing.
            psi.ArgumentList.Add("-v");
            // --dry-run and --server-side-across-configs only apply to the transfer
            // ops. rclone check has no --dry-run (it's inherently read-only), and
            // server-side is meaningless when nothing's being written. Bisync
            // supports --dry-run but not --server-side-across-configs.
            if (spec.IsCheck)
            {
                if (spec.OneWay) psi.ArgumentList.Add("--one-way");
                if (spec.SizeOnly) psi.ArgumentList.Add("--size-only");
                psi.ArgumentList.Add("--differ");         psi.ArgumentList.Add(Path.Combine(reportDir!, "differ.txt"));
                psi.ArgumentList.Add("--missing-on-src"); psi.ArgumentList.Add(Path.Combine(reportDir!, "missing-on-src.txt"));
                psi.ArgumentList.Add("--missing-on-dst"); psi.ArgumentList.Add(Path.Combine(reportDir!, "missing-on-dst.txt"));
                psi.ArgumentList.Add("--error");          psi.ArgumentList.Add(Path.Combine(reportDir!, "errors.txt"));
                psi.ArgumentList.Add("--match");          psi.ArgumentList.Add(Path.Combine(reportDir!, "match.txt"));
            }
            else if (spec.IsBisync)
            {
                if (spec.DryRun) psi.ArgumentList.Add("--dry-run");
                if (spec.Resync) psi.ArgumentList.Add("--resync");
                if (spec.CheckAccess) psi.ArgumentList.Add("--check-access");
                if (!string.IsNullOrWhiteSpace(spec.ConflictResolve))
                {
                    psi.ArgumentList.Add("--conflict-resolve");
                    psi.ArgumentList.Add(spec.ConflictResolve.Trim());
                }
                if (spec.MaxDeletePct > 0)
                {
                    psi.ArgumentList.Add("--max-delete");
                    psi.ArgumentList.Add(spec.MaxDeletePct.ToString());
                }
            }
            else
            {
                if (spec.DryRun)
                    psi.ArgumentList.Add("--dry-run");
                if (spec.ServerSideAcrossConfigs && !string.Equals(spec.SourceRemote, spec.DestRemote, StringComparison.Ordinal))
                    psi.ArgumentList.Add("--server-side-across-configs");
            }
            if (!string.IsNullOrWhiteSpace(spec.BwLimit))
            {
                psi.ArgumentList.Add("--bwlimit");
                psi.ArgumentList.Add(spec.BwLimit.Trim());
            }
            foreach (var extra in spec.ExtraArgs)
                psi.ArgumentList.Add(extra);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderrTail = new StringBuilder();
            var stderrLock = new object();
            // Per-file history rows sourced from -v INFO lines. Enqueued from the
            // stderr thread, drained periodically to transfers.jsonl by a helper
            // task so a slow disk on the append side can't stall stderr reading.
            var pendingHistory = new ConcurrentQueue<TransferHistoryEntry>();
            var direction = spec.SourceRemote.Length == 0
                ? "up"
                : (spec.DestRemote.Length == 0 ? "down" : "");
            var rowRemote = direction switch
            {
                "up"   => spec.DestRemote,
                "down" => spec.SourceRemote,
                _      => spec.SourceRemote.Length > 0 ? spec.SourceRemote : spec.DestRemote,
            };
            // Local source lets us look up each transferred file's byte count on
            // disk when we log the row; for remote sources we leave size at 0
            // (the summary row has the accurate aggregate).
            var localSourceRoot = spec.SourceRemote.Length == 0 ? spec.SourcePath : null;
            // Per-verb counters incremented alongside each per-file enqueue so
            // the end-of-run summary row can say "N uploaded · M downloaded ·
            // K deleted" instead of just a single files total.
            var counts = new OpCounts();
            // Dry-run only: collect the "Skipped … as --dry-run is set" NOTICE
            // lines rclone prints per would-be action, so the dialog can show a
            // report instead of the user seeing "Dry-run finished" and nothing
            // else. Null for real runs to avoid the enqueue overhead.
            var pendingDryRun = spec.DryRun && !spec.IsCheck ? new ConcurrentQueue<DryRunAction>() : null;

            proc.OutputDataReceived += (_, ev) => { /* discarded — RC has the stats */ };
            proc.ErrorDataReceived += (_, ev) =>
            {
                if (ev.Data is null) return;
                lock (stderrLock)
                {
                    stderrTail.AppendLine(ev.Data);
                    if (stderrTail.Length > 8_000)
                        stderrTail.Remove(0, stderrTail.Length - 8_000);
                }
                if (!spec.IsCheck)
                    TryEnqueueHistoryRow(ev.Data, spec, jobName, rowRemote, direction, localSourceRoot, pendingHistory, counts);
                if (pendingDryRun is not null)
                    TryEnqueueDryRunAction(ev.Data, pendingDryRun);
            };

            // Wire Exited BEFORE Start(). A fast job (e.g. FTP dry-run with nothing
            // to do) can exit within milliseconds; if we attach the handler after
            // Start, the event has already fired and the TCS never completes —
            // the dialog then hangs on await forever, stuck showing "Cancel".
            var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            proc.Exited += (_, _) => exitTcs.TrySetResult(proc.ExitCode);

            if (!proc.Start())
                throw new IOException("Failed to launch rclone job.");
            ChildJob.Attach(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Belt-and-braces for the same race: if the process exited between
            // Start() and here, EnableRaisingEvents' Exited may already have
            // fired or may not fire again. Fault the TCS in ourselves.
            if (proc.HasExited)
                exitTcs.TrySetResult(proc.ExitCode);

            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pollTask = PollLoopAsync(rcPort, progress, pollCts.Token);
            // Drain the per-file history queue every 2s while the job runs so a
            // long copy doesn't pile up rows in memory. Skipped for check/cryptcheck
            // — those don't move bytes and don't emit Copied/Deleted lines.
            var flushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var flushTask = spec.IsCheck
                ? Task.CompletedTask
                : FlushHistoryLoopAsync(pendingHistory, flushCts.Token);
            JobProgress? finalProgress = null;

            using var cancelReg = ct.Register(() =>
            {
                // Cooperative cancel: try RC /core/quit first, then hard-kill after a beat.
                _ = TryStopAsync(rcPort);
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(4)); } catch { }
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                });
            });

            var exit = await exitTcs.Task.ConfigureAwait(false);
            pollCts.Cancel();
            try { finalProgress = await pollTask.ConfigureAwait(false); } catch { }
            // Stop the periodic flush and drain any rows left in the queue —
            // the stderr thread may have enqueued lines between the last flush
            // and process exit.
            flushCts.Cancel();
            try { await flushTask.ConfigureAwait(false); } catch { }
            flushCts.Dispose();
            try { await DrainHistoryAsync(pendingHistory).ConfigureAwait(false); } catch { }

            // One summary row per real (non-dry) job run — gives the History tab
            // an at-a-glance "Job: X · N uploaded · M downloaded · K deleted"
            // line above the per-file rows so users can see which job produced
            // what without scanning each file. Skipped for check/cryptcheck
            // (no bytes move) and dry-runs (nothing actually happened).
            // Emitted from RunAsync itself so both TransferDialog and scheduled
            // runs get the row — earlier the dialog wrote it and scheduled runs
            // showed only per-file rows.
            if (!spec.IsCheck && !spec.DryRun && !string.IsNullOrWhiteSpace(jobName))
            {
                try { await AppendJobSummaryAsync(spec, jobName, exit, finalProgress, counts, direction, rowRemote).ConfigureAwait(false); }
                catch { /* history is best-effort */ }
            }

            string tail;
            lock (stderrLock) tail = stderrTail.ToString();

            // Best-effort delete of the caller's temp filter file — we own the
            // deletion (rather than the dialog) because a modeless close mid-run
            // could otherwise leave it behind.
            if (!string.IsNullOrEmpty(spec.CleanupTempFile))
            {
                try { File.Delete(spec.CleanupTempFile); } catch { }
            }

            // Roll up the check report by line-counting each output file. We keep
            // the dir around so the caller can offer "Open report folder" — a
            // stale dir per run in %TEMP% is a fine trade for direct file access.
            CheckReport? report = null;
            if (reportDir is not null)
            {
                report = new CheckReport
                {
                    Dir = reportDir,
                    Match = CountLines(Path.Combine(reportDir, "match.txt")),
                    Differ = CountLines(Path.Combine(reportDir, "differ.txt")),
                    MissingOnSrc = CountLines(Path.Combine(reportDir, "missing-on-src.txt")),
                    MissingOnDst = CountLines(Path.Combine(reportDir, "missing-on-dst.txt")),
                    Errors = CountLines(Path.Combine(reportDir, "errors.txt")),
                };
            }
            IReadOnlyList<DryRunAction>? dryRunList = null;
            if (pendingDryRun is not null)
            {
                var list = new List<DryRunAction>(pendingDryRun.Count);
                while (pendingDryRun.TryDequeue(out var a)) list.Add(a);
                dryRunList = list;
            }
            return new JobResult(exit, tail, report, finalProgress, dryRunList);
        }

        private static int CountLines(string path)
        {
            try
            {
                if (!File.Exists(path)) return 0;
                var n = 0;
                using var sr = new StreamReader(path);
                while (sr.ReadLine() is not null) n++;
                return n;
            }
            catch { return 0; }
        }

        // Progress only. Completion is the process exiting: rclone ends on its own when the
        // command finishes, --rc or not (verified on v1.76) — never infer "done" from stats.
        private static async Task<JobProgress?> PollLoopAsync(int rcPort, IProgress<JobProgress> progress, CancellationToken ct)
        {
            progress.Report(new JobProgress { IsRunning = true });
            JobProgress? lastGood = null;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var snap = await FetchStatsAsync(rcPort, ct).ConfigureAwait(false);
                    if (snap is not null)
                    {
                        snap.IsRunning = true;
                        progress.Report(snap);
                        lastGood = snap;
                    }
                }
                catch
                {
                    // Poll error — process may be starting or already dying. Just try again.
                }

                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            return lastGood;
        }

        private static async Task<JobProgress?> FetchStatsAsync(int port, CancellationToken ct)
        {
            using var resp = await Http.PostAsync($"http://127.0.0.1:{port}/core/stats", new StringContent("{}", Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var p = new JobProgress();
            p.BytesTransferred = ReadLong(root, "bytes");
            p.TotalBytes = ReadLong(root, "totalBytes");
            p.TransfersDone = (int)ReadLong(root, "transfers");
            p.TotalTransfers = (int)ReadLong(root, "totalTransfers");
            p.SpeedBytesPerSecond = ReadDouble(root, "speed");
            p.Errors = (int)ReadLong(root, "errors");
            p.Checks = (int)ReadLong(root, "checks");
            p.ElapsedSeconds = ReadDouble(root, "elapsedTime");
            if (root.TryGetProperty("lastError", out var le) && le.ValueKind == JsonValueKind.String)
                p.LastError = le.GetString();
            if (root.TryGetProperty("eta", out var eta) && eta.ValueKind == JsonValueKind.Number)
                p.EtaSeconds = eta.GetDouble();
            // Current file: pick the first name from the "transferring" array if present.
            if (root.TryGetProperty("transferring", out var tr) && tr.ValueKind == JsonValueKind.Array)
            {
                p.TransferringCount = tr.GetArrayLength();
                foreach (var t in tr.EnumerateArray())
                {
                    if (t.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    {
                        p.CurrentFile = n.GetString();
                        break;
                    }
                }
            }
            // "checking" is a parallel array for files being hashed during check
            // ops — treat those as active too so a long check isn't cut short.
            if (root.TryGetProperty("checking", out var ch) && ch.ValueKind == JsonValueKind.Array)
                p.TransferringCount += ch.GetArrayLength();
            return p;
        }

        private static long ReadLong(JsonElement obj, string prop) =>
            obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        private static double ReadDouble(JsonElement obj, string prop) =>
            obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        private static async Task TryStopAsync(int port)
        {
            try
            {
                using var resp = await Http.PostAsync($"http://127.0.0.1:{port}/core/quit", new StringContent("{}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
            }
            catch { }
        }

        // Matches the `-v` INFO lines rclone prints per completed file operation:
        //   2026/08/26 13:30:07 INFO  : path/name.ext: Copied (new)
        //   2026/08/26 13:30:07 INFO  : path/name.ext: Copied (replaced existing)
        //   2026/08/26 13:30:07 INFO  : path/name.ext: Deleted
        //   2026/08/26 13:30:07 INFO  : path/name.ext: Moved (server-side)
        // The filename group is non-greedy so trailing ": Copied ..." binds last.
        internal static readonly Regex InfoOpRe = new(
            @"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2} INFO\s+:\s+(?<name>.+?):\s+(?<verb>Copied|Deleted|Moved|Renamed)\b(?:\s+(?<detail>.*))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Matches the NOTICE lines rclone prints under `-v --dry-run`:
        //   2026/08/26 13:30:07 NOTICE: path/name.ext: Skipped copy as --dry-run is set (size 12.3M)
        //   2026/08/26 13:30:07 NOTICE: path/name.ext: Skipped delete as --dry-run is set
        //   2026/08/26 13:30:07 NOTICE: path/name.ext: Skipped update modification time as --dry-run is set
        // Verb is non-greedy so "update modification time" binds as the verb, and
        // any trailing "(...)" is captured as detail.
        internal static readonly Regex DryRunSkipRe = new(
            @"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\s+NOTICE\s*:\s+(?<name>.+?):\s+Skipped\s+(?<verb>.+?)\s+as\s+--dry-run\s+is\s+set(?:\s+\((?<detail>.*)\))?\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Parse one stderr line; enqueue a history row if it's a per-file
        // completion. Silent on non-matching lines (stats, notices, etc.).
        // Also increments counts so the end-of-run summary row has an accurate
        // per-verb breakdown without a second parse pass.
        private static void TryEnqueueHistoryRow(
            string line, JobSpec spec, string jobName,
            string rowRemote, string direction, string? localSourceRoot,
            ConcurrentQueue<TransferHistoryEntry> queue, OpCounts counts)
        {
            var m = InfoOpRe.Match(line);
            if (!m.Success) return;
            var name = m.Groups["name"].Value;
            var verb = m.Groups["verb"].Value;

            // Deletions carry no "direction" from the user's POV — mark them with
            // a distinct token so the History renderer can show a different glyph
            // later. Copied/Moved keep the job's direction (up/down).
            string dir;
            long bytes = 0;
            if (verb == "Deleted")
            {
                dir = "del";
                Interlocked.Increment(ref counts.Deleted);
            }
            else
            {
                dir = direction;
                if (dir == "up") Interlocked.Increment(ref counts.Uploaded);
                else if (dir == "down") Interlocked.Increment(ref counts.Downloaded);
                else Interlocked.Increment(ref counts.Transferred);
                // Best-effort size from the local source. Path.Combine handles the
                // subdir case (rclone prints paths relative to the source root).
                if (!string.IsNullOrEmpty(localSourceRoot))
                {
                    try
                    {
                        var localPath = Path.Combine(localSourceRoot, name);
                        if (File.Exists(localPath))
                            bytes = new FileInfo(localPath).Length;
                    }
                    catch { /* best-effort */ }
                }
            }

            queue.Enqueue(new TransferHistoryEntry
            {
                CompletedAtUtc = DateTime.UtcNow,
                DriveLetter = "",
                RemoteName = rowRemote,
                FileName = name,
                SizeBytes = bytes,
                TransferredBytes = bytes,
                DurationMs = null,
                Error = "",
                Direction = dir,
                Job = jobName ?? "",
            });
        }

        // Same shape as TryEnqueueHistoryRow but for --dry-run's NOTICE lines.
        // Silent on non-matching lines (stats, INFO, unrelated NOTICEs).
        private static void TryEnqueueDryRunAction(string line, ConcurrentQueue<DryRunAction> queue)
        {
            var m = DryRunSkipRe.Match(line);
            if (!m.Success) return;
            queue.Enqueue(new DryRunAction(
                m.Groups["verb"].Value,
                m.Groups["name"].Value,
                m.Groups["detail"].Success ? m.Groups["detail"].Value : ""));
        }

        // Mutable per-verb tally passed through the stderr callback so the
        // summary row can print a breakdown without re-parsing the log.
        // Fields kept as public ints for use with Interlocked.Increment.
        internal sealed class OpCounts
        {
            public int Uploaded;
            public int Downloaded;
            public int Transferred; // cross-remote / server-side copies with no clear direction
            public int Deleted;

            public int TotalFiles => Uploaded + Downloaded + Transferred + Deleted;
        }

        // Emit one synthetic transfers.jsonl row summarising the run. Title is
        // "Job: <name>" with the per-verb breakdown appended so History reads
        // as e.g. "Job: BackupPhotos · 47 uploaded · 3 deleted". Direction is
        // picked to match the dominant verb so the row's arrow chip is useful.
        private static async Task AppendJobSummaryAsync(
            JobSpec spec, string jobName, int exitCode,
            JobProgress? finalProgress, OpCounts counts,
            string jobDirection, string rowRemote)
        {
            var pieces = new List<string>();
            if (counts.Uploaded > 0) pieces.Add($"{counts.Uploaded} uploaded");
            if (counts.Downloaded > 0) pieces.Add($"{counts.Downloaded} downloaded");
            if (counts.Transferred > 0) pieces.Add($"{counts.Transferred} transferred");
            if (counts.Deleted > 0) pieces.Add($"{counts.Deleted} deleted");
            var breakdown = pieces.Count > 0
                ? string.Join(" · ", pieces)
                : "no changes";
            var title = $"Job: {jobName} · {breakdown}";

            // Prefer the accurate byte total from rclone /core/stats; fall back
            // to zero if we never got a snapshot (very fast job).
            var bytes = finalProgress?.BytesTransferred ?? 0;
            var durMs = finalProgress is null ? (long?)null : (long)Math.Round(finalProgress.ElapsedSeconds * 1000);
            var err = exitCode == 0 ? "" : $"Job exit {exitCode}";
            // If everything the job did was a delete, mark the row del so the
            // History renderer shows a distinct glyph. Otherwise keep the job's
            // own source→dest direction.
            var summaryDir = (counts.Deleted > 0 && counts.Uploaded + counts.Downloaded + counts.Transferred == 0)
                ? "del"
                : jobDirection;

            var entry = new TransferHistoryEntry
            {
                CompletedAtUtc = DateTime.UtcNow,
                DriveLetter = "",
                RemoteName = rowRemote,
                FileName = title,
                SizeBytes = bytes,
                TransferredBytes = bytes,
                DurationMs = durMs,
                Error = err,
                Direction = summaryDir,
                Job = jobName,
            };
            await TransferHistoryService.AppendAsync(new[] { entry }).ConfigureAwait(false);
        }

        // Drain the queue every 2s so a long-running job doesn't accumulate
        // thousands of pending rows in memory.
        private static async Task FlushHistoryLoopAsync(ConcurrentQueue<TransferHistoryEntry> queue, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try { await DrainHistoryAsync(queue).ConfigureAwait(false); }
                catch { /* history is best-effort */ }
            }
        }

        private static async Task DrainHistoryAsync(ConcurrentQueue<TransferHistoryEntry> queue)
        {
            if (queue.IsEmpty) return;
            var batch = new List<TransferHistoryEntry>();
            while (queue.TryDequeue(out var entry)) batch.Add(entry);
            if (batch.Count > 0)
                await TransferHistoryService.AppendAsync(batch).ConfigureAwait(false);
        }

        // Same trick used by RcloneMountService — grab an ephemeral port, close the
        // socket, hand the number to rclone. There's a race, but the window is small
        // and the failure mode (rclone reports bind error) is loud enough to spot.
        private static int ReserveFreeLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    public sealed record JobResult(int ExitCode, string StderrTail, CheckReport? CheckReport = null, JobProgress? FinalProgress = null, IReadOnlyList<DryRunAction>? DryRunActions = null);
}
