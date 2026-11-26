using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RcloneMount.Services;

namespace RcloneMount.Tests;

// Fixtures are real rclone v1.76 `vfs/stats` replies, captured from `rclone serve webdav --rc`
// on a temp dir (paths shortened): "writes" with two files queued by --vfs-write-back 60s,
// "off" with --vfs-cache-mode off, where rclone leaves out `diskCache` entirely.
public class VfsStatsTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ParseUploadsPending_RealWritesSample_CountsQueued() =>
        Assert.Equal(2, RcloneStatsPoller.ParseUploadsPending(Fixture("vfs-stats-writes.json")));

    [Fact]
    public void ParseUploadsPending_RealCacheOffSample_IsZero() =>
        Assert.Equal(0, RcloneStatsPoller.ParseUploadsPending(Fixture("vfs-stats-off.json")));

    [Theory]
    [InlineData("""{"diskCache":{"uploadsInProgress":1,"uploadsQueued":3}}""", 4)]
    [InlineData("""{"diskCache":{"uploadsInProgress":2}}""", 2)]
    [InlineData("""{"diskCache":{}}""", 0)]
    [InlineData("""{"diskCache":null}""", 0)]
    public void ParseUploadsPending_SumsInProgressAndQueued(string json, int expected) =>
        Assert.Equal(expected, RcloneStatsPoller.ParseUploadsPending(json));

    // End to end through the app's own RC client (basic auth, no `fs` parameter) against a
    // real rclone serving a temp dir. Never a mount.
    [Fact]
    public async Task FetchUploadsPendingAsync_RealRclone_SeesQueuedUpload()
    {
        if (RealRclone.Exe is not { } exe) return;
        var dir = Directory.CreateTempSubdirectory("rm-vfs-").FullName;
        var (davPort, rcPort) = (FreePort(), FreePort());
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "serve", "webdav", Path.Combine(dir, "data"),
                     "--config", Path.Combine(dir, "rclone.conf"), "--cache-dir", Path.Combine(dir, "cache"),
                     "--addr", $"127.0.0.1:{davPort}", "--vfs-cache-mode", "writes", "--vfs-write-back", "60s",
                     "--rc", "--rc-addr", $"127.0.0.1:{rcPort}" })
            psi.ArgumentList.Add(a);
        RcAuth.AddServerArgs(psi.ArgumentList);
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        File.WriteAllText(Path.Combine(dir, "rclone.conf"), "");

        using var p = Process.Start(psi)!;
        try
        {
            int? idle = null;
            for (var i = 0; i < 50 && idle is null; i++)
            {
                idle = await RcloneStatsPoller.FetchUploadsPendingAsync(rcPort);
                if (idle is null) await Task.Delay(200);
            }
            Assert.Equal(0, idle);

            using var dav = new HttpClient();
            (await dav.PutAsync($"http://127.0.0.1:{davPort}/a.txt", new StringContent("hello"))).EnsureSuccessStatusCode();

            // On a loaded PC one request can pass the app's 2 s RC timeout: that reads as null ("unknown"),
            // so ask again. The upload stays queued for the 60 s write-back.
            int? pending = null;
            for (var i = 0; i < 20 && pending is null; i++)
                pending = await RcloneStatsPoller.FetchUploadsPendingAsync(rcPort);
            Assert.Equal(1, pending);
        }
        finally
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task FetchUploadsPendingAsync_NothingListening_IsNull() =>
        Assert.Null(await RcloneStatsPoller.FetchUploadsPendingAsync(FreePort()));

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}
