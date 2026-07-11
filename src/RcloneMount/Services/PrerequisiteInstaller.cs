using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace RcloneMount.Services
{
    public static class PrerequisiteInstaller
    {
        private static readonly HttpClient http = CreateClient();

        public static string DefaultRcloneExePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "rclone", "rclone.exe");

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("RcloneMount/1.0");
            return c;
        }

        // Downloads the rclone Windows zip, extracts rclone.exe, and replaces the file at targetExePath.
        // Assumes any running rclone processes have already been stopped.
        public static async Task<string> InstallRcloneAsync(string targetExePath, IProgress<string>? log = null)
        {
            log?.Report("Downloading rclone-current-windows-amd64.zip...");
            var zipPath = Path.Combine(Path.GetTempPath(), $"rclone-{Guid.NewGuid():N}.zip");
            try
            {
                using (var resp = await http.GetAsync("https://downloads.rclone.org/rclone-current-windows-amd64.zip", HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(zipPath);
                    await resp.Content.CopyToAsync(fs);
                }

                var extractDir = Path.Combine(Path.GetTempPath(), $"rclone-{Guid.NewGuid():N}");
                Directory.CreateDirectory(extractDir);
                log?.Report("Extracting...");
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                var extractedExe = Directory.EnumerateFiles(extractDir, "rclone.exe", SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new FileNotFoundException("rclone.exe not found inside downloaded zip.");

                var targetDir = Path.GetDirectoryName(targetExePath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                log?.Report($"Replacing {targetExePath}...");
                File.Copy(extractedExe, targetExePath, overwrite: true);

                try { Directory.Delete(extractDir, recursive: true); } catch { }
                log?.Report("rclone updated.");
                return targetExePath;
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            }
        }

        // Downloads the latest WinFsp MSI and launches msiexec /i "path" /passive.
        // /passive shows the installer progress but the UAC prompt still appears.
        // Returns after msiexec is started; caller should let the user finish the install.
        public static async Task LaunchWinFspInstallerAsync(string assetUrl, IProgress<string>? log = null)
        {
            log?.Report("Downloading WinFsp installer...");
            var msiPath = Path.Combine(Path.GetTempPath(), $"winfsp-{Guid.NewGuid():N}.msi");
            using (var resp = await http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(msiPath);
                await resp.Content.CopyToAsync(fs);
            }

            log?.Report("Launching WinFsp installer (UAC prompt will appear)...");
            var psi = new ProcessStartInfo("msiexec.exe")
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            psi.ArgumentList.Add("/i");
            psi.ArgumentList.Add(msiPath);
            psi.ArgumentList.Add("/passive");
            Process.Start(psi);
        }
    }
}
