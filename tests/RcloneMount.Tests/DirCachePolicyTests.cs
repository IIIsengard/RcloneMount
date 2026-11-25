using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.Tests;

public class DirCachePolicyTests
{
    private static readonly string[] Polling = { "--dir-cache-time", "1000h", "--poll-interval", "15s" };
    private static readonly string[] Default = { "--dir-cache-time", "15s" };

    private static RcloneRemote R(string name, string type, params (string k, string v)[] p)
    {
        var r = new RcloneRemote { Name = name, Type = type };
        foreach (var (k, v) in p) r.Parameters[k] = v;
        return r;
    }

    [Theory]
    [InlineData("drive")]
    [InlineData("onedrive")]
    [InlineData("dropbox")]
    [InlineData("box")]
    [InlineData("pcloud")]
    [InlineData("pixeldrain")]
    [InlineData("huaweidrive")]
    public void PollingBackends_GetLongCacheAndPollInterval(string type)
    {
        Assert.Equal(Polling, DirCachePolicy.FlagsFor("x", new[] { R("x", type) }));
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("webdav")]
    [InlineData("sftp")]
    [InlineData("local")]
    [InlineData("smb")]
    [InlineData("iclouddrive")]
    [InlineData("b2")]
    public void NonPollingBackends_Keep15s(string type)
    {
        Assert.Equal(Default, DirCachePolicy.FlagsFor("x", new[] { R("x", type) }));
    }

    [Fact]
    public void OneDriveChina_DoesNotPoll() // rclone disables ChangeNotify for region=cn
    {
        Assert.Equal(Default, DirCachePolicy.FlagsFor("x", new[] { R("x", "onedrive", ("region", "cn")) }));
    }

    [Theory]
    [InlineData("crypt")]
    [InlineData("alias")]
    [InlineData("chunker")]
    [InlineData("hasher")]
    [InlineData("compress")]
    public void Wrappers_FollowWhatTheyWrap(string wrapper)
    {
        var over = new[] { R("gd", "drive"), R("w", wrapper, ("remote", "gd:enc/dir")) };
        Assert.Equal(Polling, DirCachePolicy.FlagsFor("w", over));
        var overS3 = new[] { R("s", "s3"), R("w", wrapper, ("remote", "s:bucket")) };
        Assert.Equal(Default, DirCachePolicy.FlagsFor("w", overS3));
    }

    [Fact]
    public void NestedWrappers_Resolve()
    {
        var remotes = new[] { R("gd", "drive"), R("al", "alias", ("remote", "gd:x")), R("c", "crypt", ("remote", "al:y")) };
        Assert.Equal(Polling, DirCachePolicy.FlagsFor("c", remotes));
    }

    [Fact]
    public void UnionAndCombine_PollOnlyWhenEveryUpstreamPolls()
    {
        var remotes = new[]
        {
            R("gd", "drive"), R("db", "dropbox"), R("s", "s3"),
            R("u1", "union", ("upstreams", "gd:a db:b:ro")),
            R("u2", "union", ("upstreams", "gd:a s:b")),
            R("c1", "combine", ("upstreams", "a=gd:a b=db:b")),
            R("c2", "combine", ("upstreams", "a=gd:a b=s:b")),
        };
        Assert.Equal(Polling, DirCachePolicy.FlagsFor("u1", remotes));
        Assert.Equal(Default, DirCachePolicy.FlagsFor("u2", remotes));
        Assert.Equal(Polling, DirCachePolicy.FlagsFor("c1", remotes));
        Assert.Equal(Default, DirCachePolicy.FlagsFor("c2", remotes));
    }

    [Fact]
    public void UnknownRemote_LocalPathOrCycle_FallBackToDefault()
    {
        var remotes = new[]
        {
            R("a", "alias", ("remote", "b:")), R("b", "alias", ("remote", "a:")),
            R("loc", "alias", ("remote", @"C:\data")), R("cs", "alias", ("remote", ":drive,x=y:")),
            R("noparam", "crypt"),
        };
        Assert.Equal(Default, DirCachePolicy.FlagsFor("missing", remotes));
        Assert.Equal(Default, DirCachePolicy.FlagsFor("a", remotes));
        Assert.Equal(Default, DirCachePolicy.FlagsFor("loc", remotes));
        Assert.Equal(Default, DirCachePolicy.FlagsFor("cs", remotes));
        Assert.Equal(Default, DirCachePolicy.FlagsFor("noparam", remotes));
    }
}
