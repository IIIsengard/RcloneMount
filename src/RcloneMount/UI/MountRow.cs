using System;
using System.Drawing;
using System.Windows.Forms;
using RcloneMount.Models;

namespace RcloneMount.UI
{
    public enum MountRowStatus { Mounted, Stopped, External, Detached }

    public sealed class TransferRequestedEventArgs : EventArgs
    {
        public Services.TransferOperation Operation { get; }
        public TransferRequestedEventArgs(Services.TransferOperation op) { Operation = op; }
    }

    // A row in the Mounts card: drive chip · title · meta · status pill · action buttons.
    // Mixed rendering: chip + pill are painted; buttons are real Buttons for hit-testing
    // and a11y.
    public class MountRow : UserControl
    {
        private const int RowHeight = 62;

        public Mount Mount { get; private set; } = new();
        public MountRowStatus Status { get; private set; } = MountRowStatus.Stopped;

        // Live throughput (bytes/sec) set by the stats poller; drawn under the meta line
        // when > 0. Cleared automatically when the row is rebound to a non-mounted state.
        private double liveRate;

        // Backend space (from rclone /operations/about). Zero-total means "not known" —
        // either the backend doesn't support about or we haven't polled yet.
        private long spaceTotalBytes;
        private long spaceUsedBytes;

        public event EventHandler? MountRequested;
        public event EventHandler? UnmountRequested;
        public event EventHandler? OpenRequested;
        public event EventHandler? EditRequested;
        public event EventHandler? RemoveRequested;
        public event EventHandler<TransferRequestedEventArgs>? TransferRequested;

        private readonly FlowLayoutPanel actions;
        private readonly Button btnPrimary;
        private readonly Button btnSecondary;
        private readonly Button btnEdit;
        private readonly Button btnRemove;

        public MountRow()
        {
            DoubleBuffered = true;
            // ResizeRedraw is what keeps the status pill from leaving ghost copies of itself
            // behind when the parent card widens — the pill's X position is derived from
            // Width, so a resize moves it and we need the whole row invalidated, not just the
            // exposed strip on the right edge.
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = RowHeight;
            BackColor = Theme.SurfaceRaised;
            MouseEnter += (_, _) => { BackColor = Theme.SurfaceSunken; Invalidate(); };
            MouseLeave += (_, _) => { BackColor = Theme.SurfaceRaised; Invalidate(); };

            actions = new FlowLayoutPanel
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Padding = new Padding(0),
                Margin = new Padding(0),
                BackColor = Color.Transparent
            };

            btnPrimary   = MakeButton("Mount",   primary: false);
            btnSecondary = MakeButton("Open",    primary: false);
            btnEdit      = MakeButton("Edit",    primary: false);
            btnRemove    = MakeButton("Remove",  primary: false);

            btnPrimary.Click   += (_, _) => RaisePrimary();
            btnSecondary.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
            btnEdit.Click      += (_, _) => EditRequested?.Invoke(this, EventArgs.Empty);
            btnRemove.Click    += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty);

            actions.Controls.Add(btnPrimary);
            actions.Controls.Add(btnSecondary);
            actions.Controls.Add(btnEdit);
            actions.Controls.Add(btnRemove);

            Controls.Add(actions);
            RoundedButtons.Apply(this);
            Resize += (_, _) => RepositionActions();

