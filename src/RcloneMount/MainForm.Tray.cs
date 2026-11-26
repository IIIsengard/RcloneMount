using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount
{
    // Tray-icon and show/hide-to-tray behaviour. The tray menu is rebuilt on every
    // Opening event so it reflects the current mount states without needing
    // subscription plumbing.
    public partial class MainForm
    {
        protected override void SetVisibleCore(bool value)
        {
            if (!allowVisible)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
                // Startup-to-tray path: the form never becomes visible, so the Load
                // event won't fire on its own — kick the startup work off explicitly
                // once the message loop is pumping, otherwise auto-mounts don't
                // attempt to connect until the user opens the window.
                if (!loadStarted && !loadKickoffScheduled)
                {
                    loadKickoffScheduled = true;
                    BeginInvoke(new Action(() => MainForm_Load(this, EventArgs.Empty)));
                }
            }
            base.SetVisibleCore(value);
        }

        private void ShowFromTray()
        {
            allowVisible = true;
            ShowInTaskbar = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void HideToTray()
        {
            // Order matters. Setting ShowInTaskbar=false on a VISIBLE form makes WinForms
            // recreate the window handle, which flashes the window back on screen for a
            // frame before it goes away. Hide first — then updating the taskbar flag on
            // an already-invisible form is silent.
            Hide();
            ShowInTaskbar = false;
        }

        private void TrayMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            trayMenu.Items.Clear();
            trayMenu.Items.Add("Show window", null, (_, _) => ShowFromTray());
            trayMenu.Items.Add(new ToolStripSeparator());

            if (mounts.Count == 0)
            {
                var noMounts = new ToolStripMenuItem("(no mounts configured)") { Enabled = false };
                trayMenu.Items.Add(noMounts);
            }
            else
            {
                foreach (var m in mounts)
                {
                    var mounted = mountService is not null && mountService.IsMounted(m);
                    var external = !mounted && RcloneMountService.IsDriveMountedExternally(m.DriveLetter);
                    var state = mounted ? "Mounted" : external ? "External" : "Idle";
                    var label = $"{m.RemoteName} → {m.DriveLetter}  ({state})";
                    var sub = new ToolStripMenuItem(label.Replace("&", "&&")); // a lone & is a mnemonic

                    if (mounted || external)
                    {
                        var mount = m;
                        sub.DropDownItems.Add("Open in Explorer", null, (_, _) => OpenDriveExternally(mount));
                        sub.DropDownItems.Add("Unmount", null, async (_, _) => await UnmountFromTrayAsync(mount));
                    }
                    else
                    {
                        var mount = m;
                        sub.DropDownItems.Add("Mount", null, async (_, _) => await MountFromTrayAsync(mount));
                    }
                    trayMenu.Items.Add(sub);
                }
            }

            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(new ToolStripMenuItem("rclone terminal", null, BtnRcloneTerminal_Click) { Enabled = configService is not null });
            var silence = new ToolStripMenuItem("Silence notifications")
            {
                Checked = !notificationsEnabled,
                CheckOnClick = true
            };
            silence.Click += async (_, _) =>
            {
                notificationsEnabled = !silence.Checked;
                if (chkNotifications is not null)
                    chkNotifications.SetCheckedSilent(notificationsEnabled);
                await SaveSettingsAsync();
            };
            trayMenu.Items.Add(silence);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit", null, (_, _) => { exitFromTray = true; Close(); });
        }

        private async Task MountFromTrayAsync(Mount m)
        {
            if (mountService is null || winFspInfo is null) return;
            if (!remotes.Any(r => r.Name == m.RemoteName)) return;
            CancelRetry(m.Id);
            try
            {
                if (!await StartMountAsync(m))
                {
                    Notify("Mount failed", $"No free drive letters are available for {m.RemoteName}.", ToolTipIcon.Error);
                    return;
                }
                var live = m.DriveLetter;
                if (await WaitForDriveReadyAsync(live, TimeSpan.FromSeconds(15)))
                {
                    ShellNotifier.DriveAdded(live);
                    NotifyMountUp(m);
                }
                RefreshMountList();
            }
            catch (Exception ex)
            {
                Notify("Mount failed", ex.Message, ToolTipIcon.Error);
            }
        }

        private async Task UnmountFromTrayAsync(Mount m)
        {
            if (mountService is null) return;
            if (!await UploadsGuardAsync(new[] { m }, "Unmount", interactive: true)) return;
            KeepUnmounted(m);
            try
            {
                var (live, _) = await StopMountAsync(m);
                NotifyMountDown(m, live);
                RefreshMountList();
            }
            catch (Exception ex)
            {
                Notify("Unmount failed", ex.Message, ToolTipIcon.Error);
            }
        }

        private void OpenDriveExternally(Mount m)
        {
            var root = m.DriveLetter.TrimEnd(':') + ":\\";
            if (!System.IO.Directory.Exists(root)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true }); } catch { }
        }
    }
}
