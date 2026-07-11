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
                return !string.IsNullOrEmpty(fvi.ProductVersion) ? fvi.ProductVersion : fvi.FileVersion;
            }
            catch
            {
                return null;
            }
        }

        private static System.Collections.Generic.IEnumerable<string> DefaultDirs()
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinFsp");
        }
    }
}
