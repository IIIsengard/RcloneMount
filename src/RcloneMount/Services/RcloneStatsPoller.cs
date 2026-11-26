using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RcloneMount.Models;

namespace RcloneMount.Services
{
    // Aggregate throughput across every running mount, sampled from each mount's rclone RC
    // /core/stats endpoint. rclone reports one "speed" per process in bytes/sec — we sum
    // across mounts to give a single global number for the info bar. `PerMount` carries the
    // same signal broken down by Mount.Id so individual rows can show their own rate + health.
    public sealed class AggregateStats
    {
        public double BytesPerSecond { get; init; }
        // Direction-split live rates. When rclone's `transferring[]` doesn't tag a
        // group, that transfer's speed falls into DownBytesPerSecond as a best-effort
        // default (mount activity is mostly reads).
        public double DownBytesPerSecond { get; init; }
        public double UpBytesPerSecond { get; init; }
        public long CumulativeBytes { get; init; }
        // Net new bytes transferred in this tick, summed across mounts. Comes from
        // rclone's cumulative `bytes` counter delta (accurate for total). Consumers use
        // this to feed a lifetime counter — split direction using the ratio of the
        // Down/Up rate fields above.
        public long DeltaBytesThisTick { get; init; }
        public int ActiveMounts { get; init; }
        public bool AnyErrors { get; init; }
        public IReadOnlyList<TransferInfo> ActiveTransfers { get; init; } = Array.Empty<TransferInfo>();
        // Transfers whose `completed_at` moved past the per-mount high-water mark
        // since the previous poll. Consumers append these to the history log.
        // Empty on the first poll after a mount comes up so we don't retro-log
        // whatever's still in rclone's ring buffer from before we were watching.
        public IReadOnlyList<TransferHistoryEntry> NewlyCompleted { get; init; } =
            Array.Empty<TransferHistoryEntry>();
        public IReadOnlyDictionary<string, MountSample> PerMount { get; init; } =
            new Dictionary<string, MountSample>();
    }

    // Per-mount snapshot used by both the mount row (rate strip) and the watchdog.
    // Healthy means the child process is up AND the drive-letter root is readable —
    // rclone can stay alive after WinFsp detaches, so process liveness alone isn't enough.
    // Space fields are only populated when the backend supports /operations/about (some
    // backends — bucket-style S3, sftp roots, etc. — don't). Consumers should gate on
    // HasSpaceInfo before dividing.
    public sealed class MountSample
    {
        public string MountId { get; init; } = "";
        public double BytesPerSecond { get; init; }
        // Direction-split per-mount live rates. Sum matches BytesPerSecond in the
        // common case; may under-count if rclone reports on transfers we can't classify.
        public double DownBytesPerSecond { get; init; }
        public double UpBytesPerSecond { get; init; }
        public bool Healthy { get; init; }
        public bool RcResponded { get; init; }
        public bool HasSpaceInfo { get; init; }
        public long TotalBytes { get; init; }
        public long UsedBytes { get; init; }
        public long FreeBytes { get; init; }
        // VFS write-backs not yet on the remote (uploading + queued), from `vfs/stats`.
        public int UploadsPending { get; init; }
    }

    // One in-flight transfer from a mount's rclone RC /core/stats.transferring[] array.
    public sealed class TransferInfo
    {
        public string DriveLetter { get; init; } = "";  // owning mount's drive letter
        public string RemoteName { get; init; } = "";
        public string FileName { get; init; } = "";
        public long TotalBytes { get; init; }
        public long TransferredBytes { get; init; }
        public double BytesPerSecond { get; init; }
        public double? EtaSeconds { get; init; }
    }

