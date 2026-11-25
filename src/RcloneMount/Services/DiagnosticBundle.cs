using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RcloneMount.Services
{
    // Bundle for bug reports. Deliberately excludes rclone.conf (contains obscured
    // passwords and OAuth tokens). settings.json has its ExtraFlags redacted and
    // activity.log goes through RedactLogLine line by line — older builds logged failed
    // config commands with their pass=/token= arguments. If future fields ever start
    // storing secrets, redact them here before adding them to the zip.
    public static class DiagnosticBundle
    {
        public sealed record Inputs(
            string AppVersion,
            RcloneInfo? Rclone,
            WinFspInfo? WinFsp);

        public static async Task WriteAsync(string destinationZipPath, Inputs inputs)
        {
            // Overwrite if the user reused a filename — SaveFileDialog already prompted.
            if (File.Exists(destinationZipPath))
                File.Delete(destinationZipPath);

            using var zip = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create);

            await WriteSummaryAsync(zip, inputs).ConfigureAwait(false);
            await WriteRedactedSettingsAsync(zip, SettingsService.CurrentSettingsPath).ConfigureAwait(false);
            await CopyRedactedLogIfExistsAsync(zip, SettingsService.ActivityLogPath, "activity.log").ConfigureAwait(false);
            await CopyIntoZipIfExistsAsync(zip, TransferHistoryService.HistoryPath,     "transfers.jsonl").ConfigureAwait(false);
        }

        private static async Task WriteSummaryAsync(ZipArchive zip, Inputs inputs)
        {
            var entry = zip.CreateEntry("diagnostic.txt", CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await using var writer = new StreamWriter(stream);

            await writer.WriteLineAsync("RcloneMount diagnostic bundle").ConfigureAwait(false);
            await writer.WriteLineAsync($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync($"App version:    {inputs.AppVersion}").ConfigureAwait(false);
            await writer.WriteLineAsync($"OS:             {RuntimeInformation.OSDescription}").ConfigureAwait(false);
            await writer.WriteLineAsync($".NET runtime:   {RuntimeInformation.FrameworkDescription}").ConfigureAwait(false);
            await writer.WriteLineAsync($"Architecture:   OS={RuntimeInformation.OSArchitecture}, process={RuntimeInformation.ProcessArchitecture}").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);

            if (inputs.Rclone is null)
            {
                await writer.WriteLineAsync("rclone:         not detected").ConfigureAwait(false);
            }
            else
            {
                await writer.WriteLineAsync($"rclone:         {inputs.Rclone.Version}").ConfigureAwait(false);
                await writer.WriteLineAsync($"  path:         {inputs.Rclone.ExecutablePath}").ConfigureAwait(false);
            }

            if (inputs.WinFsp is null)
            {
                await writer.WriteLineAsync("WinFsp:         not detected").ConfigureAwait(false);
            }
            else
            {
                var v = string.IsNullOrEmpty(inputs.WinFsp.Version) ? "(version unavailable)" : inputs.WinFsp.Version;
                await writer.WriteLineAsync($"WinFsp:         {v}").ConfigureAwait(false);
                await writer.WriteLineAsync($"  install dir:  {inputs.WinFsp.InstallDir}").ConfigureAwait(false);
            }

            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync("Files included:").ConfigureAwait(false);
            await writer.WriteLineAsync("  - settings.json  (app preferences + mounts; ExtraFlags values matching").ConfigureAwait(false);
            await writer.WriteLineAsync("                    --*password/token/secret/key/auth/bearer are replaced").ConfigureAwait(false);
            await writer.WriteLineAsync("                    with [REDACTED])").ConfigureAwait(false);
            await writer.WriteLineAsync("  - activity.log   (recent app events, ~30 day retention)").ConfigureAwait(false);
            await writer.WriteLineAsync("  - transfers.jsonl (per-file transfer history — filenames + sizes; may not").ConfigureAwait(false);
            await writer.WriteLineAsync("                     be present on a fresh install)").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync("NOT included: rclone.conf (contains OAuth tokens and obscured passwords).").ConfigureAwait(false);
        }

        // Standard rclone/RcloneMount schema fields don't carry secrets, but Mount.ExtraFlags
        // is free-form user input — a tester may have dropped `--drive-token=…` or `--rc-pass=…`
        // in there. Parse settings.json via JsonNode so unknown fields survive round-trip,
        // then rewrite ExtraFlags values in-place with RedactSensitiveFlags. If parsing fails
        // (malformed file), fall back to omitting settings.json — the alternative is leaking
        // whatever secret happened to be in the file.
        private static async Task WriteRedactedSettingsAsync(ZipArchive zip, string sourcePath)
        {
            if (!File.Exists(sourcePath)) return;

            string original;
            try
            {
                original = await File.ReadAllTextAsync(sourcePath).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            string toWrite;
            try
            {
                var root = JsonNode.Parse(original);
                if (root is JsonObject obj && obj["Mounts"] is JsonArray mounts)
                {
                    foreach (var mountNode in mounts)
                    {
                        if (mountNode is not JsonObject mount) continue;
                        if (mount["ExtraFlags"] is not JsonValue val) continue;
                        var s = val.GetValue<string>();
                        var redacted = RedactSensitiveFlags(s);
                        if (!ReferenceEquals(redacted, s))
                            mount["ExtraFlags"] = redacted;
                    }
                }
                toWrite = root?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? original;
            }
            catch (JsonException)
            {
                // Malformed settings — refuse to ship the raw file since we can't sanitise it.
                var note = zip.CreateEntry("settings.json.SKIPPED.txt", CompressionLevel.Optimal);
                await using var s = note.Open();
                await using var w = new StreamWriter(s);
                await w.WriteAsync("settings.json could not be parsed for redaction and was omitted from the bundle.").ConfigureAwait(false);
                return;
            }

            var entry = zip.CreateEntry("settings.json", CompressionLevel.Optimal);
            await using var dst = entry.Open();
            await using var writer = new StreamWriter(dst, new UTF8Encoding(false));
            await writer.WriteAsync(toWrite).ConfigureAwait(false);
        }

        // Redacts secret-looking values from an rclone-style flag string. Handles both
        // `--flag=value` and `--flag value` forms, plus simple double-quoted values.
        // The name pattern is intentionally broad — false positives (a harmless flag
        // getting redacted) are a minor debug annoyance; false negatives leak credentials.
        private static readonly Regex SecretFlagRegex = new(
            @"(--[A-Za-z0-9-]*(?:password|passwd|pass|secret|token|key|auth|credential|bearer)[A-Za-z0-9-]*)(\s*=\s*|\s+)(""[^""]*""|'[^']*'|\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Catches HTTP-auth tokens tucked inside --header values, where the flag NAME
        // (`--header`) doesn't look sensitive but the payload is.
        private static readonly Regex AuthHeaderRegex = new(
            @"(Authorization\s*:\s*(?:Bearer|Basic|Token|Digest)\s+)[^\s""']+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string RedactSensitiveFlags(string extraFlags)
        {
            if (string.IsNullOrWhiteSpace(extraFlags)) return extraFlags;
            var afterFlags = SecretFlagRegex.Replace(extraFlags, m =>
                $"{m.Groups[1].Value}{m.Groups[2].Value}[REDACTED]");
            var afterHeaders = AuthHeaderRegex.Replace(afterFlags, m =>
                $"{m.Groups[1].Value}[REDACTED]");
            return afterHeaders;
        }

        // key=value secrets as rclone config commands spell them (pass=…, token={…}, sftp-key-file=…).
        private static readonly Regex SecretKeyValueRegex = new(
            @"\b([A-Za-z0-9_-]*(?:password|passwd|pass|secret|token|key|credential)[A-Za-z0-9_-]*\s*=\s*)\S+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Logs written before v1.1 could hold failed-command args with secrets in them.
        internal static string RedactLogLine(string line) =>
            SecretKeyValueRegex.Replace(RedactSensitiveFlags(line), m => $"{m.Groups[1].Value}[REDACTED]");

        private static async Task CopyRedactedLogIfExistsAsync(ZipArchive zip, string sourcePath, string entryName)
        {
            if (!File.Exists(sourcePath)) return;

            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var dst = new StreamWriter(entry.Open());
            using var src = new StreamReader(new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            while (await src.ReadLineAsync().ConfigureAwait(false) is { } line)
                await dst.WriteLineAsync(RedactLogLine(line)).ConfigureAwait(false);
        }

        private static async Task CopyIntoZipIfExistsAsync(ZipArchive zip, string sourcePath, string entryName)
        {
            if (!File.Exists(sourcePath)) return;

            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var dst = entry.Open();
            // Read via a shared handle so an actively-written log file doesn't error out.
            await using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await src.CopyToAsync(dst).ConfigureAwait(false);
        }
    }
}
