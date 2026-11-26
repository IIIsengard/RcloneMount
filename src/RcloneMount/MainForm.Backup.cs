using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;
using RcloneMount.UI;

namespace RcloneMount
{
    // Backup / restore concerns split out of MainForm.cs for readability.
    // Members here are the ones plumbed to the App backup card in the Settings tab.
    public partial class MainForm
    {
        private void RefreshBackupsList()
        {
            if (backupsCombo is null) return;
            backupsFolderTextbox.Text = SettingsService.BackupsFolder;
            var backups = SettingsService.ListBackups();
            backupsCombo.ClearItems();
            foreach (var b in backups)
            {
                var local = b.TimestampUtc.ToLocalTime();
                backupsCombo.AddItem($"{local:yyyy-MM-dd HH:mm:ss}  —  {b.SizeBytes / 1024.0:F1} KB");
            }
            if (backupsCombo.Items.Count > 0)
                backupsCombo.SelectedIndex = 0;
            backupsCombo.Invalidate();
            backupsCountLabel.Text = backups.Count == 0
                ? "No backups yet — one will be saved the next time settings change."
                : $"{backups.Count} backup(s) available (most recent first).";
        }

        private async Task ChangeBackupsFolderAsync()
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Choose a folder for RcloneMount settings backups.",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                InitialDirectory = System.IO.Directory.Exists(SettingsService.BackupsFolder)
                    ? SettingsService.BackupsFolder
                    : SettingsService.DefaultBackupsFolder
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var chosen = dlg.SelectedPath;
            if (string.IsNullOrWhiteSpace(chosen)) return;
            try
            {
                if (!System.IO.Directory.Exists(chosen))
                    System.IO.Directory.CreateDirectory(chosen);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not use folder: {ex.Message}", "Backups folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            backupsFolderOverride = chosen;
            if (!await SaveSettingsAsync()) return;
            RefreshBackupsList();
            SetStatus($"Backups folder set to {chosen}.");
        }

        private async Task ResetBackupsFolderAsync()
        {
            backupsFolderOverride = null;
            if (!await SaveSettingsAsync()) return;
            RefreshBackupsList();
            SetStatus("Backups folder reset to default.");
        }

        // Forces a snapshot right now. Saves current in-memory state first so the backup
        // reflects the live app rather than a stale on-disk settings.json.
        private async Task BackupNowAsync()
        {
            try
            {
                if (!await SaveSettingsAsync())
                    throw new InvalidOperationException("The current settings could not be saved. See the Log tab.");
                SettingsService.ForceBackup();
                RefreshBackupsList();
                SetStatus("Backup created.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Backup failed: {ex.Message}", "Backup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        internal static string RestoreWarning(string question, int activeTransfers) =>
            $"{question}\n\n" +
            "All mounts will be unmounted and RcloneMount will restart to apply the restored settings. " +
            (activeTransfers > 0
                ? $"{activeTransfers} running or queued transfer(s) will be cancelled. "
                : "Any transfer running by then will be cancelled. ") +
            "Close any rclone terminal or config window first, because it can change rclone.conf. " +
            "Your current settings will be backed up first so this can be undone.";

        private bool restoreRunning;

        // Both restore buttons end here.
        private async Task RestoreAndRestartAsync(string backupPath, string question)
        {
            if (restoreRunning) return;
            var active = jobQueue?.Snapshot().Count(r => r.IsActive) ?? 0;
            var ok = MessageBox.Show(RestoreWarning(question, active),
                "Restore settings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (ok != DialogResult.Yes) return;

            restoreRunning = true;
            try
            {
                if (!await UploadsGuardAsync(mounts, "Restore", interactive: true)) return;
                SetStatus("Stopping mounts and transfers before restore...");
                await RestoreWithWritersStoppedAsync(
                    () => SettingsService.RestoreFromBackup(backupPath), TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Restore failed: {ex.Message}", "Restore error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            finally { restoreRunning = false; }

            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    Process.Start(new ProcessStartInfo(exe, "--restarted") { UseShellExecute = true });
            }
            catch { }
            exitFromTray = true;
            Close();
        }

        // Stops everything that can write rclone.conf or settings, then runs `restore`. Throws,
        // without restoring, when something will not stop. After success the schedule stays off,
        // because the app restarts.
        private async Task RestoreWithWritersStoppedAsync(Action restore, TimeSpan stopTimeout)
        {
            static void RefuseOpenTerminals()
            {
                if (RcloneConfigService.Terminals.Any(t => !t.HasExited))
                    throw new InvalidOperationException(
                        "An rclone terminal or config window is still open. Close it, then restore again.");
            }

            RefuseOpenTerminals();
            var wasScheduling = schedulerTimer is not null;
            StopScheduler();
            CancelAllRetries();
            try
            {
                var deadline = DateTime.UtcNow + stopTimeout;
                while (true)
                {
                    if (mountService is not null) await mountService.UnmountAllAsync();
                    var active = jobQueue?.Snapshot().Where(r => r.IsActive).ToList() ?? new List<JobRun>();
                    var mounted = mountService is not null && mounts.Any(mountService.IsMounted);
                    if (active.Count == 0 && !mounted) break;
                    if (DateTime.UtcNow > deadline)
                        throw new InvalidOperationException("Transfers or mounts are still running, so nothing was restored.");
                    // Queued first, so cancelling a running one cannot start the next.
                    foreach (var run in active.OrderBy(r => r.State == JobRunState.Running))
                        jobQueue!.Cancel(run.Id);
                    await Task.Delay(100);
                }
                // No await from the check above to the end of the restore: nothing can start in between.
                RefuseOpenTerminals();
                CancelAllRetries();
                restore();
            }
            catch
            {
                if (wasScheduling) StartScheduler();
                _ = KickAutoRemountsAsync("restore did not finish");
                throw;
            }
        }

        // Restore from an arbitrary .zip / .json file — used to bring settings + remotes
        // over from another machine, an external backup drive, or a manual snapshot.
        private async void RestoreFromFileDialog()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Choose a settings backup to restore",
                Filter = "RcloneMount backup (*.zip;*.json)|*.zip;*.json|All files|*.*",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = System.IO.Directory.Exists(SettingsService.BackupsFolder)
                    ? SettingsService.BackupsFolder
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            await RestoreAndRestartAsync(dlg.FileName, $"Restore settings from:\n\n{dlg.FileName}");
        }

        private async void RestoreSelectedBackup(ThemedDropDown combo)
        {
            var backups = SettingsService.ListBackups();
            if (combo.SelectedIndex < 0 || combo.SelectedIndex >= backups.Count) return;
            var chosen = backups[combo.SelectedIndex];
            var when = chosen.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

            await RestoreAndRestartAsync(chosen.Path, $"Restore settings from {when}?");
        }
    }
}
