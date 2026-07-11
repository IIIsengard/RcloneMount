using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RcloneMount.Models;

namespace RcloneMount.Services
{
    public class RcloneConfigService
    {
        private readonly string exePath;

        public RcloneConfigService(string exePath)
        {
            this.exePath = exePath;
        }

        public async Task<IReadOnlyList<RcloneRemote>> ListRemotesAsync()
        {
            var (stdout, stderr, exit) = await RunAsync("config", "dump").ConfigureAwait(false);
            if (exit != 0)
                throw new InvalidOperationException($"rclone config dump failed: {stderr}");

            if (string.IsNullOrWhiteSpace(stdout))
                return Array.Empty<RcloneRemote>();

            var remotes = new List<RcloneRemote>();
            using var doc = JsonDocument.Parse(stdout);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var remote = new RcloneRemote { Name = entry.Name };
                foreach (var field in entry.Value.EnumerateObject())
                {
                    var value = field.Value.ValueKind == JsonValueKind.String
                        ? field.Value.GetString() ?? string.Empty
                        : field.Value.GetRawText();

                    if (string.Equals(field.Name, "type", StringComparison.OrdinalIgnoreCase))
                        remote.Type = value;
                    else
                        remote.Parameters[field.Name] = value;
                }
                remotes.Add(remote);
            }
            return remotes;
        }

        public Task DeleteRemoteAsync(string name) => RunExpectSuccessAsync("config", "delete", name);

        // rclone has no `config rename` subcommand — rename by editing the section header
        // in rclone.conf directly. `rclone config file` prints the path on its last line.
        public async Task RenameRemoteAsync(string oldName, string newName)
        {
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return;

            var (stdout, stderr, exit) = await RunAsync("config", "file").ConfigureAwait(false);
            if (exit != 0)
                throw new InvalidOperationException($"rclone config file failed: {stderr}");

            var configPath = stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .LastOrDefault(l => l.Length > 0)
                ?? throw new InvalidOperationException("rclone config file returned no path.");

            if (!File.Exists(configPath))
                throw new FileNotFoundException("rclone.conf not found.", configPath);

            var lines = await File.ReadAllLinesAsync(configPath).ConfigureAwait(false);
            var oldHeader = "[" + oldName + "]";
            var newHeader = "[" + newName + "]";
            var replaced = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (string.Equals(lines[i].TrimEnd(), oldHeader, StringComparison.Ordinal))
                {
                    lines[i] = newHeader;
                    replaced = true;
                    break;
                }
                if (string.Equals(lines[i].TrimEnd(), newHeader, StringComparison.Ordinal))
                    throw new InvalidOperationException($"A remote named '{newName}' already exists in rclone.conf.");
            }
            if (!replaced)
                throw new InvalidOperationException($"Section [{oldName}] not found in rclone.conf.");

            await File.WriteAllLinesAsync(configPath, lines).ConfigureAwait(false);
        }

        public void LaunchInteractiveConfig()
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/k \"\"{exePath}\" config\"")
            {
                UseShellExecute = true,
                CreateNoWindow = false
            };
            Process.Start(psi);
        }

        public void LaunchInteractiveReconnect(string remoteName)
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/k \"\"{exePath}\" config reconnect {remoteName}:\"")
            {
                UseShellExecute = true,
                CreateNoWindow = false
            };
            Process.Start(psi);
        }

        public async Task<IReadOnlyList<RcloneProvider>> ListProvidersAsync()
        {
            var (stdout, stderr, exit) = await RunAsync("rc", "--loopback", "config/providers").ConfigureAwait(false);
            if (exit != 0)
                throw new InvalidOperationException($"rclone rc config/providers failed: {stderr}");

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            using var doc = JsonDocument.Parse(stdout);
            var providers = new List<RcloneProvider>();
            if (doc.RootElement.TryGetProperty("providers", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var p = JsonSerializer.Deserialize<RcloneProvider>(el.GetRawText(), options);
                    if (p is not null && !string.IsNullOrEmpty(p.Name))
                        providers.Add(p);
                }
            }
            return providers;
        }

        public Task CreateRemoteAsync(string name, string type, IReadOnlyDictionary<string, string> values, bool obscurePasswords) =>
            RunConfigWriteAsync("create", name, type, values, obscurePasswords);

        public Task UpdateRemoteAsync(string name, IReadOnlyDictionary<string, string> values, bool obscurePasswords) =>
            RunConfigWriteAsync("update", name, null, values, obscurePasswords);

        private async Task RunConfigWriteAsync(string verb, string name, string? type, IReadOnlyDictionary<string, string> values, bool obscurePasswords)
        {
            var args = new List<string> { "config", verb, name };
            if (type is not null) args.Add(type);
            foreach (var kv in values)
                args.Add($"{kv.Key}={kv.Value}");
            args.Add("--non-interactive");
            if (obscurePasswords) args.Add("--obscure");
            else args.Add("--no-obscure");
            await RunExpectSuccessAsync(args.ToArray()).ConfigureAwait(false);
        }

        // Runs `rclone authorize <type>`, streams stderr (rclone prints the auth URL there),
        // captures stdout (rclone prints the resulting token JSON there when done). Progress
        // messages (like "Please go to this URL...") go to onProgress.
        public async Task<string> AuthorizeAsync(string type, IProgress<string>? onProgress, CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("authorize");
            psi.ArgumentList.Add(type);

            using var proc = Process.Start(psi) ?? throw new IOException("Failed to start rclone authorize.");
            using var _ = cancellationToken.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });

            var stderrBuilder = new System.Text.StringBuilder();
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stderrBuilder.AppendLine(e.Data);
                onProgress?.Report(e.Data);
            };
            proc.BeginErrorReadLine();

            var stdout = await proc.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"rclone authorize {type} exited {proc.ExitCode}: {stderrBuilder}");

            // rclone authorize prints instructions then a JSON blob between two lines of dashes,
            // e.g.  --------------------\n{"access_token":"...","token_type":"Bearer","refresh_token":"...","expiry":"..."}\n--------------------
            var match = Regex.Match(stdout, @"\{[^{}]*""access_token""[^{}]*\}", RegexOptions.Singleline);
            if (!match.Success)
                throw new InvalidOperationException("Could not parse token from rclone authorize output: " + stdout);
            return match.Value;
        }

        private async Task RunExpectSuccessAsync(params string[] args)
        {
            var (_, stderr, exit) = await RunAsync(args).ConfigureAwait(false);
            if (exit != 0)
                throw new InvalidOperationException($"rclone {string.Join(' ', args)} failed: {stderr}");
        }

        private async Task<(string Stdout, string Stderr, int ExitCode)> RunAsync(params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi) ?? throw new IOException("Failed to start rclone.");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return (await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false), proc.ExitCode);
        }
    }
}
