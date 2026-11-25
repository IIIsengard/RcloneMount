using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using RcloneMount.Services;
using RcloneMount.UI;

namespace RcloneMount
{
    // Log tab: construction of the RichTextBox log view + bottom action strip
    // (Open folder · Export · Clear · Copy all · Auto-scroll), plus the runtime
    // helpers (activity-log subscriber, appender, scroll-to-tail).
    public partial class MainForm
    {
        // Constructs the Log-tab UI. Also seeds the log with any persisted history
        // and hooks the ActivityLog subscription.
        private void BuildLogTab(Panel tabLog)
        {
            tabLog.Padding = new Padding(6);
            txtLog = new RichTextBox
            {
                ReadOnly = true,
                // Both = auto-hide (unlike TextBox's ScrollBars.Both which is always-on).
                ScrollBars = RichTextBoxScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9),
                BackColor = Theme.SurfaceRaised,
                ForeColor = Theme.TextPrimary,
                // BorderStyle intentionally None — RichTextBox's FixedSingle border is
                // drawn by a different visual-style class than TextBox, so the dark-mode
                // theme hint we apply doesn't catch it and it renders as a stark white
                // edge. Padding on tabLog + the raised surface color provide separation.
                BorderStyle = BorderStyle.None,
                DetectUrls = false // don't rewrite pasted text as hyperlinks
            };

            // Single bottom strip carrying: log path (left) · Open folder · Export · Clear ·
            // Copy all · Auto-scroll (right, in that reading order). One strip is quieter
            // than two and gives the log text area more vertical room.
            btnAutoScroll = new ToggleButton { Text = "Auto-scroll", Location = Point.Empty, Size = new Size(110, 30) };
            btnAutoScroll.SetCheckedSilent(true);
            btnAutoScroll.CheckedChanged += (_, _) => { if (btnAutoScroll.Checked) ScrollLogToEnd(); };
            btnGroupErrors = new ToggleButton { Text = "Group errors", Location = Point.Empty, Size = new Size(110, 30) };
            btnGroupErrors.CheckedChanged += (_, _) => ApplyGroupErrorsMode();
            var btnLogClear = MakeFlatButton("Clear",    Point.Empty, new Size(80, 30));
            btnLogClear.Click += (_, _) => { activityLog.Clear(); txtLog.Clear(); RebuildErrorGroups(); };
            var btnLogCopy  = MakeFlatButton("Copy all", Point.Empty, new Size(80, 30));
            btnLogCopy.Click += (_, _) =>
            {
                try
                {
                    if (btnGroupErrors.Checked) CopyErrorGroupsToClipboard();
                    else if (txtLog.TextLength > 0) Clipboard.SetText(txtLog.Text);
                }
                catch { }
            };

