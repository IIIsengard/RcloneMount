using System.Collections.Generic;

namespace RcloneMount.Models
{
    public class RcloneRemote
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, string> Parameters { get; set; } = new();

        // rclone's built-in Google client_id (blank client_id) is being retired. Service-account
        // Drive remotes never use it.
        public bool UsesSharedGoogleClientId =>
            Type is "drive" or "google photos"
            && string.IsNullOrWhiteSpace(Parameters.GetValueOrDefault("client_id"))
            && string.IsNullOrWhiteSpace(Parameters.GetValueOrDefault("service_account_file"))
            && string.IsNullOrWhiteSpace(Parameters.GetValueOrDefault("service_account_credentials"));
    }
}
