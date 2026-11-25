using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RcloneMount.Services
{
    // One completed transfer as recorded to transfers.jsonl. Sourced from rclone's
    // `core/transferred` RC endpoint (bounded ring buffer), so a burst of transfers
    // between polls could clip a few entries — good enough for a "did that upload
    // finish?" view, not a forensic audit. Explicit JsonPropertyName so file format
    // stays stable across refactors.
    public sealed class TransferHistoryEntry
    {
        [JsonPropertyName("t")]     public DateTime CompletedAtUtc { get; set; }
        [JsonPropertyName("drive")] public string DriveLetter { get; set; } = "";
        [JsonPropertyName("remote")] public string RemoteName { get; set; } = "";
        [JsonPropertyName("name")]  public string FileName { get; set; } = "";
        [JsonPropertyName("size")]  public long SizeBytes { get; set; }
        [JsonPropertyName("bytes")] public long TransferredBytes { get; set; }
        [JsonPropertyName("dur")]   public long? DurationMs { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; } = "";
        // Direction hint from rclone's stats group tag ("up", "down", or "" when unknown).
        // Cheap to store, avoids re-deriving in the UI.
        [JsonPropertyName("dir")]   public string Direction { get; set; } = "";
        // Friendly name of the transfer job that produced this entry, when the file
        // came from a saved/ad-hoc Transfer job. Empty for mount-side transfers
        // (which have no owning job). Optional in the file format — old lines
        // without it deserialize with an empty string.
        [JsonPropertyName("job")]   public string Job { get; set; } = "";
    }

    // Append-only JSONL store at %APPDATA%\RcloneMount\transfers.jsonl. Reads
    // stream the whole file — capped by PruneAsync so it stays bounded (~5000
    // lines ≈ 1-2 MB). All I/O behind a single SemaphoreSlim; writers are
    // fire-and-forget from the stats poller so a slow disk can't stall polling.
    public static class TransferHistoryService
    {
        private const string AppFolderName = "RcloneMount";
        private const int DefaultMaxEntries = 5000;

        private static readonly SemaphoreSlim gate = new(1, 1);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string HistoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppFolderName,
            "transfers.jsonl");

        // Raised after a successful append so the UI can prepend the new rows
        // to its in-memory tail without re-reading the file. Producers besides
        // the mount stats poller (transfer jobs, scheduled runs) write here
        // directly; without this hook the History tab wouldn't refresh until
        // the app restarted.
        public static event Action<IReadOnlyList<TransferHistoryEntry>>? EntriesAppended;

        public static async Task AppendAsync(IReadOnlyList<TransferHistoryEntry> entries)
        {
            if (entries.Count == 0) return;
            var sb = new StringBuilder(entries.Count * 200);
            foreach (var e in entries)
                sb.AppendLine(JsonSerializer.Serialize(e, JsonOptions));

            var wrote = false;
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var dir = Path.GetDirectoryName(HistoryPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                await File.AppendAllTextAsync(HistoryPath, sb.ToString()).ConfigureAwait(false);
                wrote = true;
            }
            catch
            {
                // Never propagate write failures back to the poller — history is a
                // convenience, not a load-bearing feature. A bad disk or antivirus
                // lock shouldn't take the app down.
            }
            finally
            {
                gate.Release();
            }
            if (wrote)
            {
                try { EntriesAppended?.Invoke(entries); }
                catch { /* subscriber must not break the writer */ }
            }
        }

        // Returns the most-recent `limit` entries in reverse-chronological order
        // (newest first). Reads the whole file — fine at bounded size, revisit if
        // the cap ever grows past ~50k lines.
        public static async Task<IReadOnlyList<TransferHistoryEntry>> ReadRecentAsync(int limit = 200)
        {
            if (!File.Exists(HistoryPath)) return Array.Empty<TransferHistoryEntry>();

            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var list = new List<TransferHistoryEntry>();
                using var stream = new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<TransferHistoryEntry>(line, JsonOptions);
                        if (entry is not null) list.Add(entry);
                    }
                    catch { /* skip malformed line */ }
                }
                // Newest first, honour limit.
                list.Reverse();
                if (list.Count > limit) list.RemoveRange(limit, list.Count - limit);
                return list;
            }
            catch
            {
                return Array.Empty<TransferHistoryEntry>();
            }
            finally
            {
                gate.Release();
            }
        }

        // Trims the file to the most recent `maxEntries` entries, if it's grown past
        // that. Cheap check: if line count ≤ max, skip. Called opportunistically at
        // startup so runtime never pays for it.
        public static async Task PruneAsync(int maxEntries = DefaultMaxEntries)
        {
            if (!File.Exists(HistoryPath)) return;

            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var all = await File.ReadAllLinesAsync(HistoryPath).ConfigureAwait(false);
                if (all.Length <= maxEntries) return;
                var keep = all.Length - maxEntries;
                var tmp = HistoryPath + ".tmp";
                await File.WriteAllLinesAsync(tmp, new ArraySegment<string>(all, keep, maxEntries)).ConfigureAwait(false);
                File.Move(tmp, HistoryPath, overwrite: true);
            }
            catch { /* best-effort */ }
            finally
            {
                gate.Release();
            }
        }
    }
}
