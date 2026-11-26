using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using RcloneMount.Services;
using Velopack;

namespace RcloneMount
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Velopack install/update/uninstall hooks run here and exit; must precede everything else.
            VelopackApp.Build().OnBeforeUninstallFastCallback(_ => AutostartService.Disable()).Run();

            // CLI mode (--list, --mount NAME, --unmount NAME, --help): talk to the
            // resident tray over a named pipe, print the reply, exit. Never opens the
            // UI. Falls back to a friendly "not running" message if nothing answers.
            var cliRequest = ParseCliRequest(args, out var showHelp, out var cliError);
            if (showHelp || cliError is not null || cliRequest is not null)
            {
                CliRelay.AttachParentConsole();
                // Windows-1252 default mangles UTF-8 escapes (e.g. the ellipsis used
                // by Trunc). Set UTF-8 explicitly; harmless when the caller doesn't
                // consume our output.
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
                if (cliError is not null)
                {
                    Console.Error.WriteLine(cliError);
                    PrintHelp();
                    return 64;
                }
                if (showHelp)
                {
                    PrintHelp();
                    return 0;
                }
                return RunCli(cliRequest!);
            }

            // One tray per user session. Two would both auto-mount, both run the scheduler
            // (double runs) and both write settings.json. A second launch asks the running
            // one to show itself. "--restarted" (Restore) waits for the old one to exit.
            var restarted = Array.Exists(args, a => a.Equals("--restarted", StringComparison.OrdinalIgnoreCase));
            using var instance = new System.Threading.Mutex(false,
                @"Local\RcloneMount." + (System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "user"));
            if (!TryAcquire(instance, restarted ? TimeSpan.FromSeconds(10) : TimeSpan.Zero))
            {
                try { CliRelay.SendAsync(new CliRelay.Request(CliRelay.Command.Show, null), TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
                catch { }
                return 0;
            }

            ApplicationConfiguration.Initialize();

            var earlySettings = RcloneMount.Services.SettingsService.LoadSync();
            var mode = (earlySettings.ThemePreference ?? "Auto").ToLowerInvariant() switch
            {
                "light" => RcloneMount.UI.ThemeMode.Light,
                "dark"  => RcloneMount.UI.ThemeMode.Dark,
                _       => RcloneMount.UI.Theme.DetectFromSystem()
            };
            RcloneMount.UI.Theme.SetMode(mode);

            // Load any provider logos dropped into a "provider-icons" folder alongside the
            // exe. Filename (without extension) must match the rclone provider Name — e.g.
            // b2.png, drive.png, s3.png. Missing folder is fine; we fall back to the letter
            // badge.
            var iconDir = System.IO.Path.Combine(AppContext.BaseDirectory, "provider-icons");
            RcloneMount.UI.ProviderVisuals.LoadIconsFromDirectory(iconDir);

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
            return 0;
        }

        // Returns non-null when the args request a CLI action. Sets showHelp true for
        // --help/-h/-?, and cliError non-null when args are recognisable but malformed
        // (e.g. --mount with no name).
        private static CliRelay.Request? ParseCliRequest(string[] args, out bool showHelp, out string? cliError)
        {
            showHelp = false;
            cliError = null;
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.Equals("--help", StringComparison.OrdinalIgnoreCase)
                    || a.Equals("-h", StringComparison.OrdinalIgnoreCase)
                    || a == "-?" || a == "/?")
                {
                    showHelp = true;
                    return null;
                }
                if (a.Equals("--list", StringComparison.OrdinalIgnoreCase)
                    || a.Equals("--mounts", StringComparison.OrdinalIgnoreCase))
                {
                    return new CliRelay.Request(CliRelay.Command.List, null);
                }
                if (a.Equals("--mount", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        cliError = "--mount requires a mount name.";
                        return null;
                    }
                    return new CliRelay.Request(CliRelay.Command.Mount, args[i + 1]);
                }
                if (a.Equals("--unmount", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        cliError = "--unmount requires a mount name.";
                        return null;
                    }
                    return new CliRelay.Request(CliRelay.Command.Unmount, args[i + 1]);
                }
            }
            return null;
        }

        private static int RunCli(CliRelay.Request req)
        {
            try
            {
                var reply = CliRelay.SendAsync(req, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                if (req.Command == CliRelay.Command.List && reply.Mounts is not null)
                {
                    // Compact fixed-width listing — width picked so a 25-char remote name
                    // still fits without wrapping in an 80-column terminal.
                    Console.WriteLine($"{"REMOTE",-25}  {"PATH",-20}  {"DRIVE",-5}  STATE");
                    foreach (var m in reply.Mounts)
                    {
                        var path = string.IsNullOrEmpty(m.RemotePath) ? "/" : m.RemotePath;
                        Console.WriteLine($"{Trunc(m.RemoteName, 25),-25}  {Trunc(path, 20),-20}  {Trunc(m.DriveLetter, 5),-5}  {(m.Mounted ? "mounted" : "unmounted")}");
                    }
                    return reply.ExitCode;
                }
                if (!string.IsNullOrWhiteSpace(reply.Message))
                {
                    (reply.ExitCode == 0 ? Console.Out : Console.Error).WriteLine(reply.Message);
                }
                return reply.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("CLI failed: " + ex.Message);
                return 5;
            }
        }

        private static bool TryAcquire(System.Threading.Mutex m, TimeSpan wait)
        {
            try { return m.WaitOne(wait); }
            catch (System.Threading.AbandonedMutexException) { return true; } // previous owner died — it's ours now
        }

        private static string Trunc(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        private static void PrintHelp()
        {
            Console.WriteLine(
@"RcloneMount CLI — commands are forwarded to the resident tray process.

Usage:
  RcloneMount.exe                   Launch the tray UI (default).
  RcloneMount.exe --startup         Launch minimised to tray (used by autostart).
  RcloneMount.exe --list            Print each configured mount and its state.
  RcloneMount.exe --mount NAME      Ask the tray to mount NAME.
  RcloneMount.exe --unmount NAME    Ask the tray to unmount NAME.
  RcloneMount.exe --help            Show this message.

Exit codes:
  0  Success.
  2  Unknown mount name.
  3  Mount did not become ready in time.
  4  Tray process not running — start RcloneMount first.
  5  Other error (details on stderr).
  6  Not unmounted: the mount still has uploads pending.");
        }
    }
}
