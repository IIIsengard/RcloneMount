using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.UI;

namespace RcloneMount
{
    // Master-detail rebuilders for the Mounts tab: the left rail (RemoteRow list),
    // the right detail pane (settings card + mounts card), and the utility methods
    // that keep those in sync. Also carries the debounced RefreshMountList
    // coalescer so multiple state-change triggers collapse into one repaint.
    public partial class MainForm
    {
        private Button railTerminalBtn = null!;

        private void BuildMountsTab(Panel tabMounts)
        {
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
                Text = "SERVICES",
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
                PlaceholderText = "Search services"
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
            var railAddBtn     = MakeIconButton("+",  "Add service");
            var railRemoveBtn  = MakeIconButton("−",  "Remove selected service");
            var railRefreshBtn = MakeIconButton("⟳", "Refresh service list");
            var railMoreBtn    = MakeIconButton("≡", "Open rclone config (CLI)");
            railTerminalBtn    = MakeIconButton(">_", "rclone terminal (run any rclone command)");
            railAddBtn.Click     += BtnAddRemote_Click;
            railRemoveBtn.Click  += BtnDeleteRemote_Click;
            railRefreshBtn.Click += async (_, _) => await RefreshRemotesAsync();
            railMoreBtn.Click    += BtnConfigureRemotes_Click;
            railTerminalBtn.Click += BtnRcloneTerminal_Click;
            railFooterFlow.Controls.Add(railAddBtn);
            railFooterFlow.Controls.Add(railRemoveBtn);
            railFooterFlow.Controls.Add(railRefreshBtn);
            railFooterFlow.Controls.Add(new Panel { Width = 4, Height = 1, BackColor = Color.Transparent });
            railFooterFlow.Controls.Add(railMoreBtn);
            railFooterFlow.Controls.Add(railTerminalBtn);
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
            detailScroll = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase, AutoScroll = true, Padding = new Padding(24, 16, 0, 24) };

            // Header row: badge · title/subtitle · action buttons
            detailHeader = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.SurfaceBase };
            detailBadge = new Panel { Location = new Point(0, 10), Size = new Size(44, 44), BackColor = Color.Transparent };
            detailBadge.Paint += (_, e) => ProviderVisuals.DrawBadge(e.Graphics, new Rectangle(0, 0, detailBadge.Width, detailBadge.Height), GetSelectedRemote());

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

            detailTestRemoteBtn = MakeToolbarButton("Test", primary: false);
            detailTestRemoteBtn.Size = new Size(64, 28);
            detailTestRemoteBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            detailTestRemoteBtn.Click += BtnTestRemote_Click;
            detailDuplicateRemoteBtn = MakeToolbarButton("Duplicate", primary: false);
            detailDuplicateRemoteBtn.Size = new Size(92, 28);
            detailDuplicateRemoteBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            detailDuplicateRemoteBtn.Click += BtnDuplicateRemote_Click;
            detailReconnectRemoteBtn = MakeToolbarButton("Reconnect", primary: false);
            detailReconnectRemoteBtn.Size = new Size(92, 28);
            detailReconnectRemoteBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            detailReconnectRemoteBtn.Click += BtnReconnectRemote_Click;
            detailReconnectRemoteBtn.Visible = false;
            detailEditRemoteBtn = MakeToolbarButton("Edit", primary: false);
            detailEditRemoteBtn.Size = new Size(72, 28);
            detailEditRemoteBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            detailEditRemoteBtn.Click += BtnEditRemote_Click;
            detailDeleteRemoteBtn = MakeToolbarButton("Remove", primary: false);
            detailDeleteRemoteBtn.Size = new Size(72, 28);
            detailDeleteRemoteBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            detailDeleteRemoteBtn.Click += BtnDeleteRemote_Click;

