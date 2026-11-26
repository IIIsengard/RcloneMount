using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace RcloneMount.Services
{
    // Lifetime cumulative byte counters. Persisted between sessions so the numbers
    // survive rclone process restarts (whose own /core/stats bytes counters reset).
    public sealed class TransferTotals
    {
        public long DownBytes { get; set; }
        public long UpBytes { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastUpdatedUtc { get; set; }

        public long TotalBytes => DownBytes + UpBytes;
    }

    public static class TransferTotalsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        // Tests point this at a temp file so they never write the user's %AppData%.
        internal static string? PathOverride { get; set; }

        public static string FilePath => PathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RcloneMount",
            "transfer-totals.json");

        public static TransferTotals Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new TransferTotals { FirstSeenUtc = DateTime.UtcNow };
                var json = File.ReadAllText(FilePath);
                var t = JsonSerializer.Deserialize<TransferTotals>(json) ?? new TransferTotals();
                if (t.FirstSeenUtc == default) t.FirstSeenUtc = DateTime.UtcNow;
                return t;
            }
            catch
            {
                return new TransferTotals { FirstSeenUtc = DateTime.UtcNow };
            }
        }

        public static async Task SaveAsync(TransferTotals totals)
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            totals.LastUpdatedUtc = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(totals, JsonOptions);
            // Atomic: saves run right before the process is killed (shutdown, update), and a torn
            // file would load as zero totals.
            await Task.Run(() => SettingsService.WriteUnlessRestored(FilePath, json)).ConfigureAwait(false);
        }
    }
}
