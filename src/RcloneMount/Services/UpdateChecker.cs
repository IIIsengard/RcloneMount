using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RcloneMount.Services
{
    public record LatestRelease(string Version, string TagName, IReadOnlyList<ReleaseAsset> Assets);
    public record ReleaseAsset(string Name, string Url, long Size);

    public static class UpdateChecker
    {
        private static readonly HttpClient http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("RcloneMount/1.0");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        // GitHub repo Velopack checks for app updates. Placeholder until the app has a
        // public home; while it says "REPLACE-WITH" the app-update check stays silent.
        // When the repo is created, swap the value; nothing else changes.
        public const string AppRepoUrl = "https://github.com/REPLACE-WITH-OWNER/RcloneMount";

        public static bool AppRepoConfigured => !AppRepoUrl.Contains("REPLACE-WITH", StringComparison.Ordinal);

        public static Task<LatestRelease?> LatestRcloneAsync() => LatestGithubReleaseAsync("rclone/rclone");
        public static Task<LatestRelease?> LatestWinFspAsync() => LatestGithubReleaseAsync("winfsp/winfsp");

        private static async Task<LatestRelease?> LatestGithubReleaseAsync(string ownerRepo)
        {
            try
            {
                var resp = await http.GetAsync($"https://api.github.com/repos/{ownerRepo}/releases/latest").ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return null;

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                var assets = new List<ReleaseAsset>();
                if (root.TryGetProperty("assets", out var arr))
                {
                    foreach (var a in arr.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                        var size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                        if (!string.IsNullOrEmpty(url))
                            assets.Add(new ReleaseAsset(name, url, size));
                    }
                }
                return new LatestRelease(NormalizeVersion(tag), tag, assets);
            }
            catch
            {
                return null;
            }
        }

        // "rclone v1.65.2\n..." -> "1.65.2"; "v1.65.2" -> "1.65.2"
        public static string NormalizeVersion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var m = Regex.Match(raw, @"v?(\d+(?:\.\d+){1,3})");
            return m.Success ? m.Groups[1].Value : raw.Trim();
        }

        // Returns -1 if left < right, 0 if equal, 1 if left > right; null on parse failure.
        public static int? Compare(string left, string right)
        {
            var l = ParseParts(left);
            var r = ParseParts(right);
            if (l is null || r is null) return null;

            var len = Math.Max(l.Length, r.Length);
            for (var i = 0; i < len; i++)
            {
                var a = i < l.Length ? l[i] : 0;
                var b = i < r.Length ? r[i] : 0;
                if (a < b) return -1;
                if (a > b) return 1;
            }
            return 0;
        }

        private static int[]? ParseParts(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            var norm = NormalizeVersion(version);
            var parts = norm.Split('.');
            var result = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out var v))
                    return null;
                result[i] = v;
            }
            return result;
        }
    }
}
