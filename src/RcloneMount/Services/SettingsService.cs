using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RcloneMount.Models;

namespace RcloneMount.Services
{
    public class AppSettings
    {
        public string? RcloneExePath { get; set; }
        public bool CheckForUpdatesOnStartup { get; set; } = true;
        public bool AutoInstallUpdates { get; set; }
        public bool FirstRunSetupDone { get; set; }
        public string ThemePreference { get; set; } = "Auto"; // Auto | Light | Dark
        public List<Mount> Mounts { get; set; } = new();
    }

    public static class SettingsService
    {
        private const string AppFolderName = "RcloneMount";
        private const string LegacyAppFolderName = "MegaCloudDrive";
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SettingsFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppFolderName,
            "settings.json");

        public static string ActivityLogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppFolderName,
            "activity.log");

        private static string LegacySettingsFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            LegacyAppFolderName,
            "settings.json");

        // Synchronous flavor for very-early startup (theme selection happens before we can
        // await). Falls back to defaults if anything fails — never throws.
        public static AppSettings LoadSync()
        {
            try
            {
                MigrateLegacyIfNeeded();
                if (!File.Exists(SettingsFilePath)) return new AppSettings();
                var json = File.ReadAllText(SettingsFilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch { return new AppSettings(); }
        }

        public static async Task<AppSettings> LoadAsync()
        {
            try
            {
                MigrateLegacyIfNeeded();

                if (!File.Exists(SettingsFilePath))
                    return new AppSettings();

                await using var stream = File.OpenRead(SettingsFilePath);
                var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream).ConfigureAwait(false);
                return settings ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public static async Task SaveAsync(AppSettings settings)
        {
            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            await using var stream = File.Create(SettingsFilePath);
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions).ConfigureAwait(false);
        }

        private static void MigrateLegacyIfNeeded()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                    return;
                if (!File.Exists(LegacySettingsFilePath))
                    return;

                var newDir = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrWhiteSpace(newDir) && !Directory.Exists(newDir))
                    Directory.CreateDirectory(newDir);

                File.Copy(LegacySettingsFilePath, SettingsFilePath, overwrite: false);
                try { File.Delete(LegacySettingsFilePath); } catch { }

                var legacyDir = Path.GetDirectoryName(LegacySettingsFilePath);
                if (!string.IsNullOrWhiteSpace(legacyDir) && Directory.Exists(legacyDir))
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(legacyDir).Length == 0)
                            Directory.Delete(legacyDir);
                    }
                    catch { }
                }
            }
            catch
            {
                // migration is best-effort
            }
        }
    }
}
