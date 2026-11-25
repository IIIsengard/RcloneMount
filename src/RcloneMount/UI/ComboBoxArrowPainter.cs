using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RcloneMount.UI
{
    // Attaches to a ComboBox's window and paints over its drop-down arrow well after the
    // OS control finishes painting. WinForms' ComboBox is a wrapped native control so this
    // is the only reliable way to hide the light-gray arrow area without owner-drawing the
    // whole combo. Runs after WM_PAINT so it overwrites whatever the native combo drew.
    internal sealed class ComboBoxArrowPainter : NativeWindow
    {
        private const int WM_PAINT       = 0x000F;
        private const int WM_ERASEBKGND  = 0x0014;
        // Widen paint area beyond SM_CXVSCROLL — Win11 leaves a 1-2 px strip that peeks
        // through and reads as a lighter stripe next to the arrow. 26 px reliably covers it
        // at standard 100% DPI without eating into the text region on the small combos.
        private const int ArrowAreaWidth = 26;
        private readonly ComboBox combo;

        private ComboBoxArrowPainter(ComboBox cb)
        {
            combo = cb;
            if (cb.IsHandleCreated) AssignHandle(cb.Handle);
            else cb.HandleCreated += (_, _) => AssignHandle(cb.Handle);
            cb.HandleDestroyed += (_, _) => ReleaseHandle();
            cb.MouseEnter += (_, _) => cb.Invalidate();
            cb.MouseLeave += (_, _) => cb.Invalidate();
        }

        public static void Attach(ComboBox cb)
        {
            if (cb.Tag as string == "arrow-painted") return;
            cb.Tag = "arrow-painted";
            var _ = new ComboBoxArrowPainter(cb);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ERASEBKGND)
            {
                try
                {
                    using var g = Graphics.FromHdc(m.WParam);
                    using var bg = new SolidBrush(Theme.SurfaceRaised);
                    g.FillRectangle(bg, combo.ClientRectangle);
                    m.Result = (IntPtr)1;
                    return;
                }
                catch { }
            }

            base.WndProc(ref m);
            if (m.Msg == WM_PAINT)
                PaintArrow();
        }

        private void PaintArrow()
        {
            try
            {
                using var g = Graphics.FromHwnd(combo.Handle);
                var rect = new Rectangle(combo.ClientSize.Width - ArrowAreaWidth, 0,
                                         ArrowAreaWidth, combo.ClientSize.Height);
                using var bg = new SolidBrush(Theme.SurfaceRaised);
                g.FillRectangle(bg, rect);

                using var divider = new Pen(Theme.BorderSubtle);
                g.DrawLine(divider, rect.Left, 3, rect.Left, rect.Bottom - 3);

                g.SmoothingMode = SmoothingMode.AntiAlias;
                var cx = rect.X + rect.Width / 2;
                var cy = rect.Y + rect.Height / 2;
                var pts = new[]
                {
                    new Point(cx - 4, cy - 2),
                    new Point(cx + 4, cy - 2),
                    new Point(cx,     cy + 3)
                };
                using var fg = new SolidBrush(Theme.TextSecondary);
                g.FillPolygon(fg, pts);
            }
            catch { }
        }
    }
}
