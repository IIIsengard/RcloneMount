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
        public bool AutoMount { get; set; }
    }
}
