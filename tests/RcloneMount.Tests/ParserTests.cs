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
