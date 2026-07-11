using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace RcloneMount.Services
{
    public record RcloneInfo(string ExecutablePath, string Version);

    public static class RcloneLocator
    {
        public static async Task<RcloneInfo?> LocateAsync()
        {
            var candidate = FindOnPath("rclone.exe")
                ?? TryPath(Path.Combine(AppContext.BaseDirectory, "rclone.exe"))
                ?? TryPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "rclone", "rclone.exe"))
                ?? TryPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rclone", "rclone.exe"));

            return candidate is null ? null : await ExamineAsync(candidate).ConfigureAwait(false);
        }

        public static async Task<RcloneInfo?> ExamineAsync(string exePath)
        {
            if (!File.Exists(exePath))
                return null;
            var version = await GetVersionAsync(exePath).ConfigureAwait(false);
            return version is null ? null : new RcloneInfo(exePath, version);
        }

        private static string? TryPath(string path) => File.Exists(path) ? path : null;

        private static string? FindOnPath(string exeName)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv))
                return null;

            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var full = Path.Combine(dir.Trim('"'), exeName);
                    if (File.Exists(full))
                        return full;
                }
                catch { }
            }
            return null;
        }

        private static async Task<string?> GetVersionAsync(string exePath)
        {
            try
            {
                var psi = new ProcessStartInfo(exePath, "version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc is null)
                    return null;

                var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);

                using var reader = new StringReader(stdout);
                var firstLine = await reader.ReadLineAsync().ConfigureAwait(false);
                return firstLine?.Trim();
            }
            catch
            {
                return null;
            }
        }
    }
}
