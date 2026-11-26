using RcloneMount.Services;

namespace RcloneMount.Tests;

// Exercises only the pure decision function: the real HKCU Run key is never touched.
public class AutostartRepairTests
{
    private const string Current = @"C:\Apps\RcloneMount\RcloneMount.exe";
    private const string Other = @"D:\Old Build\RcloneMount.exe";

    [Theory]
    [InlineData(@"""D:\Old Build\RcloneMount.exe"" --startup")]
    [InlineData(@"""D:\Old Build\RcloneMount.exe""")]
    public void MissingExe_IsRepointedToCurrentExe(string value) =>
        Assert.Equal($"\"{Current}\" --startup",
            AutostartService.RepairedValue(value, Current, path => path == Current));

    [Fact]
    public void ExistingExe_IsLeftAlone_EvenWhenNotCurrent() =>
        Assert.Null(AutostartService.RepairedValue($"\"{Other}\" --startup", Current, path => path == Other));

    // No value (autostart off) and anything we cannot parse for certain must never be written.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"""")]
    [InlineData(@""""" --startup")]
    [InlineData(@"""C:\Gone\RcloneMount.exe --startup")]
    [InlineData(@"C:\Gone\RcloneMount.exe --startup")]
    [InlineData(@"""RcloneMount.exe"" --startup")]
    [InlineData(@"""%LOCALAPPDATA%\RcloneMount\RcloneMount.exe"" --startup")]
    public void NoValueOrUnparseable_IsLeftAlone(string? value) =>
        Assert.Null(AutostartService.RepairedValue(value, Current, _ => false));
}
