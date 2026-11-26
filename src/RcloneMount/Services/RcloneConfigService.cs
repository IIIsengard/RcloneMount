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

        // Longest wait for `config dump`, which may also run a password command. Settable for tests.
        internal TimeSpan ListTimeout { get; init; } = TimeSpan.FromSeconds(30);

        public RcloneConfigService(string exePath)
        {
            this.exePath = exePath;
        }

        public async Task<IReadOnlyList<RcloneRemote>> ListRemotesAsync()
        {
            var (stdout, stderr, exit) = await RunAsync(ListTimeout, "config", "dump").ConfigureAwait(false);
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

        internal const string EncryptedRenameMessage =
            "rclone.conf is encrypted, so RcloneMount can't rename remotes. Nothing was changed. " +
            "To rename, open the rclone terminal, run 'rclone config' and choose 'r) Rename remote'.";

        // rclone has no `config rename` subcommand — rename by editing the section header
        // in rclone.conf directly. Refused for an encrypted config: the file is ciphertext.
        public async Task RenameRemoteAsync(string oldName, string newName)
        {
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return;

            await RenameInFileAsync(SettingsService.ResolveRcloneConfigPath(), oldName, newName).ConfigureAwait(false);
        }

        // One rclone.conf setting with its "oldName:path" references moved to newName. Only wrapping
        // backends hold them: "remote" (alias, crypt, chunker, compress, hasher, cache, archive) and
        // "upstreams" (union, combine), a space-separated list whose items may be quoted.
        internal static string RepointValue(string type, string key, string value, string oldName, string newName)
        {
            // rclone reads "c:..." as a drive, so it never referred to a one-letter remote.
            if (RcloneRemote.DriveLetterError(oldName) is not null) return value;
            if (key == "remote") return RepointSpec(value, oldName, newName);
            if (key != "upstreams") return value;
            return Regex.Replace(value, ListItem, match =>
            {
                var raw = match.Value;
                var item = Unquote(raw, out var quoted);
                var start = type == "combine" ? item.IndexOf('=') + 1 : 0; // combine items are dir=remote:path
                var changed = item[..start] + RepointSpec(item[start..], oldName, newName);
                if (changed == item) return raw;
                return quoted || changed.Contains(' ') ? "\"" + changed.Replace("\"", "\"\"") + "\"" : changed;
            });
        }

        private const string ListItem = @"""(?:[^""]|"""")*""|[^ ]+"; // one item of an "upstreams" list

        private static string Unquote(string raw, out bool quoted)
        {
            quoted = raw.Length > 1 && raw[0] == '"' && raw[^1] == '"';
            return quoted ? raw[1..^1].Replace("\"\"", "\"") : raw;
        }

        // The first remote that `remote` reaches through wrapper settings and `read` lacks; null when none.
        // rclone takes such a setting from a flag, RCLONE_CONFIG_<NAME>_<KEY>, RCLONE_<TYPE>_<KEY>, then the config.
        // shortcut: ":backend,remote=x:" and "name,remote=x:" overrides are not followed, add if jobs use them.
        internal static string? MissingBehind(string remote, IReadOnlyCollection<RcloneRemote> read,
            IReadOnlyCollection<string> flags, HashSet<string>? seen = null)
        {
            if (string.IsNullOrEmpty(remote) || remote[0] == ':' || RcloneRemote.DeletedName(remote) is not null) return null;
            var name = remote.Split(',')[0];
            if (!(seen ??= new HashSet<string>(StringComparer.Ordinal)).Add(name)) return null; // wrappers in a loop
            var configured = read.FirstOrDefault(r => r.Name == name);
            var type = RcloneRemote.FromEnvironment(name)?.Type ?? configured?.Type;
            var key = type switch
            {
                "alias" or "archive" or "cache" or "chunker" or "compress" or "crypt" or "hasher" => "remote",
                "combine" or "union" => "upstreams",
                _ => null,
            };
            if (key is null || flags.Any(f => f.Split('=')[0] == $"--{type}-{key}")) return null;
            var value = RcloneRemote.EnvironmentSetting($"RCLONE_CONFIG_{name}_{key}")
                ?? RcloneRemote.EnvironmentSetting($"RCLONE_{type}_{key}")
                ?? configured?.Parameters.GetValueOrDefault(key) ?? "";
            var specs = key == "remote" ? new[] { value }
                : Regex.Matches(value, ListItem).Select(m => Unquote(m.Value, out _))
                    .Select(item => type == "combine" ? item[(item.IndexOf('=') + 1)..] : item).ToArray();
            foreach (var spec in specs)
            {
                var colon = spec.IndexOf(':');
                if (colon <= 0) continue; // a local path, or ":backend:"
                var target = spec[..colon].Split(',')[0];
                if (target.IndexOfAny(new[] { '/', '\\' }) >= 0 || RcloneRemote.DriveLetterError(target) is not null) continue;
                if (RcloneRemote.IsMissing(target, read)) return target;
                if (MissingBehind(target, read, flags, seen) is { } further) return further;
            }
            return null;
        }

        // "name:path", or "name,option=x:path" with settings overridden inline. Anything else is a local path.
        private static string RepointSpec(string spec, string oldName, string newName)
        {
            if (spec.Length <= oldName.Length || !spec.StartsWith(oldName, StringComparison.Ordinal)) return spec;
            var next = spec[oldName.Length];
            return next == ':' || (next == ',' && spec.IndexOf(':', oldName.Length) >= 0)
                ? newName + spec[oldName.Length..]
                : spec;
        }

        // The remotes whose settings point at `name`.
        internal static IReadOnlyList<string> DependentsOf(IEnumerable<RcloneRemote> remotes, string name) =>
            remotes.Where(r => r.Parameters.Any(p => RepointValue(r.Type, p.Key, p.Value, name, name + "_") != p.Value))
                .Select(r => r.Name).ToList();

        // `name` and every remote that reaches it through wrappers.
        internal static IReadOnlyList<string> WithDependents(IReadOnlyCollection<RcloneRemote> remotes, string name)
        {
            var found = new List<string> { name };
            for (var i = 0; i < found.Count; i++)
                found.AddRange(DependentsOf(remotes, found[i]).Except(found).ToList());
            return found;
        }

        internal static async Task RenameInFileAsync(string configPath, string oldName, string newName)
        {
            if (!File.Exists(configPath))
                throw new FileNotFoundException("rclone.conf not found.", configPath);
            if (RcloneConfigPassword.IsEncrypted(configPath))
                throw new InvalidOperationException(EncryptedRenameMessage);

            var lines = await File.ReadAllLinesAsync(configPath).ConfigureAwait(false);
            var oldHeader = "[" + oldName + "]";
            var newHeader = "[" + newName + "]";
            var replaced = false;
            // Anywhere in the file: rclone would be left with two sections of one name.
            if (lines.Any(l => string.Equals(l.TrimEnd(), newHeader, StringComparison.Ordinal)))
                throw new InvalidOperationException($"A remote named '{newName}' already exists in rclone.conf.");
            for (var i = 0; i < lines.Length; i++)
            {
                if (string.Equals(lines[i].TrimEnd(), oldHeader, StringComparison.Ordinal))
                {
                    lines[i] = newHeader;
                    replaced = true;
                    break;
                }
            }
            if (!replaced)
                throw new InvalidOperationException($"Section [{oldName}] not found in rclone.conf.");

            // Remotes that wrap the renamed one would break, or reach a later remote reusing the name.
            // Same write as the header, so the file never holds one without the other.
            var type = "";
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith('['))
                {
                    type = SectionType(lines, i);
                    continue;
                }
                var eq = lines[i].IndexOf('=');
                if (eq <= 0) continue;
                var key = lines[i][..eq].Trim();
                var value = lines[i][(eq + 1)..].Trim();
                // rclone's config writer wraps some values (# or ; in a path) in backticks or triple quotes.
                var wrap = ConfigQuotes.FirstOrDefault(q => value.Length >= 2 * q.Length
                    && value.StartsWith(q, StringComparison.Ordinal) && value.EndsWith(q, StringComparison.Ordinal)) ?? "";
                var inner = value[wrap.Length..^wrap.Length];
                var repointed = RepointValue(type, key, inner, oldName, newName);
                if (repointed != inner) lines[i] = $"{key} = {wrap}{repointed}{wrap}";
            }

            SettingsService.WriteAtomic(configPath, string.Join("\r\n", lines) + "\r\n");
        }

        private static readonly string[] ConfigQuotes = { "\"\"\"", "`" };

        private static string SectionType(string[] lines, int header) =>
            lines.Skip(header + 1).TakeWhile(l => !l.StartsWith('['))
                .Select(l => l.Split('=', 2))
                .FirstOrDefault(kv => kv.Length == 2 && kv[0].Trim() == "type")?[1].Trim() ?? "";

        // Terminals opened this run. A restore refuses while one is open: it can rewrite rclone.conf.
        internal static List<Process> Terminals { get; } = new();

        internal static void PruneTerminals() => Terminals.RemoveAll(t =>
        {
            if (!t.HasExited) return false;
            t.Dispose();
            return true;
        });

        // Console where any rclone command works (config, ncdu, …) against the same rclone.exe
        // and rclone.conf the app uses. UseShellExecute=false so the Environment edits apply.
        public void LaunchTerminal(string? args = null)
        {
            var command = string.IsNullOrWhiteSpace(args)
                ? "rclone version & echo. & echo Type any rclone command; 'rclone help' lists them."
                : "rclone " + args;
            var psi = new ProcessStartInfo("cmd.exe", "/k " + command)
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                // Autostart launches us with cwd System32; start somewhere writable instead.
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            psi.Environment["PATH"] = Path.GetDirectoryName(exePath) + ";" + psi.Environment["PATH"];
            psi.Environment["RCLONE_CONFIG"] = SettingsService.ResolveRcloneConfigPath();
            RcloneConfigPassword.AddTo(psi);
            PruneTerminals();
            if (Process.Start(psi) is { } terminal) Terminals.Add(terminal);
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

        // Runs `rclone authorize <type> [config-blob]`, streams stderr (rclone prints
        // the auth URL there), captures stdout (rclone prints the resulting token JSON
        // there when done). Progress messages (like "Please go to this URL...") go to
        // onProgress.
        //
        // authConfig, when supplied, becomes the base64-encoded JSON blob rclone accepts
        // as authorize's second argument — this is how OAuth-shaping options like
        // drive_scope, onedrive region, or custom client_id/client_secret are honoured
        // by the token that comes back. Auth-managed fields (`token` etc.) are stripped
        // before encoding so we don't feed an expired token into its own reissue.
        public async Task<string> AuthorizeAsync(string type, IReadOnlyDictionary<string, string>? authConfig, IProgress<string>? onProgress, CancellationToken cancellationToken)
        {
            var psi = RcloneConfigPassword.StartInfo(exePath);
            psi.ArgumentList.Add("authorize");
            psi.ArgumentList.Add(type);

            var encoded = EncodeAuthConfig(authConfig);
            if (encoded is not null)
                psi.ArgumentList.Add(encoded);

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
                throw new InvalidOperationException("Could not parse token from rclone authorize output."); // stdout may hold the token
            return match.Value;
        }

        // Fields the OAuth flow issues, not consumes — never round-trip these into
        // authorize's config blob or we'd tell rclone to use an expired token to mint
        // a fresh one.
        private static readonly HashSet<string> AuthManagedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "token", "refresh_token", "expiry", "access_token", "token_type"
        };

        private static string? EncodeAuthConfig(IReadOnlyDictionary<string, string>? values)
        {
            if (values is null) return null;
            var filtered = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in values)
            {
                if (AuthManagedKeys.Contains(kv.Key)) continue;
                if (string.IsNullOrEmpty(kv.Value)) continue;
                filtered[kv.Key] = kv.Value;
            }
            if (filtered.Count == 0) return null;
            var json = JsonSerializer.Serialize(filtered);
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        }

        // Removes `keysToRemove` from the [name] section of rclone.conf, leaving other
        // keys and other sections alone. Used by the edit flow to strip options that
        // stopped applying after a sub-provider switch — `rclone config update` can only
        // add or change keys, never delete them, so we edit the file directly (same
        // pattern as RenameRemoteAsync). No-op when the section is missing or the set
        // is empty. Returns null when done, otherwise why the keys were left in place: the
        // update itself has already succeeded, so that is a note, not a failure.
        public async Task<string?> PruneRemoteAsync(string name, IReadOnlySet<string> keysToRemove, string? rcloneVersion)
        {
            if (keysToRemove is null || keysToRemove.Count == 0) return null;

            var configPath = SettingsService.ResolveRcloneConfigPath();
            if (!File.Exists(configPath))
                return null;

            // Encrypted config is ciphertext: let rclone remove the keys and re-encrypt.
            // shortcut: plain configs keep the text edit so rclone < v1.75 still prunes them,
            // switch everything to `config unset` once v1.75 is the minimum supported rclone.
            if (RcloneConfigPassword.IsEncrypted(configPath))
            {
                if (!SupportsConfigUnset(rcloneVersion))
                    return EncryptedPruneNeedsNewerRclone;
                var args = new[] { "config", "unset", name }.Concat(keysToRemove).ToArray();
                var (_, stderr, exit) = await RunAsync(args).ConfigureAwait(false);
                return exit == 0 ? null : "Old options were left in place: " + DescribeFailure(args, stderr);
            }

            var lines = await File.ReadAllLinesAsync(configPath).ConfigureAwait(false);
            var header = "[" + name + "]";
            var output = new List<string>(lines.Length);
            var inSection = false;
            var changed = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimEnd();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    inSection = string.Equals(trimmed, header, StringComparison.Ordinal);
                    output.Add(lines[i]);
                    continue;
                }

                if (inSection)
                {
                    var eq = lines[i].IndexOf('=');
                    if (eq > 0)
                    {
                        var key = lines[i].Substring(0, eq).Trim();
                        if (keysToRemove.Contains(key))
                        {
                            changed = true;
                            continue;
                        }
                    }
                }
                output.Add(lines[i]);
            }

            if (changed)
                SettingsService.WriteAtomic(configPath, string.Join("\r\n", output) + "\r\n");
            return null;
        }

        internal const string EncryptedPruneNeedsNewerRclone =
            "Old options were left in place: removing them from an encrypted rclone.conf needs rclone 1.75 or later.";

        // `config unset` arrived in rclone v1.75; older builds read it as `rclone config` plus
        // stray arguments and only print usage.
        internal static bool SupportsConfigUnset(string? rcloneVersion) =>
            UpdateChecker.Compare(UpdateChecker.NormalizeVersion(rcloneVersion ?? ""), "1.75") >= 0;

        // Result of TestConnectionAsync — a boolean gate plus enough detail to render
        // a "✓ Connected in 425 ms · Free 12.3 GB / Total 100 GB" line without the UI
        // having to parse rclone output itself. UsedBytes / TotalBytes are null when
        // the backend doesn't implement `about` and we fell back to `lsd`.
        public sealed record TestConnectionResult(
            bool Success,
            TimeSpan Elapsed,
            string Method,
            long? FreeBytes,
            long? UsedBytes,
            long? TotalBytes,
            string? ErrorMessage);

        // Probes a remote by asking rclone for its quota (`about`), falling back to
        // a shallow listing (`lsd --max-depth 1`) when the backend doesn't support
        // about. Both are cheap round-trips that surface auth/network/config errors
        // without pulling any file bytes. Cancels + kills the child on timeout so a
        // hung remote can't stall the UI thread.
        public async Task<TestConnectionResult> TestConnectionAsync(string remoteName, TimeSpan timeout, CancellationToken ct = default)
        {
            // Without this the test reaches the local drive and reports success.
            if (RcloneRemote.DriveLetterError(remoteName) is { } nameError)
                throw new InvalidOperationException(nameError);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (stdout, stderr, exit) = await RunCancellableAsync(cts.Token, "about", $"{remoteName}:", "--json").ConfigureAwait(false);
            sw.Stop();

            if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                long? free = null, used = null, total = null;
                try
                {
                    using var doc = JsonDocument.Parse(stdout);
                    if (doc.RootElement.TryGetProperty("free", out var f) && f.ValueKind == JsonValueKind.Number) free = f.GetInt64();
                    if (doc.RootElement.TryGetProperty("used", out var u) && u.ValueKind == JsonValueKind.Number) used = u.GetInt64();
                    if (doc.RootElement.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number) total = t.GetInt64();
                }
                catch { /* rclone gave us non-JSON — still a success, just no quota */ }
                return new TestConnectionResult(true, sw.Elapsed, "about", free, used, total, null);
            }

            // Fall back to `lsd` when about is unimplemented for this backend, or when
            // it returned exit=1 but with a stderr that suggests config/auth is fine.
            // Cheaper than trying to sniff the reason from stderr.
            if (cts.IsCancellationRequested)
                return new TestConnectionResult(false, sw.Elapsed, "about", null, null, null, "Timed out after " + FormatTimeout(timeout));

            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(timeout);
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            var (_, stderr2, exit2) = await RunCancellableAsync(cts2.Token, "lsd", $"{remoteName}:", "--max-depth", "1").ConfigureAwait(false);
            sw2.Stop();

            if (exit2 == 0)
                return new TestConnectionResult(true, sw2.Elapsed, "lsd", null, null, null, null);

            if (cts2.IsCancellationRequested)
                return new TestConnectionResult(false, sw2.Elapsed, "lsd", null, null, null, "Timed out after " + FormatTimeout(timeout));

            // Neither worked — surface the more informative of the two stderrs.
            // Prefer lsd's stderr; about's failure is more often "not supported" noise.
            var msg = FirstMeaningfulLine(stderr2)
                   ?? FirstMeaningfulLine(stderr)
                   ?? $"rclone exited {exit2}";
            return new TestConnectionResult(false, sw2.Elapsed, "lsd", null, null, null, msg);
        }

        private static string FormatTimeout(TimeSpan t) =>
            t.TotalSeconds >= 1 ? $"{t.TotalSeconds:0}s" : $"{t.TotalMilliseconds:0}ms";

        private static string? FirstMeaningfulLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            foreach (var raw in s.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                // rclone prefixes info/debug lines with a timestamp+level tag; strip it
                // so the message is what the user actually sees.
                var colon = line.IndexOf(':');
                if (colon > 0 && line.Length > colon + 2)
                {
                    var tail = line.Substring(colon + 1).TrimStart();
                    if (tail.Length > 0) return tail;
                }
                return line;
            }
            return null;
        }

        private async Task<(string Stdout, string Stderr, int ExitCode)> RunCancellableAsync(CancellationToken ct, params string[] args)
        {
            var psi = RcloneConfigPassword.StartInfo(exePath);
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi) ?? throw new IOException("Failed to start rclone.");
            using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Cancellation-token registration already killed the process — swallow
                // and fall through so we return whatever we captured before the kill.
            }
            return (await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false), proc.HasExited ? proc.ExitCode : -1);
        }

        private async Task RunExpectSuccessAsync(params string[] args)
        {
            var (_, stderr, exit) = await RunAsync(args).ConfigureAwait(false);
            if (exit != 0)
                throw new InvalidOperationException(DescribeFailure(args, stderr));
        }

        // Only the verb goes in the message: the remaining args carry key=value pairs such as
        // pass=… or token=…, and this message is shown, logged to activity.log and bundled.
        internal static string DescribeFailure(string[] args, string stderr) =>
            $"rclone {string.Join(' ', args.Take(2))} failed: {stderr}";

        private Task<(string Stdout, string Stderr, int ExitCode)> RunAsync(params string[] args) =>
            RunAsync(Timeout.InfiniteTimeSpan, args);

        private async Task<(string Stdout, string Stderr, int ExitCode)> RunAsync(TimeSpan timeout, params string[] args)
        {
            var psi = RcloneConfigPassword.StartInfo(exePath);
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi) ?? throw new IOException("Failed to start rclone.");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            try
            {
                // The pipes too: a grandchild (password command) can keep them open after rclone exits.
                await Task.WhenAll(stdoutTask, stderrTask, proc.WaitForExitAsync()).WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { } // may have just exited
                throw new TimeoutException(
                    $"rclone {string.Join(' ', args.Take(2))} did not finish within {timeout.TotalSeconds:0.#} s and was stopped.");
            }
            return (await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false), proc.ExitCode);
        }
    }
}
