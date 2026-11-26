using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using RcloneMount.Services;
using RcloneMount.UI;

namespace RcloneMount
{
    // Construction of the Settings tab. Four sectioned blocks:
    //   PREREQUISITES · STARTUP & UPDATES · APPEARANCE · BACKUP & DATA
    //
    // Fields assigned here (chkAutoCheck, themeRadioAuto, backupsCombo, etc.) are
    // declared in MainForm.cs — this file just owns the construction pass.
    public partial class MainForm
    {
        private void BuildSettingsTab(Panel tabPrereq)
        {
            // Small uppercase section headers group the eight items into four blocks.
            Label MakeSectionHeader(string text, int y) => new()
            {
                Text = text,
                UseMnemonic = false,
                Location = new Point(12, y),
                AutoSize = true,
                Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold),
                ForeColor = Theme.TextTertiary,
                BackColor = Color.Transparent
            };
            // Y positions below shift when sections are added. The App section slots
            // in between PREREQUISITES and STARTUP & UPDATES so update UI sits next to
            // the rclone/WinFsp version panels users already look at.
            var hdrPrereq     = MakeSectionHeader("PREREQUISITES",       12);
            var hdrApp        = MakeSectionHeader("RCLONEMOUNT",         230);
            var hdrStartup    = MakeSectionHeader("STARTUP & UPDATES",   346);
            var hdrAppearance = MakeSectionHeader("APPEARANCE",          462);
            var hdrData       = MakeSectionHeader("BACKUP & DATA",       576);

            // rclone and WinFsp side by side. Two-row button layout because the buttons
            // don't fit on one line at the halved card width.
            var rcloneGroup = MakeSettingsCard("rclone", new Point(12, 40), new Size(LayoutMetrics.CardHalfWidth, 170), AnchorStyles.Top | AnchorStyles.Left);
            lblRcloneInstalled = new Label { UseMnemonic = false, Location = new Point(15, 30), Size = new Size(500, 22), Text = "Detecting rclone...", TextAlign = ContentAlignment.MiddleLeft };
            lblRcloneLatest    = new Label { Location = new Point(15, 56), Size = new Size(500, 22), Text = "Latest: (not checked)", TextAlign = ContentAlignment.MiddleLeft };
            btnLocateRclone  = new Button { Text = "Locate rclone.exe...", Location = new Point(15, 90),  Size = new Size(170, 30) };
            btnLocateRclone.Click += BtnLocateRclone_Click;
            btnCheckRclone   = new Button { Text = "Check for Update",     Location = new Point(195, 90), Size = new Size(140, 30) };
            btnCheckRclone.Click += async (_, _) => await CheckRcloneUpdateAsync(showMessages: true);
            btnInstallRclone = new Button { Text = "Download && Install",  Location = new Point(15, 128), Size = new Size(170, 30), Enabled = false };
            btnInstallRclone.Click += async (_, _) => await InstallRcloneUpdateAsync();
            var btnRcloneWebsite = new Button { Text = "Open rclone.org",  Location = new Point(195, 128), Size = new Size(140, 30) };
            btnRcloneWebsite.Click += (_, _) => OpenUrl("https://rclone.org/downloads/");
            rcloneGroup.Controls.AddRange(new Control[] { lblRcloneInstalled, lblRcloneLatest, btnLocateRclone, btnCheckRclone, btnInstallRclone, btnRcloneWebsite });

            var winFspGroup = MakeSettingsCard("WinFsp", new Point(554, 40), new Size(LayoutMetrics.CardHalfWidth, 170), AnchorStyles.Top | AnchorStyles.Left);
            lblWinFspInstalled = new Label { UseMnemonic = false, Location = new Point(15, 30), Size = new Size(500, 22), Text = "Detecting WinFsp...", TextAlign = ContentAlignment.MiddleLeft };
            lblWinFspLatest    = new Label { Location = new Point(15, 56), Size = new Size(500, 22), Text = "Latest: (not checked)", TextAlign = ContentAlignment.MiddleLeft };
            btnCheckWinFsp   = new Button { Text = "Check for Update",    Location = new Point(15, 90),  Size = new Size(140, 30) };
            btnCheckWinFsp.Click += async (_, _) => await CheckWinFspUpdateAsync(showMessages: true);
            btnInstallWinFsp = new Button { Text = "Download && Install", Location = new Point(165, 90), Size = new Size(170, 30), Enabled = false };
            btnInstallWinFsp.Click += async (_, _) => await InstallWinFspUpdateAsync();
            var btnWinFspWebsite = new Button { Text = "Open winfsp.dev", Location = new Point(15, 128), Size = new Size(140, 30) };
            btnWinFspWebsite.Click += (_, _) => OpenUrl("https://winfsp.dev/rel/");
            winFspGroup.Controls.AddRange(new Control[] { lblWinFspInstalled, lblWinFspLatest, btnCheckWinFsp, btnInstallWinFsp, btnWinFspWebsite });

