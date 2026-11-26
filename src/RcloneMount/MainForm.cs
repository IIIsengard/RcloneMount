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
    public partial class MainForm : Form
    {
        private readonly List<Mount> mounts = new();
        private string? savedRclonePath;
        private bool checkForUpdatesOnStartup = true;
        private bool autoInstallUpdates;
        private bool notificationsEnabled = true;
        private bool firstRunSetupDone;
        private IReadOnlyList<RcloneRemote> remotes = Array.Empty<RcloneRemote>();

        private RcloneInfo? rcloneInfo;
        private WinFspInfo? winFspInfo;
        private LatestRelease? latestRclone;
        private LatestRelease? latestWinFsp;
        private RcloneConfigService? configService;
        private RcloneMountService? mountService;
        private RcloneJobService? jobService;
        private JobQueueService? jobQueue;
        private readonly List<TransferJob> jobs = new();
        private RcloneMount.Services.RcloneStatsPoller? statsPoller;
        private System.Windows.Forms.Timer? statsTimer;
        private double aggregateBytesPerSecond;
        private double aggregateDownBytesPerSecond;
        private double aggregateUpBytesPerSecond;
        private string statusCenterText = "↕ 0 B/s";

        // Lifetime cumulative totals — persisted across sessions.
        private Services.TransferTotals lifetimeTotals = new();
        private DateTime lastTotalsSaveUtc;
        private static readonly TimeSpan TotalsSaveInterval = TimeSpan.FromSeconds(30);
        private string statusRightText = "";
        private Panel? tabTransfers;
        private ListBox? historyList;
        private Label? historyEmpty;
        private Label? historyHeader;
        private Label? activeHeader;
        private FlowLayoutPanel? jobsFlow;
        private Label? jobsEmpty;
        private Label? jobsHeaderLabel;
        private Button? btnAddJob;
        private FlowLayoutPanel? queueFlow;
        private Label? queueEmpty;
        private IReadOnlyList<Services.TransferInfo> lastTransfers = Array.Empty<Services.TransferInfo>();
        // In-memory tail of the on-disk transfers.jsonl. Bounded — the on-disk file
        // holds more, this is just what the UI shows without a re-read per paint.
        private readonly List<Services.TransferHistoryEntry> transferHistory = new();
        private const int TransferHistoryDisplayCap = 200;

        // Silent-detach watchdog: mount IDs currently flagged as detached (rclone process
        // alive but the drive letter isn't reachable), plus a per-mount counter of
        // consecutive unhealthy polls we've seen. We flip to "detached" only after a few
        // hits in a row so a single blip doesn't spam the tray with false alarms.
        private readonly HashSet<string> detachedMountIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> unhealthyPollHits = new(StringComparer.Ordinal);
        private const int DetachHitThreshold = 3;
        // Detached mounts whose auto-recover kill waits for their pending uploads. One entry per
        // episode (until the mount is healthy again or killed), so it logs and notifies once.
        private readonly Dictionary<string, RecoverDeferral> recoverDeferred = new(StringComparer.Ordinal);
        private record struct RecoverDeferral(DateTime SinceUtc, bool Requeued, bool Notified);
        private static readonly TimeSpan RecoverDeferralNotifyAfter = TimeSpan.FromMinutes(10);
        // The open "Uploads still pending" prompt, so a second Exit can bring it to the front.
        private PendingUploadsDialog? uploadsDialog;

        // When the user's configured drive letter is already taken at mount time we
        // auto-fall-back to the next free letter and mutate mount.DriveLetter for the
        // session so the rest of the codebase (RC lookups, ShellNotifier, drive-ready
        // polls) transparently uses the letter that's actually live. This dict is the
        // shadow of the ORIGINAL configured letter so we can restore it on unmount /
        // process exit and — critically — save it in place of the live letter when we
        // serialize settings so the user's intent isn't overwritten on disk.
        private readonly Dictionary<string, string> configuredDriveLetters = new(StringComparer.Ordinal);

        // Mounts tab — master-detail. Fields drop `readonly` because they're now
        // assigned by BuildMountsTab (a non-constructor method); same pattern as the
        // other tab extractions.
        private FlowLayoutPanel railList = null!;
        private Label railCountLabel = null!;
        private Panel detailScroll = null!;
        private FlowLayoutPanel detailStack = null!;
        private Panel detailHeader = null!;
        private Panel detailBadge = null!;
        private Label detailTitleLabel = null!;
        private Label detailSubtitleLabel = null!;
        private LinkLabel clientIdWarning = null!;
        private Button detailReconnectRemoteBtn = null!;
        private Button detailEditRemoteBtn = null!;
        private Button detailDeleteRemoteBtn = null!;
        private Button detailDuplicateRemoteBtn = null!;
        private Button detailTestRemoteBtn = null!;
        // Provider types whose schema exposes a `token` option — i.e. OAuth backends.
        // Populated by LoadProvidersAsync so BuildMountsTab can show/hide the Reconnect
        // button per-selection without hitting rclone on every rail click.
        private readonly HashSet<string> oauthProviderTypes = new(StringComparer.OrdinalIgnoreCase);
        private Panel settingsCard = null!;
        private Panel settingsCardBody = null!;
        private Action<bool> settingsCardSetExpanded = null!;
        private Panel mountsCard = null!;
        private Panel mountsCardBody = null!;
        private FlowLayoutPanel mountsListFlow = null!;
        private Button addMountBtn = null!;
        private Label emptyStateLabel = null!;
        private Button toolbarAddRemoteBtn = null!;
        private Button toolbarRefreshBtn = null!;
        private Button toolbarAdvancedBtn = null!;
        private string? selectedRemoteName;
        // Which remote's settings card body is currently laid out — used to skip
        // rebuilding it on every mount-state tick (settings only change when the remote
        // itself changes).
        private string? renderedSettingsRemoteName;

        // Prerequisites tab
        // These fields drop `readonly` because they're now assigned by BuildSettingsTab
        // (a non-constructor method). They were never truly immutable — the readonly
        // was aspirational — and dropping it is the price of moving construction to
        // a partial file.
        private Label lblRcloneInstalled = null!;
        private Label lblRcloneLatest = null!;
        private Button btnLocateRclone = null!;
        private Button btnCheckRclone = null!;
        private Button btnInstallRclone = null!;
        private Label lblWinFspInstalled = null!;
        private Label lblWinFspLatest = null!;
        private Button btnCheckWinFsp = null!;
        private Button btnInstallWinFsp = null!;
        // App-update UI. `btnInstallAppUpdate` is enabled only when a newer version
        // is available; clicking opens the GitHub Releases page (self-install is
        // Path C — Velopack — deferred, see [[project-beta-readiness]]).
        private Label lblAppInstalled = null!;
        private Label lblAppLatest = null!;
        private Button btnCheckAppUpdate = null!;
        private Button btnInstallAppUpdate = null!;
        private ToggleButton chkAutoCheck = null!;
        private ToggleButton chkAutoInstall = null!;
        private ToggleButton chkStartWithWindows = null!;
        private ToggleButton chkNotifications = null!;
        private ToggleButton themeRadioAuto = null!;
        private ToggleButton themeRadioLight = null!;
        private ToggleButton themeRadioDark = null!;
        private string themePreferenceOverride = "Auto";

        // Settings backup card — populated on Settings tab activation and after saves.
        private ThemedDropDown backupsCombo = null!;
        private Label backupsCountLabel = null!;
        private TextBox backupsFolderTextbox = null!;
        private string? backupsFolderOverride;
        private Label? lifetimeTotalsLabel;
        private Label? lifetimeSinceLabel;

        private readonly NotifyIcon trayIcon;
        private readonly ContextMenuStrip trayMenu;
        private readonly bool startHidden;
        private bool allowVisible;
        private bool exitFromTray;
        private bool shutdownStarted;
        private bool shutdownComplete;
        private bool suppressChangeEvents;
        private bool loadStarted;
        private bool loadKickoffScheduled;

        // Per-mount retry loops (auto-remount on unexpected exit)
        private readonly Dictionary<string, CancellationTokenSource> retryCts = new();
        private static readonly int[] RetryDelaysMs = { 2000, 5000, 15000, 30000, 60000 };

        private readonly Label lblStatus;

        // One shared ToolTip for the rail's icon buttons. Using a per-button ToolTip lets
        // the previous button's tip linger when the mouse slides onto a neighbour, since
        // each component only manages hide on its own button.
        private readonly ToolTip iconToolTip = new();

        // Log tab
        private readonly ActivityLog activityLog = new(capacity: 2000, persistPath: SettingsService.ActivityLogPath, retention: TimeSpan.FromDays(30));
        // RichTextBox rather than TextBox — RichTextBox auto-hides its horizontal
        // scrollbar when content fits, TextBox always shows both when set to Both.
        private RichTextBox txtLog = null!;
        private ToggleButton btnAutoScroll = null!;
        // Grouped-errors view: swappable with txtLog on the Log tab. ListView shows one
        // row per unique warn/error message with its count and first/last-seen times.
        private ListView lvErrorGroups = null!;
        private ToggleButton btnGroupErrors = null!;
        private System.Windows.Forms.Timer? groupRefreshTimer;

        public MainForm() : this(false) { }

        public MainForm(bool startHidden)
        {
            this.startHidden = startHidden;
            allowVisible = !startHidden;
            Icon = AppIcon.Get();
            Text = "RcloneMount";
            ClientSize = new Size(LayoutMetrics.WindowClientWidth, LayoutMetrics.WindowClientHeight);
            StartPosition = FormStartPosition.CenterScreen;
            // Minimum window size guarantees the two-column Prereq layout fits. Width is
            // ClientSize width + ~16 for typical Windows chrome, so the client area stays
            // at least as wide as the initial ClientSize width. Users can still resize
            // larger.
            MinimumSize = new Size(LayoutMetrics.WindowClientWidth + 16, 560);
            BackColor = Theme.SurfaceBase;
            ForeColor = Theme.TextPrimary;

            // Mockup-style pill tabs. Three panels stacked in a content host; the pill bar
            // toggles visibility. This replaces the native TabControl chrome (which is impossible
            // to theme in dark mode).
            var tabHost = new Panel { Location = new Point(LayoutMetrics.TabPadding, LayoutMetrics.TabPadding), Size = new Size(LayoutMetrics.TabHostWidth, 578), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, BackColor = Theme.SurfaceBase };

            var pillBar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.SurfaceRaised };
            var pillBarBorder = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.BorderSubtle };
            pillBar.Controls.Add(pillBarBorder);

            // Brand: app icon + name on the left.
            var brand = new Panel { Location = new Point(14, 8), Size = new Size(180, 30), BackColor = Color.Transparent };
            var brandIcon = new Panel { Location = new Point(0, 3), Size = new Size(24, 24), BackColor = Color.Transparent };
            brandIcon.Paint += (_, e) =>
            {
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.DrawIcon(AppIcon.Get(), new Rectangle(0, 0, 24, 24));
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

            var pillMounts    = MakePillTab("Mounts",    isActive: true);
            var pillLog       = MakePillTab("Log",       isActive: false);
            var pillTransfers = MakePillTab("Transfers", isActive: false);
            var pillJobs      = MakePillTab("Jobs",      isActive: false);
            var pillSettings  = MakePillTab("Settings",  isActive: false);
            var pillAbout     = MakePillTab("About",     isActive: false);
            pillGroup.Controls.Add(pillMounts);
            pillGroup.Controls.Add(pillTransfers);
            pillGroup.Controls.Add(pillJobs);
            pillGroup.Controls.Add(pillLog);
            pillGroup.Controls.Add(pillSettings);
            pillGroup.Controls.Add(pillAbout);
            pillBar.Controls.Add(pillGroup);

            var contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase };
            var tabMounts    = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase };
            var tabLog       = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase, Visible = false };
            var tabTransfers = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase, Visible = false };
            var tabJobs      = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase, Visible = false };
            var tabPrereq    = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase, Visible = false, AutoScroll = true };
            var tabAbout     = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase, Visible = false, AutoScroll = true };
            contentHost.Controls.Add(tabMounts);
            contentHost.Controls.Add(tabLog);
            contentHost.Controls.Add(tabTransfers);
            contentHost.Controls.Add(tabJobs);
            contentHost.Controls.Add(tabPrereq);
            contentHost.Controls.Add(tabAbout);
            this.tabTransfers = tabTransfers;

            tabHost.Controls.Add(contentHost);
            tabHost.Controls.Add(pillBar);

            void SwitchTab(int index)
            {
                tabMounts.Visible    = index == 0;
                tabLog.Visible       = index == 1;
                tabTransfers.Visible = index == 2;
                tabJobs.Visible      = index == 5;
                tabPrereq.Visible    = index == 3;
                tabAbout.Visible     = index == 4;
                SetPillActive(pillMounts,    index == 0);
                SetPillActive(pillLog,       index == 1);
                SetPillActive(pillTransfers, index == 2);
                SetPillActive(pillJobs,      index == 5);
                SetPillActive(pillSettings,  index == 3);
                SetPillActive(pillAbout,     index == 4);
            }
            pillMounts.Click    += (_, _) => { SwitchTab(0); contentHost.Focus(); };
            pillLog.Click       += (_, _) => { SwitchTab(1); contentHost.Focus(); ScrollLogToEnd(); };
            pillTransfers.Click += (_, _) => { SwitchTab(2); contentHost.Focus(); };
            pillJobs.Click      += (_, _) => { SwitchTab(5); contentHost.Focus(); };
            pillSettings.Click  += (_, _) => { SwitchTab(3); contentHost.Focus(); RefreshBackupsList(); };
            pillAbout.Click     += (_, _) => { SwitchTab(4); contentHost.Focus(); };

            // ── Transfers tab ─────────────────────────
            BuildTransfersTab(tabTransfers);

            // ── Jobs tab (saved templates) ────────────
            BuildJobsTab(tabJobs);

            // ── Mounts tab (master-detail) ─────────────────────────
            BuildMountsTab(tabMounts);

            // ── Log tab ────────────────────────────────────────────
            BuildLogTab(tabLog);

            // ── Settings tab (sectioned) ───────────────────────────
            BuildSettingsTab(tabPrereq);

            // ── About tab ──────────────────────────────────────────
            BuildAboutTab(tabAbout);

            // Status strip is a single label that paints three text regions itself — left
            // (mount count), center (transfer rate), right (app version). Overlaying separate
            // labels turned out to be fragile with Anchor+Location math, so we owner-draw.
            lblStatus = new Label { Text = "", UseMnemonic = false, Location = new Point(12, 605), Size = new Size(990, 24), BorderStyle = BorderStyle.None, TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, BackColor = Theme.SurfaceRaised, ForeColor = Theme.TextSecondary, Padding = new Padding(28, 0, 120, 0) };
            statusRightText = $"RcloneMount v{AppVersion}";
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

                // Center + right regions painted here so the strip stays a single label —
                // left region is handled by lblStatus's own Text/TextAlign.
                var centerRect = new Rectangle(0, 0, lblStatus.Width, lblStatus.Height);
                var rateColor = aggregateBytesPerSecond > 0 ? Theme.Good : lblStatus.ForeColor;
                TextRenderer.DrawText(e.Graphics, statusCenterText, lblStatus.Font, centerRect, rateColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                var rightRect = new Rectangle(0, 0, lblStatus.Width - 12, lblStatus.Height);
                TextRenderer.DrawText(e.Graphics, statusRightText, lblStatus.Font, rightRect, lblStatus.ForeColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
            var btnQuit = new Button
            {
                Text = "Quit",
                Location = new Point(1010, 605),
                Size = new Size(98, 24),
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
            NativeTheme.ApplyDarkChromeIfDark(tabPrereq);
            NativeTheme.ApplyDarkChromeIfDark(tabAbout);

            // Double-buffer the mount-area containers so the multi-refresh sequence during
            // startup (initial paint → post-rclone-detect rebuild → repair pass → per-mount
            // auto-mount refreshes) blends into a single frame instead of flashing 3-4×.
            // Also the cards themselves + their bodies: switching services rebuilds the
            // settings-card body (dispose+recreate all children) and reconciles the
            // mounts-card children — buffering those containers turns the churn into a
            // single frame.
            EnableDoubleBuffering(mountsCardBody);
            EnableDoubleBuffering(mountsCard);
            EnableDoubleBuffering(settingsCardBody);
            EnableDoubleBuffering(settingsCard);
            EnableDoubleBuffering(detailScroll);
            EnableDoubleBuffering(detailStack);
            EnableDoubleBuffering(railList);
            Controls.Add(tabHost);
            Controls.Add(lblStatus);
            Controls.Add(btnQuit);

            RoundedButtons.Apply(this);

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

            if (startHidden)
            {
                // Startup-to-tray path: the form is never shown, so the Load event
                // won't fire on its own — and Application.Run does not reliably
                // toggle Visible=true on our main form here, so the SetVisibleCore
                // fallback can't be counted on either. Force the handle now and
                // queue MainForm_Load ourselves so auto-mounts run whether or not
                // the window is ever opened.
                if (!IsHandleCreated) CreateHandle();
                loadKickoffScheduled = true;
                BeginInvoke(new Action(() => MainForm_Load(this, EventArgs.Empty)));
            }
        }

        private void ChkStartWithWindows_CheckedChanged(object? sender, EventArgs e)
        {
            if (suppressChangeEvents) return;
            // Bound to ToggleButton.CheckedChanged — the button has already flipped its
            // own state (via AutoToggle). We commit the OS-side change; on failure we
            // revert silently to keep the visual in sync.
            var desired = chkStartWithWindows.Checked;
            try
            {
                var exePath = Environment.ProcessPath
                    ?? System.IO.Path.Combine(AppContext.BaseDirectory, "RcloneMount.exe");
                if (exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    exePath = System.IO.Path.ChangeExtension(exePath, ".exe");

                if (desired) AutostartService.Enable(exePath);
                else AutostartService.Disable();
                SetStatus(desired ? "Autostart enabled." : "Autostart disabled.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not change autostart: {ex.Message}", "Autostart", MessageBoxButtons.OK, MessageBoxIcon.Error);
                chkStartWithWindows.SetCheckedSilent(!desired);
            }
        }

        private async void MainForm_Load(object? sender, EventArgs e)
        {
            if (loadStarted) return;
            loadStarted = true;
            try
            {
                await MainForm_LoadCore();
            }
            catch (Exception ex)
            {
                HandleUnexpectedError("startup", ex);
            }
        }

        private async Task MainForm_LoadCore()
        {
            var settings = await SettingsService.LoadAsync();
            if (SettingsService.LastQuarantinedPath is { } bad)
            {
                activityLog.Error($"settings.json could not be parsed; moved to {bad}.");
                MessageBox.Show(
                    $"Your settings file could not be read, so RcloneMount started with empty settings.\n\n" +
                    $"The unreadable file was kept as:\n{bad}\n\n" +
                    "To get your mounts and jobs back, open the Settings tab and use \"Restore Selected\" to pick a backup.",
                    "Settings could not be loaded", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (SettingsService.LoadReadError is { } err)
            {
                activityLog.Error($"settings.json could not be read: {err}. Saving is disabled this session.");
                MessageBox.Show(
                    $"Your settings file exists but could not be opened:\n{err}\n\n" +
                    "RcloneMount started with empty settings and will NOT save changes this session, " +
                    "so your file is not overwritten. Restart RcloneMount once the file is accessible.",
                    "Settings could not be loaded", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            savedRclonePath = settings.RcloneExePath;
            checkForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
            autoInstallUpdates = settings.AutoInstallUpdates;
            notificationsEnabled = settings.NotificationsEnabled;
            firstRunSetupDone = settings.FirstRunSetupDone;
            themePreferenceOverride = string.IsNullOrWhiteSpace(settings.ThemePreference) ? "Auto" : settings.ThemePreference;
            HookSystemThemeChanges();
            HookAutoRecoverSignals();
            StartCliServer();
            backupsFolderOverride = settings.BackupsFolderOverride;
            lifetimeTotals = Services.TransferTotalsService.Load();
            RefreshLifetimeTotalsLabel();
            suppressChangeEvents = true;
            chkAutoCheck.SetCheckedSilent(checkForUpdatesOnStartup);
            chkAutoInstall.SetCheckedSilent(autoInstallUpdates);
            chkNotifications.SetCheckedSilent(notificationsEnabled);
            chkStartWithWindows.SetCheckedSilent(AutostartService.IsEnabled());
            var pref = themePreferenceOverride.ToLowerInvariant();
            themeRadioAuto.SetCheckedSilent(pref != "light" && pref != "dark");
            themeRadioLight.SetCheckedSilent(pref == "light");
            themeRadioDark.SetCheckedSilent(pref == "dark");
            suppressChangeEvents = false;
            mounts.AddRange(settings.Mounts);
            jobs.AddRange(settings.Jobs);
            // BuildTransfersTab ran during construction with jobs still empty, so
            // the JOBS panel is showing its empty-state label. Repaint it now that
            // the saved jobs have loaded from disk.
            RefreshJobsList();
            // No initial RefreshMountList() here — DetectRcloneAsync (below) will run
            // RebuildRail + RefreshDetail once the remote list is loaded, and painting
            // an empty-state pass first would just add a startup flicker.

            DetectWinFsp();
            await DetectRcloneAsync();

            try
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    AutostartService.MigrateLegacyIfNeeded(exePath);
                    if (AutostartService.RepairIfStale(exePath) is { } stale)
                        activityLog.Info($"Start with Windows pointed to a missing program ({stale}); re-pointed it to {exePath}.");
                }
            }
            catch { }

            UpdateActionButtons();
            UpdateStatusBar();

            var setupRan = startHidden ? false : await OfferFirstRunSetupAsync();

            if (checkForUpdatesOnStartup)
            {
                await CheckRcloneUpdateAsync(showMessages: false);
                await CheckWinFspUpdateAsync(showMessages: false);
                await CheckAppUpdateAsync(showMessages: false);
                if (autoInstallUpdates && !setupRan)
                    await AutoApplyUpdatesAsync();
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

            if (shutdownComplete) return;
            var busy = CurrentBusyReason();
            switch (PowerGuard.Closing(e.CloseReason == CloseReason.WindowsShutDown, Visible, busy))
            {
                // Busy with the window open: refuse the shutdown query so Windows lists RcloneMount with
                // its reason; the user can still pick "Shut down anyway" (WndProc's WM_ENDSESSION).
                case ClosingStep.Hold:
                    e.Cancel = true;
                    PowerGuard.Apply(Handle, busy);
                    activityLog.Warn($"Windows shutdown held: {busy}");
                    _ = Services.TransferTotalsService.SaveAsync(lifetimeTotals); // in case Windows ends us without WM_ENDSESSION
                    return;
                // Hidden in the tray, or idle: answer yes and change nothing. WndProc tears down on
                // WM_ENDSESSION(true); a cancelled shutdown (wParam 0) leaves the app running as it was.
                case ClosingStep.TearDownAtEndSession:
                    return;
            }

            // async void: without this the form closes at the first await and the process
            // exits before mounts unmount. Cancel, finish the teardown, then close for real.
            e.Cancel = true;
            if (shutdownStarted) { uploadsDialog?.Activate(); return; }
            shutdownStarted = true; // a second Exit while the prompt is open must not start teardown
            var proceed = false;
            try { proceed = await UploadsGuardAsync(mounts, "Exit", interactive: true); }
            catch (Exception ex) { HandleUnexpectedError("Exit stopped: the pending-uploads check failed", ex); }
            if (!proceed)
            {
                shutdownStarted = false;
                exitFromTray = false;
                return;
            }

            StopBackgroundWork();
            if (mountService is not null)
                await mountService.UnmountAllAsync();
            try { await Services.TransferTotalsService.SaveAsync(lifetimeTotals); } catch { }
            trayIcon.Visible = false;
            trayIcon.Dispose();
            // Running transfer jobs are not cancelled: their processes die with ours (job
            // object), their InFlight flag stays set, and next launch offers to resume them.
            PowerGuard.Apply(Handle, null);
            shutdownComplete = true;
            BeginInvoke(new Action(Close));
        }

        // Safe to run twice: the WM_ENDSESSION path may follow a teardown already in progress.
        private void StopBackgroundWork()
        {
            CancelAllRetries();
            UnhookSystemThemeChanges();
            UnhookAutoRecoverSignals();
            StopCliServer();
            // Detach event subscriptions before we start tearing down services so a late
            // append from mountService.UnmountAllAsync doesn't race with disposal.
            try { activityLog.Appended -= OnActivityLogAppended; } catch { }
            if (statsTimer is not null) { statsTimer.Stop(); statsTimer.Dispose(); statsTimer = null; }
            if (refreshDebounce is not null) { refreshDebounce.Stop(); refreshDebounce.Dispose(); refreshDebounce = null; }
            StopScheduler();
        }

        private const int WM_ENDSESSION = 0x0016;

        // WinForms raises no FormClosing for WM_ENDSESSION, and FormClosing never tears down for a
        // Windows shutdown (a cancelled one would leave a zombie), so every shutdown teardown runs
        // here: after "Shut down anyway" on a held shutdown, or when we did not hold. Windows ends
        // the process a few seconds after this returns, so it is best-effort and time-bounded.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ENDSESSION && m.WParam != IntPtr.Zero && !shutdownComplete)
            {
                StopBackgroundWork();
                // Task.Run: no continuation needs this (blocked) UI thread, so Wait cannot deadlock.
                try { Task.Run(() => Services.TransferTotalsService.SaveAsync(lifetimeTotals)).Wait(TimeSpan.FromSeconds(1)); } catch { }
                if (mountService is { } ms)
                    try { Task.Run(ms.UnmountAllAsync).Wait(TimeSpan.FromSeconds(3)); } catch { }
                try { trayIcon.Visible = false; } catch { }
                PowerGuard.Apply(Handle, null);
                shutdownComplete = true;
            }
            base.WndProc(ref m);
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
                SettingsService.SetRcloneExePath(null);
                if (mountService is not null)
                {
                    await mountService.UnmountAllAsync();
                    mountService = null;
                }
                jobService = null;
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
            mountService.Remotes = () => remotes;
            jobService = new RcloneJobService(rcloneInfo.ExecutablePath);
            NewQueueIfIdle();
            // Let SettingsService resolve rclone.conf via `rclone config file` — respects
            // RCLONE_CONFIG and --config overrides instead of assuming the default path.
            SettingsService.SetRcloneExePath(rcloneInfo.ExecutablePath);
            mountService.MountExited += MountService_MountExited;
            StartStatsPolling();

            // Repair prunes mounts whose remote is gone; an unread (e.g. still locked) config
            // would make every mount look orphaned and delete them all from settings.json.
            if (await RefreshRemotesAsync())
                await RepairMountRemoteNamesAsync();
            UpdateActionButtons();
            // Fire-and-forget: seeds the OAuth-type index so the Reconnect button appears
            // on first rail selection without needing the user to open Add/Edit first.
            _ = LoadProvidersAsync(showErrors: false);
            await AutoMountOnStartupAsync();
            await ResumeInterruptedJobsAsync();
            StartScheduler();
        }

        // ── Update checks ─────────────────────────────────────────

        // ── Existing flow (unchanged behavior) ────────────────────


        // Keep a queue that still has active runs, or they vanish from the UI mid-transfer.
        private void NewQueueIfIdle()
        {
            if (jobQueue is not null)
            {
                if (jobQueue.Snapshot().Any(r => r.IsActive)) return;
                jobQueue.RunsChanged -= OnJobQueueChanged;
                jobQueue.RunFinished -= OnJobRunFinished;
            }
            jobQueue = new JobQueueService(jobService!, activityLog);
            jobQueue.CheckBeforeRun = run => InvokeAsync(async _ => await CheckRunRemotesAsync(run));
            jobQueue.RunsChanged += OnJobQueueChanged;
            jobQueue.RunFinished += OnJobRunFinished;
        }

        private void MountService_MountExited(object? sender, MountExitedEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                var mount = mounts.FirstOrDefault(m => m.Id == e.MountId);
                var label = mount is null ? e.MountId : $"{mount.RemoteName}:{mount.RemotePath} at {mount.DriveLetter}";
                // Process is gone — flip back to the user's configured letter so the
                // row shows their intent, not the fallback we happened to grab. The
                // retry loop will resolve a fresh letter on its own next attempt.
                if (mount is not null) RestoreConfiguredDriveLetter(mount);
                RefreshMountList();
                if (e.WasIntentional)
                {
                    SetStatus($"Unmounted {label}.");
                }
                else
                {
                    lblStatus.Text = $"Mount {label} exited unexpectedly (code {e.ExitCode}).";
                    activityLog.Warn($"Mount {label} exited unexpectedly (code {e.ExitCode}).");
                }

                // UnmountedByUser: a crash that arrives after the user's own unmount needs no remount or popup.
                if (e.WasIntentional || e.ExitCode == 0 || mount?.UnmountedByUser == true)
                    return;

                if (mount is not null && mount.AutoMount)
                {
                    Notify("Mount dropped",
                        $"{mount.RemoteName} → {mount.DriveLetter} exited. Attempting to remount...",
                        ToolTipIcon.Warning);
                    ScheduleRetry(mount);
                    return;
                }

                var tail = e.RecentOutput.Count == 0
                    ? "(no output captured)"
                    : string.Join(Environment.NewLine, e.RecentOutput);

                // Always fire a tray balloon so the user knows even when the window is
                // hidden. Only pop the modal log dialog when the window is actually
                // visible — otherwise a modal on an invisible form is disorienting.
                Notify("Mount failed",
                    $"{label} exited unexpectedly (code {e.ExitCode}).",
                    ToolTipIcon.Error, 5000);

                if (Visible && WindowState != FormWindowState.Minimized)
                {
                    using var box = new MountLogForm($"Mount failed (exit {e.ExitCode})", label, tail);
                    box.ShowDialog(this);
                }
            }));
        }

        private async void BtnLocateRclone_Click(object? sender, EventArgs e)
        {
            try
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
                if (!await UploadsGuardAsync(mounts, "Switch", interactive: true)) return;

                rcloneInfo = info;
                lblRcloneInstalled.Text = $"Installed: {info.Version}   —   {info.ExecutablePath}";
                lblRcloneInstalled.ForeColor = Theme.TextPrimary;
                configService = new RcloneConfigService(info.ExecutablePath);
                SettingsService.SetRcloneExePath(info.ExecutablePath); // else rclone.conf resolves via the old (or no) exe
                if (mountService is not null)
                {
                    await mountService.UnmountAllAsync();
                    mountService.Dispose();
                }
                mountService = new RcloneMountService(info.ExecutablePath);
                mountService.Remotes = () => remotes;
                mountService.MountExited += MountService_MountExited;
                jobService = new RcloneJobService(info.ExecutablePath);
                NewQueueIfIdle();
                StartStatsPolling();

                savedRclonePath = info.ExecutablePath;
                await SaveSettingsAsync();
                await RefreshRemotesAsync();
            }
            catch (Exception ex)
            {
                HandleUnexpectedError("locating rclone", ex);
            }
        }

        // Set while saves keep failing, so only the first failure of a streak raises a notice.
        private bool saveFailureShown;
        // The same for skipped automatic backups, which a locked rclone.conf causes on every save.
        private bool backupSkipLogged;

        // False when the settings could not be written. Never throws: the failure is logged and
        // shown here, so callers that discard the task are covered too.
        private async Task<bool> SaveSettingsAsync()
        {
            // Any mount currently running on a session-fallback letter has its
            // DriveLetter mutated to the letter it actually got. Serializing that would
            // silently overwrite the user's configured choice on disk, so save a copy that
            // carries the original. The live object is never touched: the CLI pipe and the
            // stats poller read it from other threads.
            var toSave = mounts.Select(m =>
                configuredDriveLetters.TryGetValue(m.Id, out var original) && !string.IsNullOrEmpty(original)
                    ? m.WithDriveLetter(original) : m).ToList();
            try
            {
                await SettingsService.SaveAsync(new AppSettings
                {
                    RcloneExePath = savedRclonePath,
                    CheckForUpdatesOnStartup = checkForUpdatesOnStartup,
                    AutoInstallUpdates = autoInstallUpdates,
                    NotificationsEnabled = notificationsEnabled,
                    FirstRunSetupDone = firstRunSetupDone,
                    ThemePreference = themePreferenceOverride,
                    BackupsFolderOverride = backupsFolderOverride,
                    Mounts = toSave,
                    Jobs = jobs,
                });
                saveFailureShown = false;
                var skipped = SettingsService.BackupError;
                if (skipped is not null && !backupSkipLogged)
                    activityLog.Warn($"Settings were saved, but the automatic backup was skipped: {skipped}");
                backupSkipLogged = skipped is not null;
                return true;
            }
            catch (Exception ex)
            {
                if (saveFailureShown) activityLog.Error($"Settings still not saved: {ex.Message}");
                else HandleUnexpectedError("Settings could not be saved", ex);
                saveFailureShown = true;
                return false;
            }
        }

        // Tray balloon that honours the Notifications toggle. Callers should route
        // user-facing status here rather than calling trayIcon.ShowBalloonTip directly.
        // The uncaught-exception path in HandleUnexpectedError intentionally bypasses
        // this — serious app errors should always surface.
        private void Notify(string title, string text, ToolTipIcon icon, int timeoutMs = 3000)
        {
            if (!notificationsEnabled) return;
            if (trayIcon is null) return;
            trayIcon.ShowBalloonTip(timeoutMs, title, text, icon);
        }

        // Rebuilds the backup dropdown on the Settings tab. Called when the user opens
        // the tab; no need to keep it live-updated on every save.
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

        // Bolts a chevron + click-to-toggle behavior onto a card built by MakeCard. Returns a
        // setter delegate: pass true to expand (show body), false to collapse (header only).
        // Card starts collapsed. Rendered chevron is a small right-arrow when collapsed and
        // down-arrow when expanded.
        private Action<bool> MakeCollapsible(Panel card)
        {
            var body = (Panel)card.Tag!;
            var header = card.Controls.OfType<Panel>().First(p => p.Dock == DockStyle.Top);

            const int headerHeight = 42;
            const int rightPadding = 14;
            var chevron = new Label
            {
                Text = "▶", // ▶
                AutoSize = false,
                Size = new Size(20, headerHeight),
                Font = new Font(Font.FontFamily, 8f),
                ForeColor = Theme.TextSecondary,
                TextAlign = ContentAlignment.MiddleCenter,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            void PositionChevron() =>
                chevron.Location = new Point(header.Width - chevron.Width - rightPadding, 0);
            header.Controls.Add(chevron);
            PositionChevron();
            header.Resize += (_, _) => PositionChevron();

            var expanded = false;
            var collapsedHeight = headerHeight + 2;
            var expandedHeight  = card.Height;

            int MeasureBodyContentHeight()
            {
                var maxBottom = 0;
                foreach (Control c in body.Controls)
                    if (c.Visible) maxBottom = Math.Max(maxBottom, c.Bottom);
                return maxBottom;
            }

            void Apply(bool wantExpanded)
            {
                expanded = wantExpanded;
                chevron.Text = expanded ? "▼" : "▶"; // ▼ / ▶
                body.Visible = expanded;
                if (expanded)
                {
                    // Measure the body's actual content on expand — the snapshot may be
                    // stale if the card was rebuilt while collapsed (body is DockStyle.Fill,
                    // so its own SizeChanged only tracks the card's height, not the content).
                    var contentBottom = MeasureBodyContentHeight();
                    if (contentBottom > 0)
                        expandedHeight = headerHeight + contentBottom + body.Padding.Bottom + 4;
                    card.Height = expandedHeight;
                }
                else
                {
                    if (body.Visible) expandedHeight = card.Height;
                    card.Height = collapsedHeight;
                }
            }

            // Snapshot the "natural" height whenever the body resizes while expanded — that
            // way rebuilds with more/fewer rows still restore to the right size next expand.
            body.SizeChanged += (_, _) =>
            {
                if (expanded) expandedHeight = card.Height;
            };

            void Toggle(object? _, EventArgs __) => Apply(!expanded);
            header.Click += Toggle;
            // Wire every header Label (title, subtitle, chevron) to toggle. The chevron is
            // itself a Label, so we skip the explicit chevron.Click subscription to avoid
            // firing Toggle twice per click — which caused an expand-then-collapse "flash".
            foreach (Control c in header.Controls)
                if (c is Label) c.Click += Toggle;

            Apply(false);
            return Apply;
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
                Margin = new Padding(0, 0, 2, 0),
                TabStop = false,
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
            // Owner-painted border needs the whole rect invalidated when the card is
            // resized (anchored L+R inside a scrolling tab) — otherwise the old right/
            // bottom edge lingers as a ghost line at the previous position. Panel doesn't
            // expose SetStyle publicly so we force a repaint on Resize.
            p.Resize += (_, _) => p.Invalidate();
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

        private Button MakeFlatButton(string text, Point location, Size size)
        {
            var b = new Button
            {
                Text = text,
                Location = location,
                Size = size,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                BackColor = Theme.SurfaceRaised,
                ForeColor = Theme.TextPrimary,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Theme.BorderEmphasis;
            b.FlatAppearance.MouseOverBackColor = Theme.SurfaceSunken;
            b.FlatAppearance.MouseDownBackColor = Theme.SurfaceSunken;
            return b;
        }

        // Enable OptimizedDoubleBuffer + AllPaintingInWmPaint on an arbitrary Control.
        // Panel/FlowLayoutPanel don't expose DoubleBuffered publicly, but the underlying
        // Control.SetStyle exists — we reach it via reflection so we don't have to
        // subclass every container that needs to stop flickering on rebuild.
        private static void EnableDoubleBuffering(Control c)
        {
            try
            {
                var setStyle = typeof(Control).GetMethod("SetStyle",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                setStyle?.Invoke(c, new object[]
                {
                    ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.UserPaint,
                    true
                });
            }
            catch { /* not fatal — worst case we get the flicker we started with */ }
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
                TabStop = false,
                FlatAppearance = { BorderSize = 0 }
            };
            b.FlatAppearance.MouseOverBackColor = Theme.RailHoverBg;
            b.FlatAppearance.MouseDownBackColor = Theme.RailHoverBg;
            iconToolTip.SetToolTip(b, tooltip);
            b.AccessibleName = tooltip; // otherwise screen readers read the glyph
            return b;
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

        // Safety net for async void event handlers. WinForms event handlers must be
        // async void; an uncaught exception in one of them terminates the process.
        // Every handler that could realistically throw wraps its body in try/catch and
        // routes here so the user sees a survivable message instead of the app vanishing.
        private void HandleUnexpectedError(string context, Exception ex)
        {
            try { activityLog.Error($"{context}: {ex.Message}"); } catch { }
            try
            {
                trayIcon?.ShowBalloonTip(3500, "RcloneMount error",
                    $"{context}: {ex.Message}", ToolTipIcon.Error);
            }
            catch { }
        }

        // Mounts whose RemoteName still resolves to a known remote — orphaned mounts
        // (left behind after the user deleted the remote) don't render in the rail or
        // detail pane, so counting them would give a status bar that doesn't match
        // what's on screen.
        private System.Collections.Generic.List<Models.Mount> LiveMounts()
        {
            var knownRemotes = new System.Collections.Generic.HashSet<string>(
                remotes.Select(r => r.Name), StringComparer.Ordinal);
            return mounts.Where(m => knownRemotes.Contains(m.RemoteName)).ToList();
        }

        private (Color color, Color tint) ComputeStatusDot()
        {
            var live = LiveMounts();
            if (live.Count == 0 || mountService is null)
                return (Theme.Idle, Theme.IdleTint);
            var running = live.Count(m => mountService.IsMounted(m));
            if (running == live.Count) return (Theme.Good, Theme.GoodTint);
            if (running == 0)          return (Theme.Idle, Theme.IdleTint);
            return (Theme.Warn, Theme.WarnTint);
        }

        private void UpdateStatusBar()
        {
            if (lblStatus is null) return;

            string leftText;
            var live = LiveMounts();
            if (mountService is null || live.Count == 0)
                leftText = "No mounts configured";
            else
            {
                var running = live.Count(m => mountService.IsMounted(m));
                leftText = $"{running} of {live.Count} mount{(live.Count == 1 ? "" : "s")}";
            }
            lblStatus.Text = leftText;
            // When any direction has meaningful movement (≥ 1 KB/s to avoid twitchy 0),
            // show ↓ down / ↑ up split. Otherwise fall back to the aggregate indicator.
            var hasSplit = aggregateDownBytesPerSecond >= 1024 || aggregateUpBytesPerSecond >= 1024;
            statusCenterText = hasSplit
                ? $"↓ {Services.RcloneStatsPoller.FormatRate(aggregateDownBytesPerSecond)}   ↑ {Services.RcloneStatsPoller.FormatRate(aggregateUpBytesPerSecond)}"
                : $"↕ {Services.RcloneStatsPoller.FormatRate(aggregateBytesPerSecond)}";
            statusRightText  = $"RcloneMount v{AppVersion}";
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

        // Named so we can unsubscribe cleanly on close; anonymous lambdas can't be
        // detached from the ActivityLog subscription list.
    }
}
