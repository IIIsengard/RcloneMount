using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RcloneMount.Services
{
    // Checks the per-mount VFS cache fields before they reach rclone's command line.
    // Each returns null when the text is fine (empty = "not set", no flag) or a message for the user.
    internal static class VfsCacheLimits
    {
        // rclone's fs.SizeSuffix (checked against v1.76): "off" in any case, or a number (ASCII digits)
        // with an optional suffix B/b, or K, M, G, T, P in either case followed by nothing, i or I, or
        // i + b/B (10G, 1.5G, 500M, 10GiB, 10GI, 10Gib). "10GB", "10kB", "10GIB", "10GIb", "1G2M" fail there.
        // A bare number means KiB.
        private static readonly Regex Size = new(
            @"^((?i:off)|([0-9]+(\.[0-9]+)?|\.[0-9]+)([bB]|[kKmMgGtTpP](i|I|i[bB])?)?)$", RegexOptions.CultureInvariant);

        internal static string? ValidateSize(string? text)
        {
            var t = (text ?? "").Trim();
            return t.Length == 0 || Size.IsMatch(t)
                ? null
                : $"\"{t}\" is not a size. Use a number with K, M, G or T (for example 10G or 500M), or off.";
        }

        internal static string? ValidateDir(string? text)
        {
            var t = (text ?? "").Trim();
            if (t.Length == 0) return null;
            if (t.IndexOfAny(Path.GetInvalidPathChars().Concat(new[] { '*', '?' }).ToArray()) >= 0
                || !Path.IsPathFullyQualified(t))
                return "The cache folder must be a full local path such as D:\\RcloneCache.";
            // A UNC share is not probed: that could hang on an unreachable server.
            if (!t.StartsWith(@"\\") && !Directory.Exists(Path.GetPathRoot(t)))
                return $"Drive {Path.GetPathRoot(t)} does not exist.";
            return null;
        }
    }
}
