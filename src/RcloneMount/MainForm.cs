using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;
using RcloneMount.UI;

namespace RcloneMount
{
    public class MainForm : Form
    {
        private readonly List<Mount> mounts = new();
        private string? savedRclonePath;
        private bool checkForUpdatesOnStartup = true;
        private bool autoInstallUpdates;
        private bool firstRunSetupDone;
        private IReadOnlyList<RcloneRemote> remotes = Array.Empty<RcloneRemote>();

        private RcloneInfo? rcloneInfo;
        private WinFspInfo? winFspInfo;
        private LatestRelease? latestRclone;
        private LatestRelease? latestWinFsp;
        private RcloneConfigService? configService;
        private RcloneMountService? mountService;

        // Mounts tab — master-detail
        private readonly FlowLayoutPanel railList;
        private readonly Label railCountLabel;
        private readonly Panel detailScroll;
        private readonly Panel detailHeader;
        private readonly Panel detailBadge;
        private readonly Label detailTitleLabel;
        private readonly Label detailSubtitleLabel;
        private readonly Button detailEditRemoteBtn;
        private readonly Button detailDeleteRemoteBtn;
        private readonly Panel settingsCard;
        private readonly Panel settingsCardBody;
        private readonly Panel mountsCard;
        private readonly Panel mountsCardBody;
        private readonly Button addMountBtn;
        private readonly Label emptyStateLabel;
        private readonly Button toolbarAddRemoteBtn;
        private readonly Button toolbarRefreshBtn;
        private readonly Button toolbarAdvancedBtn;
        private string? selectedRemoteName;

        // Prerequisites tab
        private readonly Label lblRcloneInstalled;
        private readonly Label lblRcloneLatest;
        private readonly Button btnLocateRclone;
        private readonly Button btnCheckRclone;
        private readonly Button btnInstallRclone;
        private readonly Label lblWinFspInstalled;
        private readonly Label lblWinFspLatest;
        private readonly Button btnCheckWinFsp;
        private readonly Button btnInstallWinFsp;
        private readonly CheckBox chkAutoCheck;
        private readonly CheckBox chkAutoInstall;
        private readonly CheckBox chkStartWithWindows;
        private readonly RadioButton themeRadioAuto;
        private readonly RadioButton themeRadioLight;
        private readonly RadioButton themeRadioDark;
        private string themePreferenceOverride = "Auto";

        private readonly NotifyIcon trayIcon;
        private readonly ContextMenuStrip trayMenu;
        private readonly bool startHidden;
        private bool allowVisible;
        private bool exitFromTray;
        private bool suppressChangeEvents;

        // Per-mount retry loops (auto-remount on unexpected exit)
        private readonly Dictionary<string, CancellationTokenSource> retryCts = new();
        private static readonly int[] RetryDelaysMs = { 2000, 5000, 15000, 30000, 60000 };

        private readonly Label lblStatus;

        // Log tab
        private readonly ActivityLog activityLog = new(capacity: 2000, persistPath: SettingsService.ActivityLogPath, retention: TimeSpan.FromDays(30));
        private readonly TextBox txtLog;
        private readonly CheckBox chkAutoScroll;

        public MainForm() : this(false) { }

        public MainForm(bool startHidden)
        {
            this.startHidden = startHidden;
            allowVisible = !startHidden;
            Icon = AppIcon.Get();
            Text = "RcloneMount";
            ClientSize = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 500);
            BackColor = Theme.SurfaceBase;
            ForeColor = Theme.TextPrimary;

            // Mockup-style pill tabs. Three panels stacked in a content host; the pill bar
            // toggles visibility. This replaces the native TabControl chrome (which is impossible
            // to theme in dark mode).
            var tabHost = new Panel { Location = new Point(12, 12), Size = new Size(976, 578), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, BackColor = Theme.SurfaceBase };

