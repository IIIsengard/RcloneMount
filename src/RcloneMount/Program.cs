using System;
using System.Windows.Forms;

namespace RcloneMount
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();

            var earlySettings = RcloneMount.Services.SettingsService.LoadSync();
            var mode = (earlySettings.ThemePreference ?? "Auto").ToLowerInvariant() switch
            {
                "light" => RcloneMount.UI.ThemeMode.Light,
                "dark"  => RcloneMount.UI.ThemeMode.Dark,
                _       => RcloneMount.UI.Theme.DetectFromSystem()
            };
            RcloneMount.UI.Theme.SetMode(mode);

            var startHidden = false;
            foreach (var a in args)
            {
                if (a.Equals("--startup", StringComparison.OrdinalIgnoreCase)
                    || a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)
                    || a.Equals("/startup", StringComparison.OrdinalIgnoreCase))
                {
                    startHidden = true;
                    break;
                }
            }

            Application.Run(new MainForm(startHidden));
        }
    }
}
