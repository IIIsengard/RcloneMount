using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;
using RcloneMount.UI;

namespace RcloneMount
{
    // Mount lifecycle: mount / unmount / edit / remove, plus drive-ready polling,
    // status-enum computation, and the auto-remount retry loop with exponential
    // backoff. Everything that operates on a single Mount lives here.
    public partial class MainForm
    {
        private async void BtnAddMount_Click(object? sender, EventArgs e)
        {
            if (remotes.Count == 0)
            {
                MessageBox.Show("Configure at least one remote first.", "No remotes", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var dlg = new AddMountForm(remotes, existing: null, allMounts: mounts);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result is not null)
            {
                mounts.Add(dlg.Result);
                RefreshMountList();
                await SaveSettingsAsync();
                SetStatus($"Added mount {dlg.Result.RemoteName} → {dlg.Result.DriveLetter}.");
            }
        }

        private async Task EditMountAsync(Mount selected)
        {
            using var dlg = new AddMountForm(remotes, selected, allMounts: mounts);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result is not null)
            {
                selected.RemoteName = dlg.Result.RemoteName;
                selected.RemotePath = dlg.Result.RemotePath;
                selected.DriveLetter = dlg.Result.DriveLetter;
                selected.VfsCacheMode = dlg.Result.VfsCacheMode;
                selected.ExtraFlags = dlg.Result.ExtraFlags;
                selected.CacheDir = dlg.Result.CacheDir;
                selected.VfsCacheMaxSize = dlg.Result.VfsCacheMaxSize;
                selected.VfsCacheMinFreeSpace = dlg.Result.VfsCacheMinFreeSpace;
                selected.AutoMount = dlg.Result.AutoMount;

                if (!selected.AutoMount) CancelRetry(selected.Id);

                RefreshMountList();
                await SaveSettingsAsync();
                SetStatus("Mount updated.");
            }
        }

        private async Task RemoveMountAsync(Mount selected)
        {
            if (ConfirmDialog.Show(this, "Remove mount",
                    $"Remove mount for {selected.RemoteName} at {selected.DriveLetter}?") != DialogResult.Yes)
                return;
            if (!await UploadsGuardAsync(new[] { selected }, "Remove", interactive: true, resumes: false)) return;

            CancelRetry(selected.Id);

            if (mountService is not null && mountService.IsMounted(selected))
                await mountService.UnmountAsync(selected);

            mounts.Remove(selected);
            RefreshMountList();
            await SaveSettingsAsync();
            SetStatus("Mount removed.");
        }

