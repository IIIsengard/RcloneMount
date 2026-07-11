using System;
using Microsoft.Win32;

namespace RcloneMount.Services
{
    public static class AutostartService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "RcloneMount";
        private const string LegacyValueName = "MegaCloudDrive";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is not null;
            }
            catch
            {
                return false;
            }
        }

        public static void Enable(string exePath)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(ValueName, $"\"{exePath}\" --startup");
        }

        public static void Disable()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        // If an old MegaCloudDrive Run value is present, migrate it: re-point to the current exe under the new name
        // and remove the stale entry.
        public static void MigrateLegacyIfNeeded(string currentExePath)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key is null) return;

                var legacy = key.GetValue(LegacyValueName);
                if (legacy is null) return;

                if (key.GetValue(ValueName) is null)
                    key.SetValue(ValueName, $"\"{currentExePath}\" --startup");

                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }
            catch
            {
                // migration is best-effort
            }
        }
    }
}
