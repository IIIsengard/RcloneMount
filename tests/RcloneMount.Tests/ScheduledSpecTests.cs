using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.Tests;

public class ScheduledSpecTests
{
    [Fact]
    public void Bisync_KeepsSafetySettings()
    {
        var spec = MainForm.BuildSpecFromSaved(new TransferJob
        {
            Operation = "Bisync", SourceRemote = "a", DestRemote = "b",
            MaxDeletePct = 10, CheckAccess = true, ConflictResolve = "newer",
            ServerSideAcrossConfigs = true, DryRun = true,
        });

        Assert.Equal(TransferOperation.Bisync, spec.Operation);
        Assert.Equal(10, spec.MaxDeletePct);
        Assert.True(spec.CheckAccess);
        Assert.Equal("newer", spec.ConflictResolve);
        Assert.True(spec.DryRun);
        Assert.False(spec.ServerSideAcrossConfigs);
        Assert.False(spec.Resync);
    }

    [Fact]
    public void Check_KeepsCoverageFlags_DropsDryRun()
    {
        var spec = MainForm.BuildSpecFromSaved(new TransferJob
        {
            Operation = "Check", SourceRemote = "a", DestRemote = "b",
            OneWay = true, SizeOnly = true, DryRun = true, MaxDeletePct = 10,
        });

        Assert.True(spec.OneWay);
        Assert.True(spec.SizeOnly);
        Assert.False(spec.DryRun);
        Assert.Equal(0, spec.MaxDeletePct);
    }

    [Fact]
    public void Copy_ServerSideOnlyBetweenTwoRemotes()
    {
        var local = MainForm.BuildSpecFromSaved(new TransferJob { Operation = "Copy", SourcePath = @"C:\x", DestRemote = "b", ServerSideAcrossConfigs = true, OneWay = true });
        var remote = MainForm.BuildSpecFromSaved(new TransferJob { Operation = "Copy", SourceRemote = "a", DestRemote = "b", ServerSideAcrossConfigs = true });

        Assert.False(local.ServerSideAcrossConfigs);
        Assert.False(local.OneWay);
        Assert.True(remote.ServerSideAcrossConfigs);
    }

    // Scheduled and resumed runs pass the saved name on; rclone must refuse it, not use another place.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingRemote_FailsTheRun(bool missingSource)
    {
        if (RealRclone.Exe is not { } exe) return;
        var local = Directory.CreateTempSubdirectory("rm-src-").FullName;
        File.WriteAllText(Path.Combine(local, "a.txt"), "a");
        var gone = "rm-gone-" + Guid.NewGuid().ToString("N");
        var job = new TransferJob
        {
            Operation = "Copy",
            SourceRemote = missingSource ? gone : "", SourcePath = missingSource ? "x" : local,
            DestRemote = missingSource ? "" : gone, DestPath = missingSource ? Path.Combine(local, "out") : "x",
            ExtraFlags = $"--config \"{Path.Combine(local, "empty.conf")}\"", // never the user's rclone config
        };
        // Only a hang guard: on a loaded PC this has taken more than 30 s.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var result = await new RcloneJobService(exe).RunAsync(MainForm.BuildSpecFromSaved(job), new Progress<JobProgress>(), timeout.Token, "missing-remote");

        Assert.False(timeout.IsCancellationRequested, "rclone did not exit on its own");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("didn't find section in config file", result.StderrTail);
        Assert.False(Directory.Exists(Path.Combine(local, "out")));
    }

    // On Windows rclone reads "q:" as drive Q:, even when a remote "q" exists.
    [Theory]
    [InlineData("q", "")]
    [InlineData("", "Q")]
    public async Task OneLetterRemote_IsRefused_NotReadAsADrive(string source, string dest)
    {
        var service = new RcloneJobService(Path.Combine(TestIsolation.Dir, "no-rclone.exe"));
        var spec = MainForm.BuildSpecFromSaved(new TransferJob
        {
            Operation = "Copy", SourceRemote = source, SourcePath = "s", DestRemote = dest, DestPath = "d",
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RunAsync(spec, new Progress<JobProgress>(), CancellationToken.None));

        Assert.Contains("drive", error.Message);
    }

    [Fact]
    public async Task OneLetterRemote_IsNotMounted_NorConnectionTested()
    {
        var exe = Path.Combine(TestIsolation.Dir, "no-rclone.exe");
        using var mounts = new RcloneMountService(exe);

        var mount = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mounts.MountAsync(new Mount { RemoteName = "q", DriveLetter = "X:" }));
        var test = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new RcloneConfigService(exe).TestConnectionAsync("q", TimeSpan.FromSeconds(5)));

        Assert.Contains("drive", mount.Message);
        Assert.Contains("drive", test.Message);
    }

    [Fact]
    public void ExtraFlags_AppendedAfterFilterArgs()
    {
        var spec = MainForm.BuildSpecFromSaved(new TransferJob
        {
            Operation = "Copy", SourceRemote = "a", DestRemote = "b",
            FilterMinSize = "1M", ExtraFlags = "--fast-list --transfers 8",
        });

        Assert.Contains("--min-size", spec.ExtraArgs);
        Assert.Equal(new[] { "--fast-list", "--transfers", "8" }, spec.ExtraArgs.TakeLast(3));
    }
}