            // Right-click anywhere on the row (not just the empty area) to open a
            // Transfer dialog with source pre-filled. Keeps the visible action buttons
            // uncluttered while adding the entry point for Slice 1 of the transfer
            // engine (Copy / Sync / Move).
            var menu = new ContextMenuStrip();
            var miCopy = new ToolStripMenuItem("Copy from here…");
            var miSync = new ToolStripMenuItem("Sync from here…");
            var miMove = new ToolStripMenuItem("Move from here…");
            var miCheck = new ToolStripMenuItem("Check against… (read-only)");
            var miBisync = new ToolStripMenuItem("Bisync with… (two-way)");
            miCopy.Click   += (_, _) => TransferRequested?.Invoke(this, new TransferRequestedEventArgs(Services.TransferOperation.Copy));
            miSync.Click   += (_, _) => TransferRequested?.Invoke(this, new TransferRequestedEventArgs(Services.TransferOperation.Sync));
            miMove.Click   += (_, _) => TransferRequested?.Invoke(this, new TransferRequestedEventArgs(Services.TransferOperation.Move));
            miCheck.Click  += (_, _) => TransferRequested?.Invoke(this, new TransferRequestedEventArgs(Services.TransferOperation.Check));
            miBisync.Click += (_, _) => TransferRequested?.Invoke(this, new TransferRequestedEventArgs(Services.TransferOperation.Bisync));
            menu.Items.Add(miCopy);
            menu.Items.Add(miSync);
            menu.Items.Add(miMove);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miCheck);
            menu.Items.Add(miBisync);
            ContextMenuStrip = menu;
            RepositionActions();
        }

        private static Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = false,
                Size = new Size(72, 28),
                Margin = new Padding(0, 0, 6, 0),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f, FontStyle.Regular),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 1;
            ApplyButtonStyle(b, primary);
            return b;
        }

        private static void ApplyButtonStyle(Button b, bool primary)
        {
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
        }

        public void Bind(Mount mount, MountRowStatus status)
        {
            Mount = mount;
            Status = status;

            var mounted = status == MountRowStatus.Mounted;
            var external = status == MountRowStatus.External;
            var detached = status == MountRowStatus.Detached;

            btnPrimary.Text = (mounted || external || detached) ? "Unmount" : "Mount";
            ApplyButtonStyle(btnPrimary, primary: false);

            btnSecondary.Visible = mounted || external;
            btnSecondary.Text = "Open";

            // A detached mount has no live throughput; clear so the strip disappears rather
            // than lying with a stale reading until the next rebind.
            if (!mounted) liveRate = 0;
            // Space info is only meaningful for a live mount — a stopped/detached row
            // shouldn't advertise a possibly-stale bar. It'll repopulate on next poll.
            if (!mounted) { spaceTotalBytes = 0; spaceUsedBytes = 0; }

            RepositionActions();
            Invalidate();
        }

        // Called by the stats poller each tick. Only repaints when the displayed digits
        // actually change to avoid burning cycles on sub-KB jitter.
        public void UpdateLiveRate(double bytesPerSecond)
        {
            var newLabel = FormatRateShort(bytesPerSecond);
            var oldLabel = FormatRateShort(liveRate);
            liveRate = bytesPerSecond;
            if (newLabel != oldLabel) Invalidate();
        }

        // Backend total/used bytes. Pass total=0 to hide the space strip (used when the
        // backend doesn't implement /operations/about). Same "only repaint when the
        // label changes" gate as the rate strip so a stable number doesn't churn paints.
        public void UpdateSpace(long totalBytes, long usedBytes)
        {
            var oldLabel = FormatSpaceLabel(spaceUsedBytes, spaceTotalBytes);
            var newLabel = FormatSpaceLabel(usedBytes, totalBytes);
            spaceTotalBytes = totalBytes;
            spaceUsedBytes = usedBytes;
            if (newLabel != oldLabel) Invalidate();
        }

        private void RaisePrimary()
        {
            if (Status == MountRowStatus.Mounted || Status == MountRowStatus.External)
                UnmountRequested?.Invoke(this, EventArgs.Empty);
            else
                MountRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RepositionActions()
        {
            actions.PerformLayout();
            actions.Location = new Point(Width - actions.PreferredSize.Width - 12,
                                         (Height - actions.PreferredSize.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var bg = new SolidBrush(BackColor))
                g.FillRectangle(bg, ClientRectangle);

            // Bottom hairline
            using (var pen = new Pen(Theme.BorderSubtle))
                g.DrawLine(pen, 16, Height - 1, Width - 16, Height - 1);

            var mounted = Status == MountRowStatus.Mounted || Status == MountRowStatus.External;
            var detached = Status == MountRowStatus.Detached;

            // Drive-letter chip
            var chip = new Rectangle(20, (Height - 34) / 2, 46, 34);
            // Detached reuses the warn palette so the chip catches the eye without
            // reading as "everything's fine" (accent) or "not running" (idle).
            var chipBg = detached ? Theme.WarnTint
                       : mounted ? Theme.AccentTint
                       : Theme.BorderEmphasis;
            var chipFg = detached ? Theme.Warn
                       : mounted ? Theme.Accent
                       : Theme.TextSecondary;
            using (var brush = new SolidBrush(chipBg))
            using (var path = Drawing.RoundedRect(chip, 6))
                g.FillPath(brush, path);

            var chipFont = new Font(FontFamily.GenericMonospace, 11.5f, FontStyle.Bold);
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var brush = new SolidBrush(chipFg))
                g.DrawString(Mount.DriveLetter, chipFont, brush, chip, fmt);
            chipFont.Dispose();

            // Title + meta. textRight is the "true" right edge for text — the actions
            // button strip and status pill start after it.
            var textX = chip.Right + 12;
            var actionsW = actions.Width + 12;
            // Widened from 92 → 118 so "Auto-mounted" fits without truncating.
            var pillW = 118;
            var textRight = Math.Max(textX + 60, Width - actionsW - pillW - 12);
            var textW = textRight - textX;

            var title = string.IsNullOrEmpty(Mount.RemotePath) ? "(root)" : Mount.RemotePath;

            var titleFont = new Font(Font.FontFamily, 9.75f, FontStyle.Bold);
            var metaFont  = new Font(Font.FontFamily, 8.25f);

            // Live rate goes RIGHT-aligned on the title row (top-right of the text area)
            // where nothing else draws. Space info + usage bar go on the meta row, with
            // meta text inlined into a single string so nothing overlaps.
            var titleRightReserve = 0;
            var showRate = Status == MountRowStatus.Mounted && liveRate >= 1024;
            if (showRate)
            {
                var rateText = "↕ " + FormatRateShort(liveRate);
                var rateW = TextRenderer.MeasureText(rateText, metaFont).Width;
                titleRightReserve = rateW + 8;
                TextRenderer.DrawText(g, rateText, metaFont,
                    new Rectangle(textRight - rateW, 12, rateW, 18), Theme.Accent,
                    TextFormatFlags.NoPrefix | TextFormatFlags.Right);
            }

            TextRenderer.DrawText(g, title, titleFont,
                new Rectangle(textX, 10, Math.Max(40, textW - titleRightReserve), 20),
                Theme.TextPrimary,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);

            // The space label + its bar are treated as ONE right-anchored group so the
            // narrow-window ellipsis eats "VFS · Auto-mount" first (nice-to-know), not
            // the size (headline info). Bar sits directly under the number so they read
            // as one indicator.
            var meta = FormatMeta();
            var showSpace = Status == MountRowStatus.Mounted && spaceTotalBytes > 0;
            var spaceLabel = showSpace ? FormatSpaceLabel(spaceUsedBytes, spaceTotalBytes) : "";
            var spaceLabelW = showSpace ? TextRenderer.MeasureText(spaceLabel, metaFont).Width : 0;
            var spaceReserve = showSpace ? spaceLabelW + 12 : 0;

            // Drop the space group entirely if reserving it would leave less than 60px
            // for the meta text — better a clean meta than a squeezed-illegible row.
            if (showSpace && textW - spaceReserve < 60)
            {
                showSpace = false;
                spaceReserve = 0;
            }

            var metaW = Math.Max(40, textW - spaceReserve);
            TextRenderer.DrawText(g, meta, metaFont,
                new Rectangle(textX, 32, metaW, 18), Theme.TextTertiary,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);

            if (showSpace)
            {
                // Vertically center the label + bar as a two-line group so it lines up
                // with the Mounted pill on its right instead of floating below the meta.
                // Group height = label(14) + gap(3) + bar(4) = 21; nudge up 1px so it
                // reads as sitting inside the pill's y band.
                const int groupHeight = 21;
                var groupTop = (Height - groupHeight) / 2;
                var labelRect = new Rectangle(textRight - spaceLabelW, groupTop, spaceLabelW, 14);
                TextRenderer.DrawText(g, spaceLabel, metaFont, labelRect, Theme.TextSecondary,
                    TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

                var barRect = new Rectangle(labelRect.Left, labelRect.Bottom + 3, spaceLabelW, 4);
                using (var bg = new SolidBrush(Theme.SurfaceSunken))
                using (var path = Drawing.RoundedRect(barRect, 2))
                    g.FillPath(bg, path);
                var ratio = Math.Clamp((double)spaceUsedBytes / spaceTotalBytes, 0, 1);
                if (ratio > 0)
                {
                    // Palette: green < 85%, amber 85–95%, red 95%+.
                    var fillColor = ratio >= 0.95 ? Color.FromArgb(0xE0, 0x3B, 0x3B)
                                   : ratio >= 0.85 ? Theme.Warn
                                   : Theme.Good;
                    var fillW = Math.Max(3, (int)(barRect.Width * ratio));
                    using var fill = new SolidBrush(fillColor);
                    using var fp = Drawing.RoundedRect(new Rectangle(barRect.X, barRect.Y, fillW, barRect.Height), 2);
                    g.FillPath(fill, fp);
                }
            }
            titleFont.Dispose();
            metaFont.Dispose();

            // Status pill
            var pillRect = new Rectangle(Width - actionsW - pillW, (Height - 22) / 2, pillW - 8, 22);
            DrawPill(g, pillRect);
        }

        private void DrawPill(Graphics g, Rectangle rect)
        {
            // Pill label folds in AutoMount for the two states where it's meaningful.
            // "Auto-mounted" = currently mounted AND set to auto-mount on startup.
            // "Auto"         = idle right now but will auto-mount when possible.
            // External/Detached ignore automount — they're error states.
            var (label, color, tint) = Status switch
            {
                MountRowStatus.Mounted  => (Mount.AutoMount ? "Auto-mounted" : "Mounted", Theme.Good, Theme.GoodTint),
                MountRowStatus.External => ("External", Theme.Warn, Theme.WarnTint),
                MountRowStatus.Detached => ("Detached", Theme.Warn, Theme.WarnTint),
                _                       => (Mount.AutoMount ? "Auto"     : "Stopped",     Theme.Idle, Theme.IdleTint),
            };

            using (var bg = new SolidBrush(tint))
            using (var path = Drawing.RoundedRect(rect, rect.Height / 2))
                g.FillPath(bg, path);

            var pip = new Rectangle(rect.X + 8, rect.Y + (rect.Height - 6) / 2, 6, 6);
            using (var brush = new SolidBrush(color))
                g.FillEllipse(brush, pip);

            var pillFont = new Font(Font.FontFamily, 8.25f, FontStyle.Bold);
            TextRenderer.DrawText(g, label, pillFont,
                new Rectangle(pip.Right + 4, rect.Y, rect.Right - pip.Right - 8, rect.Height),
                color, TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            pillFont.Dispose();
        }

        private string FormatMeta()
        {
            // Auto-mount is now expressed by the pill label, so the meta line just
            // carries the VFS cache mode (which the pill can't easily fit).
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(Mount.VfsCacheMode)) parts.Add($"VFS {Mount.VfsCacheMode}");
            return string.Join(" · ", parts);
        }

        private static string FormatSpaceLabel(long used, long total)
        {
            if (total <= 0) return "";
            return $"{FormatBytes(used)} / {FormatBytes(total)}";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
            var i = 0;
            double v = bytes;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v >= 100 ? $"{v:F0} {u[i]}" : $"{v:F1} {u[i]}";
        }

        // Compact rate label for the row strip — one decimal, single unit. Deliberately
        // narrower than the poller's full FormatRate so it stays out of the title's way.
        private static string FormatRateShort(double bps)
        {
            if (bps < 1024) return "0";
            string[] u = { "KB/s", "MB/s", "GB/s" };
            var i = 0;
            var v = bps / 1024.0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v >= 100 ? $"{v:F0} {u[i]}" : $"{v:F1} {u[i]}";
        }
    }
}
