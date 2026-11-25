using System.Drawing;
using System.Drawing.Drawing2D;

namespace RcloneMount.UI
{
    internal static class Drawing
    {
        // Rectangle with four quarter-circle corners of the given radius. Caller disposes.
        internal static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            var d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            p.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            p.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            p.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
