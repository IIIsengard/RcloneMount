using System.Collections.Generic;

namespace RcloneMount.Models
{
    public class RcloneProvider
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Prefix { get; set; } = string.Empty;
        public List<RcloneProviderOption> Options { get; set; } = new();
    }

    public class RcloneProviderOption
    {
        public string Name { get; set; } = string.Empty;
        public string Help { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string DefaultStr { get; set; } = string.Empty;
        public string ValueStr { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool Required { get; set; }
        public bool IsPassword { get; set; }
        public bool Advanced { get; set; }
        public bool Sensitive { get; set; }
        public bool Exclusive { get; set; }
        public int Hide { get; set; }
        public List<RcloneProviderExample> Examples { get; set; } = new();
    }

    public class RcloneProviderExample
    {
        public string Value { get; set; } = string.Empty;
        public string Help { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
    }
}
