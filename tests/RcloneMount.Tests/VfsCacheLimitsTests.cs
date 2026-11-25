using System.Collections.ObjectModel;
using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.Tests;

public class VfsCacheLimitsTests
{
    private static readonly RcloneRemote[] Remotes = { new() { Name = "gd", Type = "drive" } };

    private static Collection<string> Build(Mount m) => RcloneMountService.BuildArgs(m, 5572, Remotes);

    private static Mount M(Action<Mount>? edit = null)
    {
        var m = new Mount { RemoteName = "gd", DriveLetter = "G:" };
        edit?.Invoke(m);
        return m;
    }

    // ---- args builder ----

    [Fact]
    public void Empty_AddsNoCacheFlags()
    {
        var a = Build(M());
        Assert.DoesNotContain("--cache-dir", a);
        Assert.DoesNotContain("--vfs-cache-max-size", a);
        Assert.DoesNotContain("--vfs-cache-min-free-space", a);
    }

    [Fact]
    public void Set_AddsTheThreeFlags_BeforeExtraFlags()
    {
        var cache = Path.Combine(Path.GetTempPath(), "rm-cache");
        var a = Build(M(m =>
        {
            m.CacheDir = "  " + cache + "  ";
            m.VfsCacheMaxSize = "10G";
            m.VfsCacheMinFreeSpace = "off";
            m.ExtraFlags = "--vfs-cache-max-size 50G";
        }));
        Assert.Equal(cache, a[a.IndexOf("--cache-dir") + 1]);
        Assert.Equal("10G", a[a.IndexOf("--vfs-cache-max-size") + 1]);
        Assert.Equal("off", a[a.IndexOf("--vfs-cache-min-free-space") + 1]);
        // The user's extra flag stays last, so it still wins.
        Assert.Equal(new[] { "--vfs-cache-max-size", "50G" }, a.TakeLast(2));
        Assert.True(a.ToList().LastIndexOf("--vfs-cache-max-size") > a.IndexOf("--vfs-cache-max-size"));
    }

