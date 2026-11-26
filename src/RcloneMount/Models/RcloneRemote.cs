using System.Collections.Generic;

namespace RcloneMount.Models
{
    public class RcloneRemote
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, string> Parameters { get; set; } = new();

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
