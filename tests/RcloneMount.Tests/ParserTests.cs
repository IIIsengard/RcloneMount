using RcloneMount.Services;

namespace RcloneMount.Tests;

// Inputs are real rclone v1.76 `-v` output lines.
public class ParserTests
{
    [Theory]
    [InlineData("2026/11/24 10:00:00 INFO  : a.txt: Copied (new)", "a.txt", "Copied")]
    [InlineData("2026/11/24 10:00:00 INFO  : dir/b c.txt: Deleted", "dir/b c.txt", "Deleted")]
    public void InfoOpRe_ReadsNameAndVerb(string line, string name, string verb)
    {
        var m = RcloneJobService.InfoOpRe.Match(line);

        Assert.True(m.Success);
        Assert.Equal(name, m.Groups["name"].Value);
        Assert.Equal(verb, m.Groups["verb"].Value);
    }

    [Fact]
    public void DryRunSkipRe_ReadsNameVerbAndDetail()
    {
        var m = RcloneJobService.DryRunSkipRe.Match(
            "2026/11/24 20:50:48 NOTICE: a.txt: Skipped copy as --dry-run is set (size 3)");

        Assert.True(m.Success);
        Assert.Equal("a.txt", m.Groups["name"].Value);
        Assert.Equal("copy", m.Groups["verb"].Value);
        Assert.Equal("size 3", m.Groups["detail"].Value);
    }

    [Theory]
    [InlineData("daily 03:00", 3, 0)]
    [InlineData("Daily 23:59", 23, 59)]
    public void TryParseDailySchedule_AcceptsValidTimes(string expr, int h, int m)
    {
        Assert.True(MainForm.TryParseDailySchedule(expr, out var target));
        Assert.Equal(new TimeSpan(h, m, 0), target);
    }

    [Theory]
    [InlineData("daily 24:00")]
    [InlineData("weekly 03:00")]
    public void TryParseDailySchedule_RejectsInvalid(string expr)
    {
        Assert.False(MainForm.TryParseDailySchedule(expr, out _));
    }

    [Fact]
    public void RedactSensitiveFlags_HidesSecrets_KeepsOtherFlags()
    {
        var red = DiagnosticBundle.RedactSensitiveFlags(
            "--sftp-pass x --drive-token=y --header \"Authorization: Bearer z\" --transfers 4");

        Assert.Equal(
            "--sftp-pass [REDACTED] --drive-token=[REDACTED] --header \"Authorization: Bearer [REDACTED]\" --transfers 4",
            red);
    }
}
