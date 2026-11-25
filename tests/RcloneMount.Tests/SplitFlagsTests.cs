using RcloneMount.Services;

namespace RcloneMount.Tests;

public class SplitFlagsTests
{
    [Fact]
    public void QuotedValue_StaysOneToken()
    {
        Assert.Equal(new[] { "--a", "1", "x y" }, RcloneMountService.SplitFlags("--a 1 \"x y\""));
    }

    [Fact]
    public void Blank_GivesNoTokens()
    {
        Assert.Empty(RcloneMountService.SplitFlags("   "));
    }
}
