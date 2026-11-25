using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace RcloneMount.Services
{
    public record WinFspInfo(string InstallDir, string? Version);

    public static class WinFspLocator
    {
        public static WinFspInfo? Locate()
        {
            var dir = InstallDirFromRegistry();
            if (dir is null || !Directory.Exists(dir))
            {
                foreach (var candidate in DefaultDirs())
                {
                    if (Directory.Exists(candidate))
                    {
                        dir = candidate;
                        break;
                    }
                }
            }
            if (dir is null || !Directory.Exists(dir))
                return null;

            var version = ReadDllVersion(dir);
            return new WinFspInfo(dir, version);
        }

        private static string? InstallDirFromRegistry()
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                    .OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp");
                return key?.GetValue("InstallDir") as string;
            }
            catch
            {
                return null;
            }
        }

        private static string? ReadDllVersion(string installDir)
        {
            var dllName = Environment.Is64BitOperatingSystem ? "winfsp-x64.dll" : "winfsp-x86.dll";
            var dll = Path.Combine(installDir, "bin", dllName);
            if (!File.Exists(dll))
                return null;
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(dll);
                // FileVersion is numeric (e.g. "2.1.25156.24000"); ProductVersion is the
                // release name (e.g. "2025"). Combine them so the UI shows "2.1 (2025)".
                var numeric = ShortenNumericVersion(fvi.FileVersion);
                var product = (fvi.ProductVersion ?? "").Trim();
                if (!string.IsNullOrEmpty(numeric) && !string.IsNullOrEmpty(product) &&
                    !product.Equals(numeric, StringComparison.OrdinalIgnoreCase) &&
                    !numeric.StartsWith(product, StringComparison.OrdinalIgnoreCase))
                    return $"{numeric} ({product})";
                if (!string.IsNullOrEmpty(numeric)) return numeric;
                return string.IsNullOrEmpty(product) ? null : product;
            }
            catch
            {
                return null;
            }
        }

        private static string? ShortenNumericVersion(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var parts = raw.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _))
                return $"{parts[0]}.{parts[1]}";
            return raw.Trim();
        }

        private static System.Collections.Generic.IEnumerable<string> DefaultDirs()
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinFsp");
        }
    }
}
