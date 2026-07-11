using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RcloneMount.UI
{
    public static class AppIcon
    {
        private static Icon? cached;

        public static Icon Get()
        {
            if (cached is not null) return cached;
            cached = Build(32);
            return cached;
        }

        private static Icon Build(int size)
        {
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                var top = Color.FromArgb(255, 90, 155, 235);
                var bottom = Color.FromArgb(255, 45, 100, 180);

                using var body = new LinearGradientBrush(new Rectangle(0, 0, size, size), top, bottom, 90f);
                using var outline = new Pen(Color.FromArgb(255, 30, 70, 140), Math.Max(1, size / 16f));

                // Cloud: three bumps + base rectangle
                float unit = size / 32f;
                var puffs = new[]
                {
                    new RectangleF(3 * unit, 12 * unit, 13 * unit, 13 * unit),
                    new RectangleF(9 * unit, 5 * unit, 15 * unit, 15 * unit),
                    new RectangleF(18 * unit, 11 * unit, 12 * unit, 12 * unit),
                };
                var baseRect = new RectangleF(3 * unit, 19 * unit, 27 * unit, 8 * unit);

                foreach (var p in puffs) g.FillEllipse(body, p);
                g.FillRectangle(body, baseRect);
                foreach (var p in puffs) g.DrawEllipse(outline, p);

                // Little drive-letter "M" inset
                using var textFont = new Font(FontFamily.GenericSansSerif, unit * 7f, FontStyle.Bold, GraphicsUnit.Pixel);
                var text = "R";
                var sz = g.MeasureString(text, textFont);
                using var textBrush = new SolidBrush(Color.White);
                g.DrawString(text, textFont, textBrush,
                    (size - sz.Width) / 2f,
                    (size - sz.Height) / 2f + unit * 1f);
            }

            var hIcon = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(hIcon).Clone();
            DestroyIcon(hIcon);
            return icon;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
