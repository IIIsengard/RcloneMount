using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

        // Downloads the release's rclone Windows zip, checks it against the release's SHA256SUMS,
        // extracts rclone.exe, and replaces the file at targetExePath.
        // Assumes any running rclone processes have already been stopped.
        public static async Task<string> InstallRcloneAsync(string targetExePath, LatestRelease release, IProgress<string>? log = null)
        {
            var zipName = $"rclone-{release.TagName}-windows-amd64.zip";
            var zipAsset = release.Assets.FirstOrDefault(a => a.Name == zipName)
                ?? throw new FileNotFoundException($"{zipName} is not attached to rclone release {release.TagName}.");
            var sumsAsset = release.Assets.FirstOrDefault(a => a.Name == "SHA256SUMS")
                ?? throw new FileNotFoundException($"SHA256SUMS is not attached to rclone release {release.TagName}.");

            log?.Report($"Downloading {zipName}...");
            var zipPath = Path.Combine(Path.GetTempPath(), $"rclone-{Guid.NewGuid():N}.zip");
            try
            {
                using (var resp = await http.GetAsync(zipAsset.Url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(zipPath);
                    await resp.Content.CopyToAsync(fs);
                }

                // shortcut: SHA256SUMS is checked, its PGP signature is not; add PGP verify if rclone downloads move off GitHub.
                log?.Report("Verifying checksum...");
                var expected = FindSha256(await http.GetStringAsync(sumsAsset.Url), zipName)
                    ?? throw new InvalidDataException($"{zipName} is not listed in SHA256SUMS; not installed.");
                string actual;
                await using (var zs = File.OpenRead(zipPath))
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(zs));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{zipName} does not match its SHA256SUMS checksum; not installed.");

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

            if (!IsTrustedSignature(msiPath))
            {
                try { File.Delete(msiPath); } catch { }
                throw new InvalidDataException("The downloaded WinFsp installer has no valid digital signature; not launched.");
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

        // Hash for fileName from a SHA256SUMS text ("<hex>  <name>" lines). PGP clear-sign
        // armor and signature lines never match the two-token, 64-hex shape, so they are skipped.
        internal static string? FindSha256(string sha256sums, string fileName)
        {
            foreach (var line in sha256sums.Split('\n'))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].TrimStart('*') == fileName
                    && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
                    return parts[0];
            }
            return null;
        }

        // shortcut: any valid Authenticode signer passes, the WinFsp publisher is not pinned; pin the signer if downloads move off GitHub.
        internal static bool IsTrustedSignature(string path)
        {
            var pathPtr = Marshal.StringToHGlobalUni(path);
            var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(new WinTrustFileInfo
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    pcwszFilePath = pathPtr,
                }, fileInfoPtr, false);
                var data = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                    dwUIChoice = 2,     // WTD_UI_NONE
                    dwUnionChoice = 1,  // WTD_CHOICE_FILE
                    pFile = fileInfoPtr,
                };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
                return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(fileInfoPtr);
                Marshal.FreeHGlobal(pathPtr);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }
    }
}