            detailHeader.Controls.Add(detailBadge);
            detailHeader.Controls.Add(detailTitleLabel);
            detailHeader.Controls.Add(detailSubtitleLabel);
            detailHeader.Controls.Add(detailTestRemoteBtn);
            detailHeader.Controls.Add(detailDuplicateRemoteBtn);
            detailHeader.Controls.Add(detailReconnectRemoteBtn);
            detailHeader.Controls.Add(detailEditRemoteBtn);
            detailHeader.Controls.Add(detailDeleteRemoteBtn);
            detailHeader.Resize += (_, _) =>
            {
                // Reading order right-to-left: Remove · Edit · Reconnect · Duplicate · Test.
                detailDeleteRemoteBtn.Location    = new Point(detailHeader.Width - detailDeleteRemoteBtn.Width, 18);
                detailEditRemoteBtn.Location      = new Point(detailDeleteRemoteBtn.Left - detailEditRemoteBtn.Width - 6, 18);
                detailReconnectRemoteBtn.Location = new Point(detailEditRemoteBtn.Left - detailReconnectRemoteBtn.Width - 6, 18);
                // When Reconnect is hidden its Left is still meaningful — anchor the
                // rest to whichever of Reconnect/Edit is currently the leftmost visible.
                var afterReconnect = detailReconnectRemoteBtn.Visible ? detailReconnectRemoteBtn.Left : detailEditRemoteBtn.Left;
                detailDuplicateRemoteBtn.Location = new Point(afterReconnect - detailDuplicateRemoteBtn.Width - 6, 18);
                detailTestRemoteBtn.Location      = new Point(detailDuplicateRemoteBtn.Left - detailTestRemoteBtn.Width - 6, 18);
            };

            // Settings card (read-only summary of remote params). Collapsible — starts
            // collapsed each time a service is selected so the values aren't on display
            // just from clicking a service in the rail.
            settingsCard = MakeCard("Info", "");
            settingsCardBody = (Panel)settingsCard.Tag!;
            settingsCardSetExpanded = MakeCollapsible(settingsCard);

