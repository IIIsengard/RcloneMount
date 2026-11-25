using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using RcloneMount.Models;

namespace RcloneMount.UI
{
    internal static class ProviderVisuals
    {
        // Runtime map "internal name" → "friendly description", populated the first time we
        // successfully call rclone's provider list. Keeps display names in sync with whatever
        // rclone version is installed.
        private static readonly Dictionary<string, string> DescriptionCatalog =
            new(StringComparer.OrdinalIgnoreCase);

        // Static fallback so the mounts card, remote rows, and dialogs read nicely before
        // (or when) the runtime catalog is loaded. Covers the common backends by hand.
        private static readonly Dictionary<string, string> DefaultDescriptions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["drive"]        = "Google Drive",
                ["onedrive"]     = "Microsoft OneDrive",
                ["dropbox"]      = "Dropbox",
                ["box"]          = "Box",
                ["b2"]           = "Backblaze B2",
                ["s3"]           = "Amazon S3",
                ["gcs"]          = "Google Cloud Storage",
                ["azureblob"]    = "Microsoft Azure Blob Storage",
                ["azurefiles"]   = "Microsoft Azure Files",
                ["ftp"]          = "FTP",
                ["sftp"]         = "SSH/SFTP",
                ["webdav"]       = "WebDAV",
                ["http"]         = "HTTP",
                ["local"]        = "Local disk",
                ["alias"]        = "Alias",
                ["crypt"]        = "Encrypted",
                ["union"]        = "Union",
                ["compress"]     = "Compressed",
                ["cache"]        = "Cache",
                ["chunker"]      = "Chunker",
                ["mega"]         = "Mega",
                ["pcloud"]       = "pCloud",
                ["yandex"]       = "Yandex Disk",
                ["jottacloud"]   = "Jottacloud",
                ["smb"]          = "SMB / CIFS",
                ["storj"]        = "Storj",
                ["seafile"]      = "Seafile",
                ["koofr"]        = "Koofr",
                ["mailru"]       = "Mail.ru Cloud",
                ["opendrive"]    = "OpenDrive",
                ["premiumizeme"] = "premiumize.me",
                ["putio"]        = "put.io",
                ["sharefile"]    = "Citrix ShareFile",
                ["sugarsync"]    = "SugarSync",
                ["swift"]        = "OpenStack Swift",
                ["hidrive"]      = "HiDrive",
                ["oracleobjectstorage"] = "Oracle Object Storage",
                ["internetarchive"] = "Internet Archive",
                ["googlephotos"] = "Google Photos",
                ["protondrive"]  = "Proton Drive",
                ["iclouddrive"]  = "Apple iCloud Drive",
                ["pikpak"]       = "PikPak",
                ["linkbox"]      = "Linkbox",
                ["pixeldrain"]   = "Pixeldrain",
                ["quatrix"]      = "Quatrix",
                ["filefabric"]   = "Storage Made Easy File Fabric",
                ["filelu"]       = "FileLu",
                ["combine"]      = "Combine",
                ["hasher"]       = "Hasher",
                ["netstorage"]   = "Akamai NetStorage",
                ["sia"]          = "Sia Decentralized Cloud",
                ["qingstor"]     = "QingCloud Object Storage",
                ["ulozto"]       = "Uloz.to",
                ["fichier"]      = "1Fichier",
                ["zoho"]         = "Zoho WorkDrive",
                ["gofile"]       = "Gofile",
                ["imagekit"]     = "ImageKit.io",
                ["internxt"]     = "Internxt Drive",
                ["magalu"]       = "Magalu Object Storage",
            };

        public static void SetProviderCatalog(IEnumerable<RcloneProvider> providers)
        {
            DescriptionCatalog.Clear();
            foreach (var p in providers)
            {
                if (!string.IsNullOrEmpty(p.Name) && !string.IsNullOrEmpty(p.Description))
                    DescriptionCatalog[p.Name] = p.Description;
            }
        }

