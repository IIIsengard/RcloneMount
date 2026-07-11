using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace RcloneMount.UI
{
    internal static class ProviderVisuals
    {
        private static readonly Dictionary<string, Color> BrandColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["drive"]         = Color.FromArgb(0x1A, 0x73, 0xE8),
            ["onedrive"]      = Color.FromArgb(0x00, 0x78, 0xD4),
            ["dropbox"]       = Color.FromArgb(0x00, 0x61, 0xFF),
            ["box"]           = Color.FromArgb(0x00, 0x37, 0x86),
            ["pcloud"]        = Color.FromArgb(0x22, 0x82, 0xB9),
            ["mega"]          = Color.FromArgb(0xD9, 0x27, 0x2E),
            ["yandex"]        = Color.FromArgb(0xFF, 0xCC, 0x00),
            ["jottacloud"]    = Color.FromArgb(0xE6, 0x00, 0x28),
            ["s3"]            = Color.FromArgb(0xFF, 0x99, 0x00),
            ["b2"]            = Color.FromArgb(0xC0, 0x27, 0x27),
            ["gcs"]           = Color.FromArgb(0x42, 0x85, 0xF4),
            ["azureblob"]     = Color.FromArgb(0x00, 0x8A, 0xD7),
            ["azurefiles"]    = Color.FromArgb(0x00, 0x8A, 0xD7),
            ["swift"]         = Color.FromArgb(0xED, 0x1C, 0x24),
            ["oracleobjectstorage"] = Color.FromArgb(0xF8, 0x00, 0x00),
            ["ftp"]           = Color.FromArgb(0x66, 0x66, 0x66),
            ["sftp"]          = Color.FromArgb(0x2C, 0x3E, 0x50),
            ["webdav"]        = Color.FromArgb(0x4A, 0x4A, 0x8A),
            ["http"]          = Color.FromArgb(0x55, 0x55, 0x55),
            ["local"]         = Color.FromArgb(0x8E, 0x8E, 0x93),
            ["alias"]         = Color.FromArgb(0xA0, 0x77, 0x00),
            ["crypt"]         = Color.FromArgb(0x38, 0x8E, 0x3C),
            ["chunker"]       = Color.FromArgb(0x66, 0x33, 0x99),
            ["union"]         = Color.FromArgb(0x88, 0x44, 0xAA),
            ["compress"]      = Color.FromArgb(0x00, 0x80, 0x66),
            ["cache"]         = Color.FromArgb(0x5E, 0x35, 0xB1),
            ["hidrive"]       = Color.FromArgb(0xE5, 0x00, 0x00),
            ["sharefile"]     = Color.FromArgb(0x00, 0x6E, 0xC7),
            ["sugarsync"]     = Color.FromArgb(0x00, 0xA6, 0xE1),
            ["putio"]         = Color.FromArgb(0xCC, 0x33, 0x00),
            ["mail.ru"]       = Color.FromArgb(0x00, 0x5F, 0xF9),
            ["opendrive"]     = Color.FromArgb(0xF3, 0x9C, 0x12),
            ["premiumizeme"]  = Color.FromArgb(0xFF, 0x66, 0x00),
            ["zoho"]          = Color.FromArgb(0xE4, 0x2A, 0x2A),
            ["smb"]           = Color.FromArgb(0x37, 0x47, 0x5B),
            ["storj"]         = Color.FromArgb(0x2E, 0x33, 0xFF),
            ["seafile"]       = Color.FromArgb(0xF6, 0x87, 0x1F),
            ["koofr"]         = Color.FromArgb(0x18, 0x8F, 0x4E),
            ["fichier"]       = Color.FromArgb(0xCC, 0x00, 0x00),
            ["filefabric"]    = Color.FromArgb(0x00, 0x6F, 0x9C),
            ["uptobox"]       = Color.FromArgb(0x00, 0x99, 0xCC),
            ["internetarchive"] = Color.FromArgb(0x00, 0x00, 0x00),
            ["hdfs"]          = Color.FromArgb(0xFF, 0x66, 0x33)
        };

        public static Color ColorFor(string providerName)
        {
            if (string.IsNullOrEmpty(providerName)) return Color.SlateGray;
            if (BrandColors.TryGetValue(providerName, out var c)) return c;
            // Deterministic fallback: hash to hue so unknown backends still get distinct colors.
            var h = 0;
            foreach (var ch in providerName) h = unchecked(h * 31 + ch);
            var hue = ((h & 0x7FFFFFFF) % 360) / 360f;
            return FromHsv(hue, 0.55f, 0.75f);
        }

        public static string BadgeLetter(string providerName) =>
            string.IsNullOrEmpty(providerName) ? "?" : providerName.Substring(0, 1).ToUpperInvariant();

        // Optional embedded icons keyed by provider name. Populate via LoadIcon when we ship
        // real per-brand assets; until then, DrawBadge falls back to the colored initial tile.
        private static readonly Dictionary<string, Image> IconCache = new(StringComparer.OrdinalIgnoreCase);

        public static void RegisterIcon(string providerName, Image image)
        {
            if (string.IsNullOrEmpty(providerName) || image is null) return;
            IconCache[providerName] = image;
        }

        public static bool HasIcon(string providerName) => IconCache.ContainsKey(providerName ?? "");

        public static void DrawBadge(Graphics g, Rectangle rect, string providerName)
        {
            var prevSmoothing = g.SmoothingMode;
            var prevText = g.TextRenderingHint;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            var color = ColorFor(providerName);
            using (var brush = new SolidBrush(color))
            using (var path = RoundedRect(rect, 6))
                g.FillPath(brush, path);

            if (!string.IsNullOrEmpty(providerName) && IconCache.TryGetValue(providerName, out var icon))
            {
                var pad = Math.Max(2, rect.Height / 8);
                var iconRect = new Rectangle(rect.X + pad, rect.Y + pad, rect.Width - pad * 2, rect.Height - pad * 2);
                g.DrawImage(icon, iconRect);
            }
            else
            {
                var letter = BadgeLetter(providerName);
                using var font = new Font("Segoe UI", rect.Height * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(letter, font, Brushes.White, rect, fmt);
            }

            g.SmoothingMode = prevSmoothing;
            g.TextRenderingHint = prevText;
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color FromHsv(float h, float s, float v)
        {
            var c = v * s;
            var x = c * (1 - Math.Abs((h * 6 % 2) - 1));
            var m = v - c;
            float r, g, b;
            if      (h < 1f / 6f) { r = c; g = x; b = 0; }
            else if (h < 2f / 6f) { r = x; g = c; b = 0; }
            else if (h < 3f / 6f) { r = 0; g = c; b = x; }
            else if (h < 4f / 6f) { r = 0; g = x; b = c; }
            else if (h < 5f / 6f) { r = x; g = 0; b = c; }
            else                  { r = c; g = 0; b = x; }
            return Color.FromArgb(
                (int)((r + m) * 255),
                (int)((g + m) * 255),
                (int)((b + m) * 255));
        }
    }
}
