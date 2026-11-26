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
            DeletedName(remote) is { } name // deleted here, or renamed or deleted outside the app
                ? $"Remote \"{name}\" is no longer in the rclone configuration. Edit the job and pick a remote." : null;

        // True when `remote` names a configured remote that `read` lacks. Not local disk, a deleted mark
        // or an on-the-fly ":backend:".
        internal static bool IsMissing(string remote, IEnumerable<RcloneRemote> read)
        {
            if (string.IsNullOrEmpty(remote) || remote[0] == ':' || DeletedName(remote) is not null) return false;
            var name = remote.Split(',')[0]; // "name,option=x" overrides settings of "name"
            return !read.Any(r => r.Name == name) && FromEnvironment(name) is null;
        }

        // An rclone environment variable's value; null when unset or empty. rclone inherits ours.
        internal static string? EnvironmentSetting(string variable) =>
            System.Environment.GetEnvironmentVariable(variable.ToUpperInvariant()) is { Length: > 0 } value ? value : null;

        // The remote that RCLONE_CONFIG_<NAME>_TYPE defines for `remote` ("name" or "name,option=x"), or null.
        // `config dump` leaves such remotes out.
        internal static RcloneRemote? FromEnvironment(string remote) =>
            EnvironmentSetting($"RCLONE_CONFIG_{remote.Split(',')[0]}_TYPE") is { } type
                ? new RcloneRemote { Name = remote, Type = type } : null;

        // `remotes`, plus those of `names` that only environment variables define.
        internal static IReadOnlyList<RcloneRemote> WithEnvironmentRemotes(IReadOnlyList<RcloneRemote> remotes, params string?[] names) =>
            remotes.Concat(names.OfType<string>().Distinct()
                .Where(name => name.Length > 0 && !remotes.Any(r => r.Name == name))
                .Select(FromEnvironment).OfType<RcloneRemote>()).ToArray();

        // Extra flags that give rclone its own way into a config: such a run may work when the app cannot read it.
        internal static bool BringsConfigAccess(IEnumerable<string> flags) =>
            flags.Any(f => f.Split('=')[0] is "--config" or "--password-command");

        // The file the --config flag names (rclone takes the last one; "" when it names none); null without the flag.
        internal static string? ConfigFlag(IEnumerable<string> flags)
        {
            var all = flags.ToList();
            var i = all.FindLastIndex(f => f.Split('=')[0] == "--config");
            if (i < 0) return null;
            if (all[i].Contains('=')) return all[i][(all[i].IndexOf('=') + 1)..];
            return i + 1 < all.Count ? all[i + 1] : "";
        }

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
