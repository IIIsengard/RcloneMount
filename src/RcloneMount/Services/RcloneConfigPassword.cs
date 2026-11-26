using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RcloneMount.Services
{
    // Encrypted rclone.conf support. The password is stored DPAPI-protected for the current
    // Windows user and reaches rclone only through a child's environment: never a command
    // line, a log line or this app's own environment.
    internal static class RcloneConfigPassword
    {
        // Settable so tests never touch the user's real file.
        internal static string FilePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RcloneMount",
            "config-pass.bin");

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RcloneMount rclone.conf password");

        // Null when nothing is stored, or the blob belongs to another Windows user or machine.
        public static string? Load()
        {
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                return null;
            }
        }

        // A typed password on trial: children get it, but it is saved only once rclone accepted it.
        internal static string? Pending { get; set; }

        // What the next rclone child will be given.
        public static string? Current() => Pending ?? Load();

        public static void Save(string password)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            SettingsService.WriteAtomic(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));
        }

        public static void Clear()
        {
            try { File.Delete(FilePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        public static bool IsEncrypted(string configPath) => ReadEncrypted(configPath) == true;

        // Same rule as rclone's loader: the first line that is not blank or a comment decides.
        // rclone itself writes "# Encrypted rclone configuration File" above the header.
        // Null when the file can't be read (missing, or locked while rclone rewrites it).
        public static bool? ReadEncrypted(string configPath)
        {
            try
            {
                var first = File.ReadLines(configPath)
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith(';'));
                return first is not null && first.StartsWith("RCLONE_ENCRYPT_V", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // shortcut: matches rclone's English wording (v1.76: "Couldn't decrypt configuration",
        // "unable to decrypt configuration"), upgrade if rclone rewords it or gets a distinct exit code.
        public static bool IsPasswordError(string stderr) =>
            stderr.Contains("decrypt configuration", StringComparison.OrdinalIgnoreCase);

        // Background jobs and mounts never prompt; their failure text carries this instead.
        public const string RefreshHint = "Open RcloneMount and click Refresh to enter the config password again.";

        // Every background rclone child starts from here, so an encrypted config works for
        // mounts, jobs and config calls alike. RCLONE_ASK_PASSWORD=false: with no or a wrong
        // password rclone exits with an error instead of waiting on a prompt nobody can see.
        public static ProcessStartInfo StartInfo(string exePath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["RCLONE_ASK_PASSWORD"] = "false";
            AddTo(psi);
            return psi;
        }

        // The terminal gets only the password: rclone there is interactive and may prompt.
        public static void AddTo(ProcessStartInfo psi)
        {
            if (Current() is { } password)
                psi.Environment["RCLONE_CONFIG_PASS"] = password;
        }
    }
}
