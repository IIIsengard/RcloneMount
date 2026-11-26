using RcloneMount.Services;

namespace RcloneMount.Tests;

public class JobServiceIntegrationTests
{
    // rclone with --rc exits on its own when the command ends (verified on v1.76), so a job
    // must never be stopped early. A failing copy with a 6 s retry sleep produces activity
    // (errors=1) followed by 6 s of flat stats — exactly what the old quiet-stats detector
    // misread as "done". The run must reach rclone's second attempt.
    [Fact]
    public async Task RunAsync_WaitsThroughQuietRetryGap()
    {
        if (RealRclone.Exe is not { } exe) return;
        var dst = Path.Combine(Path.GetTempPath(), "rm-test-" + Guid.NewGuid().ToString("N"));
        var spec = new JobSpec
        {
            Operation = TransferOperation.Copy,
            SourcePath = @"C:\nonexistent-rm-" + Guid.NewGuid().ToString("N"),
            DestPath = dst,
            ExtraArgs = new[] { "--retries", "2", "--retries-sleep", "6s", "--low-level-retries", "1" },
        };

        var result = await new RcloneJobService(exe).RunAsync(spec, new Progress<JobProgress>(), CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Attempt 2/2", result.StderrTail);
    }

    [Fact]
    public async Task RunAsync_DryRunCopy_ExitsOnItsOwn_ReportsActions()
    {
        if (RealRclone.Exe is not { } exe) return;
        var src = Directory.CreateTempSubdirectory("rm-src-").FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        File.WriteAllText(Path.Combine(src, "b.txt"), "b");
        var spec = new JobSpec
        {
            Operation = TransferOperation.Copy,
            SourcePath = src,
            DestPath = Path.Combine(Path.GetTempPath(), "rm-dst-" + Guid.NewGuid().ToString("N")),
            DryRun = true,
        };

        // Only a guard against hanging, well past what a loaded PC needs. When it fires, rclone is told
        // to quit and exits 0 with nothing reported.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var result = await new RcloneJobService(exe).RunAsync(spec, new Progress<JobProgress>(), timeout.Token);

        Assert.False(timeout.IsCancellationRequested, "rclone did not exit on its own");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, result.DryRunActions!.Count);
    }
}
