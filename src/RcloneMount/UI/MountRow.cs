using System;
using System.Drawing;
using System.Windows.Forms;
using RcloneMount.Models;

namespace RcloneMount.UI
{
    public enum MountRowStatus { Mounted, Stopped, External }

    // A row in the Mounts card: drive chip · title · meta · status pill · action buttons.
    // Mixed rendering: chip + pill are painted; buttons are real Buttons for hit-testing
    // and a11y.
    public class MountRow : UserControl
    {
        private const int RowHeight = 62;

        public Mount Mount { get; private set; } = new();
        public MountRowStatus Status { get; private set; } = MountRowStatus.Stopped;

        public event EventHandler? MountRequested;
        public event EventHandler? UnmountRequested;
        public event EventHandler? OpenRequested;
        public event EventHandler? EditRequested;
        public event EventHandler? RemoveRequested;

        private readonly FlowLayoutPanel actions;
        private readonly Button btnPrimary;
        private readonly Button btnSecondary;
        private readonly Button btnEdit;
        private readonly Button btnRemove;

        public MountRow()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
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

            btnPrimary   = MakeButton("Mount",   primary: true);
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
            Resize += (_, _) => RepositionActions();
            RepositionActions();
        }

        private static Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = false,
                Size = new Size(72, 26),
                Margin = new Padding(0, 0, 6, 0),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.25f, FontStyle.Regular),
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

            btnPrimary.Text = (mounted || external) ? "Unmount" : "Mount";
            ApplyButtonStyle(btnPrimary, primary: !(mounted || external));

            btnSecondary.Visible = mounted || external;
            btnSecondary.Text = "Open";

            RepositionActions();
            Invalidate();
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

            // Drive-letter chip
            var chip = new Rectangle(20, (Height - 34) / 2, 46, 34);
            var chipBg = mounted ? Theme.AccentTint       : Theme.BorderEmphasis;
            var chipFg = mounted ? Theme.Accent           : Theme.TextSecondary;
            using (var brush = new SolidBrush(chipBg))
            using (var path = RoundedRect(chip, 6))
                g.FillPath(brush, path);

            var chipFont = new Font(FontFamily.GenericMonospace, 11.5f, FontStyle.Bold);
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var brush = new SolidBrush(chipFg))
                g.DrawString(Mount.DriveLetter, chipFont, brush, chip, fmt);
            chipFont.Dispose();

            // Title + meta
            var textX = chip.Right + 12;
            var actionsW = actions.Width + 12;
            var pillW = 92;
            var textRight = Width - actionsW - pillW - 12;
            var textW = Math.Max(60, textRight - textX);

            var title = string.IsNullOrEmpty(Mount.RemotePath) ? "(root)" : Mount.RemotePath;
            var meta = FormatMeta();

            var titleFont = new Font(Font.FontFamily, 9.75f, FontStyle.Bold);
            var metaFont  = new Font(Font.FontFamily, 8.25f);
            TextRenderer.DrawText(g, title, titleFont,
                new Rectangle(textX, 10, textW, 20), Theme.TextPrimary,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
            TextRenderer.DrawText(g, meta, metaFont,
                new Rectangle(textX, 32, textW, 18), Theme.TextTertiary,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
            titleFont.Dispose();
            metaFont.Dispose();

            // Status pill
            var pillRect = new Rectangle(Width - actionsW - pillW, (Height - 22) / 2, pillW - 8, 22);
            DrawPill(g, pillRect);
        }

        private void DrawPill(Graphics g, Rectangle rect)
        {
            var (label, color, tint) = Status switch
            {
                MountRowStatus.Mounted  => ("Mounted",  Theme.Good, Theme.GoodTint),
                MountRowStatus.External => ("External", Theme.Warn, Theme.WarnTint),
                _                       => ("Stopped",  Theme.Idle, Theme.IdleTint),
            };

            using (var bg = new SolidBrush(tint))
            using (var path = RoundedRect(rect, rect.Height / 2))
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
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(Mount.VfsCacheMode)) parts.Add($"VFS {Mount.VfsCacheMode}");
            parts.Add(Mount.AutoMount ? "Auto-mount on" : "Auto-mount off");
            return string.Join(" · ", parts);
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle b, int r)
        {
            var d = r * 2;
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddArc(b.X, b.Y, d, d, 180, 90);
            p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
            p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
            p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
