namespace RcloneMount.Tests;

// Messages must not claim something happened when it did not.
public class ResultMessageTests
{
    [Fact]
    public void CliUnmount_OfADriveAnotherProcessStillHolds_IsAFailure()
    {
        var reply = MainForm.UnmountReply("media", "M:", stillMounted: true);

        Assert.Equal(5, reply.ExitCode);
        Assert.Contains("M: is still mounted", reply.Message);
    }

    [Fact]
    public void CliUnmount_OfAGoneDrive_Succeeds()
    {
        var reply = MainForm.UnmountReply("media", "M:", stillMounted: false);

        Assert.Equal(0, reply.ExitCode);
        Assert.Equal("Unmounted 'media'.", reply.Message);
    }

    [Theory]
    [InlineData(0, 0, "Renamed to new and updated.")]
    [InlineData(2, 2, "Renamed to new, 2 mount(s) remounted.")]
    [InlineData(2, 1, "Renamed to new, 1 of 2 mount(s) remounted. See the activity log.")]
    public void Rename_CountsOnlyTheDrivesThatCameBack(int unmounted, int back, string expected) =>
        Assert.Equal(expected, MainForm.RenameStatus("new", unmounted, back));
}
