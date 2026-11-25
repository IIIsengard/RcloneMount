using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RcloneMount.Services
{
    public enum ActivityLevel { Info, Warn, Error }

    public class ActivityEntry
    {
        public DateTime Timestamp { get; }
        public ActivityLevel Level { get; }
        public string Message { get; }
        public ActivityEntry(DateTime ts, ActivityLevel level, string message)
        {
            Timestamp = ts;
            Level = level;
            Message = message;
        }
        public override string ToString() =>
            $"{Timestamp:yyyy-MM-dd HH:mm:ss}  {LevelTag(Level)}  {Message}";

        // Persisted form uses ISO date + level + single-line message.
        public string ToPersistLine() =>
            $"{Timestamp:o}\t{Level}\t{Message.Replace('\n', ' ').Replace('\r', ' ')}";

        public static ActivityEntry? TryParsePersistLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            var parts = line.Split('\t', 3);
            if (parts.Length < 3) return null;
            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
                return null;
            if (!Enum.TryParse<ActivityLevel>(parts[1], ignoreCase: true, out var level))
                level = ActivityLevel.Info;
            return new ActivityEntry(ts, level, parts[2]);
        }

        private static string LevelTag(ActivityLevel level) => level switch
        {
            ActivityLevel.Warn  => "WARN ",
            ActivityLevel.Error => "ERROR",
            _                   => "INFO ",
        };
    }

    // In-memory rolling log with a rolling on-disk tail (30 days by default). Fires Appended
    // so the UI can tail it.
    public class ActivityLog
    {
        private readonly object gate = new();
        private readonly Queue<ActivityEntry> entries = new();
        private readonly int capacity;
        private readonly string? persistPath;
        private readonly TimeSpan retention;

        public event Action<ActivityEntry>? Appended;

        public ActivityLog(int capacity = 2000, string? persistPath = null, TimeSpan? retention = null)
        {
            this.capacity = capacity;
            this.persistPath = persistPath;
            this.retention = retention ?? TimeSpan.FromDays(30);

            if (!string.IsNullOrEmpty(persistPath))
                LoadAndPrunePersistedFile();
        }

        public void Append(ActivityLevel level, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            var entry = new ActivityEntry(DateTime.Now, level, message);
            lock (gate)
            {
                entries.Enqueue(entry);
                while (entries.Count > capacity) entries.Dequeue();
            }
            AppendToPersistFile(entry);
            try { Appended?.Invoke(entry); } catch { }
        }

        public void Info(string message)  => Append(ActivityLevel.Info,  message);
        public void Warn(string message)  => Append(ActivityLevel.Warn,  message);
        public void Error(string message) => Append(ActivityLevel.Error, message);

        public IReadOnlyList<ActivityEntry> Snapshot()
        {
            lock (gate) return entries.ToArray();
        }

        public void Clear()
        {
            lock (gate) entries.Clear();
            if (!string.IsNullOrEmpty(persistPath))
            {
                try { File.Delete(persistPath); } catch { }
            }
        }

        private void LoadAndPrunePersistedFile()
        {
            try
            {
                if (!File.Exists(persistPath)) return;

                var cutoff = DateTime.Now - retention;
                var kept = new List<ActivityEntry>();
                foreach (var line in File.ReadLines(persistPath!))
                {
                    var entry = ActivityEntry.TryParsePersistLine(line);
                    if (entry is null) continue;
                    if (entry.Timestamp < cutoff) continue;
                    kept.Add(entry);
                }

                // Rewrite the file with only entries in the retention window.
                File.WriteAllLines(persistPath!, kept.Select(e => e.ToPersistLine()));

                // Seed the in-memory buffer with the most recent capacity worth.
                var seed = kept.Count > capacity ? kept.GetRange(kept.Count - capacity, capacity) : kept;
                lock (gate)
                {
                    foreach (var e in seed) entries.Enqueue(e);
                }
            }
            catch
            {
                // best-effort: bad log file shouldn't crash the app
            }
        }

        private void AppendToPersistFile(ActivityEntry entry)
        {
            if (string.IsNullOrEmpty(persistPath)) return;
            try
            {
                var dir = Path.GetDirectoryName(persistPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(persistPath!, entry.ToPersistLine() + Environment.NewLine);
            }
            catch
            {
                // don't propagate persistence failures to callers
            }
        }
    }
}
