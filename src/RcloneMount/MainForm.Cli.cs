using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount
{
    // CLI-relay server side. The tray hosts a named-pipe server (started once from
    // MainForm_Load); external `RcloneMount.exe --mount X` invocations connect,
    // send a JSON request, and get a JSON reply. Actions run on the UI thread against
    // mountService and skip any UI prompts — a scripted caller can't answer a MessageBox.
    public partial class MainForm
    {
        private CliRelay.Server? cliServer;

        private void StartCliServer()
        {
            if (cliServer is not null) return;
            cliServer = new CliRelay.Server(HandleCliRequestAsync);
            cliServer.Start();
        }

        private void StopCliServer()
        {
            cliServer?.Dispose();
            cliServer = null;
        }

        private async Task<CliRelay.Response> HandleCliRequestAsync(CliRelay.Request req, CancellationToken ct)
        {
            // We arrive on the pipe thread, but mounts, remotes and drive letters belong to
            // the UI thread: run the whole command there. Show only posts to it, so a second
            // launch is not kept waiting on a busy UI.
            if (req.Command != CliRelay.Command.Show && InvokeRequired)
                return await Invoke(() => HandleCliRequestAsync(req, ct));

            switch (req.Command)
            {
                case CliRelay.Command.List:
                    return BuildListReply();
                case CliRelay.Command.Mount:
                    return await CliMountAsync(req.Name ?? "", ct);
                case CliRelay.Command.Unmount:
                    return await CliUnmountAsync(req.Name ?? "");
                case CliRelay.Command.Show:
                    // A second launch hands over to us instead of starting a second tray.
                    try { BeginInvoke(new Action(ShowFromTray)); } catch { }
                    return new CliRelay.Response(0, "");
                default:
                    return new CliRelay.Response(5, "Unknown command.");
            }
        }

        private CliRelay.Response BuildListReply()
        {
            var entries = new List<CliRelay.MountEntry>(mounts.Count);
            foreach (var m in mounts)
            {
                var mounted = mountService?.IsMounted(m) ?? false;
                entries.Add(new CliRelay.MountEntry(m.RemoteName ?? "", m.RemotePath ?? "", m.DriveLetter ?? "", mounted));
            }
            return new CliRelay.Response(0, "", entries);
        }

        private async Task<CliRelay.Response> CliMountAsync(string name, CancellationToken ct)
        {
            var found = FindMountByName(name);
            if (found is null)
                return new CliRelay.Response(2, $"No mount named '{name}'. Use --list to see configured mounts.");

            if (mountService is null)
                return new CliRelay.Response(5, "Mount service not initialised — rclone not detected yet.");

            if (mountService.IsMounted(found))
                return new CliRelay.Response(0, $"'{found.RemoteName}' is already mounted at {found.DriveLetter}.");

            if (!remotes.Any(r => r.Name == found.RemoteName))
                return new CliRelay.Response(2, $"Remote '{found.RemoteName}' is not in rclone.conf.");

            if (winFspInfo is null)
                return new CliRelay.Response(5, "WinFsp is not installed — rclone mount cannot start.");

            CancelRetry(found.Id);

            try
            {
                if (!await StartMountAsync(found))
                    return new CliRelay.Response(5, $"Mount failed: no free drive letter for '{found.RemoteName}'.");
            }
            catch (Exception ex)
            {
                return new CliRelay.Response(5, "Mount failed: " + ex.Message);
            }

            // Wait for the drive letter to actually appear so a script that immediately
            // reads from it doesn't race the mount. Same 15s ceiling as the UI path.
            var live = found.DriveLetter;
            var ready = await WaitForDriveReadyAsync(live, TimeSpan.FromSeconds(15));

            // Refresh the row visuals — mount succeeded either way, but the timer-driven
            // refresh could be up to 1s behind and users notice.
            try { RefreshMountList(); }
            catch { }

            if (!ready)
                return new CliRelay.Response(3, $"rclone launched, but {live} did not become ready within 15s.");

            return new CliRelay.Response(0, $"Mounted '{found.RemoteName}' at {live}.");
        }

        private async Task<CliRelay.Response> CliUnmountAsync(string name)
        {
            var found = FindMountByName(name);
            if (found is null)
                return new CliRelay.Response(2, $"No mount named '{name}'. Use --list to see configured mounts.");

            if (mountService is null)
                return new CliRelay.Response(5, "Mount service not initialised.");

            if (!mountService.IsMounted(found))
                return new CliRelay.Response(0, $"'{found.RemoteName}' is not mounted.");

            // No one to ask from a script: refuse instead of cutting the uploads off.
            // shortcut: no --force, so a script cannot override this; add one if users ask for it.
            if (!await UploadsGuardAsync(new[] { found }, "Unmount", interactive: false))
                return new CliRelay.Response(6,
                    $"'{found.RemoteName}' at {found.DriveLetter} still has uploads pending. Try again when they finish, or unmount it in the app.");

            try
            {
                await mountService.UnmountAsync(found);
            }
            catch (Exception ex)
            {
                return new CliRelay.Response(5, "Unmount failed: " + ex.Message);
            }

            try { RefreshMountList(); }
            catch { }

            return new CliRelay.Response(0, $"Unmounted '{found.RemoteName}'.");
        }

        private Mount? FindMountByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            // Exact remote-name match wins; fall back to case-insensitive, then to
            // drive-letter match ("R:" or "R") — the latter lets scripts disambiguate
            // when the same remote is mounted twice at different letters.
            var exact = mounts.FirstOrDefault(m => string.Equals(m.RemoteName, name, StringComparison.Ordinal));
            if (exact is not null) return exact;
            var ci = mounts.FirstOrDefault(m => string.Equals(m.RemoteName, name, StringComparison.OrdinalIgnoreCase));
            if (ci is not null) return ci;
            var letter = name.TrimEnd(':', '\\', '/').ToUpperInvariant();
            return mounts.FirstOrDefault(m =>
                string.Equals((m.DriveLetter ?? "").TrimEnd(':', '\\', '/'), letter, StringComparison.OrdinalIgnoreCase));
        }
    }
}
