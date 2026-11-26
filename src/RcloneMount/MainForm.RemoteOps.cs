using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;
using RcloneMount.UI;

namespace RcloneMount
{
    // Remote lifecycle: Add / Edit / Delete / OAuth, plus the RefreshRemotesAsync
    // hook that reloads rclone.conf into the rail. Also carries the startup
    // book-keeping (auto-mount, mount-remote-name repair) that runs after remotes
    // become available.
    public partial class MainForm
    {
        private async Task AutoMountOnStartupAsync()
        {
            if (mountService is null || winFspInfo is null) return;

            var attempted = 0;
            var ok = new List<string>();
            var failed = new List<string>();

            foreach (var m in mounts.Where(m => m.AutoMount).ToList())
            {
                if (!remotes.Any(r => r.Name == m.RemoteName)) continue;
                if (mountService.IsMounted(m)) continue;
                // A hit here means the configured letter is already up under someone
                // else — leave it alone (row will show "External"); don't fall back.
                if (RcloneMountService.IsDriveMountedExternally(m.DriveLetter)) continue;

                var requestedLetter = m.DriveLetter;
                if (!ResolveDriveLetterForMount(m, out var chosen, out var wasFallback))
                {
                    failed.Add($"{m.RemoteName} → {requestedLetter} (no free drive letter)");
                    continue;
                }

                attempted++;
                try
                {
                    if (wasFallback)
                    {
                        activityLog.Info($"Auto-mount: {requestedLetter} was taken — using {chosen} for {m.RemoteName}.");
                    }
                    SetStatus($"Auto-mounting {m.RemoteName} at {m.DriveLetter}...");
                    await mountService.MountAsync(m);
                    RefreshMountList();
                    var ready = await WaitForDriveReadyAsync(m.DriveLetter, TimeSpan.FromSeconds(15));
                    if (ready)
                    {
                        ShellNotifier.DriveAdded(m.DriveLetter);
                        ok.Add($"{m.RemoteName} → {m.DriveLetter}");
                    }
                    else
                    {
                        failed.Add($"{m.RemoteName} → {m.DriveLetter} (slow to start)");
                    }
                    RefreshMountList();
                }
                catch (Exception ex)
                {
                    SetStatusError($"Auto-mount of {m.RemoteName} on {m.DriveLetter} failed.", ex);
                    failed.Add($"{m.RemoteName} → {m.DriveLetter}");
                }
            }

            if (attempted == 0) return;

            if (failed.Count == 0)
            {
                Notify("Mounts ready",
                    ok.Count == 1
                        ? $"{ok[0]} is ready."
                        : $"{ok.Count} mounts ready: {string.Join(", ", ok)}",
                    ToolTipIcon.Info);
            }
            else
            {
                var lines = new List<string>();
                if (ok.Count > 0) lines.Add($"Ready: {string.Join(", ", ok)}");
                lines.Add($"Failed: {string.Join(", ", failed)}");
                Notify("Auto-mount finished with errors",
                    string.Join(Environment.NewLine, lines),
                    ToolTipIcon.Warning, 5000);
            }
        }

