using System;
using System.IO;
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
            key.SetValue(ValueName, RunValue(exePath));
        }

        // Re-points a Run value whose exe is gone (app moved or rebuilt). Never creates a value.
        // Returns the replaced value, or null if nothing was written.
        // shortcut: an exe on an unplugged or offline drive counts as gone; check the drive too if portable use matters.
        public static string? RepairIfStale(string currentExePath)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                var old = key?.GetValue(ValueName) as string;
                var repaired = RepairedValue(old, currentExePath, File.Exists);
                if (repaired is null) return null;

                key!.SetValue(ValueName, repaired);
                return old;
            }
            catch
            {
                return null; // repair is best-effort
            }
        }

        internal static string? RepairedValue(string? value, string currentExePath, Func<string, bool> fileExists)
        {
            // Only a quoted full path (the form Enable writes) is trusted; anything else is left alone.
            if (value is null || !value.StartsWith('"')) return null;
            var end = value.IndexOf('"', 1);
            if (end < 0) return null;

            var exe = value[1..end];
            return Path.IsPathFullyQualified(exe) && !fileExists(exe) ? RunValue(currentExePath) : null;
        }

        private static string RunValue(string exePath) => $"\"{exePath}\" --startup";

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
