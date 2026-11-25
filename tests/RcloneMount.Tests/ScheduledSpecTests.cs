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
