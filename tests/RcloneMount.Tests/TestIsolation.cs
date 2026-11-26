using System.Runtime.CompilerServices;
using RcloneMount.Services;

namespace RcloneMount.Tests;

// Tests that run real jobs must never write the user's %AppData%\RcloneMount files.
// Runs once, before any test in this assembly.
internal static class TestIsolation
{
    internal static readonly string Dir = Directory.CreateTempSubdirectory("rm-test-appdata-").FullName;

    [ModuleInitializer]
    internal static void Init()
    {
        TransferHistoryService.PathOverride = Path.Combine(Dir, "transfers.jsonl");
        TransferTotalsService.PathOverride = Path.Combine(Dir, "transfer-totals.json");
        SettingsService.PathOverride = Path.Combine(Dir, "RcloneMount", "settings.json");
        SettingsService.RcloneConfigPathOverride = Path.Combine(Dir, "rclone", "rclone.conf");
        RcloneConfigPassword.FilePath = Path.Combine(Dir, "RcloneMount", "config-pass.bin");
    }
}

// For classes that swap or assert on process-wide statics (settings paths and flags, the save gate,
// the stored-password path, RCLONE_CONFIG): nothing else runs beside them.
[CollectionDefinition(nameof(SharedStatics), DisableParallelization = true)]
public class SharedStatics { }

public class TestIsolationTests
{
    [Fact]
    public void HistoryPath_IsRedirectedUnderTempDir()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RcloneMount");
        Assert.StartsWith(TestIsolation.Dir, TransferHistoryService.HistoryPath);
        Assert.False(TransferHistoryService.HistoryPath.StartsWith(real, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RealJobRun_AppendsToRedirectedHistory()
    {
        if (RealRclone.Exe is not { } exe) return;
        var src = Directory.CreateTempSubdirectory("rm-src-").FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        var spec = new JobSpec
        {
            Operation = TransferOperation.Copy,
            SourcePath = src,
            DestPath = Path.Combine(Path.GetTempPath(), "rm-dst-" + Guid.NewGuid().ToString("N")),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await new RcloneJobService(exe).RunAsync(spec, new Progress<JobProgress>(), timeout.Token, "isolation-test");

        Assert.Contains("isolation-test", File.ReadAllText(TransferHistoryService.HistoryPath));
    }
}