        private async Task MountAsync(Mount selected)
        {
            if (mountService is null) return;

            CancelRetry(selected.Id);

            if (!remotes.Any(r => r.Name == selected.RemoteName))
            {
                MessageBox.Show(
                    $"Remote '{selected.RemoteName}' is not in rclone.conf. Edit this mount and select a valid remote.",
                    "Unknown remote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (winFspInfo is null)
            {
                var choice = MessageBox.Show(
                    "WinFsp is not installed. rclone mount cannot work without it.\n\nOpen the WinFsp download page now?",
                    "WinFsp required", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (choice == DialogResult.Yes)
                    OpenUrl("https://winfsp.dev/rel/");
                return;
            }

            try
            {
                var requested = selected.DriveLetter;
                if (!ResolveDriveLetterForMount(selected, out var chosen, out var wasFallback))
                {
                    MessageBox.Show(
                        $"No free drive letters are available for {selected.RemoteName}. Free up a letter and try again.",
                        "No drive letter available", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    SetStatus("Mount aborted — no free drive letter.");
                    return;
                }
                if (wasFallback)
                {
                    activityLog.Info($"Drive {requested} was taken — falling back to {chosen} for {selected.RemoteName}.");
                    SetStatus($"{requested} taken; using {chosen} for {selected.RemoteName}.");
                }

                SetStatus($"Mounting {selected.DriveLetter}...");
                await mountService.MountAsync(selected);
                RefreshMountList();

                var ready = await WaitForDriveReadyAsync(selected.DriveLetter, TimeSpan.FromSeconds(15));
                RefreshMountList();
                if (ready)
                {
                    ShellNotifier.DriveAdded(selected.DriveLetter);
                    if (wasFallback)
                    {
                        SetStatus($"Mounted {selected.RemoteName} at {selected.DriveLetter} (fallback from {requested}).");
                        Notify("Drive letter changed",
                            $"{requested} was in use — {selected.RemoteName} is now on {selected.DriveLetter}.",
                            ToolTipIcon.Warning, 4000);
                    }
                    else
                    {
                        SetStatus($"Mounted {selected.RemoteName} at {selected.DriveLetter}.");
                    }
                    NotifyMountUp(selected);
                }
                else
                {
                    SetStatus($"rclone launched, but {selected.DriveLetter} did not become ready within 15s. Check log for errors.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Mount failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatusError("Mount failed.", ex);
            }
        }

        private static async Task<bool> WaitForDriveReadyAsync(string driveLetter, TimeSpan timeout)
        {
            var root = driveLetter.TrimEnd(':') + ":\\";
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (System.IO.Directory.Exists(root))
                        return true;
                }
                catch { }
                await Task.Delay(250);
            }
            return false;
        }

        // Every unmount path calls this first so a mount upload is never cut off without a
        // word. Interactive: Wait / <action> anyway / Cancel, also when rclone does not answer.
        // Automatic: refuse while uploads are pending; an RC that does not answer does not block.
        // resumes: false for Remove / Rename / Delete, whose queued files never upload afterwards.
        private async Task<bool> UploadsGuardAsync(IEnumerable<Mount> targets, string action, bool interactive, bool resumes = true)
        {
            var list = targets.ToList();
            var pending = await CountPendingUploadsAsync(list);
            var step = UploadsGuard.Decide(pending, interactive);
            if (step != UploadsGuardStep.Ask) return step == UploadsGuardStep.Proceed;

            using var dlg = new PendingUploadsDialog(action, pending, resumes, () => CountPendingUploadsAsync(list));
            // Hidden in the tray or minimised: an owned dialog would hide with its owner (Windows hides
            // a minimised owner's windows), so show it unowned, centred and on top.
            var owner = Visible && WindowState != FormWindowState.Minimized ? this : null;
            if (owner is null) { dlg.StartPosition = FormStartPosition.CenterScreen; dlg.TopMost = true; }
            DialogResult result;
            uploadsDialog = dlg;
            try { result = dlg.ShowDialog(owner); }
            finally { uploadsDialog = null; }
            if (result == DialogResult.Yes)
                activityLog.Warn($"{action} went ahead with {pending?.ToString() ?? "an unknown number of"} mount upload(s) pending; " +
                    (resumes ? "rclone resumes them on the next mount." : "they stay only in rclone's local cache and will not be uploaded."));
            return result is DialogResult.OK or DialogResult.Yes;
        }

        // null when a running target's rclone did not answer: unknown, not 0.
        private async Task<int?> CountPendingUploadsAsync(IReadOnlyList<Mount> targets)
        {
            if (mountService is null) return 0;
            var ports = targets.Select(m => mountService.GetRcPort(m)).OfType<int>().ToList();
            return UploadsGuard.Sum(await Task.WhenAll(ports.Select(p => RcloneStatsPoller.FetchUploadsPendingAsync(p))));
        }

        private async Task UnmountAsync(Mount selected)
        {
            if (mountService is null) return;
            if (!await UploadsGuardAsync(new[] { selected }, "Unmount", interactive: true)) return;

            CancelRetry(selected.Id);

            try
            {
                SetStatus($"Unmounting {selected.DriveLetter}...");
                await mountService.UnmountAsync(selected);
                ShellNotifier.DriveRemoved(selected.DriveLetter);
                // If this mount was on a session-fallback letter, revert to the user's
                // configured letter now the drive is gone so the row shows their intent.
                RestoreConfiguredDriveLetter(selected);

                if (RcloneMountService.IsDriveMountedExternally(selected.DriveLetter))
                {
                    var choice = MessageBox.Show(
                        $"{selected.DriveLetter} is still mounted by another rclone process (not started by this app).\n\nKill it now?",
                        "External mount", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (choice == DialogResult.Yes)
                    {
                        var killed = await RcloneMountService.KillExternalRcloneMountAsync(selected.DriveLetter);
                        SetStatus(killed > 0
                            ? $"Killed {killed} external rclone process(es) on {selected.DriveLetter}."
                            : $"No matching external rclone process found for {selected.DriveLetter}.");
                    }
                }
                else
                {
                    SetStatus($"Unmounted {selected.DriveLetter}.");
                    NotifyMountDown(selected);
                }
                RefreshMountList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unmount failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenDrive(Mount selected)
        {
            if (string.IsNullOrEmpty(selected.DriveLetter)) return;

            var root = selected.DriveLetter.TrimEnd(':') + ":\\";
            if (!System.IO.Directory.Exists(root))
            {
                MessageBox.Show($"Drive {selected.DriveLetter} is not accessible yet. It may still be initializing.",
                    "Drive not ready", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open drive: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Builds the set of drive letters that are currently OFF-LIMITS for a new mount:
        // every letter Windows has already assigned (physical, network, SUBST, another
        // rclone…) plus every letter reserved by another configured mount (excluding
        // the one we're resolving for, so its own letter can still be picked). We look
        // at BOTH the currently-mutated DriveLetter and the shadow "configured" letter
        // so an in-flight fallback doesn't accidentally free up the original letter for
        // someone else to grab.
        private HashSet<char> BuildUsedDriveLetters(string? excludeMountId)
        {
            var used = new HashSet<char>();
            foreach (var d in System.IO.DriveInfo.GetDrives())
            {
                if (!string.IsNullOrEmpty(d.Name))
                    used.Add(char.ToUpperInvariant(d.Name[0]));
            }
            foreach (var other in mounts)
            {
                if (excludeMountId is not null && other.Id == excludeMountId) continue;
                if (!string.IsNullOrEmpty(other.DriveLetter))
                    used.Add(char.ToUpperInvariant(other.DriveLetter[0]));
                if (configuredDriveLetters.TryGetValue(other.Id, out var orig) && !string.IsNullOrEmpty(orig))
                    used.Add(char.ToUpperInvariant(orig[0]));
            }
            return used;
        }

        // Returns "X:" for a letter that's actually free right now, preferring the mount's
        // configured letter and scanning D→Z when it's taken. Returns null if every letter
        // in the searchable range is in use.
        private string? PickAvailableDriveLetter(Mount m)
        {
            var used = BuildUsedDriveLetters(excludeMountId: m.Id);
            if (!string.IsNullOrEmpty(m.DriveLetter))
            {
                var preferred = char.ToUpperInvariant(m.DriveLetter[0]);
                if (preferred >= 'A' && preferred <= 'Z' && !used.Contains(preferred))
                    return preferred + ":";
            }
            for (var c = 'D'; c <= 'Z'; c++)
                if (!used.Contains(c)) return c + ":";
            return null;
        }

        // Call BEFORE handing a mount to mountService.MountAsync. If the configured
        // letter is taken (by Windows or another rclone), quietly mutates mount.DriveLetter
        // to the next free letter and stashes the original in configuredDriveLetters so
        // it can be restored later. `fallbackLetter` receives the chosen letter (which
        // may equal the original when no fallback was needed) so callers can log/notify.
        // Returns false when we can't find any free letter — the caller should abort
        // the mount attempt with a clear error rather than passing "" to rclone.
        private bool ResolveDriveLetterForMount(Mount m, out string chosenLetter, out bool changed)
        {
            var picked = PickAvailableDriveLetter(m);
            if (picked is null)
            {
                chosenLetter = m.DriveLetter;
                changed = false;
                return false;
            }
            changed = !string.Equals(picked, m.DriveLetter, StringComparison.OrdinalIgnoreCase);
            if (changed)
            {
                // Preserve the FIRST original we ever saw — subsequent re-fallbacks in
                // the same session should still map back to the user's actual choice.
                if (!configuredDriveLetters.ContainsKey(m.Id))
                    configuredDriveLetters[m.Id] = m.DriveLetter;
                m.DriveLetter = picked;
            }
            chosenLetter = picked;
            return true;
        }

        // Called once a mount is truly gone (process exited or user unmounted) so the
        // row rebinds to the user's configured letter rather than the session fallback.
        private void RestoreConfiguredDriveLetter(Mount m)
        {
            if (configuredDriveLetters.Remove(m.Id, out var original) && !string.IsNullOrEmpty(original))
                m.DriveLetter = original;
        }

        private MountRowStatus GetMountStatusEnum(Mount m)
        {
            if (mountService is not null && mountService.IsMounted(m))
            {
                // Process still running, but the watchdog flagged the drive letter as
                // unreachable — surface as "Detached" so the user sees something is off
                // even though the child hasn't exited yet.
                return detachedMountIds.Contains(m.Id) ? MountRowStatus.Detached : MountRowStatus.Mounted;
            }
            if (RcloneMountService.IsDriveMountedExternally(m.DriveLetter)) return MountRowStatus.External;
            return MountRowStatus.Stopped;
        }

        private void ScheduleRetry(Mount mount)
        {
            CancelRetry(mount.Id);
            var cts = new CancellationTokenSource();
            retryCts[mount.Id] = cts;
            _ = RetryLoopAsync(mount, cts.Token);
        }

        private void CancelRetry(string mountId)
        {
            if (retryCts.Remove(mountId, out var cts))
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }
        }

        private void CancelAllRetries()
        {
            foreach (var kv in retryCts)
            {
                try { kv.Value.Cancel(); kv.Value.Dispose(); } catch { }
            }
            retryCts.Clear();
        }

        private async Task RetryLoopAsync(Mount mount, CancellationToken ct)
        {
            var attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                var delayMs = RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)];
                try { await Task.Delay(delayMs, ct); }
                catch (OperationCanceledException) { return; }

                if (ct.IsCancellationRequested) return;
                if (mountService is null || winFspInfo is null) continue;
                if (!remotes.Any(r => r.Name == mount.RemoteName)) continue;
                if (mountService.IsMounted(mount) || RcloneMountService.IsDriveMountedExternally(mount.DriveLetter))
                {
                    return;
                }

                attempt++;
                try
                {
                    // Resolve on every attempt: what was taken last try may be free now,
                    // and vice-versa. Bail this attempt (not the loop) if nothing's free.
                    if (!ResolveDriveLetterForMount(mount, out _, out _)) continue;

                    if (IsHandleCreated) BeginInvoke(new Action(() =>
                        SetStatus($"Auto-remount {mount.RemoteName} → {mount.DriveLetter} (attempt {attempt})...")));

                    await mountService.MountAsync(mount);
                    if (await WaitForDriveReadyAsync(mount.DriveLetter, TimeSpan.FromSeconds(15)))
                    {
                        ShellNotifier.DriveAdded(mount.DriveLetter);
                        if (IsHandleCreated) BeginInvoke(new Action(() =>
                        {
                            RefreshMountList();
                            Notify("Mount recovered",
                                $"{mount.RemoteName} → {mount.DriveLetter} is back online.",
                                ToolTipIcon.Info);
                        }));
                        retryCts.Remove(mount.Id);
                        return;
                    }
                }
                catch
                {
                    // fall through to next backoff attempt
                }
            }
        }

        // Shared balloon copy for a successful mount / unmount — used by both the window
        // buttons and the tray menu so tray-triggered operations get the same feedback
        // as clicks in the app. Muted by chkNotifications (via Notify).
        private void NotifyMountUp(Mount m) =>
            Notify("Drive mounted", $"{m.RemoteName} is available at {m.DriveLetter}.", ToolTipIcon.Info);

        private void NotifyMountDown(Mount m) =>
            Notify("Drive unmounted", $"{m.DriveLetter} ({m.RemoteName}) is no longer mounted.", ToolTipIcon.Info);

        // Slice 1 of the transfer engine — opens a modeless dialog with source
        // pre-filled from the mount. No persistence, no scheduling; the user picks
        // an op, dest remote/path, and hits Start. See [[project-transfer-engine]].
        private void OpenTransferDialog(Mount source, TransferOperation preselectedOp)
        {
            if (jobService is null || configService is null)
            {
                MessageBox.Show(this, "rclone isn't set up yet.", "Transfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (remotes.Count == 0)
            {
                MessageBox.Show(this, "Configure at least one remote first.", "Transfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            NewTransferDialog(source.RemoteName, source.RemotePath ?? "", preselectedOp).Show(this);
        }

        private void AddMountForSelectedRemote()
        {
            var remote = GetSelectedRemote();
            if (remote is null) { BtnAddMount_Click(this, EventArgs.Empty); return; }
            using var dlg = new AddMountForm(remotes);
            dlg.PresetRemoteName(remote.Name);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result is not null)
            {
                mounts.Add(dlg.Result);
                _ = SaveSettingsAsync();
                RefreshMountList();
                SetStatus($"Added mount {dlg.Result.RemoteName} → {dlg.Result.DriveLetter}.");
            }
        }
    }
}