            // Mounts card (per-remote mounts list + Add mount button).
            mountsCard = MakeCard("Mounts", "");
            mountsCardBody = (Panel)mountsCard.Tag!;
            // Inner FlowLayoutPanel owns the actual MountRow list. AutoSize + TopDown flow
            // means adding/removing rows re-flows without manual y-position arithmetic —
            // the earlier absolute-positioning + Dock=Fill body dropped the second row on
            // add because body's height didn't cascade before the row's Y was measured.
            mountsListFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.SurfaceRaised,
                Padding = new Padding(0),
                Margin = new Padding(0),
            };
            mountsCardBody.Controls.Add(mountsListFlow);
            // Card height tracks the flow's autosize so newly-added rows are visible.
            mountsListFlow.SizeChanged += (_, _) =>
                mountsCard.Height = Math.Max(60, mountsListFlow.Height) + 42;
            // Keep row widths in sync with the flow's client width as the card resizes.
            mountsListFlow.Resize += (_, _) =>
            {
                var w = mountsListFlow.ClientSize.Width;
                if (w <= 0) return;
                foreach (Control c in mountsListFlow.Controls)
                    if (c is MountRow r) r.Width = w;
            };
            addMountBtn = MakeToolbarButton("Add Mount", primary: false);
            addMountBtn.Size = new Size(90, 28);
            addMountBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            addMountBtn.Click += (_, _) => AddMountForSelectedRemote();
            // Attach add button to the card's header. Position it now AND on every resize —
            // the Resize event doesn't always fire during the initial layout pass, which left
            // the button parked at (0,0). MakeCard adds body (index 0) then header (index 1),
            // so grab the header by scanning for the docked-top panel rather than assuming
            // Controls[0].
            var mountsHeaderPanel = mountsCard.Controls
                .OfType<Panel>()
                .First(p => p.Dock == DockStyle.Top);
            mountsHeaderPanel.Controls.Add(addMountBtn);
            addMountBtn.BringToFront();
            void PositionAddMountBtn()
            {
                var x = Math.Max(0, mountsHeaderPanel.Width - addMountBtn.Width - 12);
                var y = Math.Max(0, (mountsHeaderPanel.Height - addMountBtn.Height) / 2);
                addMountBtn.Location = new Point(x, y);
            }
            PositionAddMountBtn();
            mountsHeaderPanel.Resize += (_, _) => PositionAddMountBtn();
            mountsHeaderPanel.HandleCreated += (_, _) => PositionAddMountBtn();

            const string clientIdLink = "Making your own client_id";
            clientIdWarning = new LinkLabel
            {
                Text = "rclone is retiring the shared Google client_id this remote uses; its docs say it will stop working during 2026. "
                     + $"Create your own: {clientIdLink} (rclone guide). Then click Edit, paste the Client ID and Client Secret, save, and click Reconnect. "
                     + "Publish the Google app (Audience > Publish App); apps left in Testing lose sign-in after a week.",
                Padding = new Padding(10, 6, 10, 6),
                BackColor = Theme.WarnTint,
                ForeColor = Theme.TextPrimary,
                LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.AccentHover,
                VisitedLinkColor = Theme.Accent,
                Font = new Font(Font.FontFamily, 8.75f),
                Visible = false
            };
            clientIdWarning.LinkArea = new LinkArea(clientIdWarning.Text.IndexOf(clientIdLink, StringComparison.Ordinal), clientIdLink.Length);
            clientIdWarning.LinkClicked += (_, _) => OpenUrl(GetSelectedRemote()?.Type == "google photos"
                ? "https://rclone.org/googlephotos/#making-your-own-client-id"
                : "https://rclone.org/drive/#making-your-own-client-id");

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
            detailStack = new FlowLayoutPanel
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
            clientIdWarning.Margin = new Padding(0, 0, 0, 8);
            mountsCard.Margin = new Padding(0, 0, 0, 12);
            settingsCard.Margin = new Padding(0, 0, 0, 0);
            detailStack.Controls.Add(detailHeader);
            detailStack.Controls.Add(clientIdWarning);
            detailStack.Controls.Add(emptyStateLabel);
            detailStack.Controls.Add(mountsCard);
            detailStack.Controls.Add(settingsCard);

            // Cards should fill horizontally; keep them synced to the scroll pane's client width.
            void SyncCardWidths()
            {
                var w = detailScroll.ClientSize.Width - detailScroll.Padding.Horizontal;
                if (detailScroll.VerticalScroll.Visible)
                    w -= SystemInformation.VerticalScrollBarWidth;
                if (w <= 0) return;
                detailHeader.Width = w;
                emptyStateLabel.Width = w;
                clientIdWarning.Width = w;
                // Height follows the wrapped text so no step is clipped at any width, DPI or copy edit.
                clientIdWarning.Height = clientIdWarning.GetPreferredSize(new Size(w, 0)).Height;
                settingsCard.Width = w;
                mountsCard.Width = w;
                detailStack.Width = w;
            }
            detailScroll.Resize += (_, _) => SyncCardWidths();
            detailScroll.SizeChanged += (_, _) => SyncCardWidths();
            // Neither Resize nor SizeChanged fires on the initial layout pass, so cards
            // would stay at their default narrow width until the user drags the window —
            // sync once when the handle first exists.
            detailScroll.HandleCreated += (_, _) => SyncCardWidths();

            detailScroll.Controls.Add(detailStack);

            // Compose: rail left, detail fill.
            tabMounts.Controls.Add(detailScroll);
            tabMounts.Controls.Add(rail);
        }

        private void UpdateActionButtons()
        {
            var hasRclone = configService is not null;
            var selectedRemote = GetSelectedRemote();
            toolbarAddRemoteBtn.Enabled = hasRclone;
            toolbarRefreshBtn.Enabled = hasRclone;
            toolbarAdvancedBtn.Enabled = hasRclone;
            railTerminalBtn.Enabled = hasRclone;
            detailEditRemoteBtn.Enabled = hasRclone && selectedRemote is not null;
            detailDeleteRemoteBtn.Enabled = hasRclone && selectedRemote is not null;
            addMountBtn.Enabled = hasRclone && selectedRemote is not null;

            // Reconnect only surfaces on OAuth backends. When the OAuth type index
            // hasn't loaded yet (rclone slow, first launch), stay hidden — better than
            // showing a button that would error with "not an OAuth type".
            var showReconnect = hasRclone && selectedRemote is not null && IsOAuthType(selectedRemote.Type);
            detailReconnectRemoteBtn.Visible = showReconnect;
            detailReconnectRemoteBtn.Enabled = showReconnect;
            UpdateStatusBar();
        }

        // Called by RefreshRemotesAsync — rebuilds the left rail's RemoteRow list.
        private void RebuildRail()
        {
            // Remotes may have been re-fetched with fresh parameter values (e.g. after
            // an Edit) — clear the settings-card cache so the next RefreshDetail rebuilds
            // the card even for the same remote name.
            renderedSettingsRemoteName = null;
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
            // Freeze layout on the detail area so the badge-invalidate, title/subtitle
            // updates, settings-card rebuild, and mounts-card reconcile land as a single
            // paint instead of five sequential ones — that's what shows as flicker when
            // switching between services in the rail.
            detailScroll.SuspendLayout();
            detailStack.SuspendLayout();
            try
            {
                var remote = GetSelectedRemote();

                if (remote is null)
                {
                    detailHeader.Visible = false;
                    settingsCard.Visible = false;
                    mountsCard.Visible = false;
                    clientIdWarning.Visible = false;
                    emptyStateLabel.Visible = true;
                    UpdateActionButtons();
                    return;
                }

                emptyStateLabel.Visible = false;
                detailHeader.Visible = true;
                mountsCard.Visible = true;
                // settingsCard.Visible is managed by RebuildSettingsCard — for OAuth-only
                // remotes with no user-visible params it stays hidden across refreshes.
                // Unconditionally re-enabling here would let it flash back on for every
                // mount-state tick or stats poll.

                clientIdWarning.Visible = remote.UsesSharedGoogleClientId;
                detailBadge.Invalidate();
                detailTitleLabel.Text = remote.Name;

                var forRemote = mounts.Where(m => string.Equals(m.RemoteName, remote.Name, StringComparison.Ordinal)).ToList();
                var running = forRemote.Count(m => mountService is not null && mountService.IsMounted(m));
                var typeLabel = ProviderVisuals.FriendlyType(remote.Type);
                detailSubtitleLabel.Text = forRemote.Count == 0
                    ? $"{typeLabel} · no mounts yet"
                    : $"{typeLabel} · {running} of {forRemote.Count} mount(s) running";

                // Only rebuild the settings card and collapse it when the SELECTED remote
                // changes. Rebuilding on every mount-state tick was a big flicker source
                // (and rudely re-collapsed a user who had just expanded the card to look).
                if (!string.Equals(renderedSettingsRemoteName, remote.Name, StringComparison.Ordinal))
                {
                    RebuildSettingsCard(remote);
                    settingsCardSetExpanded(false);
                    renderedSettingsRemoteName = remote.Name;
                }
                RebuildMountsCard(remote, forRemote);
                UpdateActionButtons();
            }
            finally
            {
                detailStack.ResumeLayout(true);
                detailScroll.ResumeLayout(true);
            }
        }

        private void RebuildSettingsCard(RcloneRemote remote)
        {
            settingsCardBody.SuspendLayout();
            foreach (Control c in settingsCardBody.Controls) c.Dispose();
            settingsCardBody.Controls.Clear();

            // Name/Type/Mounts live in the header now — this card shows only the actual
            // provider settings stored in rclone.conf so it isn't duplicating the header.
            var pairs = new List<(string Key, string Value)>();
            foreach (var kv in remote.Parameters)
            {
                if (pairs.Count >= 8) break;
                var name = kv.Key.ToLowerInvariant();
                // The whole card is behind a collapsed chevron now, so we only strip the
                // true auth credentials — everything else is fine to show once the user
                // deliberately expands the card.
                if (name is "type" or "token" or "client_secret" or "password" or "pass"
                          or "password2" or "secret_access_key" or "service_account_credentials"
                          or "refresh_token")
                    continue;
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                pairs.Add((kv.Key, kv.Value));
            }

            // OAuth-only remotes (Google Drive, OneDrive, etc.) often have no user-visible
            // params after we filter out the auth token — hide the whole card in that case
            // instead of showing an empty box.
            if (pairs.Count == 0)
            {
                settingsCard.Visible = false;
                settingsCardBody.ResumeLayout();
                return;
            }

            settingsCard.Visible = true;
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
            // Deliberately NOT setting settingsCard.Height here — the caller collapses
            // the card immediately after via settingsCardSetExpanded(false), which sets
            // the correct collapsed height. Setting it here would flip-flop the height
            // (expanded → collapsed within the same layout pass) and reflow detailStack
            // twice, which shows as a flicker on service switch.
            settingsCardBody.ResumeLayout();
        }

        // Reconcile-in-place. Existing MountRow controls are kept and just re-bound to
        // fresh state; new mounts get a new row; missing mounts get their row removed.
        // Rows live inside mountsListFlow (a FlowLayoutPanel) so the layout engine — not
        // us — handles y-positioning; that fixes the "second row invisible" bug the
        // previous absolute-positioning had (body.Height didn't cascade in time).
        private void RebuildMountsCard(RcloneRemote remote, List<Mount> forRemote)
        {
            mountsListFlow.SuspendLayout();

            // Empty state: hide the flow, show a single label sibling inside the body.
            var emptyLabel = mountsCardBody.Controls.OfType<Label>().FirstOrDefault(l => (l.Tag as string) == "empty");
            if (forRemote.Count == 0)
            {
                mountsListFlow.Visible = false;
                if (emptyLabel is null)
                {
                    emptyLabel = new Label
                    {
                        Text = "No mounts for this remote yet.  Click \"+ Add mount\" to create one.",
                        Dock = DockStyle.Top,
                        Height = 60,
                        TextAlign = ContentAlignment.MiddleCenter,
                        ForeColor = Theme.TextTertiary,
                        Font = new Font(Font.FontFamily, 9f),
                        Tag = "empty"
                    };
                    mountsCardBody.Controls.Add(emptyLabel);
                    emptyLabel.BringToFront();
                }
                emptyLabel.Visible = true;
                mountsCard.Height = 60 + 42;
                mountsListFlow.ResumeLayout();
                return;
            }

            if (emptyLabel is not null) emptyLabel.Visible = false;
            mountsListFlow.Visible = true;

            // Index existing rows by mount id.
            var existing = mountsListFlow.Controls.OfType<MountRow>()
                .ToDictionary(r => r.Mount.Id, r => r);
            var wantedIds = new HashSet<string>();

            var flowWidth = mountsListFlow.ClientSize.Width;

            foreach (var m in forRemote)
            {
                wantedIds.Add(m.Id);
                MountRow row;
                if (!existing.TryGetValue(m.Id, out row!))
                {
                    row = new MountRow
                    {
                        Width = flowWidth > 0 ? flowWidth : mountsCardBody.Width,
                        Margin = new Padding(0)
                    };
                    var captured = m;
                    row.MountRequested    += async (_, _) => await MountAsync(captured);
                    row.UnmountRequested  += async (_, _) => await UnmountAsync(captured);
                    row.OpenRequested     += (_, _) => OpenDrive(captured);
                    row.EditRequested     += async (_, _) => await EditMountAsync(captured);
                    row.RemoveRequested   += async (_, _) => await RemoveMountAsync(captured);
                    row.TransferRequested += (_, args) => OpenTransferDialog(captured, args.Operation);
                    mountsListFlow.Controls.Add(row);
                }
                row.Bind(m, GetMountStatusEnum(m));
            }

            // Drop rows for mounts that are no longer present.
            foreach (var kv in existing)
            {
                if (!wantedIds.Contains(kv.Key))
                {
                    mountsListFlow.Controls.Remove(kv.Value);
                    kv.Value.Dispose();
                }
            }

            // Enforce iteration order in the flow (Controls order == visual order for FLP).
            for (var i = 0; i < forRemote.Count; i++)
            {
                var row = mountsListFlow.Controls.OfType<MountRow>()
                    .First(r => r.Mount.Id == forRemote[i].Id);
                mountsListFlow.Controls.SetChildIndex(row, i);
            }

            // Keep every row's width synced to the flow's client width.
            foreach (Control c in mountsListFlow.Controls)
                if (c is MountRow r && flowWidth > 0) r.Width = flowWidth;

            mountsListFlow.ResumeLayout();
            // FLP is AutoSize=true — its SizeChanged handler on card build already updates
            // mountsCard.Height. But if the size didn't actually change (same row count,
            // same width), the handler doesn't fire; set the card height explicitly here
            // to cover that path.
            mountsCard.Height = Math.Max(60, mountsListFlow.Height) + 42;
        }

        // Refresh coalescer. Multiple RefreshMountList() calls in the same tick (auto-
        // mount loops, retry logic, poll ticks that all touch state) collapse to a single
        // do-the-work pass after a very short idle window. 50 ms is imperceptible but
        // eats a burst of 4-8 calls.
        private System.Windows.Forms.Timer? refreshDebounce;

        private void RefreshMountList()
        {
            if (refreshDebounce is null)
            {
                refreshDebounce = new System.Windows.Forms.Timer { Interval = 50 };
                refreshDebounce.Tick += (_, _) =>
                {
                    refreshDebounce!.Stop();
                    DoRefreshMountList();
                };
            }
            refreshDebounce.Stop();
            refreshDebounce.Start();
        }

        private void DoRefreshMountList()
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
    }
}
