using System.Collections.Generic;

namespace RcloneMount.Models
{
    public class RcloneRemote
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, string> Parameters { get; set; } = new();
    }
}