            var pillBar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.SurfaceRaised };
            var pillBarBorder = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.BorderSubtle };
            pillBar.Controls.Add(pillBarBorder);

            // Brand: app icon + name on the left.
            var brand = new Panel { Location = new Point(14, 8), Size = new Size(180, 30), BackColor = Color.Transparent };
            var brandIcon = new Panel { Location = new Point(0, 3), Size = new Size(24, 24), BackColor = Color.Transparent };
            brandIcon.Paint += (_, e) =>
            {
                using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, 24, 24), Theme.Accent, Color.FromArgb(0x6B, 0xA0, 0xFA),
                    System.Drawing.Drawing2D.LinearGradientMode.ForwardDiagonal);
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                path.AddArc(0, 0, 8, 8, 180, 90);
                path.AddArc(16, 0, 8, 8, 270, 90);
                path.AddArc(16, 16, 8, 8, 0, 90);
                path.AddArc(0, 16, 8, 8, 90, 90);
                path.CloseFigure();
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
            };
            var brandName = new Label { Text = "RcloneMount", Location = new Point(32, 6), AutoSize = true, Font = new Font(Font.FontFamily, 10f, FontStyle.Bold), ForeColor = Theme.TextPrimary, BackColor = Color.Transparent };
            brand.Controls.Add(brandIcon);
            brand.Controls.Add(brandName);
            pillBar.Controls.Add(brand);

            var pillGroup = new FlowLayoutPanel
            {
                Location = new Point(210, 9),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.SurfaceSunken,
                Padding = new Padding(3),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            pillBar.Resize += (_, _) =>
            {
                if (pillGroup.Width > 0)
                    pillGroup.Location = new Point((pillBar.Width - pillGroup.Width) / 2, 9);
            };
            pillGroup.Paint += (_, e) =>
            {
                using var pen = new Pen(Theme.BorderSubtle);
                e.Graphics.DrawRectangle(pen, 0, 0, pillGroup.Width - 1, pillGroup.Height - 1);
            };

            var pillMounts   = MakePillTab("Mounts",   isActive: true);
            var pillLog      = MakePillTab("Log",      isActive: false);
            var pillSettings = MakePillTab("Settings", isActive: false);
            pillGroup.Controls.Add(pillMounts);
            pillGroup.Controls.Add(pillLog);
            pillGroup.Controls.Add(pillSettings);
            pillBar.Controls.Add(pillGroup);

            var contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase };
            var tabMounts  = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase };
            var tabLog     = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase, Visible = false };
            var tabPrereq  = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase, Visible = false };
            contentHost.Controls.Add(tabMounts);
            contentHost.Controls.Add(tabLog);
            contentHost.Controls.Add(tabPrereq);

            tabHost.Controls.Add(contentHost);
            tabHost.Controls.Add(pillBar);

            void SwitchTab(int index)
            {
                tabMounts.Visible = index == 0;
                tabLog.Visible    = index == 1;
                tabPrereq.Visible = index == 2;
                SetPillActive(pillMounts,   index == 0);
                SetPillActive(pillLog,      index == 1);
                SetPillActive(pillSettings, index == 2);
            }
            pillMounts.Click   += (_, _) => SwitchTab(0);
            pillLog.Click      += (_, _) => SwitchTab(1);
            pillSettings.Click += (_, _) => SwitchTab(2);

            // ── Mounts tab (master-detail) ─────────────────────────
            tabMounts.Padding = new Padding(0);
            tabMounts.BackColor = Theme.SurfaceBase;

            // Toolbar removed — Add / Refresh / more all live in the rail footer under Remotes.
            // Keep the button fields hidden so existing Enabled-tracking + click delegates still work.
            toolbarRefreshBtn   = new Button { Visible = false };
            toolbarAddRemoteBtn = new Button { Visible = false };
            toolbarAdvancedBtn  = new Button { Visible = false };
            toolbarAdvancedBtn.Click += BtnConfigureRemotes_Click;

            // Rail (left).
            var rail = new Panel { Dock = DockStyle.Left, Width = 280, BackColor = Theme.SurfaceBase };
            var railRightBorder = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = Theme.BorderSubtle };
            rail.Controls.Add(railRightBorder);

            var railHeader = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.SurfaceBase };
            var railTitle = new Label
            {
                Text = "REMOTES",
                Font = new Font(Font.FontFamily, 8.25f, FontStyle.Bold),
                ForeColor = Theme.TextTertiary,
                Location = new Point(16, 14),
                AutoSize = true
            };
            railCountLabel = new Label
            {
                Text = "0",
                Font = new Font(Font.FontFamily, 8f, FontStyle.Regular),
                ForeColor = Theme.TextTertiary,
                BackColor = Theme.SurfaceSunken,
                Padding = new Padding(6, 1, 6, 1),
                Location = new Point(0, 12),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            railHeader.Resize += (_, _) => railCountLabel.Location = new Point(railHeader.Width - railCountLabel.Width - 16, 12);
            railHeader.Controls.Add(railTitle);
            railHeader.Controls.Add(railCountLabel);

            var railSearchPanel = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.SurfaceBase, Padding = new Padding(12, 4, 12, 8) };
            var railSearchBox = new TextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.SurfaceRaised,
                ForeColor = Theme.TextPrimary,
                Font = new Font(Font.FontFamily, 9f),
                PlaceholderText = "Search remotes"
            };
            railSearchPanel.BackColor = Theme.SurfaceBase;
            var railSearchBoxHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceRaised, Padding = new Padding(8, 5, 8, 5) };
            railSearchBoxHost.Paint += (_, e) =>
            {
                using var pen = new Pen(Theme.BorderSubtle);
                e.Graphics.DrawRectangle(pen, 0, 0, railSearchBoxHost.Width - 1, railSearchBoxHost.Height - 1);
            };
            railSearchBoxHost.Controls.Add(railSearchBox);
            railSearchBox.TextChanged += (_, _) => FilterRail(railSearchBox.Text);
            railSearchPanel.Controls.Add(railSearchBoxHost);

            var railFooter = new Panel { Dock = DockStyle.Bottom, Height = 38, BackColor = Theme.SurfaceBase };
            var railFooterBorder = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.BorderSubtle };
            var railFooterFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(8, 6, 8, 6),
                BackColor = Color.Transparent
            };
            var railAddBtn     = MakeIconButton("+",  "Add remote");
            var railRemoveBtn  = MakeIconButton("−",  "Remove selected remote");
            var railRefreshBtn = MakeIconButton("⟳", "Refresh remote list");
            var railMoreBtn    = MakeIconButton("⋯", "More");
            railAddBtn.Click     += BtnAddRemote_Click;
            railRemoveBtn.Click  += BtnDeleteRemote_Click;
            railRefreshBtn.Click += async (_, _) => await RefreshRemotesAsync();
            railMoreBtn.Click    += BtnConfigureRemotes_Click;
            railFooterFlow.Controls.Add(railAddBtn);
            railFooterFlow.Controls.Add(railRemoveBtn);
            railFooterFlow.Controls.Add(railRefreshBtn);
            railFooterFlow.Controls.Add(new Panel { Width = 4, Height = 1, BackColor = Color.Transparent });
            railFooterFlow.Controls.Add(railMoreBtn);
            railFooter.Controls.Add(railFooterFlow);
            railFooter.Controls.Add(railFooterBorder);

            railList = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Theme.SurfaceBase,
                Padding = new Padding(0)
            };

            // Dock order (reverse-Z): Fill first, then Bottom, Top, Top.
            rail.Controls.Add(railList);
            rail.Controls.Add(railFooter);
            rail.Controls.Add(railSearchPanel);
            rail.Controls.Add(railHeader);

            // Detail (right).
            detailScroll = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase, AutoScroll = true, Padding = new Padding(24, 16, 24, 24) };

            // Header row: badge · title/subtitle · action buttons
            detailHeader = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.SurfaceBase };
            detailBadge = new Panel { Location = new Point(0, 10), Size = new Size(44, 44), BackColor = Color.Transparent };
            detailBadge.Paint += (_, e) => ProviderVisuals.DrawBadge(e.Graphics, new Rectangle(0, 0, detailBadge.Width, detailBadge.Height), CurrentRemoteType());

            detailTitleLabel = new Label
            {
                Location = new Point(56, 8),
                Font = new Font(Font.FontFamily, 15f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                AutoSize = true,
                Text = ""
            };
            detailSubtitleLabel = new Label
            {
                Location = new Point(56, 36),
                Font = new Font(Font.FontFamily, 8.75f),
                ForeColor = Theme.TextSecondary,
                AutoSize = true,
                Text = ""
            };

            detailEditRemoteBtn = MakeToolbarButton("Edit remote…", primary: false);
            detailEditRemoteBtn.Size = new Size(110, 28);
            detailEditRemoteBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            detailEditRemoteBtn.Click += BtnEditRemote_Click;
            detailDeleteRemoteBtn = MakeToolbarButton("Delete", primary: false);
            detailDeleteRemoteBtn.Size = new Size(72, 28);
            detailDeleteRemoteBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            detailDeleteRemoteBtn.Click += BtnDeleteRemote_Click;

            detailHeader.Controls.Add(detailBadge);
            detailHeader.Controls.Add(detailTitleLabel);
            detailHeader.Controls.Add(detailSubtitleLabel);
            detailHeader.Controls.Add(detailEditRemoteBtn);
            detailHeader.Controls.Add(detailDeleteRemoteBtn);
            detailHeader.Resize += (_, _) =>
            {
                detailEditRemoteBtn.Location  = new Point(detailHeader.Width - detailEditRemoteBtn.Width, 18);
                detailDeleteRemoteBtn.Location = new Point(detailEditRemoteBtn.Left - detailDeleteRemoteBtn.Width - 6, 18);
            };

            // Settings card (read-only summary of remote params).
            settingsCard = MakeCard("Remote settings", "Read-only summary — Edit to change");
            settingsCardBody = (Panel)settingsCard.Tag!;

            // Mounts card (per-remote mounts list + Add mount button).
            mountsCard = MakeCard("Mounts", "");
            mountsCardBody = (Panel)mountsCard.Tag!;
            addMountBtn = MakeToolbarButton("+ Add mount", primary: true);
            addMountBtn.Size = new Size(110, 28);
            addMountBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            addMountBtn.Click += (_, _) => AddMountForSelectedRemote();
            // Attach add button to the card's header
            var mountsHeaderPanel = mountsCard.Controls[0];
            mountsHeaderPanel.Controls.Add(addMountBtn);
            mountsHeaderPanel.Resize += (_, _) => addMountBtn.Location = new Point(mountsHeaderPanel.Width - addMountBtn.Width - 12, (mountsHeaderPanel.Height - addMountBtn.Height) / 2);

            emptyStateLabel = new Label
            {
                Text = "Select a remote on the left, or click \"+ New remote\" to add one.",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Theme.TextTertiary,
                Font = new Font(Font.FontFamily, 9.5f),
                Visible = false
            };

            // Detail assembly: cards flow top-down. AutoSize + Dock=Top lets the flow panel
            // grow with content and the outer detailScroll show its own scrollbar.
            var detailStack = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Padding = new Padding(0)
            };
            detailHeader.Margin = new Padding(0, 0, 0, 8);
            emptyStateLabel.Margin = new Padding(0, 0, 0, 8);
            settingsCard.Margin = new Padding(0, 0, 0, 12);
            mountsCard.Margin = new Padding(0, 0, 0, 0);
            detailStack.Controls.Add(detailHeader);
            detailStack.Controls.Add(emptyStateLabel);
            detailStack.Controls.Add(settingsCard);
            detailStack.Controls.Add(mountsCard);

            // Cards should fill horizontally; keep them synced to the scroll pane's client width.
            void SyncCardWidths()
            {
                var w = detailScroll.ClientSize.Width - detailScroll.Padding.Horizontal;
                if (detailScroll.VerticalScroll.Visible)
                    w -= SystemInformation.VerticalScrollBarWidth;
                if (w <= 0) return;
                detailHeader.Width = w;
                emptyStateLabel.Width = w;
                settingsCard.Width = w;
                mountsCard.Width = w;
                detailStack.Width = w;
            }
            detailScroll.Resize += (_, _) => SyncCardWidths();
            detailScroll.SizeChanged += (_, _) => SyncCardWidths();

            detailScroll.Controls.Add(detailStack);

            // Compose: rail left, detail fill.
            tabMounts.Controls.Add(detailScroll);
            tabMounts.Controls.Add(rail);

            // ── Log tab ────────────────────────────────────────────
            tabLog.Padding = new Padding(6);
            txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9),
                BackColor = Theme.SurfaceRaised,
                ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle
            };
            var logFooter = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            chkAutoScroll = new CheckBox { Text = "Auto-scroll", Location = new Point(0, 10), AutoSize = true, Checked = true };
            var btnLogClear = new Button { Text = "Clear", Location = new Point(110, 4), Size = new Size(80, 28) };
            btnLogClear.Click += (_, _) => { activityLog.Clear(); txtLog.Clear(); };
            var btnLogCopy = new Button { Text = "Copy all", Location = new Point(195, 4), Size = new Size(80, 28) };
            btnLogCopy.Click += (_, _) => { try { if (txtLog.TextLength > 0) Clipboard.SetText(txtLog.Text); } catch { } };
            logFooter.Controls.Add(chkAutoScroll);
            logFooter.Controls.Add(btnLogClear);
            logFooter.Controls.Add(btnLogCopy);
            tabLog.Controls.Add(txtLog);
            tabLog.Controls.Add(logFooter);

            // Seed with any persisted history (last ≤30 days, up to buffer capacity).
            foreach (var e in activityLog.Snapshot())
                txtLog.AppendText(e.ToString() + Environment.NewLine);
            if (txtLog.TextLength > 0)
            {
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            }

            activityLog.Appended += entry =>
            {
                if (IsDisposed || Disposing) return;
                if (InvokeRequired) { try { BeginInvoke(new Action(() => AppendLogLine(entry))); } catch { } }
                else AppendLogLine(entry);
            };

            // ── Prerequisites tab ──────────────────────────────────
            var rcloneGroup = MakeSettingsCard("rclone", new Point(12, 12), new Size(944, 160), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            lblRcloneInstalled = new Label { Location = new Point(15, 30), Size = new Size(700, 22), Text = "Detecting rclone...", TextAlign = ContentAlignment.MiddleLeft };
            lblRcloneLatest = new Label { Location = new Point(15, 58), Size = new Size(700, 22), Text = "Latest: (not checked)", TextAlign = ContentAlignment.MiddleLeft };
            btnLocateRclone = new Button { Text = "Locate rclone.exe...", Location = new Point(15, 110), Size = new Size(160, 32) };
            btnLocateRclone.Click += BtnLocateRclone_Click;
            btnCheckRclone = new Button { Text = "Check for update", Location = new Point(185, 110), Size = new Size(140, 32) };
            btnCheckRclone.Click += async (_, _) => await CheckRcloneUpdateAsync(showMessages: true);
            btnInstallRclone = new Button { Text = "Download && Install", Location = new Point(335, 110), Size = new Size(160, 32), Enabled = false };
            btnInstallRclone.Click += async (_, _) => await InstallRcloneUpdateAsync();
            var btnRcloneWebsite = new Button { Text = "Open rclone.org", Location = new Point(770, 110), Size = new Size(160, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnRcloneWebsite.Click += (_, _) => OpenUrl("https://rclone.org/downloads/");
            rcloneGroup.Controls.AddRange(new Control[] { lblRcloneInstalled, lblRcloneLatest, btnLocateRclone, btnCheckRclone, btnInstallRclone, btnRcloneWebsite });

            var winFspGroup = MakeSettingsCard("WinFsp", new Point(12, 182), new Size(944, 160), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            lblWinFspInstalled = new Label { Location = new Point(15, 30), Size = new Size(700, 22), Text = "Detecting WinFsp...", TextAlign = ContentAlignment.MiddleLeft };
            lblWinFspLatest = new Label { Location = new Point(15, 58), Size = new Size(700, 22), Text = "Latest: (not checked)", TextAlign = ContentAlignment.MiddleLeft };
            btnCheckWinFsp = new Button { Text = "Check for update", Location = new Point(15, 110), Size = new Size(140, 32) };
            btnCheckWinFsp.Click += async (_, _) => await CheckWinFspUpdateAsync(showMessages: true);
            btnInstallWinFsp = new Button { Text = "Download && Install", Location = new Point(165, 110), Size = new Size(160, 32), Enabled = false };
            btnInstallWinFsp.Click += async (_, _) => await InstallWinFspUpdateAsync();
            var btnWinFspWebsite = new Button { Text = "Open winfsp.dev", Location = new Point(770, 110), Size = new Size(160, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnWinFspWebsite.Click += (_, _) => OpenUrl("https://winfsp.dev/rel/");
            winFspGroup.Controls.AddRange(new Control[] { lblWinFspInstalled, lblWinFspLatest, btnCheckWinFsp, btnInstallWinFsp, btnWinFspWebsite });

            chkAutoCheck = new CheckBox { Text = "Check for updates on startup", Location = new Point(12, 352), AutoSize = true, Checked = true };
            chkAutoCheck.CheckedChanged += async (_, _) => { if (suppressChangeEvents) return; checkForUpdatesOnStartup = chkAutoCheck.Checked; await SaveSettingsAsync(); };
            chkAutoInstall = new CheckBox { Text = "Auto-install available updates on startup (unmounts, updates, remounts)", Location = new Point(12, 378), AutoSize = true };
            chkAutoInstall.CheckedChanged += async (_, _) => { if (suppressChangeEvents) return; autoInstallUpdates = chkAutoInstall.Checked; await SaveSettingsAsync(); };
            chkStartWithWindows = new CheckBox { Text = "Start with Windows (hidden to tray)", Location = new Point(12, 404), AutoSize = true };
            chkStartWithWindows.CheckedChanged += ChkStartWithWindows_CheckedChanged;

            // Appearance card — theme radio group.
            var appearanceCard = MakeSettingsCard("Appearance", new Point(12, 442), new Size(944, 100), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            var lblTheme = new Label { Text = "Theme:", Location = new Point(15, 40), AutoSize = true };
            var rbAuto  = new RadioButton { Text = "Auto (follow system)", Location = new Point(90, 38), AutoSize = true };
            var rbLight = new RadioButton { Text = "Light",  Location = new Point(260, 38), AutoSize = true };
            var rbDark  = new RadioButton { Text = "Dark",   Location = new Point(340, 38), AutoSize = true };
            var lblThemeHint = new Label { Text = "Applied on next launch", Location = new Point(15, 68), AutoSize = true, ForeColor = Theme.TextTertiary, Font = new Font(Font.FontFamily, 8f) };
            appearanceCard.Controls.AddRange(new Control[] { lblTheme, rbAuto, rbLight, rbDark, lblThemeHint });

            void HookThemeRadio(RadioButton rb, string value)
            {
                rb.CheckedChanged += async (_, _) =>
                {
                    if (suppressChangeEvents || !rb.Checked) return;
                    themePreferenceOverride = value;
                    var target = value.ToLowerInvariant() switch
                    {
                        "light" => ThemeMode.Light,
                        "dark"  => ThemeMode.Dark,
                        _       => Theme.DetectFromSystem()
                    };
                    if (target != Theme.Mode)
                        LiveSwitchTheme(target);
                    await SaveSettingsAsync();
                };
            }
            HookThemeRadio(rbAuto,  "Auto");
            HookThemeRadio(rbLight, "Light");
            HookThemeRadio(rbDark,  "Dark");
            themeRadioAuto = rbAuto; themeRadioLight = rbLight; themeRadioDark = rbDark;

            // Advanced card — CLI escape hatch(es).
            var advancedCard = MakeSettingsCard("Advanced", new Point(12, 552), new Size(944, 80), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            var btnOpenRcloneConfig = new Button
            {
                Text = "Open rclone config (CLI)…",
                Location = new Point(15, 34),
                Size = new Size(200, 32)
            };
            btnOpenRcloneConfig.Click += BtnConfigureRemotes_Click;
            var lblAdvancedHint = new Label { Text = "Launches an interactive rclone config session in a terminal.", Location = new Point(225, 42), AutoSize = true, ForeColor = Theme.TextTertiary };
            advancedCard.Controls.AddRange(new Control[] { btnOpenRcloneConfig, lblAdvancedHint });

            tabPrereq.Controls.Add(rcloneGroup);
            tabPrereq.Controls.Add(winFspGroup);
            tabPrereq.Controls.Add(chkAutoCheck);
            tabPrereq.Controls.Add(chkAutoInstall);
            tabPrereq.Controls.Add(chkStartWithWindows);
            tabPrereq.Controls.Add(appearanceCard);
            tabPrereq.Controls.Add(advancedCard);

            lblStatus = new Label { Text = "", Location = new Point(12, 605), Size = new Size(870, 24), BorderStyle = BorderStyle.None, TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, BackColor = Theme.SurfaceRaised, ForeColor = Theme.TextSecondary, Padding = new Padding(28, 0, 8, 0) };
            lblStatus.Paint += (_, e) =>
            {
                using var pen = new Pen(Theme.BorderSubtle);
                e.Graphics.DrawLine(pen, 0, 0, lblStatus.Width, 0);

                // Status dot: green when all mounts running, amber if partial, gray if none.
                var (dotColor, dotTint) = ComputeStatusDot();
                var dot = new Rectangle(12, (lblStatus.Height - 8) / 2, 8, 8);
                using (var tint = new SolidBrush(dotTint))
                    e.Graphics.FillEllipse(tint, Rectangle.Inflate(dot, 3, 3));
                using (var solid = new SolidBrush(dotColor))
                    e.Graphics.FillEllipse(solid, dot);
            };
            var btnQuit = new Button
            {
                Text = "Quit",
                Location = new Point(890, 602),
                Size = new Size(98, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                BackColor = Theme.SurfaceRaised,
                ForeColor = Theme.TextSecondary,
                Cursor = Cursors.Hand
            };
            btnQuit.FlatAppearance.BorderSize = 0;
            btnQuit.FlatAppearance.MouseOverBackColor = Theme.SurfaceSunken;
            btnQuit.FlatAppearance.MouseDownBackColor = Theme.SurfaceSunken;
            btnQuit.Click += (_, _) => { exitFromTray = true; Close(); };

            ApplyThemeToSettingsTab(tabPrereq);
            NativeTheme.ApplyTitleBarTheme(this);
            NativeTheme.ApplyDarkChromeIfDark(txtLog);
            NativeTheme.ApplyDarkChromeIfDark(railList);
            NativeTheme.ApplyDarkChromeIfDark(detailScroll);
            Controls.Add(tabHost);
            Controls.Add(lblStatus);
            Controls.Add(btnQuit);

            trayMenu = new ContextMenuStrip();
            trayMenu.Opening += TrayMenu_Opening;
            trayIcon = new NotifyIcon
            {
                Icon = AppIcon.Get(),
                Text = "RcloneMount",
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };

            Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };
            Load += MainForm_Load;
            FormClosing += MainForm_FormClosing;
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!allowVisible)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
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
            ShowInTaskbar = false;
            Hide();
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
                    var sub = new ToolStripMenuItem(label);

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
            trayMenu.Items.Add("Exit", null, (_, _) => { exitFromTray = true; Close(); });
        }

        private async Task MountFromTrayAsync(Mount m)
        {
            if (mountService is null || winFspInfo is null) return;
            if (!remotes.Any(r => r.Name == m.RemoteName)) return;
            try
            {
                await mountService.MountAsync(m);
                if (await WaitForDriveReadyAsync(m.DriveLetter, TimeSpan.FromSeconds(15)))
                    ShellNotifier.DriveAdded(m.DriveLetter);
                RefreshMountList();
            }
            catch (Exception ex)
            {
                trayIcon.ShowBalloonTip(3000, "Mount failed", ex.Message, ToolTipIcon.Error);
            }
        }

        private async Task UnmountFromTrayAsync(Mount m)
        {
            if (mountService is null) return;
            try
            {
                await mountService.UnmountAsync(m);
                ShellNotifier.DriveRemoved(m.DriveLetter);
                RefreshMountList();
            }
            catch (Exception ex)
            {
                trayIcon.ShowBalloonTip(3000, "Unmount failed", ex.Message, ToolTipIcon.Error);
            }
        }

        private void OpenDriveExternally(Mount m)
        {
            var root = m.DriveLetter.TrimEnd(':') + ":\\";
            if (!System.IO.Directory.Exists(root)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true }); } catch { }
        }

        private void ChkStartWithWindows_CheckedChanged(object? sender, EventArgs e)
        {
            if (suppressChangeEvents) return;
            try
            {
                var exePath = Environment.ProcessPath
                    ?? System.IO.Path.Combine(AppContext.BaseDirectory, "RcloneMount.exe");
                if (exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    exePath = System.IO.Path.ChangeExtension(exePath, ".exe");

                if (chkStartWithWindows.Checked) AutostartService.Enable(exePath);
                else AutostartService.Disable();
                SetStatus(chkStartWithWindows.Checked ? "Autostart enabled." : "Autostart disabled.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not change autostart: {ex.Message}", "Autostart", MessageBoxButtons.OK, MessageBoxIcon.Error);
                suppressChangeEvents = true;
                chkStartWithWindows.Checked = !chkStartWithWindows.Checked;
                suppressChangeEvents = false;
            }
        }

        private async void MainForm_Load(object? sender, EventArgs e)
        {
            var settings = await SettingsService.LoadAsync();
            savedRclonePath = settings.RcloneExePath;
            checkForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
            autoInstallUpdates = settings.AutoInstallUpdates;
            firstRunSetupDone = settings.FirstRunSetupDone;
            themePreferenceOverride = string.IsNullOrWhiteSpace(settings.ThemePreference) ? "Auto" : settings.ThemePreference;
            suppressChangeEvents = true;
            chkAutoCheck.Checked = checkForUpdatesOnStartup;
            chkAutoInstall.Checked = autoInstallUpdates;
            chkStartWithWindows.Checked = AutostartService.IsEnabled();
            switch (themePreferenceOverride.ToLowerInvariant())
            {
                case "light": themeRadioLight.Checked = true; break;
                case "dark":  themeRadioDark.Checked  = true; break;
                default:      themeRadioAuto.Checked  = true; break;
            }
            suppressChangeEvents = false;
            mounts.AddRange(settings.Mounts);
            RefreshMountList();

            DetectWinFsp();
            await DetectRcloneAsync();

            try
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                    AutostartService.MigrateLegacyIfNeeded(exePath);
            }
            catch { }

            UpdateActionButtons();
            UpdateStatusBar();

            var setupRan = startHidden ? false : await OfferFirstRunSetupAsync();

            if (checkForUpdatesOnStartup)
            {
                await CheckRcloneUpdateAsync(showMessages: false);
                await CheckWinFspUpdateAsync(showMessages: false);
                if (autoInstallUpdates && !setupRan)
                    await AutoApplyUpdatesAsync();
            }
        }

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
                    await PrerequisiteInstaller.InstallRcloneAsync(PrerequisiteInstaller.DefaultRcloneExePath, progress);
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

            if (!rcloneNeedsUpdate && !winFspNeedsUpdate) return;

            var progress = new Progress<string>(SetStatus);
            try
            {
                if (mountService is not null)
                {
                    SetStatus("Auto-update: unmounting all before applying updates...");
                    await mountService.UnmountAllAsync();
                }

                if (rcloneNeedsUpdate && rcloneInfo is not null)
                {
                    SetStatus("Auto-update: installing rclone...");
                    if (mountService is not null) { mountService.Dispose(); mountService = null; }
                    await PrerequisiteInstaller.InstallRcloneAsync(rcloneInfo.ExecutablePath, progress);
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
            }
        }

        private void DetectWinFsp()
        {
            winFspInfo = WinFspLocator.Locate();
            if (winFspInfo is null)
            {
                lblWinFspInstalled.Text = "WinFsp NOT installed — mounts will fail.";
                lblWinFspInstalled.ForeColor = Theme.Critical;
            }
            else
            {
                var v = string.IsNullOrEmpty(winFspInfo.Version) ? "installed" : "v" + winFspInfo.Version;
                lblWinFspInstalled.Text = $"Installed: WinFsp {v}   —   {winFspInfo.InstallDir}";
                lblWinFspInstalled.ForeColor = Theme.TextPrimary;
            }
        }

        private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (!exitFromTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            CancelAllRetries();
            if (mountService is not null)
                await mountService.UnmountAllAsync();
            trayIcon.Visible = false;
            trayIcon.Dispose();
        }

        private async Task DetectRcloneAsync()
        {
            SetStatus("Detecting rclone...");
            rcloneInfo = null;
            if (!string.IsNullOrWhiteSpace(savedRclonePath))
                rcloneInfo = await RcloneLocator.ExamineAsync(savedRclonePath);
            rcloneInfo ??= await RcloneLocator.LocateAsync();
            if (rcloneInfo is null)
            {
                lblRcloneInstalled.Text = "rclone.exe not found. Install rclone or locate it manually.";
                lblRcloneInstalled.ForeColor = Theme.Critical;
                configService = null;
                if (mountService is not null)
                {
                    await mountService.UnmountAllAsync();
                    mountService = null;
                }
                remotes = Array.Empty<RcloneRemote>();
                RebuildRail();
                SetStatus("rclone not detected.");
                UpdateActionButtons();
                return;
            }

            lblRcloneInstalled.Text = $"Installed: {rcloneInfo.Version}   —   {rcloneInfo.ExecutablePath}";
            lblRcloneInstalled.ForeColor = Theme.TextPrimary;
            configService = new RcloneConfigService(rcloneInfo.ExecutablePath);
            mountService = new RcloneMountService(rcloneInfo.ExecutablePath);
            mountService.MountExited += MountService_MountExited;

            await RefreshRemotesAsync();
            await RepairMountRemoteNamesAsync();
            UpdateActionButtons();
            await AutoMountOnStartupAsync();
        }

        // ── Update checks ─────────────────────────────────────────

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
            if (rcloneInfo is null) return;

            var choice = MessageBox.Show(
                $"Download the latest rclone Windows zip and replace {rcloneInfo.ExecutablePath}?\n\n" +
                "Any active mounts will be unmounted first.",
                "Install rclone update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes) return;

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
                await PrerequisiteInstaller.InstallRcloneAsync(rcloneInfo.ExecutablePath, progress);

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

        // ── Existing flow (unchanged behavior) ────────────────────

        private async Task AutoMountOnStartupAsync()
        {
            if (mountService is null || winFspInfo is null) return;

            foreach (var m in mounts.Where(m => m.AutoMount).ToList())
            {
                if (!remotes.Any(r => r.Name == m.RemoteName)) continue;
                if (mountService.IsMounted(m)) continue;
                if (RcloneMountService.IsDriveMountedExternally(m.DriveLetter)) continue;

                try
                {
                    SetStatus($"Auto-mounting {m.RemoteName} at {m.DriveLetter}...");
                    await mountService.MountAsync(m);
                    RefreshMountList();
                    if (await WaitForDriveReadyAsync(m.DriveLetter, TimeSpan.FromSeconds(15)))
                        ShellNotifier.DriveAdded(m.DriveLetter);
                    RefreshMountList();
                }
                catch (Exception ex)
                {
                    SetStatusError($"Auto-mount for {m.DriveLetter} failed.", ex);
                }
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

            if (repaired > 0)
            {
                await SaveSettingsAsync();
                RefreshMountList();
                SetStatus($"Repaired {repaired} mount(s) with mismatched remote names.");
            }
        }

        private void MountService_MountExited(object? sender, MountExitedEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                RefreshMountList();
                var mount = mounts.FirstOrDefault(m => m.Id == e.MountId);
                var label = mount is null ? e.MountId : $"{mount.RemoteName}:{mount.RemotePath} at {mount.DriveLetter}";
                if (e.WasIntentional)
                {
                    SetStatus($"Unmounted {label}.");
                }
                else
                {
                    lblStatus.Text = $"Mount {label} exited unexpectedly (code {e.ExitCode}).";
                    activityLog.Warn($"Mount {label} exited unexpectedly (code {e.ExitCode}).");
                }

                if (e.WasIntentional || e.ExitCode == 0)
                    return;

                if (mount is not null && mount.AutoMount)
                {
                    trayIcon.ShowBalloonTip(3000, "Mount dropped",
                        $"{mount.RemoteName} → {mount.DriveLetter} exited. Attempting to remount...",
                        ToolTipIcon.Warning);
                    ScheduleRetry(mount);
                    return;
                }

                var tail = e.RecentOutput.Count == 0
                    ? "(no output captured)"
                    : string.Join(Environment.NewLine, e.RecentOutput);
                using var box = new MountLogForm($"Mount failed (exit {e.ExitCode})", label, tail);
                box.ShowDialog(this);
            }));
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
                    if (IsHandleCreated) BeginInvoke(new Action(() =>
                        SetStatus($"Auto-remount {mount.RemoteName} → {mount.DriveLetter} (attempt {attempt})...")));

                    await mountService.MountAsync(mount);
                    if (await WaitForDriveReadyAsync(mount.DriveLetter, TimeSpan.FromSeconds(15)))
                    {
                        ShellNotifier.DriveAdded(mount.DriveLetter);
                        if (IsHandleCreated) BeginInvoke(new Action(() =>
                        {
                            RefreshMountList();
                            trayIcon.ShowBalloonTip(3000, "Mount recovered",
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

        private async Task RefreshRemotesAsync()
        {
            if (configService is null)
                return;

            try
            {
                remotes = await configService.ListRemotesAsync();
                RebuildRail();
                SetStatus($"Loaded {remotes.Count} remote(s).");
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to read remotes: {ex.Message}");
            }
            finally
            {
                UpdateActionButtons();
            }
        }

        private async void BtnLocateRclone_Click(object? sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog { Title = "Locate rclone.exe", Filter = "rclone.exe|rclone.exe|Executables|*.exe" };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            var info = await RcloneLocator.ExamineAsync(dlg.FileName);
            if (info is null)
            {
                MessageBox.Show("That file does not look like a working rclone.exe (version check failed).",
                    "Locate rclone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            rcloneInfo = info;
            lblRcloneInstalled.Text = $"Installed: {info.Version}   —   {info.ExecutablePath}";
            lblRcloneInstalled.ForeColor = Theme.TextPrimary;
            configService = new RcloneConfigService(info.ExecutablePath);
            if (mountService is not null)
            {
                await mountService.UnmountAllAsync();
                mountService.Dispose();
            }
            mountService = new RcloneMountService(info.ExecutablePath);
            mountService.MountExited += MountService_MountExited;

            savedRclonePath = info.ExecutablePath;
            await SaveSettingsAsync();
            await RefreshRemotesAsync();
        }

        private Task SaveSettingsAsync() =>
            SettingsService.SaveAsync(new AppSettings
            {
                RcloneExePath = savedRclonePath,
                CheckForUpdatesOnStartup = checkForUpdatesOnStartup,
                AutoInstallUpdates = autoInstallUpdates,
                FirstRunSetupDone = firstRunSetupDone,
                ThemePreference = themePreferenceOverride,
                Mounts = mounts
            });

        private void BtnConfigureRemotes_Click(object? sender, EventArgs e)
        {
            if (configService is null)
            {
                MessageBox.Show("Locate rclone.exe first.", "rclone missing", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            configService.LaunchInteractiveConfig();
            SetStatus("Interactive rclone config launched. Click Refresh after saving changes.");
        }

        private RcloneRemote? GetSelectedRemote()
        {
            if (selectedRemoteName is null) return null;
            return remotes.FirstOrDefault(r => string.Equals(r.Name, selectedRemoteName, StringComparison.Ordinal));
        }

        private async void BtnAddRemote_Click(object? sender, EventArgs e)
        {
            if (configService is null)
            {
                MessageBox.Show("Locate rclone.exe first.", "rclone missing", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            IReadOnlyList<RcloneProvider> providers;
            try
            {
                SetStatus("Loading providers...");
                providers = await configService.ListProvidersAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not load provider list: {ex.Message}\n\nFalling back to CLI.",
                    "Providers", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                configService.LaunchInteractiveConfig();
                return;
            }
            SetStatus($"Loaded {providers.Count} providers.");

            using var picker = new ChooseProviderForm(providers);
            if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is null)
                return;

            var takenNames = remotes.Select(r => r.Name);
            using var editor = new EditRemoteForm(picker.Selected, existing: null, existingName: null, takenNames);
            if (editor.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                SetStatus($"Creating remote {editor.RemoteName}...");
                await configService.CreateRemoteAsync(editor.RemoteName, picker.Selected.Name, editor.ResultValues, obscurePasswords: true);
                await RefreshRemotesAsync();
                SetStatus($"Created remote {editor.RemoteName}.");
                if (editor.RequiresOAuth)
                    await RunOAuthAsync(picker.Selected.Name, editor.RemoteName);
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

            IReadOnlyList<RcloneProvider> providers;
            try { providers = await configService.ListProvidersAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not load provider list: {ex.Message}", "Providers", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var provider = providers.FirstOrDefault(p => string.Equals(p.Name, selected.Type, StringComparison.Ordinal));
            if (provider is null)
            {
                MessageBox.Show($"Provider type '{selected.Type}' is not known to this rclone build.",
                    "Unknown provider", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var others = remotes.Where(r => !string.Equals(r.Name, selected.Name, StringComparison.Ordinal)).Select(r => r.Name);
            using var editor = new EditRemoteForm(provider, selected.Parameters, selected.Name, others);
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

            if (MessageBox.Show($"Delete remote '{selected.Name}' from rclone.conf?{extra}",
                "Delete remote", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            try
            {
                await configService.DeleteRemoteAsync(selected.Name);
                await RefreshRemotesAsync();
                SetStatus($"Deleted remote {selected.Name}.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Delete failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task RunOAuthAsync(string providerType, string remoteName)
        {
            if (configService is null) return;

            var choice = MessageBox.Show(
                $"'{remoteName}' uses OAuth. Open your browser to complete sign-in now?",
                "OAuth required", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes)
            {
                SetStatus($"OAuth for {remoteName} skipped — remote will not mount until authorized.");
                return;
            }

            using var dlg = new OAuthProgressForm(configService, providerType);
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
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Saving OAuth token failed: {ex.Message}", "OAuth", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SetStatus("OAuth token save failed.");
                }
            }
            else if (result == DialogResult.Abort)
            {
                MessageBox.Show($"OAuth failed: {dlg.ErrorMessage}\n\nYou can re-run authorization by editing the remote, or use the CLI fallback.",
                    "OAuth", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus($"OAuth failed for {remoteName}.");
            }
            else
            {
                SetStatus($"OAuth for {remoteName} cancelled.");
            }
        }

        private async void BtnAddMount_Click(object? sender, EventArgs e)
        {
            if (remotes.Count == 0)
            {
                MessageBox.Show("Configure at least one remote first.", "No remotes", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var dlg = new AddMountForm(remotes);
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
            using var dlg = new AddMountForm(remotes, selected);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result is not null)
            {
                selected.RemoteName = dlg.Result.RemoteName;
                selected.RemotePath = dlg.Result.RemotePath;
                selected.DriveLetter = dlg.Result.DriveLetter;
                selected.VfsCacheMode = dlg.Result.VfsCacheMode;
                selected.ExtraFlags = dlg.Result.ExtraFlags;
                selected.AutoMount = dlg.Result.AutoMount;

                if (!selected.AutoMount) CancelRetry(selected.Id);

                RefreshDetail();
                await SaveSettingsAsync();
                SetStatus("Mount updated.");
            }
        }

        private async Task RemoveMountAsync(Mount selected)
        {
            if (MessageBox.Show($"Remove mount for {selected.RemoteName} at {selected.DriveLetter}?",
                "Remove mount", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            CancelRetry(selected.Id);

            if (mountService is not null && mountService.IsMounted(selected))
                await mountService.UnmountAsync(selected);

            mounts.Remove(selected);
            RefreshDetail();
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
                SetStatus($"Mounting {selected.DriveLetter}...");
                await mountService.MountAsync(selected);
                RefreshMountList();

                var ready = await WaitForDriveReadyAsync(selected.DriveLetter, TimeSpan.FromSeconds(15));
                RefreshMountList();
                if (ready)
                {
                    ShellNotifier.DriveAdded(selected.DriveLetter);
                    SetStatus($"Mounted {selected.RemoteName} at {selected.DriveLetter}.");
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

        private async Task UnmountAsync(Mount selected)
        {
            if (mountService is null) return;

            CancelRetry(selected.Id);

            try
            {
                SetStatus($"Unmounting {selected.DriveLetter}...");
                await mountService.UnmountAsync(selected);
                ShellNotifier.DriveRemoved(selected.DriveLetter);

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

        private MountRowStatus GetMountStatusEnum(Mount m)
        {
            if (mountService is not null && mountService.IsMounted(m)) return MountRowStatus.Mounted;
            if (RcloneMountService.IsDriveMountedExternally(m.DriveLetter)) return MountRowStatus.External;
            return MountRowStatus.Stopped;
        }

        private void UpdateActionButtons()
        {
            var hasRclone = configService is not null;
            var selectedRemote = GetSelectedRemote();
            toolbarAddRemoteBtn.Enabled = hasRclone;
            toolbarRefreshBtn.Enabled = hasRclone;
            toolbarAdvancedBtn.Enabled = hasRclone;
            detailEditRemoteBtn.Enabled = hasRclone && selectedRemote is not null;
            detailDeleteRemoteBtn.Enabled = hasRclone && selectedRemote is not null;
            addMountBtn.Enabled = hasRclone && selectedRemote is not null;
            UpdateStatusBar();
        }

        // Called by RefreshRemotesAsync — rebuilds the left rail's RemoteRow list.
        private void RebuildRail()
        {
            railList.SuspendLayout();
            foreach (Control c in railList.Controls) c.Dispose();
            railList.Controls.Clear();

            foreach (var r in remotes)
            {
                var forRemote = mounts.Where(m => string.Equals(m.RemoteName, r.Name, StringComparison.Ordinal)).ToList();
                var running = forRemote.Count(m => mountService is not null && mountService.IsMounted(m));
                var row = new RemoteRow { Width = railList.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4 };
                row.Bind(r, forRemote.Count, running);
                var captured = r.Name;
                row.Clicked += (_, _) => { selectedRemoteName = captured; RebuildRailSelection(); RefreshDetail(); };
                railList.Controls.Add(row);
            }
            railList.ResumeLayout();

            railCountLabel.Text = remotes.Count.ToString();
            if (selectedRemoteName is not null && !remotes.Any(r => r.Name == selectedRemoteName))
                selectedRemoteName = null;
            if (selectedRemoteName is null && remotes.Count > 0)
                selectedRemoteName = remotes[0].Name;

            RebuildRailSelection();
            RefreshDetail();
        }

        private void RebuildRailSelection()
        {
            foreach (Control c in railList.Controls)
                if (c is RemoteRow rr)
                    rr.IsSelected = string.Equals(rr.Remote?.Name, selectedRemoteName, StringComparison.Ordinal);
        }

        // Called whenever the selected remote or the mount set changes — repaints the right pane.
        private void RefreshDetail()
        {
            var remote = GetSelectedRemote();

            if (remote is null)
            {
                detailHeader.Visible = false;
                settingsCard.Visible = false;
                mountsCard.Visible = false;
                emptyStateLabel.Visible = true;
                UpdateActionButtons();
                return;
            }

            emptyStateLabel.Visible = false;
            detailHeader.Visible = true;
            settingsCard.Visible = true;
            mountsCard.Visible = true;

            detailBadge.Invalidate();
            detailTitleLabel.Text = remote.Name;

            var forRemote = mounts.Where(m => string.Equals(m.RemoteName, remote.Name, StringComparison.Ordinal)).ToList();
            var running = forRemote.Count(m => mountService is not null && mountService.IsMounted(m));
            var summary = forRemote.Count == 0
                ? $"{remote.Type} · no mounts yet"
                : $"{remote.Type} · {running} of {forRemote.Count} mount(s) running";
            detailSubtitleLabel.Text = summary;

            RebuildSettingsCard(remote);
            RebuildMountsCard(remote, forRemote);
            UpdateActionButtons();
        }

        private void RebuildSettingsCard(RcloneRemote remote)
        {
            settingsCardBody.SuspendLayout();
            foreach (Control c in settingsCardBody.Controls) c.Dispose();
            settingsCardBody.Controls.Clear();

            var pairs = new List<(string Key, string Value)>
            {
                ("Name", remote.Name),
                ("Type", remote.Type)
            };
            foreach (var kv in remote.Parameters)
            {
                if (pairs.Count >= 8) break;
                var name = kv.Key.ToLowerInvariant();
                if (name is "token" or "client_secret" or "password" or "pass" or "secret_access_key" or "service_account_credentials")
                    continue;
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                pairs.Add((kv.Key, kv.Value));
            }

            int y = 8;
            foreach (var (k, v) in pairs)
            {
                var lbl = new Label
                {
                    Text = k.ToUpperInvariant(),
                    Location = new Point(12, y + 2),
                    Size = new Size(150, 18),
                    Font = new Font(Font.FontFamily, 8.25f, FontStyle.Bold),
                    ForeColor = Theme.TextTertiary
                };
                var val = new Label
                {
                    Text = v,
                    Location = new Point(170, y),
                    Size = new Size(settingsCardBody.Width - 190, 22),
                    Font = new Font(Font.FontFamily, 9f),
                    ForeColor = Theme.TextPrimary,
                    AutoEllipsis = true,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                settingsCardBody.Controls.Add(lbl);
                settingsCardBody.Controls.Add(val);
                y += 26;
            }
            settingsCardBody.Height = Math.Max(48, y + 10);
            settingsCard.Height = settingsCardBody.Height + 42; // header + padding
            settingsCardBody.ResumeLayout();
        }

        private void RebuildMountsCard(RcloneRemote remote, List<Mount> forRemote)
        {
            mountsCardBody.SuspendLayout();
            foreach (Control c in mountsCardBody.Controls) c.Dispose();
            mountsCardBody.Controls.Clear();

            if (forRemote.Count == 0)
            {
                var empty = new Label
                {
                    Text = "No mounts for this remote yet.  Click \"+ Add mount\" to create one.",
                    Dock = DockStyle.Top,
                    Height = 60,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Theme.TextTertiary,
                    Font = new Font(Font.FontFamily, 9f)
                };
                mountsCardBody.Controls.Add(empty);
                mountsCardBody.Height = 60;
            }
            else
            {
                int y = 0;
                foreach (var m in forRemote)
                {
                    var status = GetMountStatusEnum(m);
                    var row = new MountRow { Location = new Point(0, y), Width = mountsCardBody.Width, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
                    var captured = m;
                    row.MountRequested   += async (_, _) => await MountAsync(captured);
                    row.UnmountRequested += async (_, _) => await UnmountAsync(captured);
                    row.OpenRequested    += (_, _) => OpenDrive(captured);
                    row.EditRequested    += async (_, _) => await EditMountAsync(captured);
                    row.RemoveRequested  += async (_, _) => await RemoveMountAsync(captured);
                    row.Bind(m, status);
                    mountsCardBody.Controls.Add(row);
                    y += row.Height;
                }
                mountsCardBody.Height = Math.Max(60, y);
            }
            mountsCard.Height = mountsCardBody.Height + 42;
            mountsCardBody.ResumeLayout();
        }

        private void RefreshMountList()
        {
            // Legacy name — now updates rail counts/dots and repaints the detail pane.
            UpdateRailStatus();
            RefreshDetail();
            UpdateStatusBar();
        }

        private void UpdateRailStatus()
        {
            foreach (Control c in railList.Controls)
            {
                if (c is not RemoteRow row || row.Remote is null) continue;
                var forRemote = mounts.Where(m => string.Equals(m.RemoteName, row.Remote.Name, StringComparison.Ordinal)).ToList();
                var running = forRemote.Count(m => mountService is not null && mountService.IsMounted(m));
                row.Bind(row.Remote, forRemote.Count, running);
            }
        }

        // Helpers used by the master-detail layout above.
        private Button MakeToolbarButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = false,
                Size = new Size(90, 28),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 1;
            if (primary)
            {
                b.BackColor = Theme.Accent;
                b.ForeColor = Theme.TextOnAccent;
                b.FlatAppearance.BorderColor = Theme.Accent;
                b.FlatAppearance.MouseOverBackColor = Theme.AccentHover;
                b.FlatAppearance.MouseDownBackColor = Theme.AccentHover;
            }
            else
            {
                b.BackColor = Theme.SurfaceRaised;
                b.ForeColor = Theme.TextPrimary;
                b.FlatAppearance.BorderColor = Theme.BorderEmphasis;
                b.FlatAppearance.MouseOverBackColor = Theme.SurfaceSunken;
                b.FlatAppearance.MouseDownBackColor = Theme.SurfaceSunken;
            }
            return b;
        }

        private Panel MakeCard(string title, string subtitle)
        {
            var card = new Panel
            {
                BackColor = Theme.SurfaceRaised,
                Margin = new Padding(0, 12, 0, 0),
                Width = 800,
                Height = 100
            };
            // fake border via child border panels; keeps corners visible on all DPI
            card.Paint += (_, e) =>
            {
                using var pen = new Pen(Theme.BorderSubtle);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            var header = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Theme.SurfaceRaised };
            var headerBorder = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.BorderSubtle };
            var lblTitle = new Label
            {
                Text = title,
                Location = new Point(14, 14),
                AutoSize = true,
                Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary
            };
            var lblSubtitle = new Label
            {
                Text = subtitle,
                Location = new Point(14 + TextRenderer.MeasureText(title, new Font(Font.FontFamily, 9.5f, FontStyle.Bold)).Width + 10, 15),
                AutoSize = true,
                Font = new Font(Font.FontFamily, 8.25f),
                ForeColor = Theme.TextTertiary
            };
            header.Controls.Add(lblTitle);
            header.Controls.Add(lblSubtitle);
            header.Controls.Add(headerBorder);

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceRaised, Padding = new Padding(0, 4, 0, 4) };
            card.Controls.Add(body);
            card.Controls.Add(header);
            card.Tag = body;
            return card;
        }

        private Button MakePillTab(string text, bool isActive)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(88, 24),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.75f, FontStyle.Regular),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 2, 0)
            };
            b.FlatAppearance.BorderSize = 0;
            SetPillActive(b, isActive);
            return b;
        }

        private void SetPillActive(Button pill, bool active)
        {
            if (active)
            {
                // Use RailActiveBg (a tinted step above raised surface) so the active pill
                // reads clearly against SurfaceSunken in both themes. In dark mode the
                // SurfaceRaised-vs-SurfaceSunken gap is too small on its own.
                pill.BackColor = Theme.IsDark ? Theme.RailActiveBg : Theme.SurfaceRaised;
                pill.ForeColor = Theme.TextPrimary;
                pill.Font = new Font(pill.Font, FontStyle.Bold);
                pill.FlatAppearance.MouseOverBackColor = pill.BackColor;
                pill.FlatAppearance.MouseDownBackColor = pill.BackColor;
            }
            else
            {
                pill.BackColor = Theme.SurfaceSunken;
                pill.ForeColor = Theme.TextSecondary;
                pill.Font = new Font(pill.Font, FontStyle.Regular);
                pill.FlatAppearance.MouseOverBackColor = Theme.IsDark ? Theme.SurfaceRaised : Theme.RailHoverBg;
                pill.FlatAppearance.MouseDownBackColor = pill.FlatAppearance.MouseOverBackColor;
            }
        }

        // Live theme swap: snapshot palette before/after, build a color remap, walk the entire
        // control tree replacing any occurrence of an old palette color with the corresponding
        // new one. Custom-painted controls (RemoteRow, MountRow, cards, chips, pill tabs) read
        // Theme.X inside their OnPaint methods so they update automatically on Invalidate.
        private void LiveSwitchTheme(ThemeMode target)
        {
            var oldColors = Theme.SnapshotAll();
            Theme.SetMode(target);
            var newColors = Theme.SnapshotAll();

            var map = new Dictionary<Color, Color>();
            for (int i = 0; i < oldColors.Length; i++)
            {
                if (oldColors[i] == newColors[i]) continue;
                if (!map.ContainsKey(oldColors[i])) map[oldColors[i]] = newColors[i];
            }

            SuspendLayout();
            RemapColorsRecursive(this, map);
            NativeTheme.ApplyTitleBarTheme(this);
            ResumeLayout(true);
            Invalidate(true);
        }

        private static void RemapColorsRecursive(Control c, Dictionary<Color, Color> map)
        {
            if (map.TryGetValue(c.BackColor, out var newBg)) c.BackColor = newBg;
            if (map.TryGetValue(c.ForeColor, out var newFg)) c.ForeColor = newFg;

            if (c is Button btn)
            {
                if (map.TryGetValue(btn.FlatAppearance.BorderColor, out var b))
                    btn.FlatAppearance.BorderColor = b;
                if (map.TryGetValue(btn.FlatAppearance.MouseOverBackColor, out var mo))
                    btn.FlatAppearance.MouseOverBackColor = mo;
                if (map.TryGetValue(btn.FlatAppearance.MouseDownBackColor, out var md))
                    btn.FlatAppearance.MouseDownBackColor = md;
            }

            foreach (Control child in c.Controls)
                RemapColorsRecursive(child, map);

            c.Invalidate();
        }

        // Hooks a button so its disabled state paints predictably in dark mode: WinForms's
        // default disabled rendering just dims the ForeColor via SystemColors, which produces
        // near-invisible text on dark surfaces. We instead move to a sunken bg + tertiary text
        // when disabled, and restore the enabled colors when re-enabled.
        private static void ApplyEnabledStyling(Button btn)
        {
            // Read palette from Theme on every sync so a live theme swap picks up correctly.
            void Sync()
            {
                if (btn.Enabled)
                {
                    btn.BackColor = Theme.SurfaceRaised;
                    btn.ForeColor = Theme.TextPrimary;
                    btn.FlatAppearance.BorderColor = Theme.BorderEmphasis;
                }
                else
                {
                    btn.BackColor = Theme.SurfaceRaised;
                    btn.ForeColor = Theme.TextSecondary;
                    btn.FlatAppearance.BorderColor = Theme.BorderSubtle;
                }
                btn.Invalidate();
            }

            // WinForms flat-button disabled paint ignores ForeColor and dims whatever it
            // computes to something near-black on dark surfaces. Override with our own paint
            // that respects TextSecondary as the disabled label color.
            btn.Paint += (_, e) =>
            {
                if (btn.Enabled) return;
                using var bg = new SolidBrush(btn.BackColor);
                e.Graphics.FillRectangle(bg, 0, 0, btn.Width, btn.Height);
                using var pen = new Pen(btn.FlatAppearance.BorderColor);
                e.Graphics.DrawRectangle(pen, 0, 0, btn.Width - 1, btn.Height - 1);
                TextRenderer.DrawText(e.Graphics, btn.Text, btn.Font,
                    new Rectangle(0, 0, btn.Width, btn.Height), Theme.TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.HidePrefix);
            };
            btn.EnabledChanged += (_, _) => Sync();
            Sync();
        }

        // A themed replacement for GroupBox: opaque bordered panel with a bold title label
        // near the top-left. Same coordinate system as GroupBox so child Location values
        // don't need to be re-figured.
        private Panel MakeSettingsCard(string title, Point location, Size size, AnchorStyles anchor)
        {
            var p = new Panel
            {
                Location = location,
                Size = size,
                Anchor = anchor,
                BackColor = Theme.SurfaceRaised
            };
            p.Paint += (_, e) =>
            {
                using var pen = new Pen(Theme.BorderSubtle);
                e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
            };
            var lblTitle = new Label
            {
                Text = title,
                Font = new Font(Font.FontFamily, 9.75f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                Location = new Point(12, 10),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            p.Controls.Add(lblTitle);
            return p;
        }

        // Iterates the Settings tab's control tree and applies theme colors to labels,
        // checkboxes, and group boxes. Buttons keep their own explicit theming.
        private void ApplyThemeToSettingsTab(Control root)
        {
            foreach (Control c in root.Controls)
            {
                switch (c)
                {
                    case GroupBox gb:
                        gb.BackColor = Theme.SurfaceBase;
                        gb.ForeColor = Theme.TextSecondary;
                        break;
                    case CheckBox cb:
                        cb.BackColor = Theme.SurfaceBase;
                        cb.ForeColor = Theme.TextPrimary;
                        break;
                    case Label lbl:
                        lbl.BackColor = Color.Transparent;
                        lbl.ForeColor = Theme.TextPrimary;
                        break;
                    case Button btn:
                        // Only re-theme buttons that still carry the OS default look.
                        if (btn.FlatStyle != FlatStyle.Flat)
                        {
                            btn.FlatStyle = FlatStyle.Flat;
                            btn.UseVisualStyleBackColor = false;
                            btn.BackColor = Theme.SurfaceRaised;
                            btn.ForeColor = Theme.TextPrimary;
                            btn.FlatAppearance.BorderColor = Theme.BorderEmphasis;
                        }
                        ApplyEnabledStyling(btn);
                        break;
                }
                if (c.HasChildren) ApplyThemeToSettingsTab(c);
            }
        }

        private Button MakeIconButton(string glyph, string tooltip)
        {
            var b = new Button
            {
                Text = glyph,
                Size = new Size(28, 26),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                BackColor = Theme.SurfaceBase,
                ForeColor = Theme.TextSecondary,
                Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Regular),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 4, 0),
                FlatAppearance = { BorderSize = 0 }
            };
            b.FlatAppearance.MouseOverBackColor = Theme.RailHoverBg;
            b.FlatAppearance.MouseDownBackColor = Theme.RailHoverBg;
            new ToolTip().SetToolTip(b, tooltip);
            return b;
        }

        private void FilterRail(string query)
        {
            query = (query ?? "").Trim();
            foreach (Control c in railList.Controls)
            {
                if (c is not RemoteRow row || row.Remote is null) continue;
                var visible = query.Length == 0
                    || row.Remote.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || row.Remote.Type.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                row.Visible = visible;
            }
        }

        private string CurrentRemoteType() => GetSelectedRemote()?.Type ?? "";

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
                RefreshDetail();
                SetStatus($"Added mount {dlg.Result.RemoteName} → {dlg.Result.DriveLetter}.");
            }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        // Transient activity messages now flow only to the Log tab. The bottom bar shows a
        // steady summary (running mounts, tool versions) via UpdateStatusBar. Users tracking
        // in-flight actions can watch the Log tab.
        private void SetStatus(string message)
        {
            activityLog.Info(message);
        }

        private void SetStatusError(string statusMessage, Exception? ex = null)
        {
            var detail = ex is null ? statusMessage : $"{statusMessage} — {ex.Message}";
            activityLog.Error(detail);
        }

        private (Color color, Color tint) ComputeStatusDot()
        {
            if (mounts.Count == 0 || mountService is null)
                return (Theme.Idle, Theme.IdleTint);
            var running = mounts.Count(m => mountService.IsMounted(m));
            if (running == mounts.Count) return (Theme.Good, Theme.GoodTint);
            if (running == 0)           return (Theme.Idle, Theme.IdleTint);
            return (Theme.Warn, Theme.WarnTint);
        }

        private void UpdateStatusBar()
        {
            if (lblStatus is null) return;
            var parts = new List<string>();

            if (mountService is null || mounts.Count == 0)
                parts.Add("No mounts configured");
            else
            {
                var running = mounts.Count(m => mountService.IsMounted(m));
                parts.Add($"{running} of {mounts.Count} mount{(mounts.Count == 1 ? "" : "s")} running");
            }

            if (rcloneInfo is not null && !string.IsNullOrEmpty(rcloneInfo.Version))
                parts.Add($"rclone {rcloneInfo.Version}");
            else
                parts.Add("rclone missing");

            if (winFspInfo is not null && !string.IsNullOrEmpty(winFspInfo.Version))
                parts.Add($"WinFsp {winFspInfo.Version}");
            else
                parts.Add("WinFsp missing");

            parts.Add($"v{AppVersion}");

            lblStatus.Text = string.Join(" · ", parts);
            lblStatus.Invalidate();
        }

        private static string AppVersion
        {
            get
            {
                var v = typeof(Program).Assembly.GetName().Version;
                return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        private void AppendLogLine(ActivityEntry entry)
        {
            if (txtLog.IsDisposed) return;
            var line = entry.ToString() + Environment.NewLine;
            txtLog.AppendText(line);
            if (chkAutoScroll.Checked)
            {
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            }
        }
    }
}
