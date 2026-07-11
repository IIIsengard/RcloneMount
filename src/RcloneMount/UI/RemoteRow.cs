using System;
using System.Drawing;
using System.Windows.Forms;
using RcloneMount.Models;

namespace RcloneMount.UI
{
    // A single row in the left rail of the Mounts page: badge + name + subtitle + status dot.
    // Owner-painted so the accent bar + tint look consistent regardless of the system theme.
    public class RemoteRow : UserControl
    {
        private bool selected;
        private bool hover;

        public RcloneRemote? Remote { get; private set; }
        public int MountsCount { get; private set; }
        public int RunningCount { get; private set; }

        public event EventHandler? Clicked;

        public bool IsSelected
        {
            get => selected;
            set { if (selected != value) { selected = value; Invalidate(); } }
        }

        public RemoteRow()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Height = 50;
            Cursor = Cursors.Hand;
            BackColor = Theme.SurfaceBase;
            MouseEnter += (_, _) => { hover = true; Invalidate(); };
            MouseLeave += (_, _) => { hover = false; Invalidate(); };
            Click += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);
        }

        public void Bind(RcloneRemote remote, int mountsCount, int runningCount)
        {
            Remote = remote;
            MountsCount = mountsCount;
            RunningCount = runningCount;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Background — hover / selected tint
            var bg = selected ? Theme.RailActiveBg
                   : hover    ? Theme.RailHoverBg
                              : Theme.SurfaceBase;
            using (var brush = new SolidBrush(bg))
                g.FillRectangle(brush, ClientRectangle);

            // Accent bar on left when selected
            if (selected)
            {
                using var accent = new SolidBrush(Theme.Accent);
                g.FillRectangle(accent, new Rectangle(0, 8, 3, Height - 16));
            }

            const int padX = 10;
            const int badgeSize = 32;
            var badge = new Rectangle(padX + 2, (Height - badgeSize) / 2, badgeSize, badgeSize);
            ProviderVisuals.DrawBadge(g, badge, Remote?.Type ?? "");

            // Labels
            var textX = badge.Right + 10;
            var textRight = Width - 24; // reserve room for status dot
            var textW = textRight - textX;

            var nameFont = new Font(Font.FontFamily, 9.5f, FontStyle.Bold);
            var subFont  = new Font(Font.FontFamily, 8.25f);

            var name = Remote?.Name ?? "";
            var sub  = FormatSubtitle();

            TextRenderer.DrawText(g, name, nameFont,
                new Rectangle(textX, 8, textW, 18), Theme.TextPrimary,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.Top);
            TextRenderer.DrawText(g, sub, subFont,
                new Rectangle(textX, 26, textW, 16), Theme.TextTertiary,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.Top);

            nameFont.Dispose();
            subFont.Dispose();

            // Status dot
            var dot = new Rectangle(Width - 22, (Height - 8) / 2, 8, 8);
            var dotColor = ResolveStatusColor(out var tintColor);
            using (var tint = new SolidBrush(tintColor))
                g.FillEllipse(tint, Rectangle.Inflate(dot, 3, 3));
            using (var solid = new SolidBrush(dotColor))
                g.FillEllipse(solid, dot);

            // Bottom hairline
            using var line = new Pen(Theme.BorderSubtle);
            g.DrawLine(line, textX, Height - 1, Width - 8, Height - 1);
        }

        private string FormatSubtitle()
        {
            var type = Remote?.Type ?? "";
            if (MountsCount == 0) return $"{type} · no mounts";
            if (MountsCount == 1) return $"{type} · 1 mount";
            return $"{type} · {MountsCount} mounts";
        }

        private Color ResolveStatusColor(out Color tint)
        {
            if (MountsCount == 0) { tint = Color.FromArgb(0, Theme.IdleTint); return Color.FromArgb(80, Theme.Idle); }
            if (RunningCount == MountsCount) { tint = Theme.GoodTint; return Theme.Good; }
            if (RunningCount == 0) { tint = Theme.IdleTint; return Theme.Idle; }
            tint = Theme.WarnTint; return Theme.Warn;
        }
    }
}
