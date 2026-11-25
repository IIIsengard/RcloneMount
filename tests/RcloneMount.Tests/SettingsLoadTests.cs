using RcloneMount.Services;

namespace RcloneMount.Tests;

public class SettingsLoadTests
{
    private static string TempSettings(string? content)
    {
        var dir = Directory.CreateTempSubdirectory("rm-settings-").FullName;
        var path = Path.Combine(dir, "settings.json");
        if (content is not null) File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Corrupt_WithQuarantine_MovesFileAside_ReturnsDefaults()
    {
        var path = TempSettings("{ \"Mounts\": [ oops");

        var s = SettingsService.LoadFrom(path, quarantine: true, out var movedTo);

        Assert.Empty(s.Mounts);
        Assert.False(File.Exists(path));
        Assert.NotNull(movedTo);
        Assert.True(File.Exists(movedTo));
        Assert.Matches(@"settings\.json\.bad-\d{8}-\d{6}$", movedTo);
    }

    [Fact]
    public void Corrupt_WithoutQuarantine_LeavesFileAlone()
    {
        var path = TempSettings("not json");

        SettingsService.LoadFrom(path, quarantine: false, out var movedTo);

        Assert.Null(movedTo);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Missing_ReturnsDefaults_NoRename()
    {
        var path = TempSettings(null);

        var s = SettingsService.LoadFrom(path, quarantine: true, out var movedTo);

        Assert.Null(movedTo);
        Assert.True(s.CheckForUpdatesOnStartup);
    }

    [Fact]
    public void V10Settings_LoadAllJobFields()
    {
        var path = TempSettings("""
            { "Mounts": [ { "RemoteName": "gd", "DriveLetter": "G:" } ],
              "Jobs": [ { "Name": "nightly", "Operation": "Bisync", "MaxDeletePct": 10, "CheckAccess": true } ] }
            """);

        var s = SettingsService.LoadFrom(path, quarantine: true, out var movedTo);

        Assert.Null(movedTo);
        Assert.Equal("gd", s.Mounts.Single().RemoteName);
        var job = s.Jobs.Single();
        Assert.Equal(10, job.MaxDeletePct);
        Assert.True(job.CheckAccess);
        Assert.Equal("", job.ExtraFlags); // v1.0.x jobs have no ExtraFlags (Review Focus 3)
    }
}
