using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        public bool NotificationsEnabled { get; set; } = true;
        // When set, settings backups go here instead of %AppData%\RcloneMount\backups.
        public string? BackupsFolderOverride { get; set; }
        public List<Mount> Mounts { get; set; } = new();
        public List<TransferJob> Jobs { get; set; } = new();
    }

    public static class SettingsService
    {
        private const string AppFolderName = "RcloneMount";
        private const string LegacyAppFolderName = "MegaCloudDrive";
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        // Tests redirect these so they never touch the user's files. The activity log, the
        // default backups folder and the legacy settings file all follow PathOverride.
        internal static string? PathOverride { get; set; }
        internal static string? RcloneConfigPathOverride { get; set; }

        private static string SettingsFilePath => PathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppFolderName,
            "settings.json");

        private static string DataFolder => Path.GetDirectoryName(SettingsFilePath)!;

        public static string ActivityLogPath => Path.Combine(DataFolder, "activity.log");

        public static string DefaultBackupsFolder => Path.Combine(DataFolder, "backups");

        // Runtime backup location. Kept in sync with AppSettings.BackupsFolderOverride
        // via LoadAsync/LoadSync/SaveAsync — those are the only writers.
        private static string? backupsFolderOverride;
        public static string BackupsFolder =>
            string.IsNullOrWhiteSpace(backupsFolderOverride)
                ? DefaultBackupsFolder
                : backupsFolderOverride!;

        // How many timestamped backups to retain. Older ones are trimmed on each save.
        private const int MaxBackups = 20;

        public sealed record SettingsBackup(string Path, DateTime TimestampUtc, long SizeBytes);

        // Public path so the UI can restore over the live file.
        public static string CurrentSettingsPath => SettingsFilePath;

        // rclone's default config location. Used only as a fallback when we can't ask
        // rclone itself where its config lives (e.g. exe not detected yet).
        public static string DefaultRcloneConfigPath => RcloneConfigPathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "rclone",
            "rclone.conf");

        // rclone.exe path — set by MainForm once we've located rclone so backup/restore
        // can shell out `rclone config file` for the true path (which may differ from
        // the default when RCLONE_CONFIG or --config is in play).
        private static string? knownRcloneExePath;
        private static string? cachedRcloneConfigPath;
        public static void SetRcloneExePath(string? path)
        {
            if (string.Equals(path ?? "", knownRcloneExePath ?? "", StringComparison.OrdinalIgnoreCase)) return;
            knownRcloneExePath = path;
            cachedRcloneConfigPath = null; // path changed → drop cached resolution
        }

        // Asks rclone where its config file actually is. Falls back to the default
        // %AppData%\rclone\rclone.conf location when rclone.exe hasn't been located or
        // the invocation fails. Cached because "rclone config file" spawns a process.
        public static string ResolveRcloneConfigPath()
        {
            if (!string.IsNullOrWhiteSpace(cachedRcloneConfigPath))
                return cachedRcloneConfigPath!;

            var exe = knownRcloneExePath;
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                try
                {
                    var psi = new ProcessStartInfo(exe, "config file")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var p = Process.Start(psi);
                    if (p is not null)
                    {
                        var stdout = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(2000);
                        if (p.ExitCode == 0)
                        {
                            // Output looks like:
                            //   Configuration file is stored at:
                            //   C:\Users\me\AppData\Roaming\rclone\rclone.conf
                            var last = stdout
                                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                .Select(l => l.Trim())
                                .LastOrDefault(l => l.Length > 0);
                            if (!string.IsNullOrWhiteSpace(last))
                            {
                                cachedRcloneConfigPath = last;
                                return last;
                            }
                        }
                    }
                }
                catch { }
            }
            return DefaultRcloneConfigPath;
        }

        private static string LegacySettingsFilePath => Path.Combine(
            Path.GetDirectoryName(DataFolder)!,
            LegacyAppFolderName,
            "settings.json");

        // Synchronous flavor for very-early startup (theme selection happens before we can
        // await). Falls back to defaults if anything fails — never throws.
        // Theme-only early read: never quarantines, so LoadAsync still sees a bad file and
        // can tell the user about it.
        public static AppSettings LoadSync()
        {
            try
            {
                MigrateLegacyIfNeeded();
                var s = LoadFrom(SettingsFilePath, quarantine: false, out _);
                backupsFolderOverride = s.BackupsFolderOverride;
                return s;
            }
            catch { return new AppSettings(); }
        }

        // Set when LoadAsync found settings.json unparseable and moved it aside, so the UI can
        // say so instead of silently starting empty.
        public static string? LastQuarantinedPath { get; private set; }

        // Set when settings.json exists but could not be read (locked, no access). Saving
        // defaults over it would destroy the user's mounts and jobs, so SaveAsync is a no-op
        // for the rest of this run.
        public static string? LoadReadError { get; private set; }

        public static Task<AppSettings> LoadAsync()
        {
            MigrateLegacyIfNeeded();
            AppSettings s;
            try
            {
                s = LoadFrom(SettingsFilePath, quarantine: true, out var movedTo);
                LastQuarantinedPath = movedTo;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoadReadError = ex.Message;
                s = new AppSettings();
            }
            backupsFolderOverride = s.BackupsFolderOverride;
            return Task.FromResult(s);
        }

        // Missing file → defaults. Unparseable file → defaults, and with quarantine the file is
        // renamed to settings.json.bad-yyyyMMdd-HHmmss (path returned) so nothing is lost.
        // A file that can't be read even after retries throws IOException/UnauthorizedAccess.
        internal static AppSettings LoadFrom(string path, bool quarantine, out string? quarantinedTo)
        {
            quarantinedTo = null;
            if (!File.Exists(path)) return new AppSettings();
            string json;
            for (var attempt = 1; ; attempt++)
            {
                try { json = File.ReadAllText(path); break; }
                catch (IOException) when (attempt < 3) { System.Threading.Thread.Sleep(300); }
            }
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                if (quarantine)
                {
                    var bad = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
                    try { File.Move(path, bad); quarantinedTo = bad; } catch { }
                }
                return new AppSettings();
            }
        }

        // Set by a successful restore: the app is about to restart, and nothing it still holds in
        // memory may overwrite the restored files. Totals only when the backup held them, or the
        // exit save would be lost. Only touched under saveGate; tests reset them.
        internal static bool Restored { get; set; }
        internal static bool TotalsRestored { get; set; }

        public static async Task SaveAsync(AppSettings settings)
        {
            if (LoadReadError is not null) return; // see LoadReadError
            // The argument is evaluated before the first await, on the caller's thread.
            await WriteSnapshotAsync(TakeSnapshot(settings)).ConfigureAwait(false);
        }

        internal sealed record Snapshot(long Order, string Json, string? BackupsFolderOverride);

        private static long snapshotsTaken;
        private static long newestWritten; // only touched while holding saveGate

        // The caller keeps editing the same Mounts/Jobs lists, so copy everything a save needs now.
        internal static Snapshot TakeSnapshot(AppSettings settings) => new(
            System.Threading.Interlocked.Increment(ref snapshotsTaken),
            JsonSerializer.Serialize(settings, JsonOptions),
            settings.BackupsFolderOverride);

        internal static async Task WriteSnapshotAsync(Snapshot snapshot)
        {
            await saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
            // SemaphoreSlim promises no FIFO order: never replace a newer snapshot with an older one.
            if (Restored || snapshot.Order < newestWritten) return;

            // Keep the runtime override in sync with what's about to hit disk so the
            // BackupsFolder used a moment later reflects the new choice.
            backupsFolderOverride = snapshot.BackupsFolderOverride;

            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            // Serialized once (in TakeSnapshot) so we can (a) content-hash it against the previous
            // file to decide whether to snapshot a backup, and (b) atomically overwrite.
            var json = snapshot.Json;

            var changed = true;
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var oldText = await File.ReadAllTextAsync(SettingsFilePath).ConfigureAwait(false);
                    changed = !string.Equals(oldText, json, StringComparison.Ordinal);
                    if (changed)
                        BackupCurrentFile();
                }
            }
            catch
            {
                // A failed backup is not a reason to skip the save.
            }

            WriteAtomic(SettingsFilePath, json);
            newestWritten = snapshot.Order;

            if (changed)
                TrimBackups();
            }
            finally { saveGate.Release(); }
        }

        // Scheduler completions, UI actions and timers all save; one at a time.
        private static readonly System.Threading.SemaphoreSlim saveGate = new(1, 1);

        // Under saveGate, so a totals write already on its way cannot land on top of restored totals.
        internal static void WriteUnlessTotalsRestored(string path, string text)
        {
            saveGate.Wait();
            try { if (!TotalsRestored) WriteAtomic(path, text); }
            finally { saveGate.Release(); }
        }

        // Atomic write: the text goes to a unique .tmp sibling, then File.Move(overwrite)
        // renames it over the target — atomic on NTFS same-volume, so a crash or power loss
        // leaves either the old file or the new one, never a truncated one. UTF-8, no BOM.
        internal static void WriteAtomic(string path, string text) => WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(text));

        internal static void WriteAtomic(string path, byte[] bytes)
        {
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // File-name prefix for new backups. Older bundles used "settings_" — those are
        // still recognised by ListBackups/TrimBackups so nothing gets lost.
        private const string BackupPrefix = "RcloneMount-Backup-";
        private const string LegacyBackupPrefix = "settings_";

        // Zip the CURRENT settings.json (and rclone.conf if we can find it) into backups/
        // before overwriting. Filename is timestamped so the ring is naturally sorted.
        // The zip layout is a flat archive with two entries: "settings.json" and
        // "rclone.conf" (omitted if not found). Without a settings.json the zip holds defaults,
        // which is what the app is running on, so it can still be restored. Throws when the
        // backup cannot be written; the activity log and transfer totals are best-effort.
        private static void BackupCurrentFile()
        {
            if (!Directory.Exists(BackupsFolder))
                Directory.CreateDirectory(BackupsFolder);
            var ts = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var dest = Path.Combine(BackupsFolder, $"{BackupPrefix}{ts}.zip");
            var i = 1;
            while (File.Exists(dest))
            {
                dest = Path.Combine(BackupsFolder, $"{BackupPrefix}{ts}_{i++}.zip");
                if (i > 99) break;
            }

            // Built under a name ListBackups ignores: a half-written zip must not pass for a backup.
            var temp = $"{dest}.{Guid.NewGuid():N}.tmp";
            try
            {
                WriteBackupZip(temp);
                File.Move(temp, dest);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static void WriteBackupZip(string path)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            if (File.Exists(SettingsFilePath))
                zip.CreateEntryFromFile(SettingsFilePath, "settings.json", CompressionLevel.Optimal);
            else
            {
                using var defaults = zip.CreateEntry("settings.json", CompressionLevel.Optimal).Open();
                JsonSerializer.Serialize(defaults, new AppSettings(), JsonOptions);
            }
            var rclone = ResolveRcloneConfigPath();
            if (File.Exists(rclone))
                zip.CreateEntryFromFile(rclone, "rclone.conf", CompressionLevel.Optimal);
            // The activity log may be being written to right now — open with shared read
            // instead of using CreateEntryFromFile (which takes an exclusive handle).
            if (File.Exists(ActivityLogPath))
            {
                try
                {
                    var entry = zip.CreateEntry("activity.log", CompressionLevel.Optimal);
                    using var src = new FileStream(ActivityLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var dst = entry.Open();
                    src.CopyTo(dst);
                }
                catch
                {
                    // Log couldn't be read — proceed without it.
                }
            }
            if (File.Exists(TransferTotalsService.FilePath))
            {
                try
                {
                    zip.CreateEntryFromFile(TransferTotalsService.FilePath, "transfer-totals.json", CompressionLevel.Optimal);
                }
                catch { }
            }
        }

        // Public entry point for the "Backup now" button. Same underlying implementation
        // as the auto-backup path, but callable regardless of whether anything changed.
        // Takes saveGate: zipping settings.json while a save replaces it would fail that save.
        // Blocking is safe on the UI thread, since no save needs that thread to finish.
        public static void ForceBackup()
        {
            saveGate.Wait();
            try
            {
                if (!File.Exists(SettingsFilePath))
                    throw new FileNotFoundException("There are no saved settings to back up.", SettingsFilePath);
                BackupCurrentFile();
                TrimBackups();
            }
            finally { saveGate.Release(); }
        }

        private static bool IsBackupFile(FileInfo f) =>
            (f.Name.StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase)
             || f.Name.StartsWith(LegacyBackupPrefix, StringComparison.OrdinalIgnoreCase))
            && (f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                || f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase));

        private static void TrimBackups()
        {
            try
            {
                if (!Directory.Exists(BackupsFolder)) return;
                var files = new DirectoryInfo(BackupsFolder)
                    .GetFiles()
                    .Where(IsBackupFile)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                foreach (var f in files.Skip(MaxBackups))
                {
                    try { f.Delete(); } catch { }
                }
            }
            catch { }
        }

        public static IReadOnlyList<SettingsBackup> ListBackups()
        {
            try
            {
                if (!Directory.Exists(BackupsFolder))
                    return Array.Empty<SettingsBackup>();
                return new DirectoryInfo(BackupsFolder)
                    .GetFiles()
                    .Where(IsBackupFile)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => new SettingsBackup(f.FullName, f.LastWriteTimeUtc, f.Length))
                    .ToList();
            }
            catch
            {
                return Array.Empty<SettingsBackup>();
            }
        }

        // Restores from either a .zip bundle (settings.json + rclone.conf) or a legacy
        // .json file (settings only). Snapshots the current state first so an accidental
        // restore can be undone.
        public static void RestoreFromBackup(string backupPath)
        {
            if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
                throw new FileNotFoundException("Backup file not found.", backupPath);

            saveGate.Wait();
            try
            {
                TotalsRestored = RestoreFiles(backupPath);
                Restored = true; // only on success: after a failed restore the app carries on saving
            }
            finally { saveGate.Release(); }
        }

        // Caller holds saveGate. All or nothing: the backup is read and checked in full before
        // any file is touched. Returns whether it held transfer totals.
        private static bool RestoreFiles(string backupPath)
        {
            var files = new List<(string Target, byte[] Content)>();
            byte[]? log = null;
            var hasTotals = false;
            var ext = Path.GetExtension(backupPath);
            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(backupPath);
                var settingsEntry = zip.GetEntry("settings.json");
                if (settingsEntry is null)
                    throw new InvalidDataException("Backup zip is missing settings.json.");

                files.Add((SettingsFilePath, CheckedSettings(ReadEntryText(settingsEntry))));

                var rcloneEntry = zip.GetEntry("rclone.conf");
                if (rcloneEntry is not null)
                    files.Add((ResolveRcloneConfigPath(), System.Text.Encoding.UTF8.GetBytes(ReadEntryText(rcloneEntry))));

                // Lifetime transfer counters — restore alongside so the number keeps its
                // meaning after a machine migration.
                var totalsEntry = zip.GetEntry("transfer-totals.json");
                if (totalsEntry is not null)
                {
                    files.Add((TransferTotalsService.FilePath, ReadEntryBytes(totalsEntry)));
                    hasTotals = true;
                }

                var logEntry = zip.GetEntry("activity.log");
                if (logEntry is not null)
                    log = ReadEntryBytes(logEntry);
            }
            else
            {
                files.Add((SettingsFilePath, CheckedSettings(File.ReadAllText(backupPath))));
            }

            if (files.Any(f => File.Exists(f.Target)))
            {
                // The user is promised an undo, so no backup means no restore.
                try { BackupCurrentFile(); }
                catch (Exception ex)
                {
                    throw new IOException(
                        $"Your current settings could not be backed up first, so nothing was restored. {ex.Message}", ex);
                }
            }

            ReplaceAll(files);

            // Full-state restore — put activity.log back too so the user can resume
            // with their history intact. History only, so it never fails the restore.
            if (log is not null)
            {
                try { File.WriteAllBytes(ActivityLogPath, log); }
                catch
                {
                    // Log may be held open by the running app — that's the current
                    // process, which is about to exit and restart, so silently skip.
                }
            }
            TrimBackups();
            return hasTotals;
        }

        // A backup that would not load must not replace settings that do.
        private static byte[] CheckedSettings(string json)
        {
            try
            {
                if (JsonSerializer.Deserialize<AppSettings>(json) is not null)
                    return System.Text.Encoding.UTF8.GetBytes(json);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new InvalidDataException($"The backup's settings cannot be read: {ex.Message}", ex);
            }
            throw new InvalidDataException("The backup's settings are empty.");
        }

        // Stages every file beside its target, then swaps them in. If one cannot be swapped, those
        // already done are put back.
        // shortcut: a crash between two swaps leaves a mix; add a restore journal if that is ever
        // seen (the pre-restore backup covers recovery by hand).
        private static void ReplaceAll(IReadOnlyList<(string Target, byte[] Content)> files)
        {
            var staged = new List<(string Target, string Temp, byte[]? Original)>();
            var swapped = 0;
            try
            {
                foreach (var (target, content) in files)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var temp = $"{target}.{Guid.NewGuid():N}.tmp";
                    staged.Add((target, temp, File.Exists(target) ? File.ReadAllBytes(target) : null));
                    File.WriteAllBytes(temp, content);
                }
                foreach (var file in staged)
                {
                    File.Move(file.Temp, file.Target, overwrite: true);
                    swapped++;
                }
            }
            catch (Exception ex)
            {
                var stuck = new List<string>();
                foreach (var file in staged.Take(swapped))
                {
                    try
                    {
                        if (file.Original is null) File.Delete(file.Target);
                        else WriteAtomic(file.Target, file.Original);
                    }
                    catch { stuck.Add(file.Target); }
                }
                if (stuck.Count == 0) throw;
                throw new IOException($"{ex.Message} These files could not be put back and still hold the backup's " +
                    $"content: {string.Join(", ", stuck)}. Your previous settings are in the backups folder.", ex);
            }
            finally
            {
                foreach (var file in staged)
                {
                    try { if (File.Exists(file.Temp)) File.Delete(file.Temp); } catch { }
                }
            }
        }

        private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
        {
            using var s = entry.Open();
            using var memory = new MemoryStream();
            s.CopyTo(memory);
            return memory.ToArray();
        }

        private static string ReadEntryText(ZipArchiveEntry entry)
        {
            using var r = new StreamReader(entry.Open());
            return r.ReadToEnd();
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
