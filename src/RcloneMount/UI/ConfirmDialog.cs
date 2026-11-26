using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RcloneMount.UI
{
    // Themed replacement for MessageBox.Show(Yes/No) — MessageBox uses the OS palette
    // which reads as a stark white pop-up in dark mode. This dialog matches the app.
    internal sealed class ConfirmDialog : Form
    {
        public static DialogResult Show(IWin32Window? owner, string title, string message,
            string confirmText = "Remove", string cancelText = "Cancel", bool destructive = true)
        {
            using var dlg = new ConfirmDialog(title, message, confirmText, cancelText, destructive);
            return dlg.ShowDialog(owner);
        }

        private ConfirmDialog(string title, string message, string confirmText, string cancelText, bool destructive)
        {
            Icon = AppIcon.Get();
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;
            const int width = 440, messageWidth = width - 96;
            var messageFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9.5f);
            // Grow with the message (e.g. a list of job names) so none of it is cut off.
            var needed = TextRenderer.MeasureText(message, messageFont, new Size(messageWidth, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height;
            var messageHeight = Math.Max(80, needed + 8);
            ClientSize = new Size(width, messageHeight + 88);

            var iconPanel = new Panel
            {
                Location = new Point(20, 22),
                Size = new Size(40, 40),
                BackColor = Color.Transparent,
            };
            iconPanel.Paint += (_, e) => DrawWarningGlyph(e.Graphics, iconPanel.ClientRectangle, destructive);

            var lblMessage = new Label
            {
                UseMnemonic = false,
                Text = message,
                Location = new Point(76, 22),
                Size = new Size(messageWidth, messageHeight),
                Font = messageFont,
                ForeColor = Theme.TextPrimary,
                BackColor = Color.Transparent,
                AutoEllipsis = false,
                TextAlign = ContentAlignment.TopLeft,
            };

            var btnCancel = new Button
            {
                Text = cancelText,
                Size = new Size(96, 30),
                DialogResult = DialogResult.No,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            var btnConfirm = new Button
            {
                Text = confirmText,
                Size = new Size(96, 30),
                DialogResult = DialogResult.Yes,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            btnCancel.Location  = new Point(ClientSize.Width - btnCancel.Width - 16, ClientSize.Height - 46);
            btnConfirm.Location = new Point(btnCancel.Left - btnConfirm.Width - 8, ClientSize.Height - 46);

            AcceptButton = btnConfirm;
            CancelButton = btnCancel;

            Controls.Add(iconPanel);
            Controls.Add(lblMessage);
            Controls.Add(btnConfirm);
            Controls.Add(btnCancel);

            DialogTheme.Apply(this);

            // Style the confirm button distinctly. Destructive → warn palette; benign → accent.
            if (destructive)
            {
                StyleDestructive(btnConfirm);
            }
            else
            {
                btnConfirm.BackColor = Theme.Accent;
                btnConfirm.ForeColor = Theme.TextOnAccent;
                btnConfirm.FlatAppearance.BorderColor = Theme.Accent;
                btnConfirm.FlatAppearance.MouseOverBackColor = Theme.AccentHover;
                btnConfirm.FlatAppearance.MouseDownBackColor = Theme.AccentHover;
            }
        }

        // Warn palette for a button that loses data or work; call after DialogTheme.Apply.
        internal static void StyleDestructive(Button b)
        {
            b.BackColor = Theme.Warn;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = Theme.Warn;
            b.FlatAppearance.MouseOverBackColor = DarkenBy(Theme.Warn, 0.10f);
            b.FlatAppearance.MouseDownBackColor = DarkenBy(Theme.Warn, 0.18f);
        }

        private static void DrawWarningGlyph(Graphics g, Rectangle rect, bool destructive)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var color = destructive ? Theme.Warn : Theme.Accent;
            var inset = 2;
            var side = Math.Min(rect.Width, rect.Height) - inset * 2;
            var cx = rect.Left + rect.Width / 2f;
            var cy = rect.Top + rect.Height / 2f;
            var half = side / 2f;
            using var path = new GraphicsPath();
            path.AddPolygon(new[]
            {
                new PointF(cx, cy - half),
                new PointF(cx + half, cy + half),
                new PointF(cx - half, cy + half),
            });
            using (var brush = new SolidBrush(Color.FromArgb(56, color)))
                g.FillPath(brush, path);
            using (var pen = new Pen(color, 2f))
                g.DrawPath(pen, path);

            var barTop = cy - half * 0.35f;
            var barBottom = cy + half * 0.22f;
            using (var pen = new Pen(color, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, cx, barTop, cx, barBottom);
            using (var brush = new SolidBrush(color))
                g.FillEllipse(brush, cx - 1.6f, cy + half * 0.42f, 3.2f, 3.2f);
        }

        private static Color DarkenBy(Color c, float amount)
        {
            var r = (int)Math.Max(0, c.R * (1 - amount));
            var gc = (int)Math.Max(0, c.G * (1 - amount));
            var b = (int)Math.Max(0, c.B * (1 - amount));
            return Color.FromArgb(c.A, r, gc, b);
        }
    }
}
