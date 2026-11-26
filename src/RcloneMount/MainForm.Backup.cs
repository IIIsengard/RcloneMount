using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
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
            await SaveSettingsAsync();
            RefreshBackupsList();
            SetStatus($"Backups folder set to {chosen}.");
        }

        private async Task ResetBackupsFolderAsync()
        {
            backupsFolderOverride = null;
            await SaveSettingsAsync();
            RefreshBackupsList();
            SetStatus("Backups folder reset to default.");
        }

        // Forces a snapshot right now. Saves current in-memory state first so the backup
        // reflects the live app rather than a stale on-disk settings.json.
        private async Task BackupNowAsync()
        {
            try
            {
                await SaveSettingsAsync();
                SettingsService.ForceBackup();
                RefreshBackupsList();
                SetStatus("Backup created.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Backup failed: {ex.Message}", "Backup", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

            var ok = MessageBox.Show(
                $"Restore settings from:\n\n{dlg.FileName}\n\n" +
                "All mounts will be unmounted and RcloneMount will restart to apply the restored settings. " +
                "Your current settings will be backed up first so this can be undone.",
                "Restore settings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (ok != DialogResult.Yes) return;
            if (!await UploadsGuardAsync(mounts, "Restore", interactive: true)) return;

            try
            {
                if (mountService is not null)
                {
                    SetStatus("Unmounting all before restore...");
                    await mountService.UnmountAllAsync();
                }
                SettingsService.RestoreFromBackup(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Restore failed: {ex.Message}", "Restore error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

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

        private async void RestoreSelectedBackup(ThemedDropDown combo)
        {
            var backups = SettingsService.ListBackups();
            if (combo.SelectedIndex < 0 || combo.SelectedIndex >= backups.Count) return;
            var chosen = backups[combo.SelectedIndex];
            var when = chosen.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

            var ok = MessageBox.Show(
                $"Restore settings from {when}?\n\n" +
                "All mounts will be unmounted and RcloneMount will restart to apply the restored settings. " +
                "Your current settings will be backed up first so this can be undone.",
                "Restore settings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (ok != DialogResult.Yes) return;
            if (!await UploadsGuardAsync(mounts, "Restore", interactive: true)) return;

            try
            {
                if (mountService is not null)
                {
                    SetStatus("Unmounting all before restore...");
                    await mountService.UnmountAllAsync();
                }
                SettingsService.RestoreFromBackup(chosen.Path);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Restore failed: {ex.Message}", "Restore error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

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
    }
}
