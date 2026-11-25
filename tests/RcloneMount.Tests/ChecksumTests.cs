using RcloneMount.Services;

namespace RcloneMount.Tests;

public class ChecksumTests
{
    private const string Zip = "rclone-v1.76.0-windows-amd64.zip";
    private static readonly string Hash = new string('a', 63) + "b";
    private static readonly string Sums =
        $"{new string('1', 64)}  rclone-v1.76.0-linux-amd64.zip\n" +
        $"{Hash}  {Zip}\n";

    [Fact]
    public void FindSha256_ReturnsHashForListedFile() =>
        Assert.Equal(Hash, PrerequisiteInstaller.FindSha256(Sums, Zip));

    [Fact]
    public void FindSha256_ReturnsNullWhenMissing() =>
        Assert.Null(PrerequisiteInstaller.FindSha256(Sums, "rclone-v1.76.0-windows-arm64.zip"));

    [Fact]
    public void FindSha256_ReadsInsidePgpClearSignedWrapper()
    {
        var signed =
            "-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA1\n\n" +
            Sums.Replace("\n", "\r\n") +
            "-----BEGIN PGP SIGNATURE-----\n\niQEzBAEBCAAdFiEE\n=abcd\n-----END PGP SIGNATURE-----\n";
        Assert.Equal(Hash, PrerequisiteInstaller.FindSha256(signed, Zip));
        Assert.Null(PrerequisiteInstaller.FindSha256(signed, "SHA1"));
    }

    [Fact]
    public void IsTrustedSignature_RejectsUnsignedFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not signed");
            Assert.False(PrerequisiteInstaller.IsTrustedSignature(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void IsTrustedSignature_AcceptsSignedFile()
    {
        // dotnet.exe carries an embedded Microsoft Authenticode signature; proves the P/Invoke layout.
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!File.Exists(dotnet)) return;
        Assert.True(PrerequisiteInstaller.IsTrustedSignature(dotnet));
    }
}