    public sealed class RcloneStatsPoller : IDisposable
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(2),
            DefaultRequestHeaders = { Authorization = RcAuth.Header }
        };

        private readonly RcloneMountService mountService;
        private readonly Func<IReadOnlyList<Mount>> mountSource;
        // Previous byte counts per mount so we can derive instantaneous throughput from
        // the delta between polls. rclone's own `speed` field is a cumulative average and
        // never drops to zero when transfers pause, which reads wrong in a live info bar.
        private readonly Dictionary<string, (long bytes, DateTime at)> lastSamples = new();

        // operations/about can be slow on some backends (Google Drive quotas, WebDAV, etc.)
        // so we cache the response for a while instead of refetching every 2s stats tick.
        // Backends that don't implement about get a null cached value so we don't retry
        // on every poll — flushed only when the mount is torn down.
        private readonly Dictionary<string, (AboutInfo? info, DateTime at)> lastAbout = new();
        private static readonly TimeSpan AboutCacheTtl = TimeSpan.FromSeconds(30);

        // High-water mark for the most recent `completed_at` observed per mount.
        // Anything strictly newer than this on the next poll is treated as a new
        // completion. Initialised lazily to "now" when a mount is first seen so we
        // don't retro-log the ring-buffer entries left over from before we started.
        private readonly Dictionary<string, DateTime> lastCompletedSeenUtc = new();

        private sealed record AboutInfo(long Total, long Used, long Free);

        // Last vfs/stats count per mount. A failed call keeps it (no label flicker, no dip in
        // the busy total) but only for PendingKeepFor: a hung rclone must not hold the PC awake.
        private readonly Dictionary<string, (int count, DateTime at)> lastPending = new();
        private static readonly TimeSpan PendingKeepFor = TimeSpan.FromSeconds(30);

        public RcloneStatsPoller(RcloneMountService mountService, Func<IReadOnlyList<Mount>> mountSource)
        {
            this.mountService = mountService;
            this.mountSource = mountSource;
        }

        public async Task<AggregateStats> PollAsync(CancellationToken ct = default)
        {
            var mounts = mountSource();
            if (mounts.Count == 0)
                return new AggregateStats();

            var tasks = new List<Task<PerMountSample>>();
            foreach (var m in mounts)
            {
                var port = mountService.GetRcPort(m);
                if (port is null) continue;
                tasks.Add(SampleOneAsync(m, port.Value, ct));
            }
            if (tasks.Count == 0)
                return new AggregateStats();

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            double sumInstant = 0;
            double sumDown = 0, sumUp = 0;
            long sumBytes = 0;
            long sumDelta = 0;
            var anyErr = false;
            var transfers = new List<TransferInfo>();
            var newlyCompleted = new List<TransferHistoryEntry>();
            var seenKeys = new HashSet<string>();
            var perMount = new Dictionary<string, MountSample>();
            // Index the polled mounts by id so we can pair the drive-letter probe with
            // the RC sample below without doing an O(n²) lookup per mount.
            var mountsById = mounts.ToDictionary(m => m.Id, m => m);
            foreach (var r in results)
            {
                sumBytes += r.Bytes;
                anyErr |= r.Errors;
                transfers.AddRange(r.Transferring);
                seenKeys.Add(r.MountId);

                double perMountRate = 0;
                if (lastSamples.TryGetValue(r.MountId, out var prev))
                {
                    var dt = (now - prev.at).TotalSeconds;
                    // Guard against clock hiccups and rclone stats resets (byte counter drops).
                    if (dt > 0 && r.Bytes >= prev.bytes)
                    {
                        var deltaBytes = r.Bytes - prev.bytes;
                        perMountRate = deltaBytes / dt;
                        sumDelta += deltaBytes;
                    }
                }
                sumInstant += perMountRate;
                // Direction split comes from summing per-transfer speeds this tick
                // (not deltas), which lines up with rclone's own reporting cadence.
                sumDown += r.DownSpeed;
                sumUp   += r.UpSpeed;
                lastSamples[r.MountId] = (r.Bytes, now);

                // First time we see this mount: prime the watermark to now and
                // drop everything in the ring — we don't want to backfill entries
                // that completed before we started watching.
                if (!lastCompletedSeenUtc.TryGetValue(r.MountId, out var watermark))
                {
                    lastCompletedSeenUtc[r.MountId] = now;
                }
                else if (r.Completed is not null)
                {
                    var newHighWater = watermark;
                    foreach (var entry in r.Completed)
                    {
                        if (entry.CompletedAtUtc <= watermark) continue;
                        newlyCompleted.Add(entry);
                        if (entry.CompletedAtUtc > newHighWater) newHighWater = entry.CompletedAtUtc;
                    }
                    if (newHighWater != watermark)
                        lastCompletedSeenUtc[r.MountId] = newHighWater;
                }

                if (r.UploadsPending is int fresh) lastPending[r.MountId] = (fresh, now);
                var pending = lastPending.TryGetValue(r.MountId, out var lp) && now - lp.at < PendingKeepFor ? lp.count : 0;

                var driveOk = mountsById.TryGetValue(r.MountId, out var m)
                              && DriveLetterReachable(m.DriveLetter);

                // Layer in cached-or-fresh backend space. This runs sequentially per
                // mount rather than in parallel with stats so the cache dict stays
                // single-threaded (poller callers are UI-timer bound).
                AboutInfo? about = null;
                if (m is not null && r.RcResponded)
                {
                    var port = mountService.GetRcPort(m);
                    if (port is not null)
                        about = await GetOrFetchAboutAsync(m, port.Value, ct).ConfigureAwait(false);
                }

                perMount[r.MountId] = new MountSample
                {
                    MountId = r.MountId,
                    BytesPerSecond = perMountRate,
                    DownBytesPerSecond = r.DownSpeed,
                    UpBytesPerSecond   = r.UpSpeed,
                    RcResponded = r.RcResponded,
                    // Healthy only when both the RC socket answered AND the mount point is
                    // browsable. Either alone can misreport: rclone can keep RC up briefly
                    // after WinFsp detaches; the drive can be technically listable but with
                    // I/O errors when rclone has died mid-request.
                    Healthy = r.RcResponded && driveOk,
                    HasSpaceInfo = about is not null,
                    TotalBytes = about?.Total ?? 0,
                    UsedBytes = about?.Used ?? 0,
                    FreeBytes = about?.Free ?? 0,
                    UploadsPending = pending,
                };
            }

            // Drop samples for mounts that have disappeared so the dict doesn't grow unbounded.
            var stale = lastSamples.Keys.Where(k => !seenKeys.Contains(k)).ToList();
            foreach (var k in stale) { lastSamples.Remove(k); lastAbout.Remove(k); lastCompletedSeenUtc.Remove(k); lastPending.Remove(k); }

            return new AggregateStats
            {
                BytesPerSecond = sumInstant,
                DownBytesPerSecond = sumDown,
                UpBytesPerSecond = sumUp,
                CumulativeBytes = sumBytes,
                DeltaBytesThisTick = sumDelta,
                ActiveMounts = results.Length,
                AnyErrors = anyErr,
                ActiveTransfers = transfers,
                NewlyCompleted = newlyCompleted,
                PerMount = perMount,
            };
        }

        // Cheap "is the mount point browsable?" probe. Directory.Exists on the drive root
        // returns quickly even when the underlying FS is unhappy; we don't need to enumerate.
        private static bool DriveLetterReachable(string driveLetter)
        {
            if (string.IsNullOrWhiteSpace(driveLetter)) return false;
            var root = driveLetter.TrimEnd(':') + ":\\";
            try { return System.IO.Directory.Exists(root); }
            catch { return false; }
        }

        private readonly struct PerMountSample
        {
            public string MountId { get; init; }
            public double Speed { get; init; }
            public long Bytes { get; init; }
            public bool Errors { get; init; }
            public bool RcResponded { get; init; }
            public IReadOnlyList<TransferInfo> Transferring { get; init; }
            // Direction-split from summing per-transfer `speed` grouped by the transfer's
            // `group` field. See ClassifyDirection for the heuristic.
            public double DownSpeed { get; init; }
            public double UpSpeed { get; init; }
            // Raw entries pulled from core/transferred this tick. Not yet dedup'd
            // against the per-mount high-water mark — that happens in PollAsync so
            // the sampler stays stateless.
            public IReadOnlyList<TransferHistoryEntry> Completed { get; init; }
            public int? UploadsPending { get; init; } // null: vfs/stats failed this tick
        }

        // Splits a rclone `transferring[]` item into up/down using the `group` tag if
        // present. rclone tags VFS reads as `vfs/...read...` and write-backs as
        // `vfs/...upload...` / `write-back`. Unknown groups default to down since a
        // mount's dominant workload is reading.
        private static (bool isUp, bool isDown) ClassifyDirection(string? group)
        {
            if (string.IsNullOrWhiteSpace(group)) return (false, true);
            var g = group.ToLowerInvariant();
            var up = g.Contains("upload") || g.Contains("write") || g.Contains("put");
            var down = g.Contains("download") || g.Contains("read") || g.Contains("fetch") || g.Contains("get");
            if (up && !down) return (true, false);
            if (down && !up) return (false, true);
            return (false, true); // ambiguous → treat as down
        }

        private static async Task<PerMountSample> SampleOneAsync(Mount mount, int port, CancellationToken ct)
        {
            try
            {
                using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync($"http://127.0.0.1:{port}/core/stats", content, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return new PerMountSample { MountId = mount.Id, Transferring = Array.Empty<TransferInfo>() };
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var speed = root.TryGetProperty("speed", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
                var bytes = root.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : 0;
                var errors = root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Number && e.GetInt32() > 0;
                var transfers = new List<TransferInfo>();
                double downSpeed = 0, upSpeed = 0;
                if (root.TryGetProperty("transferring", out var tr) && tr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in tr.EnumerateArray())
                    {
                        var sp = it.TryGetProperty("speed", out var spEl) && spEl.ValueKind == JsonValueKind.Number ? spEl.GetDouble() : 0;
                        var group = it.TryGetProperty("group", out var gEl) && gEl.ValueKind == JsonValueKind.String ? gEl.GetString() : null;
                        var (isUp, isDown) = ClassifyDirection(group);
                        if (isUp) upSpeed += sp; else if (isDown) downSpeed += sp;

                        transfers.Add(new TransferInfo
                        {
                            DriveLetter = mount.DriveLetter,
                            RemoteName = mount.RemoteName,
                            FileName = it.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            TotalBytes = it.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0,
                            TransferredBytes = it.TryGetProperty("bytes", out var by) && by.ValueKind == JsonValueKind.Number ? by.GetInt64() : 0,
                            BytesPerSecond = sp,
                            EtaSeconds = it.TryGetProperty("eta", out var et) && et.ValueKind == JsonValueKind.Number ? et.GetDouble() : (double?)null,
                        });
                    }
                }
                var completed = await FetchTransferredAsync(mount, port, ct).ConfigureAwait(false);
                var pending = await FetchUploadsPendingAsync(port, ct).ConfigureAwait(false);
                return new PerMountSample { MountId = mount.Id, Speed = speed, Bytes = bytes, Errors = errors, RcResponded = true, Transferring = transfers, DownSpeed = downSpeed, UpSpeed = upSpeed, Completed = completed, UploadsPending = pending };
            }
            catch
            {
                // Mount might have just torn down between the port lookup and this call.
                return new PerMountSample { MountId = mount.Id, Transferring = Array.Empty<TransferInfo>(), Completed = Array.Empty<TransferHistoryEntry>() };
            }
        }

        // Snapshot of rclone's `core/transferred` ring buffer for this mount. Returns
        // raw entries — dedup + timestamp watermarking happens in PollAsync so a stray
        // clock skew here can't corrupt state that outlives one poll.
        private static async Task<IReadOnlyList<TransferHistoryEntry>> FetchTransferredAsync(Mount mount, int port, CancellationToken ct)
        {
            try
            {
                using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync($"http://127.0.0.1:{port}/core/transferred", content, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return Array.Empty<TransferHistoryEntry>();
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("transferred", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return Array.Empty<TransferHistoryEntry>();

                var list = new List<TransferHistoryEntry>();
                foreach (var it in arr.EnumerateArray())
                {
                    var completedAt = ReadTimestamp(it, "completed_at");
                    if (completedAt is null) continue; // skip malformed / not-yet-finished rows
                    var startedAt   = ReadTimestamp(it, "started_at");
                    var size  = it.TryGetProperty("size",  out var szEl) && szEl.ValueKind == JsonValueKind.Number ? szEl.GetInt64() : 0;
                    var bytes = it.TryGetProperty("bytes", out var byEl) && byEl.ValueKind == JsonValueKind.Number ? byEl.GetInt64() : 0;
                    var name  = it.TryGetProperty("name",  out var nEl)  && nEl.ValueKind  == JsonValueKind.String ? nEl.GetString() ?? "" : "";
                    var error = it.TryGetProperty("error", out var eEl)  && eEl.ValueKind  == JsonValueKind.String ? eEl.GetString() ?? "" : "";
                    var group = it.TryGetProperty("group", out var gEl)  && gEl.ValueKind  == JsonValueKind.String ? gEl.GetString() : null;
                    var (isUp, isDown) = ClassifyDirection(group);
                    long? durationMs = null;
                    if (startedAt is not null)
                        durationMs = (long)Math.Max(0, (completedAt.Value - startedAt.Value).TotalMilliseconds);

                    list.Add(new TransferHistoryEntry
                    {
                        CompletedAtUtc = completedAt.Value,
                        DriveLetter = mount.DriveLetter,
                        RemoteName = mount.RemoteName,
                        FileName = name,
                        SizeBytes = size,
                        TransferredBytes = bytes,
                        DurationMs = durationMs,
                        Error = error,
                        Direction = isUp ? "up" : isDown ? "down" : "",
                    });
                }
                return list;
            }
            catch
            {
                return Array.Empty<TransferHistoryEntry>();
            }
        }

        // Uploads this mount's rclone has not finished, or null when its RC did not answer
        // (the caller cannot tell). Every unmount path checks this first (MainForm.UploadsGuardAsync).
        internal static async Task<int?> FetchUploadsPendingAsync(int port, CancellationToken ct = default)
        {
            try
            {
                using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync($"http://127.0.0.1:{port}/vfs/stats", content, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                return ParseUploadsPending(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            }
            catch
            {
                return null;
            }
        }

        // uploadsInProgress + uploadsQueued. rclone leaves out `diskCache` when
        // --vfs-cache-mode is off: writes then go straight to the remote, so nothing is pending.
        internal static int ParseUploadsPending(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("diskCache", out var dc) || dc.ValueKind != JsonValueKind.Object)
                return 0;
            int Read(string key) =>
                dc.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            return Read("uploadsInProgress") + Read("uploadsQueued");
        }

        private static DateTime? ReadTimestamp(JsonElement obj, string prop)
        {
            if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String) return null;
            var s = el.GetString();
            if (string.IsNullOrEmpty(s)) return null;
            if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.ToUniversalTime();
            return null;
        }

        // Returns the cached AboutInfo when fresh (within AboutCacheTtl), otherwise POSTs
        // /operations/about. Caches null when the backend doesn't implement about so we
        // don't retry the endpoint every 2s — the row just won't show a space bar.
        private async Task<AboutInfo?> GetOrFetchAboutAsync(Mount mount, int port, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            if (lastAbout.TryGetValue(mount.Id, out var cached) && now - cached.at < AboutCacheTtl)
                return cached.info;

            var info = await FetchAboutAsync(mount, port, ct).ConfigureAwait(false);
            lastAbout[mount.Id] = (info, now);
            return info;
        }

        private static async Task<AboutInfo?> FetchAboutAsync(Mount mount, int port, CancellationToken ct)
        {
            try
            {
                var fsSpec = $"{mount.RemoteName}:{mount.RemotePath?.TrimStart('/') ?? string.Empty}";
                var body = JsonSerializer.Serialize(new { fs = fsSpec });
                using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync($"http://127.0.0.1:{port}/operations/about", content, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                long ReadLong(string key) =>
                    root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                var total = ReadLong("total");
                var used = ReadLong("used");
                var free = ReadLong("free");
                // Some backends only fill a subset — if there's nothing at all, treat it
                // as unsupported so the row doesn't render a meaningless empty bar.
                if (total <= 0 && used <= 0 && free <= 0) return null;
                // Derive whichever leg is missing when we have the other two — makes the
                // ratio math consistent across backends that omit one field.
                if (total <= 0 && used > 0 && free > 0) total = used + free;
                if (used <= 0 && total > 0 && free >= 0) used = total - free;
                if (free <= 0 && total > 0 && used >= 0) free = total - used;
                return new AboutInfo(total, used, free);
            }
            catch
            {
                return null;
            }
        }

        public static string FormatRate(double bytesPerSecond)
        {
            if (bytesPerSecond < 1) return "0 B/s";
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
            var i = 0;
            var v = bytesPerSecond;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return v >= 100 ? $"{v:F0} {units[i]}" : $"{v:F1} {units[i]}";
        }

        public void Dispose() { /* HttpClient is static and shared */ }
    }
}