            var logHeader = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Theme.SurfaceBase };
            var lblLogFile = new Label
            {
                Text = "Log file:",
                Location = new Point(0, 14),
                Size = new Size(70, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.TextSecondary
            };
            var txtLogPath = new TextBox
            {
                Location = new Point(74, 12),
                Size = new Size(680, 26),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                ReadOnly = true,
                Text = SettingsService.ActivityLogPath,
                BackColor = Theme.SurfaceBase,
                ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle
            };
            var btnOpenLogFolder = MakeFlatButton("Open folder", Point.Empty, new Size(110, 30));
            btnOpenLogFolder.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            var btnExportLog     = MakeFlatButton("Export...",   Point.Empty, new Size(90,  30));
            btnExportLog.Anchor     = AnchorStyles.Top | AnchorStyles.Right;
            btnLogClear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnLogCopy.Anchor  = AnchorStyles.Top | AnchorStyles.Right;
            btnAutoScroll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnGroupErrors.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            // Right-to-left placement so the reading order is
            //   Open folder · Export · Clear · Copy all · Group errors · Auto-scroll
            void PositionLogHeaderRight()
            {
                var x = logHeader.Width - 4;
                x -= btnAutoScroll.Width;    btnAutoScroll.Location    = new Point(x, 10);
                x -= 6  + btnGroupErrors.Width; btnGroupErrors.Location = new Point(x, 10);
                x -= 12 + btnLogCopy.Width;  btnLogCopy.Location       = new Point(x, 10);
                x -= 6  + btnLogClear.Width; btnLogClear.Location      = new Point(x, 10);
                x -= 12 + btnExportLog.Width;btnExportLog.Location     = new Point(x, 10);
                x -= 6  + btnOpenLogFolder.Width; btnOpenLogFolder.Location = new Point(x, 10);
                txtLogPath.Size = new Size(Math.Max(80, btnOpenLogFolder.Left - txtLogPath.Left - 8), 26);
            }
            logHeader.Resize += (_, _) => PositionLogHeaderRight();
            logHeader.HandleCreated += (_, _) => PositionLogHeaderRight();
            btnOpenLogFolder.Click += (_, _) =>
            {
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(SettingsService.ActivityLogPath);
                    if (string.IsNullOrWhiteSpace(dir)) return;
                    if (!System.IO.Directory.Exists(dir))
                        System.IO.Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        System.IO.File.Exists(SettingsService.ActivityLogPath)
                            ? $"/select,\"{SettingsService.ActivityLogPath}\""
                            : $"\"{dir}\"")
                        { UseShellExecute = true });
                }
                catch { }
            };
            btnExportLog.Click += (_, _) =>
            {
                using var dlg = new SaveFileDialog
                {
                    Title = "Export activity log",
                    Filter = "Log file (*.log)|*.log|Text file (*.txt)|*.txt|All files|*.*",
                    FileName = $"RcloneMount-log-{DateTime.Now:yyyy-MM-dd_HHmmss}.log"
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (System.IO.File.Exists(SettingsService.ActivityLogPath))
                        System.IO.File.Copy(SettingsService.ActivityLogPath, dlg.FileName, overwrite: true);
                    else
                        System.IO.File.WriteAllText(dlg.FileName, txtLog.Text);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Export failed: {ex.Message}", "Export log", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            logHeader.Controls.Add(lblLogFile);
            logHeader.Controls.Add(txtLogPath);
            logHeader.Controls.Add(btnOpenLogFolder);
            logHeader.Controls.Add(btnExportLog);
            logHeader.Controls.Add(btnLogClear);
            logHeader.Controls.Add(btnLogCopy);
            logHeader.Controls.Add(btnGroupErrors);
            logHeader.Controls.Add(btnAutoScroll);

            // Group-errors view lives in the same slot as txtLog; toggled via Visible.
            // Layout: Count · Level · First · Last · Message. Sorted by Count desc.
            lvErrorGroups = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                MultiSelect = false,
                HideSelection = false,
                BackColor = Theme.SurfaceRaised,
                ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.None,
                Visible = false,
                Font = new Font(FontFamily.GenericSansSerif, 9),
            };
            // WinForms doesn't expose DoubleBuffered on ListView (it's protected). Flip
            // it via reflection — without this, even BeginUpdate/EndUpdate leaves a
            // visible flash on every rebuild because the ListView's own paint pipeline
            // repaints in-place instead of to a back buffer.
            typeof(ListView).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(lvErrorGroups, true);
            lvErrorGroups.Columns.Add("Count",    60,  HorizontalAlignment.Right);
            lvErrorGroups.Columns.Add("Level",    60,  HorizontalAlignment.Left);
            lvErrorGroups.Columns.Add("First seen", 140, HorizontalAlignment.Left);
            lvErrorGroups.Columns.Add("Last seen",  140, HorizontalAlignment.Left);
            lvErrorGroups.Columns.Add("Message",  1200, HorizontalAlignment.Left);

            // Refresh a few times per second while the grouped view is visible — new
            // errors trickle in and the count updates should feel live. Cheap because
            // grouping only runs when the toggle is on.
            groupRefreshTimer = new System.Windows.Forms.Timer { Interval = 750 };
            groupRefreshTimer.Tick += (_, _) => { if (btnGroupErrors.Checked) RebuildErrorGroups(); };

            tabLog.Controls.Add(txtLog);
            tabLog.Controls.Add(lvErrorGroups);
            tabLog.Controls.Add(logHeader);

            // Seed with any persisted history (last ≤30 days, up to buffer capacity).
            foreach (var e in activityLog.Snapshot())
                txtLog.AppendText(e.ToString() + Environment.NewLine);
            if (txtLog.TextLength > 0)
            {
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            }

            activityLog.Appended += OnActivityLogAppended;
        }

        // Named so we can unsubscribe cleanly on close; anonymous lambdas can't be
        // detached from the ActivityLog subscription list.
        private void OnActivityLogAppended(ActivityEntry entry)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => AppendLogLine(entry))); } catch { } }
            else AppendLogLine(entry);
        }

        private void AppendLogLine(ActivityEntry entry)
        {
            if (txtLog.IsDisposed) return;
            var line = entry.ToString() + Environment.NewLine;
            txtLog.AppendText(line);
            if (btnAutoScroll.Checked)
            {
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            }
        }

        // Jumps the log view to the newest entry. Called whenever the Log tab is opened
        // so the user lands on the latest activity instead of an arbitrary earlier point.
        private void ScrollLogToEnd()
        {
            if (txtLog is null || txtLog.IsDisposed || txtLog.TextLength == 0) return;
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.SelectionLength = 0;
            txtLog.ScrollToCaret();
        }

        // Swaps between the flat RichTextBox and the grouped ListView. Auto-scroll and
        // Copy-all still work in either mode — the button click handlers gate on
        // btnGroupErrors.Checked to pick the right source.
        private void ApplyGroupErrorsMode()
        {
            var grouped = btnGroupErrors.Checked;
            txtLog.Visible = !grouped;
            lvErrorGroups.Visible = grouped;
            btnAutoScroll.Enabled = !grouped; // no scrolling to tail when there's no tail
            if (grouped)
            {
                RebuildErrorGroups();
                groupRefreshTimer?.Start();
            }
            else
            {
                groupRefreshTimer?.Stop();
            }
        }

        // Long sequences of digits ("id=847352", "attempt 3/5", timestamps) are the
        // usual source of near-duplicate error spam — collapsing them to a single "#"
        // token lets us group messages that only differ by a numeric detail. Also
        // strips hex-ish 8+ char runs (UUIDs, hashes) for the same reason.
        private static readonly Regex NormalizeDigits = new("\\d+", RegexOptions.Compiled);
        private static readonly Regex NormalizeHex    = new("\\b[0-9a-fA-F]{8,}\\b", RegexOptions.Compiled);
        private static readonly Regex NormalizeWs     = new("\\s+", RegexOptions.Compiled);

        private static string NormalizeForGrouping(string msg)
        {
            var s = msg ?? "";
            s = NormalizeHex.Replace(s, "#");
            s = NormalizeDigits.Replace(s, "#");
            s = NormalizeWs.Replace(s, " ").Trim();
            return s.ToLowerInvariant();
        }

        // Signature of the last snapshot we grouped — total entry count + timestamp of
        // the newest entry. If neither has moved, nothing new has arrived and there's
        // no reason to rebuild (or repaint) the list at all.
        private int lastGroupSnapshotCount;
        private DateTime lastGroupSnapshotNewest;

        private void RebuildErrorGroups()
        {
            if (lvErrorGroups is null || lvErrorGroups.IsDisposed) return;

            var snapshot = activityLog.Snapshot();
            var newest = snapshot.Count == 0 ? DateTime.MinValue : snapshot[snapshot.Count - 1].Timestamp;
            // Cheap change detector — skip the whole rebuild when the buffer hasn't
            // grown since last tick. Avoids repainting a list of tens of rows every
            // 750ms when the user is just staring at a stable error picture.
            if (snapshot.Count == lastGroupSnapshotCount && newest == lastGroupSnapshotNewest && lvErrorGroups.Items.Count > 0)
                return;
            lastGroupSnapshotCount = snapshot.Count;
            lastGroupSnapshotNewest = newest;

            // Only warns and errors get grouped — grouping infos would drown the
            // useful signal. Keep the raw representative message so the user still
            // sees a real, unredacted example.
            var groups = new Dictionary<string, (int Count, ActivityLevel Level, DateTime First, DateTime Last, string Sample)>();
            foreach (var e in snapshot)
            {
                if (e.Level == ActivityLevel.Info) continue;
                var key = NormalizeForGrouping(e.Message);
                if (groups.TryGetValue(key, out var g))
                {
                    groups[key] = (
                        g.Count + 1,
                        (ActivityLevel)Math.Max((int)g.Level, (int)e.Level),
                        g.First,
                        e.Timestamp > g.Last ? e.Timestamp : g.Last,
                        g.Sample);
                }
                else
                {
                    groups[key] = (1, e.Level, e.Timestamp, e.Timestamp, e.Message);
                }
            }

            var rows = groups
                .Select(kv => (Key: kv.Key, Val: kv.Value))
                .OrderByDescending(r => r.Val.Count)
                .ThenByDescending(r => r.Val.Last)
                .ToList();

            // Preserve the currently-selected row's key so a live refresh doesn't jump
            // the selection out from under the user.
            string? selectedKey = null;
            if (lvErrorGroups.SelectedItems.Count > 0)
                selectedKey = lvErrorGroups.SelectedItems[0].Tag as string;

            lvErrorGroups.BeginUpdate();
            try
            {
                // Diff in place instead of Clear+Add. The old approach discarded every
                // row and rebuilt it, which forces a full paint (and flicker) even when
                // most rows are identical between ticks. Updating each SubItem in place
                // and only touching the tail keeps paints scoped to what actually
                // changed.
                for (var i = 0; i < rows.Count; i++)
                {
                    var g = rows[i].Val;
                    var levelText = g.Level.ToString();
                    var firstText = g.First.ToString("yyyy-MM-dd HH:mm:ss");
                    var lastText  = g.Last.ToString("yyyy-MM-dd HH:mm:ss");
                    var countText = g.Count.ToString();
                    var color = g.Level == ActivityLevel.Error ? Theme.Critical : Theme.Warn;

                    if (i < lvErrorGroups.Items.Count)
                    {
                        var item = lvErrorGroups.Items[i];
                        if (item.SubItems[0].Text != countText) item.SubItems[0].Text = countText;
                        if (item.SubItems[1].Text != levelText) item.SubItems[1].Text = levelText;
                        if (item.SubItems[2].Text != firstText) item.SubItems[2].Text = firstText;
                        if (item.SubItems[3].Text != lastText)  item.SubItems[3].Text = lastText;
                        if (item.SubItems[4].Text != g.Sample)  item.SubItems[4].Text = g.Sample;
                        if (item.ForeColor != color) item.ForeColor = color;
                        item.Tag = rows[i].Key;
                    }
                    else
                    {
                        var item = new ListViewItem(new[] { countText, levelText, firstText, lastText, g.Sample })
                        {
                            ForeColor = color,
                            Tag = rows[i].Key,
                        };
                        lvErrorGroups.Items.Add(item);
                    }
                }
                // Trim any tail rows that no longer exist.
                while (lvErrorGroups.Items.Count > rows.Count)
                    lvErrorGroups.Items.RemoveAt(lvErrorGroups.Items.Count - 1);

                if (selectedKey is not null)
                {
                    foreach (ListViewItem item in lvErrorGroups.Items)
                    {
                        if (string.Equals((string?)item.Tag, selectedKey, StringComparison.Ordinal))
                        {
                            if (!item.Selected) item.Selected = true;
                            item.EnsureVisible();
                            break;
                        }
                    }
                }
            }
            finally
            {
                lvErrorGroups.EndUpdate();
            }
        }

        private void CopyErrorGroupsToClipboard()
        {
            if (lvErrorGroups is null || lvErrorGroups.Items.Count == 0) return;
            var sb = new StringBuilder();
            sb.AppendLine("Count\tLevel\tFirst seen\tLast seen\tMessage");
            foreach (ListViewItem item in lvErrorGroups.Items)
            {
                sb.Append(item.SubItems[0].Text).Append('\t');
                sb.Append(item.SubItems[1].Text).Append('\t');
                sb.Append(item.SubItems[2].Text).Append('\t');
                sb.Append(item.SubItems[3].Text).Append('\t');
                sb.AppendLine(item.SubItems[4].Text);
            }
            Clipboard.SetText(sb.ToString());
        }
    }
}
