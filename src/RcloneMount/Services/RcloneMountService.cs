using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RcloneMount.Models;

namespace RcloneMount.Services
{
    public class RcloneMountService : IDisposable
    {
        private const int StderrRingSize = 40;
        private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(6);
        private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3), DefaultRequestHeaders = { Authorization = RcAuth.Header } };

        private readonly string exePath;
        private readonly ConcurrentDictionary<string, MountProcess> processes = new();
        private readonly HashSet<string> intentionalShutdowns = new();
        private readonly object shutdownGate = new();
        private readonly ChildProcessJob job = new();

        public event EventHandler<MountExitedEventArgs>? MountExited;

        // The configured remotes, so a mount can pick its dir-cache flags from the backend type.
        public Func<IReadOnlyList<RcloneRemote>> Remotes { get; set; } = () => Array.Empty<RcloneRemote>();

        public RcloneMountService(string exePath)
        {
            this.exePath = exePath;
        }

        public void Dispose() => job.Dispose();

        public bool IsMounted(Mount mount) =>
            processes.TryGetValue(mount.Id, out var mp) && !mp.Process.HasExited;

        public IReadOnlyList<string> GetRecentStderr(Mount mount) =>
            processes.TryGetValue(mount.Id, out var mp) ? mp.SnapshotStderr() : Array.Empty<string>();

        // Returns the rclone RC control port for a running mount, or null if the mount
        // isn't currently up. Consumers use this to POST /core/stats etc for live telemetry.
        public int? GetRcPort(Mount mount) =>
            processes.TryGetValue(mount.Id, out var mp) && !mp.Process.HasExited ? mp.RcPort : (int?)null;

        public Task MountAsync(Mount mount)
        {
            if (IsMounted(mount))
                return Task.CompletedTask;

            if (string.IsNullOrWhiteSpace(mount.RemoteName))
                throw new InvalidOperationException("Mount has no remote configured.");
            if (string.IsNullOrWhiteSpace(mount.DriveLetter))
                throw new InvalidOperationException("Mount has no drive letter configured.");

            var rcPort = ReserveFreeLoopbackPort();

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in BuildArgs(mount, rcPort, Remotes()))
                psi.ArgumentList.Add(arg);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var mp = new MountProcess(proc, StderrRingSize, rcPort);

            proc.OutputDataReceived += (_, ev) => { if (ev.Data is not null) mp.AppendLine(ev.Data); };
            proc.ErrorDataReceived += (_, ev) => { if (ev.Data is not null) mp.AppendLine(ev.Data); };
            proc.Exited += (_, _) =>
            {
                processes.TryRemove(mount.Id, out _);
                bool intentional;
                lock (shutdownGate) intentional = intentionalShutdowns.Remove(mount.Id);
                MountExited?.Invoke(this, new MountExitedEventArgs(mount.Id, proc.ExitCode, mp.SnapshotStderr(), intentional));
            };

            if (!proc.Start())
                throw new IOException("Failed to launch rclone mount.");

            job.Attach(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            processes[mount.Id] = mp;
            return Task.CompletedTask;
        }

        public static bool IsDriveMountedExternally(string driveLetter)
        {
            if (string.IsNullOrWhiteSpace(driveLetter)) return false;
            var root = driveLetter.TrimEnd(':') + ":\\";
            try { return System.IO.Directory.Exists(root); } catch { return false; }
        }

        public static async Task<int> KillExternalRcloneMountAsync(string driveLetter)
        {
            var letter = driveLetter.TrimEnd(':') + ":";
            var killed = 0;
            foreach (var proc in Process.GetProcessesByName("rclone"))
            {
                try
                {
                    var cmd = GetCommandLine(proc.Id);
                    if (cmd is null || cmd.IndexOf(" mount ", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (cmd.IndexOf(" " + letter + " ", StringComparison.OrdinalIgnoreCase) < 0
                        && !cmd.TrimEnd().EndsWith(" " + letter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync().ConfigureAwait(false);
                    killed++;
                }
                catch { }
                finally { proc.Dispose(); }
            }
            return killed;
        }

        private static string? GetCommandLine(int pid)
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId={pid}");
                foreach (var obj in searcher.Get())
                    return obj["CommandLine"]?.ToString();
                return null;
            }
            catch
            {
                return null;
            }
        }

        public async Task UnmountAsync(Mount mount)
        {
            if (!processes.TryRemove(mount.Id, out var mp))
                return;

            try
            {
                lock (shutdownGate) intentionalShutdowns.Add(mount.Id);
                await ShutdownAsync(mp).ConfigureAwait(false);
            }
            finally
            {
                mp.Process.Dispose();
            }
        }

        public async Task UnmountAllAsync()
        {
            foreach (var id in processes.Keys)
            {
                if (processes.TryRemove(id, out var mp))
                {
                    try
                    {
                        lock (shutdownGate) intentionalShutdowns.Add(id);
                        await ShutdownAsync(mp).ConfigureAwait(false);
                    }
                    catch { }
                    finally { mp.Process.Dispose(); }
                }
            }
        }

        private static async Task ShutdownAsync(MountProcess mp)
        {
            if (mp.Process.HasExited)
                return;

            var quitSent = await TryRcQuitAsync(mp.RcPort).ConfigureAwait(false);
            if (quitSent)
            {
                using var cts = new CancellationTokenSource(GracefulShutdownTimeout);
                try
                {
                    await mp.Process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    // graceful shutdown timed out — fall through to kill
                }
            }

            if (!mp.Process.HasExited)
            {
                try
                {
                    mp.Process.Kill(entireProcessTree: true);
                    await mp.Process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch { }
            }
        }

        private static async Task<bool> TryRcQuitAsync(int rcPort)
        {
            if (rcPort <= 0) return false;
            try
            {
                using var content = new StringContent(string.Empty);
                var resp = await http.PostAsync($"http://127.0.0.1:{rcPort}/core/quit", content).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static int ReserveFreeLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        // Everything after "mount". Defaults first, the user's ExtraFlags last: rclone takes the
        // last occurrence of a repeated flag, so a user's --dir-cache-time etc. always wins.
        internal static Collection<string> BuildArgs(Mount mount, int rcPort, IReadOnlyList<RcloneRemote> remotes)
        {
            var args = new Collection<string>
            {
                "mount",
                $"{mount.RemoteName}:{mount.RemotePath?.TrimStart('/') ?? string.Empty}",
                mount.DriveLetter.TrimEnd(':') + ":",
                "--vfs-cache-mode",
                string.IsNullOrWhiteSpace(mount.VfsCacheMode) ? "full" : mount.VfsCacheMode,
            };
            // rclone's default --dir-cache-time of 5m hides files changed outside the mount, so
            // non-polling backends get 15 s; backends that poll for changes get a long cache.
            foreach (var flag in DirCachePolicy.FlagsFor(mount.RemoteName, remotes))
                args.Add(flag);
            // The editor validates these; a hand-edited settings.json must not reach rclone as junk.
            foreach (var (flag, value, error) in new[]
            {
                ("--cache-dir", mount.CacheDir, VfsCacheLimits.ValidateDir(mount.CacheDir)),
                ("--vfs-cache-max-size", mount.VfsCacheMaxSize, VfsCacheLimits.ValidateSize(mount.VfsCacheMaxSize)),
                ("--vfs-cache-min-free-space", mount.VfsCacheMinFreeSpace, VfsCacheLimits.ValidateSize(mount.VfsCacheMinFreeSpace)),
            })
            {
                if (error is not null) throw new InvalidOperationException($"Mount cache setting {flag}: {error}");
                if (!string.IsNullOrWhiteSpace(value)) { args.Add(flag); args.Add(value.Trim()); }
            }
            args.Add("--rc");
            args.Add("--rc-addr");
            args.Add($"127.0.0.1:{rcPort}");
            RcAuth.AddServerArgs(args);
            if (!string.IsNullOrWhiteSpace(mount.ExtraFlags))
                foreach (var token in SplitFlags(mount.ExtraFlags))
                    args.Add(token);
            return args;
        }

        internal static string[] SplitFlags(string input)
        {
            var list = new List<string>();
            var buf = new System.Text.StringBuilder();
            var inQuote = false;
            foreach (var ch in input)
            {
                if (ch == '"') { inQuote = !inQuote; continue; }
                if (char.IsWhiteSpace(ch) && !inQuote)
                {
                    if (buf.Length > 0) { list.Add(buf.ToString()); buf.Clear(); }
                }
                else buf.Append(ch);
            }
            if (buf.Length > 0) list.Add(buf.ToString());
            return list.ToArray();
        }

        private sealed class MountProcess
        {
            private readonly Queue<string> ring;
            private readonly int cap;
            private readonly object gate = new();

            public Process Process { get; }
            public int RcPort { get; }

            public MountProcess(Process proc, int capacity, int rcPort)
            {
                Process = proc;
                cap = capacity;
                RcPort = rcPort;
                ring = new Queue<string>(capacity);
            }

            public void AppendLine(string line)
            {
                lock (gate)
                {
                    if (ring.Count >= cap) ring.Dequeue();
                    ring.Enqueue(line);
                }
            }

            public IReadOnlyList<string> SnapshotStderr()
            {
                lock (gate) return ring.ToArray();
            }
        }
    }

    public class MountExitedEventArgs : EventArgs
    {
        public string MountId { get; }
        public int ExitCode { get; }
        public IReadOnlyList<string> RecentOutput { get; }
        public bool WasIntentional { get; }

        public MountExitedEventArgs(string id, int code, IReadOnlyList<string> recentOutput, bool wasIntentional)
        {
            MountId = id;
            ExitCode = code;
            RecentOutput = recentOutput;
            WasIntentional = wasIntentional;
        }
    }
}
