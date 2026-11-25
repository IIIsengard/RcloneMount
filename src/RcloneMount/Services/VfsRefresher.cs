using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RcloneMount.Models;

namespace RcloneMount.Services
{
    // With a long --dir-cache-time a mount only learns about files written by a RcloneMount job
    // when its backend reports them, or never (non-polling remotes keep 15 s). After a job ends we
    // ask the mount's own rclone to re-list the folder the job wrote to (RC vfs/refresh).
    internal static class VfsRefresher
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(60),
            DefaultRequestHeaders = { Authorization = RcAuth.Header }
        };

        // Mounts (with the folder path inside each mount) that the job may have changed.
        // shortcut: only a job whose remote name equals the mount's remote name is matched; a job
        // writing through a crypt/alias remote that wraps the mounted one is not refreshed.
        internal static IEnumerable<(Mount mount, string dir)> Targets(JobSpec spec, IEnumerable<Mount> mounts)
        {
            if (spec.DryRun || spec.Operation is TransferOperation.Check or TransferOperation.CryptCheck)
                yield break;
            var sides = new List<(string remote, string path)> { (spec.DestRemote, spec.DestPath) };
            // Move deletes from the source and bisync writes both ways.
            if (spec.Operation is TransferOperation.Move or TransferOperation.Bisync)
                sides.Add((spec.SourceRemote, spec.SourcePath));
            var seen = new HashSet<(Mount, string)>(); // a Move within one remote names the same folder twice
            foreach (var (remote, path) in sides)
                foreach (var m in mounts)
                {
                    if (!string.Equals(m.RemoteName, remote, StringComparison.Ordinal)) continue;
                    var dir = RelativeDir(m.RemotePath, path);
                    if (dir is not null && seen.Add((m, dir))) yield return (m, dir);
                }
        }

        // Path of the job folder relative to the mount root; null when the job is outside the mount.
        // A job folder above the mount root gives "" (refresh the whole mount).
        internal static string? RelativeDir(string? mountPath, string? jobPath)
        {
            var root = Norm(mountPath);
            var job = Norm(jobPath);
            if (root.Length == 0) return job;
            if (job == root) return "";
            if (job.StartsWith(root + "/", StringComparison.Ordinal)) return job.Substring(root.Length + 1);
            if (root.StartsWith(job + "/", StringComparison.Ordinal)) return "";
            return null;
        }

        private static string Norm(string? p) => (p ?? "").Replace('\\', '/').Trim('/');

        // "a/b" -> "", "a", "a/b". A folder the job just created is not in rclone's cached listing of
        // its parent, and vfs/refresh on it answers "file does not exist", so refresh top-down.
        internal static string[] Chain(string dir)
        {
            var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return Enumerable.Range(0, parts.Length + 1).Select(n => string.Join('/', parts.Take(n))).ToArray();
        }

        // Refreshes each folder from the mount root down to dir, one level at a time and never
        // recursively: a job into the root of a big remote must not crawl the whole tree (API quota,
        // timeouts, overlapping crawls). Listings below dir that the job created are picked up by the
        // mount's own 15 s re-list or by change polling.
        // A folder that does not exist (job failed or was cancelled before creating it, or a Move
        // removed the source folder) ends the chain without an error. Other failures throw; the caller logs them.
        internal static async Task RefreshAsync(int rcPort, string dir)
        {
            var chain = Chain(dir);
            for (var i = 0; i < chain.Length; i++)
            {
                var body = new Dictionary<string, string>();
                if (chain[i].Length > 0) body["dir"] = chain[i];
                using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync($"http://127.0.0.1:{rcPort}/vfs/refresh", content).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"vfs/refresh HTTP {(int)resp.StatusCode}: {text}");
                using var doc = JsonDocument.Parse(text);
                foreach (var entry in doc.RootElement.GetProperty("result").EnumerateObject())
                {
                    var answer = entry.Value.GetString();
                    if (answer == "OK") continue;
                    if (i > 0 && answer is not null && answer.Contains("does not exist", StringComparison.Ordinal)) return;
                    throw new InvalidOperationException($"vfs/refresh '{entry.Name}': {answer}");
                }
            }
        }
    }
}
