using RcloneMount.Services;

namespace RcloneMount.Tests;

public class SecretLeakTests
{
    [Fact]
    public void DescribeFailure_NamesVerbButNotValues()
    {
        var msg = RcloneConfigService.DescribeFailure(
            new[] { "config", "create", "x", "sftp", "pass=hunter2", "--obscure" }, "boom");

        Assert.DoesNotContain("hunter2", msg);
        Assert.Contains("config create", msg);
        Assert.Contains("boom", msg);
    }

    [Fact]
    public void DescribeFailure_ShortArgs()
    {
        Assert.Equal("rclone about failed: e", RcloneConfigService.DescribeFailure(new[] { "about" }, "e"));
    }

    [Fact]
    public void RedactLogLine_HidesKeyValueSecrets_KeepsTheRest()
    {
        var line = "Create failed. — rclone config create x sftp host=h pass=hunter2 token={\"access_token\":\"abc\"} --sftp-key-file=C:\\k failed: x";

        var red = DiagnosticBundle.RedactLogLine(line);

        Assert.Contains("host=h", red);
        Assert.DoesNotContain("hunter2", red);
        Assert.DoesNotContain("abc", red);
        Assert.DoesNotContain(@"C:\k", red);
    }
}