        // Human-friendly name for a provider given only its internal type ("drive", "b2").
        // Consults the runtime catalog first (accurate to installed rclone), then the static
        // fallback, and finally hands back the raw type if we've never heard of it.
        public static string FriendlyType(string? typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return "";
            if (DescriptionCatalog.TryGetValue(typeName, out var runtime))
                return ShortenDescription(runtime);
            if (DefaultDescriptions.TryGetValue(typeName, out var stat))
                return stat;
            return typeName;
        }

        public static string FriendlyType(RcloneProvider provider)
        {
            if (provider is null) return "";
            var shortened = ShortenDescription(provider.Description);
            return !string.IsNullOrEmpty(shortened) ? shortened : (provider.Name ?? "");
        }

        // Trims verbose rclone descriptions at natural break points so long ones like S3's
        // "…Compliant Storage Providers including AWS, Alibaba, Ceph, …" collapse to a caption
        // that still fits a header row.
        private static string ShortenDescription(string? desc)
        {
            if (string.IsNullOrWhiteSpace(desc)) return "";
            var s = desc.Trim();
            string[] cuts = { " including ", " – ", " — ", " - ", ",", "(" };
            foreach (var c in cuts)
            {
                var i = s.IndexOf(c, StringComparison.OrdinalIgnoreCase);
                if (i > 0) s = s.Substring(0, i).Trim();
            }
            return s;
        }


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
            ["mailru"]        = Color.FromArgb(0x00, 0x5F, 0xF9),
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
            ["hdfs"]          = Color.FromArgb(0xFF, 0x66, 0x33),
            ["googlephotos"]  = Color.FromArgb(0xEA, 0x43, 0x35),
            ["protondrive"]   = Color.FromArgb(0x6D, 0x4A, 0xFF),
            ["iclouddrive"]   = Color.FromArgb(0x00, 0x7A, 0xFF),
            ["pikpak"]        = Color.FromArgb(0x00, 0xB4, 0xFF),
            ["linkbox"]       = Color.FromArgb(0x14, 0x8F, 0xF7),
            ["pixeldrain"]    = Color.FromArgb(0xC8, 0x36, 0x2D),
            ["quatrix"]       = Color.FromArgb(0x00, 0x4A, 0x7C),
            ["filelu"]        = Color.FromArgb(0x1E, 0x88, 0xE5),
            ["combine"]       = Color.FromArgb(0x6A, 0x5A, 0xCD),
            ["hasher"]        = Color.FromArgb(0x55, 0x6B, 0x2F),
            ["netstorage"]    = Color.FromArgb(0x00, 0x93, 0xDD),
            ["sia"]           = Color.FromArgb(0x20, 0xC7, 0x53),
            ["qingstor"]      = Color.FromArgb(0x00, 0xB4, 0xEC),
            ["ulozto"]        = Color.FromArgb(0x00, 0x99, 0xCC),
            ["gofile"]        = Color.FromArgb(0xFF, 0x71, 0x00),
            ["imagekit"]      = Color.FromArgb(0x2C, 0x67, 0xFF),
            ["internxt"]      = Color.FromArgb(0x00, 0x66, 0xFF),
            ["magalu"]        = Color.FromArgb(0xE6, 0x1A, 0x27)
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

