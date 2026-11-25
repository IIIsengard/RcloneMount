using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RcloneMount.UI
{
    internal static class RoundedButtons
    {
        // No-op by default — Region-based rounding on WinForms Buttons produces aliased
        // corners that read as chipped rather than curved. Left as an opt-in with a positive
        // radius so existing callsites keep compiling; pass radius <= 0 (the default) to skip.
        public static void Apply(Control root, int radius = 0)
        {
            if (radius <= 0) return;
            foreach (Control c in root.Controls)
            {
                if (c is Button b)
                    Attach(b, radius);
                if (c.HasChildren)
                    Apply(c, radius);
            }
        }

        private static void Attach(Button b, int radius)
        {
            void Refresh()
            {
                if (b.Width <= 0 || b.Height <= 0) return;
                var r = System.Math.Max(2, System.Math.Min(radius, System.Math.Min(b.Width, b.Height) / 2));
                b.Region = new Region(BuildPath(new Rectangle(0, 0, b.Width, b.Height), r));
            }
            Refresh();
            b.Resize += (_, _) => Refresh();
        }

        private static GraphicsPath BuildPath(Rectangle rect, int radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
