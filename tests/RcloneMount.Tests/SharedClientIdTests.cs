using RcloneMount.Models;

namespace RcloneMount.Tests;

public class SharedClientIdTests
{
    private static RcloneRemote R(string type, params (string K, string V)[] p)
    {
        var r = new RcloneRemote { Name = "x", Type = type };
        foreach (var (k, v) in p) r.Parameters[k] = v;
        return r;
    }

    [Theory]
    [InlineData("drive")]
    [InlineData("google photos")]
    public void GoogleOAuthRemote_WithoutClientId_Flagged(string type)
    {
        Assert.True(R(type).UsesSharedGoogleClientId);
        Assert.True(R(type, ("client_id", "  ")).UsesSharedGoogleClientId);
    }

    [Fact]
    public void OwnClientId_NotFlagged() =>
        Assert.False(R("drive", ("client_id", "123.apps.googleusercontent.com")).UsesSharedGoogleClientId);

    [Theory]
    [InlineData("service_account_file")]
    [InlineData("service_account_credentials")]
    public void ServiceAccountDrive_NotFlagged(string key) =>
        Assert.False(R("drive", (key, "something")).UsesSharedGoogleClientId);

    [Theory]
    [InlineData("onedrive")]
    [InlineData("s3")]
    [InlineData("Drive")]
    [InlineData("")]
    public void OtherTypes_NotFlagged(string type) =>
        Assert.False(R(type).UsesSharedGoogleClientId);
}