        // Maps an rclone sub-provider value (from an s3 `provider` option or a webdav
        // `vendor` option) onto the icon filename we ship, when the raw value doesn't
        // already match a filename. Case-insensitive on both sides.
        private static readonly Dictionary<string, string> SubProviderIconAliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // s3.provider values
                ["Backblaze"]       = "b2",
                ["BackblazeB2"]     = "b2",
                ["GCS"]             = "google cloud storage",
                ["HCP"]             = "hitachivantara",
                ["HuaweiOBS"]       = "huaweiobs",
                ["IBMCOS"]          = "ibmcoss3",
                ["IDrive"]          = "idrivee2",
                ["IONOS"]           = "ionoscloud",
                ["ImpossibleCloud"] = "impossiblecloud",
                ["OVHcloud"]        = "ovhs3",
                ["Hetzner"]         = "hetznerobject",
                ["LyveCloud"]       = "seagate",
                // webdav.vendor values
                ["infinitescale"]   = "owncloud",
            };

        // Given a configured remote, resolve the icon key that best represents it —
        // for s3 remotes with a `provider=` param (Alibaba, Wasabi, IDrive, …) or webdav
        // with a `vendor=` (fastmail, nextcloud, …), prefer the sub-provider's own icon
        // when we ship one; otherwise fall back to the base type ("s3", "webdav", …).
        public static string IconKeyFor(RcloneRemote? remote)
        {
            if (remote is null) return "";
            var type = remote.Type ?? "";
            string? sub = null;
            if (remote.Parameters is not null)
            {
                if (string.Equals(type, "s3", StringComparison.OrdinalIgnoreCase))
                    remote.Parameters.TryGetValue("provider", out sub);
                else if (string.Equals(type, "webdav", StringComparison.OrdinalIgnoreCase))
                    remote.Parameters.TryGetValue("vendor", out sub);
            }
            return IconKeyFor(type, sub);
        }

        // Same resolution used by picker rows: prefer the sub-provider's icon (via alias map
        // or a direct filename match) when we ship one; otherwise fall back to the base type.
        public static string IconKeyFor(string baseType, string? subValue)
        {
            if (!string.IsNullOrEmpty(subValue))
            {
                if (SubProviderIconAliases.TryGetValue(subValue, out var mapped) && IconCache.ContainsKey(mapped))
                    return mapped;
                if (IconCache.ContainsKey(subValue)) return subValue;
                var lowered = subValue.ToLowerInvariant();
                if (IconCache.ContainsKey(lowered)) return lowered;
            }
            return baseType ?? "";
        }

        public static void DrawBadge(Graphics g, Rectangle rect, RcloneRemote? remote) =>
            DrawBadge(g, rect, IconKeyFor(remote));

        // Scans a directory for provider logos and registers each as an icon keyed by its
        // filename (without extension). Case-insensitive; silently skips unreadable files so
        // a bad asset can't crash startup. Call from Program.Main after Theme.SetMode.
        public static void LoadIconsFromDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return;
            foreach (var path in System.IO.Directory.EnumerateFiles(dir))
            {
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp" && ext != ".gif")
                    continue;
                try
                {
                    var key = System.IO.Path.GetFileNameWithoutExtension(path);
                    using var stream = System.IO.File.OpenRead(path);
                    // Copy into memory so the file handle is released immediately.
                    var img = Image.FromStream(new System.IO.MemoryStream(System.IO.File.ReadAllBytes(path)));
                    RegisterIcon(key, img);
                }
                catch { }
            }
        }

        public static void DrawBadge(Graphics g, Rectangle rect, string providerName)
        {
            var prevSmoothing = g.SmoothingMode;
            var prevText = g.TextRenderingHint;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            Image? icon = null;
            if (!string.IsNullOrEmpty(providerName))
                IconCache.TryGetValue(providerName, out icon);

            // Only paint the brand-color tile as a fallback when no icon is provided —
            // registered icons carry their own artwork and should render on the transparent
            // surface behind the badge.
            if (icon is null)
            {
                var color = ColorFor(providerName);
                using var brush = new SolidBrush(color);
                using var path = Drawing.RoundedRect(rect, 6);
                g.FillPath(brush, path);

                var letter = BadgeLetter(providerName);
                using var font = new Font("Segoe UI", rect.Height * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(letter, font, Brushes.White, rect, fmt);
            }
            else
            {
                var prevInterp = g.InterpolationMode;
                var prevOffset = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(icon, rect);
                g.InterpolationMode = prevInterp;
                g.PixelOffsetMode = prevOffset;
            }

            g.SmoothingMode = prevSmoothing;
            g.TextRenderingHint = prevText;
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
