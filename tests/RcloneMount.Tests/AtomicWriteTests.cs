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
}
