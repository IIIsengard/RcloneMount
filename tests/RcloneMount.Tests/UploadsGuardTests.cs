using RcloneMount.Services;

namespace RcloneMount.Tests;

public class UploadsGuardTests
{
    // null = a live mount's rclone did not answer, so the total is unknown, never 0.
    public static TheoryData<int?[], int?> Sums => new()
    {
        { new int?[0], 0 },
        { new int?[] { 0, 0 }, 0 },
        { new int?[] { 2, 3 }, 5 },
        { new int?[] { 1, null }, null },
        { new int?[] { 0, null }, null },
    };

    [Theory, MemberData(nameof(Sums))]
    public void Sum_UnknownIsNotZero(int?[] counts, int? expected) =>
        Assert.Equal(expected, UploadsGuard.Sum(counts));

    [Theory]
    [InlineData(0, true, UploadsGuardStep.Proceed)]
    [InlineData(0, false, UploadsGuardStep.Proceed)]
    [InlineData(3, true, UploadsGuardStep.Ask)]
    [InlineData(3, false, UploadsGuardStep.Refuse)]
    // Interactive (first check and every Wait re-count): unknown keeps asking/waiting, never goes ahead silently.
    [InlineData(null, true, UploadsGuardStep.Ask)]
    // Automatic: a dead or hung RC must not block a remount or an update.
    [InlineData(null, false, UploadsGuardStep.Proceed)]
    public void Decide(int? pending, bool interactive, UploadsGuardStep expected) =>
        Assert.Equal(expected, UploadsGuard.Decide(pending, interactive));
}
