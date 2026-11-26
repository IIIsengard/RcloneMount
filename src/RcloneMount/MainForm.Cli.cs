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
    // send a JSON request, and get a JSON reply. Actions run against mountService
    // directly and skip any UI prompts — a scripted caller can't answer a MessageBox.
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
            // Snapshot the mounts list on the UI thread — we're on the pipe thread here,
            // so directly enumerating without synchronisation risks a concurrent Add/Remove.
            List<Mount> snapshot;
            try
            {
                if (InvokeRequired)
                    snapshot = (List<Mount>)Invoke(new Func<List<Mount>>(() => mounts.ToList()));
                else
                    snapshot = mounts.ToList();
            }
            catch
            {
                snapshot = new List<Mount>();
            }

            var entries = new List<CliRelay.MountEntry>(snapshot.Count);
            foreach (var m in snapshot)
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

            try
            {
                await mountService.MountAsync(found);
            }
            catch (Exception ex)
            {
                return new CliRelay.Response(5, "Mount failed: " + ex.Message);
            }

            // Wait for the drive letter to actually appear so a script that immediately
            // reads from it doesn't race the mount. Same 15s ceiling as the UI path.
            var ready = await WaitForDriveReadyAsync(found.DriveLetter, TimeSpan.FromSeconds(15));

            // Refresh the row visuals on the UI thread — mount succeeded either way,
            // but the timer-driven refresh could be up to 1s behind and users notice.
            try
            {
                if (InvokeRequired) BeginInvoke(new Action(RefreshMountList));
                else RefreshMountList();
            }
            catch { }

            if (!ready)
                return new CliRelay.Response(3, $"rclone launched, but {found.DriveLetter} did not become ready within 15s.");

            return new CliRelay.Response(0, $"Mounted '{found.RemoteName}' at {found.DriveLetter}.");
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

            try
            {
                if (InvokeRequired) BeginInvoke(new Action(RefreshMountList));
                else RefreshMountList();
            }
            catch { }

            return new CliRelay.Response(0, $"Unmounted '{found.RemoteName}'.");
        }

        private Mount? FindMountByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            List<Mount> snapshot;
            try
            {
                if (InvokeRequired)
                    snapshot = (List<Mount>)Invoke(new Func<List<Mount>>(() => mounts.ToList()));
                else
                    snapshot = mounts.ToList();
            }
            catch
            {
                return null;
            }

            // Exact remote-name match wins; fall back to case-insensitive, then to
            // drive-letter match ("R:" or "R") — the latter lets scripts disambiguate
            // when the same remote is mounted twice at different letters.
            var exact = snapshot.FirstOrDefault(m => string.Equals(m.RemoteName, name, StringComparison.Ordinal));
            if (exact is not null) return exact;
            var ci = snapshot.FirstOrDefault(m => string.Equals(m.RemoteName, name, StringComparison.OrdinalIgnoreCase));
            if (ci is not null) return ci;
            var letter = name.TrimEnd(':', '\\', '/').ToUpperInvariant();
            return snapshot.FirstOrDefault(m =>
                string.Equals((m.DriveLetter ?? "").TrimEnd(':', '\\', '/'), letter, StringComparison.OrdinalIgnoreCase));
        }
    }
}