    [Theory]
    [InlineData(@"relative\dir", "", "", "--cache-dir")]
    [InlineData("", "10GB", "", "--vfs-cache-max-size")]
    [InlineData("", "", "-1", "--vfs-cache-min-free-space")]
    [InlineData("", "", "5 G", "--vfs-cache-min-free-space")]
    public void Invalid_ThrowsInsteadOfPassingJunkToRclone(string dir, string max, string free, string flag)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(M(m =>
        {
            m.CacheDir = dir;
            m.VfsCacheMaxSize = max;
            m.VfsCacheMinFreeSpace = free;
        })));
        Assert.Contains(flag, ex.Message);
    }

    // ---- validators ----

    [Theory]
    [InlineData("10G")]
    [InlineData("500M")]
    [InlineData("off")]
    [InlineData("0")]
    [InlineData("1.5G")]
    [InlineData("100")]
    [InlineData("10Gi")]
    [InlineData("10GiB")]
    [InlineData("10g")]
    [InlineData("5k")]
    [InlineData("1T")]
    [InlineData("10B")]
    [InlineData("  20G  ")]
    [InlineData("OFF")]
    [InlineData("Off")]
    [InlineData("10Gib")]
    [InlineData("10gib")]
    [InlineData("10GI")]
    [InlineData("10Ki")]
    [InlineData("")]
    public void Size_Accepted(string text) => Assert.Null(VfsCacheLimits.ValidateSize(text));

    [Theory]
    [InlineData("10GB")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("-5G")]
    [InlineData("1G2M")]
    [InlineData("1 G")]
    [InlineData("10Z")]
    [InlineData("10kB")]
    [InlineData("G")]
    [InlineData("--vfs-cache-mode full")]
    [InlineData("10GIB")]
    [InlineData("10GIb")]
    [InlineData("10Bi")]
    [InlineData("١٠G")]
    public void Size_Rejected_WithMessage(string text)
    {
        var err = VfsCacheLimits.ValidateSize(text);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    // The validator must agree with the real rclone parser (v1.76), except it may be stricter.
    [Theory]
    [InlineData("10G")] [InlineData("500M")] [InlineData("off")] [InlineData("1.5G")] [InlineData("10GiB")]
    [InlineData("OFF")] [InlineData("10Gib")] [InlineData("10gib")] [InlineData("10GI")] [InlineData("10GIB")] [InlineData("10GIb")]
    [InlineData("١٠G")] [InlineData("10Bi")]
    [InlineData("10GB")] [InlineData("abc")] [InlineData("-1")] [InlineData("1G2M")] [InlineData("10kB")] [InlineData("10Z")]
    public void Size_MatchesRealRclone(string text)
    {
        var exe = RealRclone.Exe;
        if (exe is null) return;
        // No pipes: the usage text rclone prints on a bad flag is bigger than a pipe buffer.
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "serve", "webdav", ".", "--vfs-cache-max-size", text, "-h" }) psi.ArgumentList.Add(x);
        using var p = System.Diagnostics.Process.Start(psi)!;
        if (!p.WaitForExit(15000)) { p.Kill(true); Assert.Fail("rclone did not exit"); }
        Assert.Equal(p.ExitCode == 0, VfsCacheLimits.ValidateSize(text) is null);
    }

    [Fact]
    public void Dir_Validation()
    {
        var ok = Path.Combine(Path.GetTempPath(), "rm-cache");
        Assert.Null(VfsCacheLimits.ValidateDir(""));
        Assert.Null(VfsCacheLimits.ValidateDir(ok));
        Assert.Null(VfsCacheLimits.ValidateDir("  " + ok + "  "));
        Assert.NotNull(VfsCacheLimits.ValidateDir(@"relative\dir"));
        Assert.NotNull(VfsCacheLimits.ValidateDir(Path.Combine(Path.GetTempPath(), "a|b")));
        Assert.NotNull(VfsCacheLimits.ValidateDir(@"gd:folder"));
        Assert.NotNull(VfsCacheLimits.ValidateDir("--cache-dir"));
        Assert.Null(VfsCacheLimits.ValidateDir(@"\\server\share\rclone cache"));       // UNC, not probed
        Assert.Null(VfsCacheLimits.ValidateDir(Path.Combine(Path.GetTempPath(), "my cache dir"))); // spaces
        Assert.NotNull(VfsCacheLimits.ValidateDir(MissingDriveRoot() + @"cache"));     // drive that does not exist
    }

    private static string MissingDriveRoot()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return char.ToUpperInvariant((char)('A' + Enumerable.Range(0, 26).First(i => !used.Contains((char)('A' + i))))) + @":\";
    }

    [Fact]
    public void Hints_FitTheirBox_AtLargeText()
    {
        // The editor's hint boxes are 308 px wide and two lines tall; check at 150% text size.
        foreach (var hint in new[] { RcloneMount.UI.AddMountForm.MaxSizeHint, RcloneMount.UI.AddMountForm.MinFreeHint })
        {
            using var f = new System.Drawing.Font(System.Drawing.SystemFonts.MessageBoxFont!.FontFamily, 9f * 1.5f);
            var h = System.Windows.Forms.TextRenderer.MeasureText(hint, f, new System.Drawing.Size(308, 0), System.Windows.Forms.TextFormatFlags.WordBreak).Height;
            Assert.True(h <= RcloneMount.UI.AddMountForm.HintBoxHeight, $"{hint} needs {h}px");
        }
    }

    // ---- settings.json compatibility ----

    [Fact]
    public void V10Mount_LoadsWithEmptyCacheFields()
    {
        var dir = Directory.CreateTempSubdirectory("rm-settings-").FullName;
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """
            { "Mounts": [ { "Id": "abc", "RemoteName": "gd", "RemotePath": "", "DriveLetter": "G:",
                            "VfsCacheMode": "full", "ExtraFlags": "--transfers 4", "AutoMount": true } ] }
            """);

        var s = SettingsService.LoadFrom(path, quarantine: true, out var movedTo);

        Assert.Null(movedTo);
        var m = s.Mounts.Single();
        Assert.Equal("--transfers 4", m.ExtraFlags);
        Assert.Equal("", m.CacheDir);
        Assert.Equal("", m.VfsCacheMaxSize);
        Assert.Equal("", m.VfsCacheMinFreeSpace);
    }
}