            // Two-column layout that stretches with the tab. Standard Anchor tricks
            // can't split a container in half — either the cards run into each other or
            // leave a growing gap in the middle. So we handle Resize explicitly.
            void LayoutPrereqCards()
            {
                var cardArea = tabPrereq.ClientSize.Width - LayoutMetrics.CardGutter * 2;
                if (cardArea <= 0) return;
                var halfWidth = (cardArea - LayoutMetrics.CardGutter) / 2;
                if (halfWidth < 260) halfWidth = 260;
                rcloneGroup.Width = halfWidth;
                winFspGroup.Location = new Point(LayoutMetrics.CardGutter + halfWidth + LayoutMetrics.CardGutter, winFspGroup.Location.Y);
                winFspGroup.Width = halfWidth;
            }
            tabPrereq.Resize += (_, _) => LayoutPrereqCards();
            tabPrereq.HandleCreated += (_, _) => LayoutPrereqCards();

            // App card — version + release-channel state. Compact single row.
            var appCard = MakeSettingsCard("", new Point(12, 258), new Size(LayoutMetrics.CardFullWidth, 76), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            lblAppInstalled = new Label
            {
                Text = $"Installed: RcloneMount v{AppVersion}",
                Location = new Point(15, 14), Size = new Size(500, 22),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            lblAppLatest = new Label
            {
                Text = "Latest: (not checked)",
                Location = new Point(15, 40), Size = new Size(500, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.TextSecondary,
            };
            btnCheckAppUpdate = new Button { Text = "Check for Update", Location = new Point(720, 24), Size = new Size(140, 30) };
            btnCheckAppUpdate.Click += async (_, _) => await CheckAppUpdateAsync(showMessages: true);
            btnInstallAppUpdate = new Button { Text = "Install Update", Location = new Point(870, 24), Size = new Size(140, 30), Enabled = false };
            btnInstallAppUpdate.Click += async (_, _) => await InstallAppUpdateAsync();
            appCard.Controls.AddRange(new Control[] { lblAppInstalled, lblAppLatest, btnCheckAppUpdate, btnInstallAppUpdate });

            // Startup card — three toggle buttons on one row with short labels; tooltips
            // carry the full explanation.
            // Title left empty — the "STARTUP & UPDATES" section header already names
            // this card, and repeating the label inside was visual noise.
            var startupCard = MakeSettingsCard("", new Point(12, 374), new Size(LayoutMetrics.CardFullWidth, 66), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            chkStartWithWindows = new ToggleButton { Text = "Start with Windows",   Location = new Point(15,  16), Size = new Size(200, 34) };
            chkAutoCheck        = new ToggleButton { Text = "Check for Updates",    Location = new Point(225, 16), Size = new Size(200, 34) };
            chkAutoInstall      = new ToggleButton { Text = "Auto-Install Updates", Location = new Point(435, 16), Size = new Size(200, 34) };
            chkNotifications    = new ToggleButton { Text = "Notifications",        Location = new Point(645, 16), Size = new Size(200, 34) };
            chkAutoCheck.SetCheckedSilent(true);
            chkNotifications.SetCheckedSilent(true);
            iconToolTip.SetToolTip(chkAutoCheck,        "Check GitHub for rclone and WinFsp updates on each startup.");
            iconToolTip.SetToolTip(chkAutoInstall,      "When on, RcloneMount unmounts, installs updates, and remounts on next startup.");
            iconToolTip.SetToolTip(chkStartWithWindows, "Launch RcloneMount hidden to the tray when Windows signs in.");
            iconToolTip.SetToolTip(chkNotifications,    "Show tray balloons for auto-mount, unexpected exits, and detach recovery. Serious app errors always surface.");
            chkAutoCheck.CheckedChanged += async (_, _) =>
            {
                if (suppressChangeEvents) return;
                checkForUpdatesOnStartup = chkAutoCheck.Checked;
                await SaveSettingsAsync();
            };
            chkAutoInstall.CheckedChanged += async (_, _) =>
            {
                if (suppressChangeEvents) return;
                autoInstallUpdates = chkAutoInstall.Checked;
                await SaveSettingsAsync();
            };
            chkStartWithWindows.CheckedChanged += ChkStartWithWindows_CheckedChanged;
            chkNotifications.CheckedChanged += async (_, _) =>
            {
                if (suppressChangeEvents) return;
                notificationsEnabled = chkNotifications.Checked;
                await SaveSettingsAsync();
            };
            startupCard.Controls.AddRange(new Control[] { chkAutoCheck, chkAutoInstall, chkStartWithWindows, chkNotifications });

            // Appearance card — three toggle buttons acting as a segmented control.
            // Title left empty — the "APPEARANCE" section header names it.
            var appearanceCard = MakeSettingsCard("", new Point(12, 490), new Size(LayoutMetrics.CardFullWidth, 66), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            var lblTheme = new Label { Text = "Theme:", Location = new Point(15, 20), AutoSize = true };
            themeRadioAuto  = new ToggleButton { Text = "System", Location = new Point(85,  16), Size = new Size(90, 32), AutoToggle = false };
            themeRadioLight = new ToggleButton { Text = "Light",  Location = new Point(180, 16), Size = new Size(90, 32), AutoToggle = false };
            themeRadioDark  = new ToggleButton { Text = "Dark",   Location = new Point(275, 16), Size = new Size(90, 32), AutoToggle = false };
            themeRadioAuto.SetCheckedSilent(true);
            appearanceCard.Controls.AddRange(new Control[] { lblTheme, themeRadioAuto, themeRadioLight, themeRadioDark });

            void PickTheme(ToggleButton chosen, string value)
            {
                if (suppressChangeEvents) return;
                // Segmented behaviour — activate self, deactivate the other two.
                themeRadioAuto.SetCheckedSilent(chosen == themeRadioAuto);
                themeRadioLight.SetCheckedSilent(chosen == themeRadioLight);
                themeRadioDark.SetCheckedSilent(chosen == themeRadioDark);
                themePreferenceOverride = value;
                var target = value.ToLowerInvariant() switch
                {
                    "light" => ThemeMode.Light,
                    "dark"  => ThemeMode.Dark,
                    _       => Theme.DetectFromSystem()
                };
                if (target != Theme.Mode)
                    LiveSwitchTheme(target);
                _ = SaveSettingsAsync();
            }
            themeRadioAuto.Click  += (_, _) => PickTheme(themeRadioAuto,  "Auto");
            themeRadioLight.Click += (_, _) => PickTheme(themeRadioLight, "Light");
            themeRadioDark.Click  += (_, _) => PickTheme(themeRadioDark,  "Dark");

            // Settings backup card.
            // All interior controls use fixed positions (Anchor default = Top+Left) so
            // the card can safely stretch with tabPrereq without dragging children with it.
            var backupCard = MakeSettingsCard("App Backup", new Point(12, 604), new Size(LayoutMetrics.CardFullWidth, 180), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);

            // Row 1 — configurable backup folder.
            var lblBackupFolder = new Label
            {
                Text = "Backups Folder:",
                Location = new Point(15, 34),
                Size = new Size(110, 24),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.TextSecondary
            };
            var txtBackupFolder = new TextBox
            {
                Location = new Point(130, 32),
                Size = new Size(720, 26),
                ReadOnly = true,
                BackColor = Theme.SurfaceBase,
                ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle
            };
            var btnChangeFolder = new Button { Text = "Change...", Location = new Point(860, 30), Size = new Size(100, 30) };
            var btnResetFolder  = new Button { Text = "Default",  Location = new Point(965, 30), Size = new Size(90, 30) };

            // Row 2 — pick one of the auto-saved snapshots.
            var lblRestoreFrom = new Label
            {
                Text = "Recent Backups:",
                Location = new Point(15, 74),
                Size = new Size(110, 24),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.TextSecondary
            };
            var cboBackups = new ThemedDropDown
            {
                Location = new Point(130, 72),
                Size = new Size(720, 26)
            };
            var btnRestoreBackup = new Button { Text = "Restore Selected", Location = new Point(860, 70), Size = new Size(140, 30), Enabled = false };
            var btnOpenBackupFolder = new Button { Text = "Open folder", Location = new Point(1005, 70), Size = new Size(50, 30) };

            // Row 3 — restore from an arbitrary file (first-run migration path), and a
            // manual "backup now" that snapshots regardless of whether anything changed.
            var btnRestoreFromFile = new Button { Text = "Restore from File...", Location = new Point(130, 110), Size = new Size(180, 30) };
            var btnBackupNow       = new Button { Text = "Backup Now",           Location = new Point(320, 110), Size = new Size(140, 30) };

            var lblBackupCount = new Label
            {
                Text = "",
                Location = new Point(15, 148),
                Size = new Size(1040, 20),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.TextTertiary
            };

            cboBackups.SelectedIndexChanged += (_, _) => btnRestoreBackup.Enabled = cboBackups.SelectedIndex >= 0;
            btnOpenBackupFolder.Text = "Open";
            btnOpenBackupFolder.Click += (_, _) =>
            {
                try
                {
                    if (!System.IO.Directory.Exists(SettingsService.BackupsFolder))
                        System.IO.Directory.CreateDirectory(SettingsService.BackupsFolder);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsService.BackupsFolder}\"") { UseShellExecute = true });
                }
                catch { }
            };
            btnChangeFolder.Click     += async (_, _) => await ChangeBackupsFolderAsync();
            btnResetFolder.Click      += async (_, _) => await ResetBackupsFolderAsync();
            btnRestoreBackup.Click    += (_, _) => RestoreSelectedBackup(cboBackups);
            btnRestoreFromFile.Click  += (_, _) => RestoreFromFileDialog();
            btnBackupNow.Click        += async (_, _) => await BackupNowAsync();

            backupCard.Controls.Add(lblBackupFolder);
            backupCard.Controls.Add(txtBackupFolder);
            backupCard.Controls.Add(btnChangeFolder);
            backupCard.Controls.Add(btnResetFolder);
            backupCard.Controls.Add(lblRestoreFrom);
            backupCard.Controls.Add(cboBackups);
            backupCard.Controls.Add(btnRestoreBackup);
            backupCard.Controls.Add(btnOpenBackupFolder);
            backupCard.Controls.Add(btnRestoreFromFile);
            backupCard.Controls.Add(btnBackupNow);
            backupCard.Controls.Add(lblBackupCount);
            backupsCombo = cboBackups;
            backupsCountLabel = lblBackupCount;
            backupsFolderTextbox = txtBackupFolder;

            // Transfer totals card — persisted lifetime counters, restart-proof.
            var totalsCard = MakeSettingsCard("Transfer totals", new Point(12, 796), new Size(LayoutMetrics.CardFullWidth, 110), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            var totalsBig = new Label
            {
                Text = "↓ 0 B   ·   ↑ 0 B   ·   Total 0 B",
                Location = new Point(15, 34),
                Size = new Size(700, 26),
                Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.TextPrimary
            };
            var totalsSince = new Label
            {
                Text = "",
                Location = new Point(15, 66),
                Size = new Size(700, 20),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.TextTertiary
            };
            var btnResetTotals = new Button { Text = "Reset", Location = new Point(965, 34), Size = new Size(90, 30) };
            btnResetTotals.Click += async (_, _) => await ResetLifetimeTotalsAsync();
            totalsCard.Controls.Add(totalsBig);
            totalsCard.Controls.Add(totalsSince);
            totalsCard.Controls.Add(btnResetTotals);
            lifetimeTotalsLabel = totalsBig;
            lifetimeSinceLabel = totalsSince;

            tabPrereq.Controls.Add(hdrPrereq);
            tabPrereq.Controls.Add(rcloneGroup);
            tabPrereq.Controls.Add(winFspGroup);
            tabPrereq.Controls.Add(hdrApp);
            tabPrereq.Controls.Add(appCard);
            tabPrereq.Controls.Add(hdrStartup);
            tabPrereq.Controls.Add(startupCard);
            tabPrereq.Controls.Add(hdrAppearance);
            tabPrereq.Controls.Add(appearanceCard);
            tabPrereq.Controls.Add(hdrData);
            tabPrereq.Controls.Add(backupCard);
            tabPrereq.Controls.Add(totalsCard);
        }
    }
}