        private async Task RepairMountRemoteNamesAsync()
        {
            var known = remotes.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
            var repaired = 0;
            foreach (var mount in mounts)
            {
                if (known.Contains(mount.RemoteName))
                    continue;

                var candidates = remotes
                    .Where(r => r.Name.StartsWith(mount.RemoteName + " ", StringComparison.Ordinal)
                             || r.Name.Equals(mount.RemoteName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (candidates.Count == 1)
                {
                    mount.RemoteName = candidates[0].Name;
                    repaired++;
                }
            }

            // Drop any mount that still points at a remote we can't match — the
            // remote was removed from rclone.conf and left this behind. Without
            // this the mount lingers in settings.json and the status-bar count
            // reads "of N" where N > the visible mount rows.
            var orphans = OrphanedMounts(mounts, remotes);
            foreach (var m in orphans)
            {
                CancelRetry(m.Id);
                mounts.Remove(m);
                activityLog.Info($"Pruned orphaned mount {m.DriveLetter} → {m.RemoteName} (remote no longer exists).");
            }

            if (repaired > 0 || orphans.Count > 0)
            {
                await SaveSettingsAsync();
                RefreshMountList();
                var parts = new List<string>();
                if (repaired > 0) parts.Add($"repaired {repaired} mount(s) with mismatched remote names");
                if (orphans.Count > 0) parts.Add($"removed {orphans.Count} orphaned mount(s)");
                SetStatus(string.Join("; ", parts) + ".");
            }
        }

        // None when the list is empty: `config dump` on a missing config (portable exe picked by
        // Locate, RCLONE_CONFIG gone) exits 0 with {}, and that must not delete every mount.
        internal static List<Mount> OrphanedMounts(IEnumerable<Mount> mounts, IReadOnlyCollection<RcloneRemote> remotes)
        {
            if (remotes.Count == 0) return new List<Mount>();
            var known = remotes.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
            return mounts.Where(m => !known.Contains(m.RemoteName)).ToList();
        }

        // False when the remotes could not be read, so callers don't take "unknown" for "none".
        private async Task<bool> RefreshRemotesAsync()
        {
            if (configService is null)
                return false;

            try
            {
                var tried = RcloneConfigPassword.Current();
                try
                {
                    remotes = await configService.ListRemotesAsync();
                }
                catch (InvalidOperationException ex) when (RcloneConfigPassword.IsPasswordError(ex.Message))
                {
                    if (!await UnlockConfigAsync(tried))
                        return false;
                }
                // Encryption removed (or never on): the stored password must not outlive it. Only
                // a file read as plain counts; a locked or missing one keeps it for unattended jobs.
                if (RcloneConfigPassword.ReadEncrypted(SettingsService.ResolveRcloneConfigPath()) == false)
                    RcloneConfigPassword.Clear();
                RebuildRail();
                SetStatus($"Loaded {remotes.Count} remote(s).");
                return true;
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to read remotes: {ex.Message}");
                return false;
            }
            finally
            {
                UpdateActionButtons();
            }
        }

        // Set while one refresh asks for the password, so overlapping refreshes (Refresh,
        // Locate rclone, startup) never stack a second dialog.
        private bool unlockingConfig;

        // Encrypted rclone.conf with no, a wrong or a stale password: forget it and ask once.
        // The typed password is stored only after rclone decrypted the config with it.
        private async Task<bool> UnlockConfigAsync(string? tried)
        {
            // Another refresh is asking, or stored a new password after this attempt started:
            // leave that password alone.
            if (unlockingConfig || configService is not { } svc || RcloneConfigPassword.Current() != tried)
                return false;
            unlockingConfig = true;
            try
            {
                RcloneConfigPassword.Clear();
                string typed;
                using (var dlg = new ConfigPasswordDialog())
                {
                    if (dlg.ShowDialog(Visible ? this : null) != DialogResult.OK)
                    {
                        SetStatus(ConfigLockedStatus);
                        return false;
                    }
                    typed = dlg.Password;
                }
                RcloneConfigPassword.Pending = typed;
                try
                {
                    remotes = await svc.ListRemotesAsync();
                }
                catch (InvalidOperationException ex) when (RcloneConfigPassword.IsPasswordError(ex.Message))
                {
                    SetStatus(ConfigLockedStatus);
                    return false;
                }
                RcloneConfigPassword.Save(typed);
                return true;
            }
            finally
            {
                RcloneConfigPassword.Pending = null;
                unlockingConfig = false;
            }
        }

        private const string ConfigLockedStatus = "rclone.conf is encrypted and still locked. Click Refresh to enter its password.";

        private void BtnConfigureRemotes_Click(object? sender, EventArgs e)
        {
            if (configService is null)
            {
                MessageBox.Show("Locate rclone.exe first.", "rclone missing", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            configService.LaunchTerminal("config");
            SetStatus("Interactive rclone config launched. Click Refresh after saving changes.");
        }

        private void BtnRcloneTerminal_Click(object? sender, EventArgs e)
        {
            if (configService is null) return;
            configService.LaunchTerminal();
            SetStatus("rclone terminal opened. Click Refresh after changing remotes there.");
        }

        private RcloneRemote? GetSelectedRemote()
        {
            if (selectedRemoteName is null) return null;
            return remotes.FirstOrDefault(r => string.Equals(r.Name, selectedRemoteName, StringComparison.Ordinal));
        }

        // Loads rclone's provider schema, caches the human-readable name catalog for the
        // badges, and rebuilds the OAuth-type index that drives the Reconnect button. Returns
        // null on failure (and, if showErrors is true, tells the user why).
        private async Task<IReadOnlyList<RcloneProvider>?> LoadProvidersAsync(bool showErrors)
        {
            if (configService is null) return null;
            try
            {
                var providers = await configService.ListProvidersAsync();
                ProviderVisuals.SetProviderCatalog(providers);
                RebuildOAuthProviderIndex(providers);
                return providers;
            }
            catch (Exception ex)
            {
                if (showErrors)
                {
                    MessageBox.Show($"Could not load provider list: {ex.Message}",
                        "Providers", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return null;
            }
        }

        private void RebuildOAuthProviderIndex(IReadOnlyList<RcloneProvider> providers)
        {
            oauthProviderTypes.Clear();
            foreach (var p in providers)
            {
                if (string.IsNullOrEmpty(p.Name) || p.Options is null) continue;
                if (p.Options.Any(o => string.Equals(o.Name, "token", StringComparison.OrdinalIgnoreCase)))
                    oauthProviderTypes.Add(p.Name);
            }
            UpdateActionButtons();
        }

        private bool IsOAuthType(string? type) =>
            !string.IsNullOrEmpty(type) && oauthProviderTypes.Contains(type);

        private async void BtnAddRemote_Click(object? sender, EventArgs e)
        {
            if (configService is null)
            {
                MessageBox.Show("Locate rclone.exe first.", "rclone missing", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetStatus("Loading providers...");
            var providers = await LoadProvidersAsync(showErrors: false);
            if (providers is null)
            {
                MessageBox.Show("Could not load provider list. Falling back to CLI.",
                    "Providers", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                configService.LaunchTerminal("config");
                return;
            }
            SetStatus($"Loaded {providers.Count} providers.");

            using var picker = new ChooseProviderForm(providers);
            if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is null)
                return;

            var takenNames = remotes.Select(r => r.Name);
            using var editor = new EditRemoteForm(picker.Selected, existing: picker.SelectedPresetParameters, existingName: null, takenNames);
            if (editor.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                SetStatus($"Creating remote {editor.RemoteName}...");
                await configService.CreateRemoteAsync(editor.RemoteName, picker.Selected.Name, editor.ResultValues, obscurePasswords: true);
                await RefreshRemotesAsync();
                SetStatus($"Created remote {editor.RemoteName}.");
                if (editor.RequiresOAuth)
                    await RunOAuthAsync(picker.Selected.Name, editor.RemoteName, editor.ResultValues);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Create failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatusError("Create failed.", ex);
            }
        }

        private async void BtnEditRemote_Click(object? sender, EventArgs e)
        {
            if (configService is null) return;
            var selected = GetSelectedRemote();
            if (selected is null) return;

            var providers = await LoadProvidersAsync(showErrors: true);
            if (providers is null) return;

            var provider = providers.FirstOrDefault(p => string.Equals(p.Name, selected.Type, StringComparison.Ordinal));
            if (provider is null)
            {
                MessageBox.Show($"Provider type '{selected.Type}' is not known to this rclone build.",
                    "Unknown provider", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var others = remotes.Where(r => !string.Equals(r.Name, selected.Name, StringComparison.Ordinal)).Select(r => r.Name);
            // Encrypted rclone.conf can't be renamed from here (RenameRemoteAsync refuses it too),
            // so the name is locked up front and no other edit is lost to that refusal.
            using var editor = new EditRemoteForm(provider, selected.Parameters, selected.Name, others,
                lockName: RcloneConfigPassword.IsEncrypted(SettingsService.ResolveRcloneConfigPath()));
            if (editor.ShowDialog(this) != DialogResult.OK)
                return;

            var oldName = selected.Name;
            var newName = editor.RemoteName;
            var renamed = !string.Equals(oldName, newName, StringComparison.Ordinal);

            List<Mount> mountsToRemount = new();
            if (renamed)
            {
                var affected = mounts.Where(m => string.Equals(m.RemoteName, oldName, StringComparison.Ordinal)).ToList();
                var running = affected.Where(m => mountService is not null && mountService.IsMounted(m)).ToList();
                if (running.Count > 0)
                {
                    var drives = string.Join(", ", running.Select(m => m.DriveLetter));
                    var choice = MessageBox.Show(
                        $"{running.Count} mount(s) using '{oldName}' are running ({drives}).\n\n" +
                        "They must be unmounted to rename. Unmount and remount them automatically after rename?",
                        "Rename affects live mounts", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (choice == DialogResult.Cancel) return;
                    if (choice == DialogResult.No)
                    {
                        // User declined the cascade — abort rather than leaving a stale mount.
                        SetStatus("Rename cancelled.");
                        return;
                    }
                    // The remount uses the new name, so files queued under the old one never upload.
                    if (!await UploadsGuardAsync(running, "Rename", interactive: true, resumes: false))
                    {
                        SetStatus("Rename cancelled.");
                        return;
                    }
                    mountsToRemount = running;
                }
            }

            try
            {
                if (renamed)
                {
                    foreach (var m in mountsToRemount)
                    {
                        SetStatus($"Unmounting {m.DriveLetter} before rename...");
                        try { if (mountService is not null) await mountService.UnmountAsync(m); } catch { }
                    }

                    SetStatus($"Renaming {oldName} → {newName}...");
                    await configService.RenameRemoteAsync(oldName, newName);

                    var affected = mounts.Where(m => string.Equals(m.RemoteName, oldName, StringComparison.Ordinal)).ToList();
                    foreach (var m in affected)
                        m.RemoteName = newName;
                    if (affected.Count > 0)
                        await SaveSettingsAsync();
                }

                SetStatus($"Updating remote {newName}...");
                await configService.UpdateRemoteAsync(newName, editor.ResultValues, obscurePasswords: true);
                var pruneNote = editor.KeysToPrune.Count > 0
                    ? await configService.PruneRemoteAsync(newName, editor.KeysToPrune, rcloneInfo?.Version)
                    : null;
                await RefreshRemotesAsync();

                foreach (var m in mountsToRemount)
                {
                    SetStatus($"Remounting {m.DriveLetter}...");
                    try { if (mountService is not null) await mountService.MountAsync(m); }
                    catch (Exception ex) { activityLog.Error($"Remount of {m.DriveLetter} failed: {ex.Message}"); }
                }
                RefreshMountList();

                SetStatus(renamed
                    ? (mountsToRemount.Count > 0
                        ? $"Renamed to {newName}, {mountsToRemount.Count} mount(s) remounted."
                        : $"Renamed to {newName} and updated.")
                    : $"Updated remote {newName}.");
                if (pruneNote is not null)
                    activityLog.Warn(pruneNote);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatusError("Update failed.", ex);
            }
        }

        private async void BtnDeleteRemote_Click(object? sender, EventArgs e)
        {
            if (configService is null) return;
            var selected = GetSelectedRemote();
            if (selected is null) return;

            var inUse = mounts.Any(m => string.Equals(m.RemoteName, selected.Name, StringComparison.Ordinal));
            var extra = inUse ? "\n\nWarning: mounts reference this remote and will stop working." : "";

            if (ConfirmDialog.Show(this, "Delete service",
                    $"Delete remote '{selected.Name}' from rclone.conf?{extra}",
                    confirmText: "Delete") != DialogResult.Yes)
                return;
            if (!await UploadsGuardAsync(mounts.Where(m => string.Equals(m.RemoteName, selected.Name, StringComparison.Ordinal)),
                    "Delete", interactive: true, resumes: false))
                return;

            try
            {
                await configService.DeleteRemoteAsync(selected.Name);

                // Unmount + drop any mounts that pointed at this remote. Without
                // this they'd stay in settings.json as orphans and re-inflate the
                // status-bar mount count on next launch.
                var affected = mounts
                    .Where(m => string.Equals(m.RemoteName, selected.Name, StringComparison.Ordinal))
                    .ToList();
                foreach (var m in affected)
                {
                    CancelRetry(m.Id);
                    if (mountService is not null && mountService.IsMounted(m))
                    {
                        try { await mountService.UnmountAsync(m); }
                        catch (Exception ex) { activityLog.Error($"Unmount of {m.DriveLetter} before remote delete failed: {ex.Message}"); }
                    }
                    mounts.Remove(m);
                }
                if (affected.Count > 0) await SaveSettingsAsync();

                await RefreshRemotesAsync();
                SetStatus(affected.Count > 0
                    ? $"Deleted remote {selected.Name} and removed {affected.Count} mount(s)."
                    : $"Deleted remote {selected.Name}.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Delete failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Fields the OAuth flow issues, not consumes — stripped from the clone so the
        // new remote gets its own fresh token instead of inheriting the source's.
        private static readonly HashSet<string> OAuthTokenKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "token", "refresh_token", "expiry", "access_token", "token_type"
        };

        private async void BtnDuplicateRemote_Click(object? sender, EventArgs e)
        {
            if (configService is null) return;
            var selected = GetSelectedRemote();
            if (selected is null) return;

            var providers = await LoadProvidersAsync(showErrors: true);
            if (providers is null) return;

            var provider = providers.FirstOrDefault(p => string.Equals(p.Name, selected.Type, StringComparison.Ordinal));
            if (provider is null)
            {
                MessageBox.Show($"Provider type '{selected.Type}' is not known to this rclone build — can't duplicate.",
                    "Unknown provider", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Clone parameters minus any OAuth-issued fields. Users typically duplicate
            // to try a variant (different scope/region/client_id) where the source token
            // would be wrong anyway, so we make them re-auth instead of silently reusing.
            var seed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in selected.Parameters)
            {
                if (OAuthTokenKeys.Contains(kv.Key)) continue;
                seed[kv.Key] = kv.Value;
            }

            // Pre-fill the new name with a "Copy" suffix that dodges any existing name
            // — user can rename freely; EditRemoteForm still enforces uniqueness itself.
            var taken = remotes.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
            var suggested = SuggestDuplicateName(selected.Name, taken);

            using var editor = new EditRemoteForm(provider, seed, existingName: null, taken);
            editor.Text = $"Duplicate — {selected.Name}";
            editor.PresetRemoteName(suggested);

            if (editor.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                SetStatus($"Creating remote {editor.RemoteName}...");
                await configService.CreateRemoteAsync(editor.RemoteName, provider.Name, editor.ResultValues, obscurePasswords: true);
                await RefreshRemotesAsync();
                SetStatus($"Duplicated {selected.Name} → {editor.RemoteName}.");
                if (editor.RequiresOAuth)
                    await RunOAuthAsync(provider.Name, editor.RemoteName, editor.ResultValues);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Duplicate failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatusError("Duplicate failed.", ex);
            }
        }

        private static string SuggestDuplicateName(string source, HashSet<string> taken)
        {
            var basePart = source + " copy";
            if (!taken.Contains(basePart)) return basePart;
            for (var i = 2; i < 1000; i++)
            {
                var candidate = $"{source} copy {i}";
                if (!taken.Contains(candidate)) return candidate;
            }
            return source + " copy " + Guid.NewGuid().ToString("N")[..6];
        }

        private async void BtnTestRemote_Click(object? sender, EventArgs e)
        {
            if (configService is null) return;
            var selected = GetSelectedRemote();
            if (selected is null) return;

            detailTestRemoteBtn.Enabled = false;
            var origText = detailTestRemoteBtn.Text;
            detailTestRemoteBtn.Text = "…";
            SetStatus($"Testing {selected.Name}...");
            try
            {
                var result = await configService.TestConnectionAsync(selected.Name, TimeSpan.FromSeconds(20));
                ShowTestResult(selected.Name, result);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Test failed to start: {ex.Message}", "Test connection", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                detailTestRemoteBtn.Text = origText;
                detailTestRemoteBtn.Enabled = true;
            }
        }

        private void ShowTestResult(string remoteName, RcloneConfigService.TestConnectionResult r)
        {
            var elapsed = r.Elapsed.TotalSeconds >= 1
                ? $"{r.Elapsed.TotalSeconds:0.0} s"
                : $"{r.Elapsed.TotalMilliseconds:0} ms";

            if (r.Success)
            {
                var line = $"Connected in {elapsed} (via rclone {r.Method}).";
                if (r.TotalBytes is long total && total > 0)
                {
                    var free = r.FreeBytes ?? 0;
                    var used = r.UsedBytes ?? Math.Max(0, total - free);
                    line += Environment.NewLine + Environment.NewLine +
                            $"Used {FormatBytes(used)} of {FormatBytes(total)}  ({FormatBytes(free)} free).";
                }
                else if (r.FreeBytes is long freeOnly && freeOnly > 0)
                {
                    line += Environment.NewLine + Environment.NewLine + $"Free: {FormatBytes(freeOnly)}.";
                }
                MessageBox.Show(this, line, $"Test connection — {remoteName}", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetStatus($"Test OK — {remoteName} responded in {elapsed}.");
            }
            else
            {
                var msg = r.ErrorMessage ?? "Unknown error.";
                MessageBox.Show(this,
                    $"Could not reach {remoteName} (via rclone {r.Method}, {elapsed}):{Environment.NewLine}{Environment.NewLine}{msg}",
                    $"Test connection — {remoteName}", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus($"Test failed for {remoteName} — {msg}");
            }
        }

        private async void BtnReconnectRemote_Click(object? sender, EventArgs e)
        {
            if (configService is null) return;
            var selected = GetSelectedRemote();
            if (selected is null) return;
            if (!IsOAuthType(selected.Type))
            {
                MessageBox.Show($"Provider '{selected.Type}' does not use OAuth — nothing to reconnect.",
                    "Reconnect", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var running = mounts.Any(m => string.Equals(m.RemoteName, selected.Name, StringComparison.Ordinal)
                                       && mountService is not null && mountService.IsMounted(m));

            // Pass the remote's stored params so OAuth-shaping options (drive_scope,
            // onedrive region, custom client_id/secret, …) survive into the new token.
            await RunOAuthAsync(selected.Type, selected.Name, selected.Parameters, promptFirst: false, hadRunningMount: running);
        }

        // promptFirst=true is the post-create flow (a "you just added an OAuth remote —
        // sign in now?" confirmation). Explicit Reconnect clicks skip that redundant
        // question. hadRunningMount seeds the success-message nudge to unmount + remount
        // so the new token is actually picked up.
        private async Task RunOAuthAsync(string providerType, string remoteName, IReadOnlyDictionary<string, string>? authConfig, bool promptFirst = true, bool hadRunningMount = false)
        {
            if (configService is null) return;

            if (promptFirst)
            {
                var choice = MessageBox.Show(
                    $"'{remoteName}' uses OAuth. Open your browser to complete sign-in now?",
                    "OAuth required", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (choice != DialogResult.Yes)
                {
                    SetStatus($"OAuth for {remoteName} skipped — remote will not mount until authorized.");
                    return;
                }
            }

            using var dlg = new OAuthProgressForm(configService, providerType, authConfig);
            var result = dlg.ShowDialog(this);

            if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.Token))
            {
                try
                {
                    SetStatus($"Saving OAuth token for {remoteName}...");
                    var tokenValues = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = dlg.Token };
                    await configService.UpdateRemoteAsync(remoteName, tokenValues, obscurePasswords: false);
                    await RefreshRemotesAsync();
                    SetStatus($"Authorized {remoteName}.");

                    if (hadRunningMount)
                    {
                        Notify("Reconnected",
                            $"{remoteName} re-authorized. Unmount and remount any drive using it to pick up the new token.",
                            ToolTipIcon.Info, 6000);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Saving OAuth token failed: {ex.Message}", "OAuth", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SetStatus("OAuth token save failed.");
                }
            }
            else if (result == DialogResult.Abort)
            {
                MessageBox.Show($"OAuth failed: {dlg.ErrorMessage}\n\nYou can re-run authorization by clicking Reconnect again, or use the CLI fallback.",
                    "OAuth", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus($"OAuth failed for {remoteName}.");
            }
            else
            {
                SetStatus($"OAuth for {remoteName} cancelled.");
            }
        }
    }
}
