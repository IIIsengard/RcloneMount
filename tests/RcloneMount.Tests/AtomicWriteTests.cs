using RcloneMount.Services;

namespace RcloneMount.Tests;

public class AtomicWriteTests
{
    [Fact]
    public void WriteAtomic_CreatesThenOverwrites_LeavesNoTemp()
    {
        var dir = Directory.CreateTempSubdirectory("rm-atomic-").FullName;
        var path = Path.Combine(dir, "rclone.conf");

        SettingsService.WriteAtomic(path, "[a]\r\ntype = local\r\n");
        SettingsService.WriteAtomic(path, "[b]\r\ntype = sftp\r\n");

        Assert.Equal("[b]\r\ntype = sftp\r\n", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(dir));
    }

    // Totals are saved right before the process is killed (shutdown, update); a torn file loads as 0.
    [Fact]
    public async Task TransferTotals_SaveReplacesIntact_LeavesNoTemp()
    {
        var path = TransferTotalsService.FilePath;
        Assert.StartsWith(TestIsolation.Dir, path);
        File.WriteAllText(path, "{ \"DownBytes\": 1 }");

        await TransferTotalsService.SaveAsync(new TransferTotals { DownBytes = 123, UpBytes = 45 });

        var back = TransferTotalsService.Load();
        Assert.Equal(123, back.DownBytes);
        Assert.Equal(45, back.UpBytes);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }
}
