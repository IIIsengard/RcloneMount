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
            // Prefer the real app.ico embedded in the exe via <ApplicationIcon>.
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    var extracted = Icon.ExtractAssociatedIcon(exe);
                    if (extracted is not null)
                    {
                        cached = extracted;
                        return cached;
                    }
                }
            }
            catch { }
            cached = Build(32);
            return cached;
        }

        // Runtime fallback for the rounded-square "rcM" tile. Kept visually in sync
        // with scripts/generate-app-icon.ps1, which produces the on-disk app.ico —
        // if you change the design, update both so the pre-launch (Explorer/taskbar)
        // and post-launch (dynamically drawn) icons don't diverge.
        private static Icon Build(int size)
        {
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode     = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                var radius = Math.Max(2, (int)Math.Round(size * 0.18));
                var rect   = new RectangleF(0, 0, size, size);
                using var path = Drawing.RoundedRect(rect, radius);

                using var body = new LinearGradientBrush(
                    rect,
                    Color.FromArgb(255,  74, 144, 226),
                    Color.FromArgb(255,  30,  80, 165),
                    90f);
                g.FillPath(body, path);

                // Top highlight for depth at larger sizes; at 16-24 px it just adds
                // noise so we skip it.
                if (size >= 32)
                {
                    var hiRect = new RectangleF(0, 0, size, size * 0.45f);
                    using var hi = new LinearGradientBrush(
                        hiRect,
                        Color.FromArgb(70, 255, 255, 255),
                        Color.FromArgb(0, 255, 255, 255),
                        90f);
                    g.SetClip(path);
                    g.FillRectangle(hi, hiRect);
                    g.ResetClip();
                }

                using var outline = new Pen(Color.FromArgb(255, 20, 55, 120), Math.Max(1, size / 48f));
                g.DrawPath(outline, path);

                // Two-row "rc/M" monogram at ≥24 px; below that the small font
                // sizes turn to mush so we fall back to a single bold "M" that
                // still reads at a taskbar glance.
                using var textBrush = new SolidBrush(Color.White);
                using var fmt = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap,
                };
                if (size < 24)
                {
                    using var mFont = new Font("Segoe UI", Math.Max(4, size * 0.68f), FontStyle.Bold, GraphicsUnit.Pixel);
                    g.DrawString("M", mFont, textBrush, new RectangleF(0, size * 0.03f, size, size), fmt);
                }
                else
                {
                    // The M is larger than "rc" because a single glyph on the
                    // bottom row needs to visually match a two-letter row above.
                    using var rcFont = new Font("Segoe UI", Math.Max(6, size * 0.30f), FontStyle.Bold, GraphicsUnit.Pixel);
                    using var mFont  = new Font("Segoe UI", Math.Max(8, size * 0.58f), FontStyle.Bold, GraphicsUnit.Pixel);
                    g.DrawString("rc", rcFont, textBrush, new RectangleF(0, size * 0.06f, size, size * 0.38f), fmt);
                    g.DrawString("M",  mFont,  textBrush, new RectangleF(0, size * 0.36f, size, size * 0.62f), fmt);
                }
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
