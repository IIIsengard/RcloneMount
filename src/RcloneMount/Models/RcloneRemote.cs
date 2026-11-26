using System.Collections.Generic;
using System.Linq;

namespace RcloneMount.Models
{
    public class RcloneRemote
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, string> Parameters { get; set; } = new();

        // What a saved job or queued run holds once its remote is deleted. '<' is not allowed in a
        // remote name, so no later remote can match it, and rclone itself refuses it.
        private const string DeletedMark = "<deleted> ";

        internal static string MarkDeleted(string name) => DeletedMark + name;

        // The deleted remote's name, or null when `remote` is not such a mark.
        internal static string? DeletedName(string remote) =>
            remote?.StartsWith(DeletedMark, System.StringComparison.Ordinal) == true // null: hand-edited settings
                ? remote[DeletedMark.Length..] : null;

        internal static string? DeletedError(string remote) =>
            DeletedName(remote) is { } name ? $"Remote \"{name}\" was deleted. Edit the job and pick a remote." : null;

        // True when `remote` names a configured remote that `read` lacks. Not local disk, a deleted mark
        // or an on-the-fly ":backend:".
        internal static bool IsMissing(string remote, IEnumerable<RcloneRemote> read)
        {
            if (string.IsNullOrEmpty(remote) || remote[0] == ':' || DeletedName(remote) is not null) return false;
            var name = remote.Split(',')[0]; // "name,option=x" overrides settings of "name"
            // `config dump` leaves out remotes defined only by environment variables, which rclone inherits from us.
            return !read.Any(r => r.Name == name)
                && string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable($"RCLONE_CONFIG_{name.ToUpperInvariant()}_TYPE"));
        }

        // Extra flags that point rclone at another config file, whose remotes the app does not read.
        internal static bool UsesOwnConfig(IEnumerable<string> flags) =>
            flags.Any(f => f == "--config" || f.StartsWith("--config=", System.StringComparison.Ordinal));

        // The same list, in the same order, with one remote under its new name.
        internal static RcloneRemote[] Renamed(IEnumerable<RcloneRemote> remotes, string oldName, string newName) =>
            remotes.Select(r => r.Name != oldName ? r
                : new RcloneRemote { Name = newName, Type = r.Type, Parameters = r.Parameters }).ToArray();

        // Why rclone cannot use this remote name on Windows; null when it can. rclone reads a
        // one-letter name as a drive ("q:" is Q:), even when a remote of that name exists.
        internal static string? DriveLetterError(string name) =>
            name.Length == 1 && char.IsAsciiLetter(name[0])
                ? $"Windows reads \"{name}:\" as drive {char.ToUpperInvariant(name[0])}:. Rename the remote to two or more characters."
                : null;

        // rclone's built-in Google client_id (blank client_id) is being retired. Service-account
        // Drive remotes never use it.
        public bool UsesSharedGoogleClientId =>
            Type is "drive" or "google photos"
            && string.IsNullOrWhiteSpace(Parameters.GetValueOrDefault("client_id"))
            && string.IsNullOrWhiteSpace(Parameters.GetValueOrDefault("service_account_file"))
            && string.IsNullOrWhiteSpace(Parameters.GetValueOrDefault("service_account_credentials"));
    }
}
