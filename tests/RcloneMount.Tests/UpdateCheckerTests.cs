using RcloneMount.Services;

namespace RcloneMount.Tests;

public class UpdateCheckerTests
{
    [Fact]
    public void Compare_OrdersVersions()
    {
        Assert.Equal(-1, UpdateChecker.Compare("1.0.9", "1.0.10"));
        Assert.Equal(0, UpdateChecker.Compare("v1.76.0", "1.76"));
        Assert.Null(UpdateChecker.Compare("x", "1"));
    }
}
