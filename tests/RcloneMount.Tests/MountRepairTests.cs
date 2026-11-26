using RcloneMount.Models;

namespace RcloneMount.Tests;

public class MountRepairTests
{
    private static readonly Mount OnGdrive = new() { RemoteName = "gdrive", DriveLetter = "G:" };
    private static readonly Mount OnGone = new() { RemoteName = "gone", DriveLetter = "H:" };

    [Fact]
    public void Orphans_AreMountsWhoseRemoteIsGone()
    {
        var remotes = new[] { new RcloneRemote { Name = "gdrive", Type = "drive" } };
        Assert.Equal(new[] { OnGone }, MainForm.OrphanedMounts(new[] { OnGdrive, OnGone }, remotes));
    }

    // `config dump` on a missing config exits 0 with {}: an empty list is not proof every remote is gone.
    [Fact]
    public void EmptyRemoteList_PrunesNothing() =>
        Assert.Empty(MainForm.OrphanedMounts(new[] { OnGdrive, OnGone }, Array.Empty<RcloneRemote>()));
}
