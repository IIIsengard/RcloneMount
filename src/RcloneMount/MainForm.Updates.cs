using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using RcloneMount.Services;
using RcloneMount.UI;
using Velopack;
using Velopack.Sources;

namespace RcloneMount
{
    // First-run setup, update-check, install, and auto-update flows. Split from
    // MainForm.cs because these all follow the same shape (download → possibly
    // unmount → run installer → re-detect) and grouping them makes the rclone/WinFsp
    // parallelism obvious side-by-side.
    public partial class MainForm
    {
        private async Task<bool> OfferFirstRunSetupAsync()
        {
            if (firstRunSetupDone) return false;
            var rcloneMissing = rcloneInfo is null;
            var winFspMissing = winFspInfo is null;
            if (!rcloneMissing && !winFspMissing)
            {
                firstRunSetupDone = true;
                await SaveSettingsAsync();
                return false;
            }

            var missingList = new List<string>();
            if (rcloneMissing) missingList.Add("rclone");
            if (winFspMissing) missingList.Add("WinFsp");
            var missing = string.Join(" and ", missingList);

            var choice = MessageBox.Show(
                $"{missing} not detected on this system.\n\n" +
                (rcloneMissing ? $"• rclone will be installed to {PrerequisiteInstaller.DefaultRcloneExePath}\n" : "") +
                (winFspMissing ? "• WinFsp will be downloaded and its MSI installer launched (UAC prompt).\n" : "") +
                "\nInstall now?",
                "First-run setup",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            firstRunSetupDone = true;
            await SaveSettingsAsync();

            if (choice != DialogResult.Yes)
                return false;

            var progress = new Progress<string>(SetStatus);
            try
            {
                if (rcloneMissing)
                {
                    var release = await UpdateChecker.LatestRcloneAsync()
                        ?? throw new InvalidOperationException("Could not fetch the latest rclone release from GitHub.");
                    await PrerequisiteInstaller.InstallRcloneAsync(PrerequisiteInstaller.DefaultRcloneExePath, release, progress);
                    savedRclonePath = PrerequisiteInstaller.DefaultRcloneExePath;
                    await SaveSettingsAsync();
                    await DetectRcloneAsync();
                }
                if (winFspMissing)
                {
                    var latest = await UpdateChecker.LatestWinFspAsync();
                    var asset = latest?.Assets.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
                    if (asset is not null)
                    {
                        await PrerequisiteInstaller.LaunchWinFspInstallerAsync(asset.Url, progress);
                        MessageBox.Show(
                            "The WinFsp installer has been launched. Finish it, then click \"Check for update\" on the Prerequisites tab.",
                            "Finish WinFsp install", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Setup failed: {ex.Message}", "Setup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return true;
        }

        private async Task AutoApplyUpdatesAsync()
        {
            var rcloneNeedsUpdate = rcloneInfo is not null && latestRclone is not null
                && UpdateChecker.Compare(rcloneInfo.Version, latestRclone.Version) < 0;

            var winFspNeedsUpdate = winFspInfo is not null && latestWinFsp is not null
                && UpdateChecker.Compare(winFspInfo.Version ?? "", latestWinFsp.Version) < 0;

            if (rcloneNeedsUpdate && TransferRunning())
            {
                SetStatus("Auto-update: rclone update skipped while a transfer is running.");
                rcloneNeedsUpdate = false;
            }

            if (!rcloneNeedsUpdate && !winFspNeedsUpdate) return;

            // Both updates unmount everything; an unattended update must not cut off uploads.
            if (!await UploadsGuardAsync(mounts, "Auto-update", interactive: false))
            {
                SetStatus("Auto-update skipped: mount uploads are still pending.");
                activityLog.Info("Auto-update skipped: mount uploads are still pending.");
                return;
            }

            var progress = new Progress<string>(SetStatus);
            try
            {
                if (mountService is not null)
                {
                    SetStatus("Auto-update: unmounting all before applying updates...");
                    await mountService.UnmountAllAsync();
                }

                if (rcloneNeedsUpdate && rcloneInfo is not null && latestRclone is not null)
                {
                    SetStatus("Auto-update: installing rclone...");
                    if (mountService is not null) { mountService.Dispose(); mountService = null; }
                    await PrerequisiteInstaller.InstallRcloneAsync(rcloneInfo.ExecutablePath, latestRclone, progress);
                    await DetectRcloneAsync();
                    await CheckRcloneUpdateAsync(showMessages: false);
                }

                if (winFspNeedsUpdate && latestWinFsp is not null)
                {
                    var asset = latestWinFsp.Assets.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
                    if (asset is not null)
                    {
                        SetStatus("Auto-update: launching WinFsp installer (UAC prompt)...");
                        await PrerequisiteInstaller.LaunchWinFspInstallerAsync(asset.Url, progress);
                    }
                }

                SetStatus("Auto-update complete.");
            }
            catch (Exception ex)
            {
                SetStatus($"Auto-update failed: {ex.Message}");
                activityLog.Error($"Auto-update failed: {ex.Message}");
            }
            finally
            {
                // A failed install leaves mountService disposed; re-detect so mounting works without a restart.
                if (mountService is null) await DetectRcloneAsync();
            }
        }

        // Replacing rclone.exe under a running transfer fails (file in use) and would kill the run.
        private bool TransferRunning() =>
            jobQueue?.Snapshot().Any(r => r.State == Models.JobRunState.Running) == true;

        private async Task CheckRcloneUpdateAsync(bool showMessages)
        {
            if (showMessages) lblRcloneLatest.Text = "Latest: checking...";
            latestRclone = await UpdateChecker.LatestRcloneAsync();
            if (latestRclone is null)
            {
                lblRcloneLatest.Text = "Latest: (check failed — no network?)";
                btnInstallRclone.Enabled = false;
                if (showMessages) SetStatus("Failed to check rclone updates.");
                return;
            }

            var installed = rcloneInfo?.Version;
            var installedNorm = UpdateChecker.NormalizeVersion(installed ?? "");
            var cmp = string.IsNullOrEmpty(installedNorm)
                ? (int?)null
                : UpdateChecker.Compare(installedNorm, latestRclone.Version);

            if (cmp is null)
            {
                lblRcloneLatest.Text = $"Latest: {latestRclone.TagName}";
                btnInstallRclone.Enabled = rcloneInfo is not null;
            }
            else if (cmp < 0)
            {
                lblRcloneLatest.Text = $"Latest: {latestRclone.TagName} — update available";
                lblRcloneLatest.ForeColor = Theme.Warn;
                btnInstallRclone.Enabled = rcloneInfo is not null;
            }
            else
            {
                lblRcloneLatest.Text = $"Latest: {latestRclone.TagName} — up to date";
                lblRcloneLatest.ForeColor = Theme.TextPrimary;
                btnInstallRclone.Enabled = false;
            }
        }

        // Startup path (showMessages=false): silently update the Settings-tab label and,
        // if a newer version is out, fire a single balloon nudging the user to open the
        // Settings tab. Button-triggered path (showMessages=true) also puts a "checking…"
        // label up while the request is in flight.
        private async Task CheckAppUpdateAsync(bool showMessages)
        {
            if (lblAppLatest is null) return;

            // Unconfigured releases repo → don't spam the network or the user with a
            // 404. The Settings-tab label already explains the state on build.
            if (!UpdateChecker.AppRepoConfigured)
            {
                lblAppLatest.Text = "Latest: (releases channel not configured yet)";
                lblAppLatest.ForeColor = Theme.TextTertiary;
                if (btnInstallAppUpdate is not null) btnInstallAppUpdate.Enabled = false;
                return;
            }

            var mgr = new UpdateManager(new GithubSource(UpdateChecker.AppRepoUrl, null, false));
            if (!mgr.IsInstalled)
            {
                lblAppLatest.Text = "Updates are delivered to the installed version (Setup.exe)";
                lblAppLatest.ForeColor = Theme.TextTertiary;
                if (btnInstallAppUpdate is not null) btnInstallAppUpdate.Enabled = false;
                return;
            }

            if (showMessages) lblAppLatest.Text = "Latest: checking...";
            UpdateInfo? update;
            try { update = await mgr.CheckForUpdatesAsync(); }
            catch
            {
                lblAppLatest.Text = "Latest: (check failed — no network?)";
                lblAppLatest.ForeColor = Theme.TextTertiary;
                if (btnInstallAppUpdate is not null) btnInstallAppUpdate.Enabled = false;
                if (showMessages) SetStatus("Failed to check RcloneMount updates.");
                return;
            }

            appUpdateManager = mgr;
            pendingAppUpdate = update;
            if (update is not null)
            {
                var tag = "v" + update.TargetFullRelease.Version;
                lblAppLatest.Text = $"Latest: {tag} — update available";
                lblAppLatest.ForeColor = Theme.Warn;
                if (btnInstallAppUpdate is not null) btnInstallAppUpdate.Enabled = true;
                // On startup only: a one-shot balloon so a user who opens the app after
                // a release cut sees it without having to visit Settings. Fire even when
                // the app came up as start-hidden — that's exactly when the balloon
                // matters most.
                if (!showMessages)
                    Notify("RcloneMount update available",
                        $"Version {tag} is out. Open Settings → RcloneMount to install.",
                        ToolTipIcon.Info, 5000);
            }
            else
            {
                lblAppLatest.Text = $"Latest: v{AppVersion} — up to date";
                lblAppLatest.ForeColor = Theme.TextPrimary;
                if (btnInstallAppUpdate is not null) btnInstallAppUpdate.Enabled = false;
            }
        }

        private UpdateManager? appUpdateManager;
        private UpdateInfo? pendingAppUpdate;

        // Download, unmount cleanly, then let Velopack swap the files and relaunch us.
        private async Task InstallAppUpdateAsync()
        {
            if (appUpdateManager is null || pendingAppUpdate is null) return;
            var choice = MessageBox.Show(
                $"Download RcloneMount v{pendingAppUpdate.TargetFullRelease.Version} and restart?\n\n" +
                "Any active mounts will be unmounted first.",
                "Install RcloneMount update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes) return;

            try
            {
                btnInstallAppUpdate.Enabled = false;
                SetStatus("Downloading RcloneMount update...");
                await appUpdateManager.DownloadUpdatesAsync(pendingAppUpdate);
                // Ask once, after the download: unmounting here keeps the close path from asking again.
                if (!await UploadsGuardAsync(mounts, "Update", interactive: true))
                {
                    SetStatus("RcloneMount update postponed: mount uploads are still pending.");
                    btnInstallAppUpdate.Enabled = true;
                    return;
                }
                // Velopack applies after we exit; the normal close path saves totals.
                SetStatus("Closing to install update...");
                appUpdateManager.WaitExitThenApplyUpdates(pendingAppUpdate.TargetFullRelease, silent: false, restart: true);
                if (mountService is not null) await mountService.UnmountAllAsync();
                exitFromTray = true;
                Close();
            }
            catch (Exception ex)
            {
                exitFromTray = false;
                MessageBox.Show($"RcloneMount update failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("RcloneMount update failed.");
                btnInstallAppUpdate.Enabled = true;
            }
        }

        private async Task CheckWinFspUpdateAsync(bool showMessages)
        {
            if (showMessages) lblWinFspLatest.Text = "Latest: checking...";
            latestWinFsp = await UpdateChecker.LatestWinFspAsync();
            if (latestWinFsp is null)
            {
                lblWinFspLatest.Text = "Latest: (check failed — no network?)";
                btnInstallWinFsp.Enabled = false;
                if (showMessages) SetStatus("Failed to check WinFsp updates.");
                return;
            }

            var installedNorm = UpdateChecker.NormalizeVersion(winFspInfo?.Version ?? "");
            var cmp = string.IsNullOrEmpty(installedNorm)
                ? (int?)null
                : UpdateChecker.Compare(installedNorm, latestWinFsp.Version);

            var hasMsi = latestWinFsp.Assets.Any(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));

            if (winFspInfo is null)
            {
                lblWinFspLatest.Text = $"Latest: {latestWinFsp.TagName} — not installed";
                lblWinFspLatest.ForeColor = Theme.Critical;
                btnInstallWinFsp.Enabled = hasMsi;
            }
            else if (cmp is null)
            {
                lblWinFspLatest.Text = $"Latest: {latestWinFsp.TagName}";
                btnInstallWinFsp.Enabled = hasMsi;
            }
            else if (cmp < 0)
            {
                lblWinFspLatest.Text = $"Latest: {latestWinFsp.TagName} — update available";
                lblWinFspLatest.ForeColor = Theme.Warn;
                btnInstallWinFsp.Enabled = hasMsi;
            }
            else
            {
                lblWinFspLatest.Text = $"Latest: {latestWinFsp.TagName} — up to date";
                lblWinFspLatest.ForeColor = Theme.TextPrimary;
                btnInstallWinFsp.Enabled = false;
            }
        }

        private async Task InstallRcloneUpdateAsync()
        {
            if (rcloneInfo is null || latestRclone is null) return;
            if (TransferRunning())
            {
                MessageBox.Show("A transfer is running. Let it finish (or cancel it), then install the rclone update.",
                    "Install rclone update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var choice = MessageBox.Show(
                $"Download the latest rclone Windows zip and replace {rcloneInfo.ExecutablePath}?\n\n" +
                "Any active mounts will be unmounted first.",
                "Install rclone update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes) return;
            if (!await UploadsGuardAsync(mounts, "Update", interactive: true)) return;

            try
            {
                btnInstallRclone.Enabled = false;
                if (mountService is not null)
                {
                    SetStatus("Unmounting all before update...");
                    await mountService.UnmountAllAsync();
                    mountService.Dispose();
                    mountService = null;
                }

                var progress = new Progress<string>(SetStatus);
                await PrerequisiteInstaller.InstallRcloneAsync(rcloneInfo.ExecutablePath, latestRclone, progress);

                await DetectRcloneAsync();
                await CheckRcloneUpdateAsync(showMessages: false);
                SetStatus("rclone updated.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"rclone install failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("rclone install failed.");
                btnInstallRclone.Enabled = true;
            }
            finally
            {
                // A failed install leaves mountService disposed; re-detect so mounting works without a restart.
                if (mountService is null) await DetectRcloneAsync();
            }
        }

        private async Task InstallWinFspUpdateAsync()
        {
            if (latestWinFsp is null) return;
            var asset = latestWinFsp.Assets.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
            if (asset is null) return;

            var choice = MessageBox.Show(
                $"Download {asset.Name} ({asset.Size / 1024 / 1024} MB) and launch the installer?\n\n" +
                "Windows will show a UAC prompt.",
                "Install WinFsp", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes) return;

            try
            {
                btnInstallWinFsp.Enabled = false;
                var progress = new Progress<string>(SetStatus);
                await PrerequisiteInstaller.LaunchWinFspInstallerAsync(asset.Url, progress);
                SetStatus("WinFsp installer launched. Re-detect after installation completes.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"WinFsp install failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("WinFsp install failed.");
                btnInstallWinFsp.Enabled = true;
            }
        }
    }
}
