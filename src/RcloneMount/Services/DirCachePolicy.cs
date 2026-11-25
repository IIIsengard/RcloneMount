using System;
using System.Collections.Generic;
using System.Linq;
using RcloneMount.Models;

namespace RcloneMount.Services
{
    // Picks the directory-cache flags for a mount. A backend that reports remote changes
    // (rclone's ChangeNotify feature) can keep listings for a long time because --poll-interval
    // invalidates them as files change. Every other backend must re-list often, so it keeps 15 s.
    //
    // Source of the polling list: methods named ChangeNotify in rclone's backend/ tree (tag v1.76.0:
    // drive, onedrive, dropbox, box, pcloud, pixeldrain, huaweidrive; iclouddrive only for its
    // Photos sub-fs, so it is left out), and `rclone backend features` run on v1.76.0, which printed
    // ChangeNotify true for dropbox/box/pcloud and for crypt/alias over dropbox, false for s3, webdav,
    // local and crypt over s3. rclone's overview.md table has no ChangeNotify column. OneDrive with
    // region=cn turns it off (backend/onedrive/onedrive.go, rclone issue 6444).
    // Wrapper backends (crypt, alias, chunker, hasher, compress, union, combine) inherit what they wrap.
    internal static class DirCachePolicy
    {
        private static readonly HashSet<string> Polling = new(StringComparer.OrdinalIgnoreCase)
            { "drive", "onedrive", "dropbox", "box", "pcloud", "pixeldrain", "huaweidrive" };
        private static readonly HashSet<string> Wraps = new(StringComparer.OrdinalIgnoreCase)
            { "crypt", "alias", "chunker", "hasher", "compress" };
        private static readonly HashSet<string> WrapsMany = new(StringComparer.OrdinalIgnoreCase)
            { "union", "combine" };

        internal static string[] FlagsFor(string remoteName, IReadOnlyList<RcloneRemote> remotes) =>
            Polls(remoteName, remotes, 0)
                ? new[] { "--dir-cache-time", "1000h", "--poll-interval", "15s" }
                : new[] { "--dir-cache-time", "15s" };

        // shortcut: quoted upstream names with spaces, connection-string remotes (":drive,...:")
        // and local paths all count as "does not poll", the safe 15 s default; upgrade if users hit it.
        private static bool Polls(string name, IReadOnlyList<RcloneRemote> remotes, int depth)
        {
            if (depth > 5) return false; // alias loops
            var r = remotes.FirstOrDefault(x => x.Name == name);
            if (r is null) return false;
            if (Polling.Contains(r.Type))
                return !(string.Equals(r.Type, "onedrive", StringComparison.OrdinalIgnoreCase)
                         && r.Parameters.TryGetValue("region", out var region) && region == "cn");
            if (Wraps.Contains(r.Type))
                return r.Parameters.TryGetValue("remote", out var w) && Polls(RemoteOf(w), remotes, depth + 1);
            if (WrapsMany.Contains(r.Type) && r.Parameters.TryGetValue("upstreams", out var ups))
            {
                var parts = ups.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 0 && parts.All(u => Polls(RemoteOf(u), remotes, depth + 1));
            }
            return false;
        }

        // "name:path[:ro]" -> "name"; combine's "dir=name:path" -> "name". No colon = local path.
        private static string RemoteOf(string spec)
        {
            var colon = spec.IndexOf(':');
            if (colon <= 0) return "";
            var eq = spec.IndexOf('=');
            var start = eq >= 0 && eq < colon ? eq + 1 : 0;
            return spec.Substring(start, colon - start);
        }
    }
}
