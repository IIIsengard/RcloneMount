using System;

namespace RcloneMount.Models
{
    public class Mount
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string RemoteName { get; set; } = string.Empty;
        public string RemotePath { get; set; } = string.Empty;
        public string DriveLetter { get; set; } = string.Empty;
        public string VfsCacheMode { get; set; } = "full";
        public string ExtraFlags { get; set; } = string.Empty;
        // VFS cache limits. Empty = no flag, so rclone's own default applies (cache in its
        // default folder, no size cap, no free-space floor). Sizes use rclone's form: 10G, 500M, off.
        public string CacheDir { get; set; } = string.Empty;
        public string VfsCacheMaxSize { get; set; } = string.Empty;
        public string VfsCacheMinFreeSpace { get; set; } = string.Empty;
        public bool AutoMount { get; set; }
    }
}
