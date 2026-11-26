using RcloneMount.Services;

namespace RcloneMount.Tests;

// Real-rclone integration tests run against C:\rclone\rclone.exe (override with RCLONE_EXE)
// and return early when it is missing, so the suite still passes on a bare machine.
internal static class RealRclone
{
    internal static string? Exe
    {
        get
        {
            var p = Environment.GetEnvironmentVariable("RCLONE_EXE");
            if (string.IsNullOrWhiteSpace(p)) p = @"C:\rclone\rclone.exe";
            return File.Exists(p) ? p : null;
        }
    }

    // False when rclone is missing or older than `min` (e.g. "1.75"), so version-bound tests return early.
    internal static bool AtLeast(string min) =>
        Exe is { } exe
        && UpdateChecker.Compare(UpdateChecker.NormalizeVersion(RcloneLocator.ExamineAsync(exe).GetAwaiter().GetResult()?.Version ?? ""), min) >= 0;
}
