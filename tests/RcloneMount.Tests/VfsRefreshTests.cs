using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.Tests;

public class VfsRefreshTests
{
    private static Mount M(string remote, string path = "") => new() { RemoteName = remote, RemotePath = path, DriveLetter = "G:" };

    private static JobSpec Spec(TransferOperation op, string dRemote, string dPath, string sRemote = "local", string sPath = "", bool dry = false) =>
        new() { Operation = op, DestRemote = dRemote, DestPath = dPath, SourceRemote = sRemote, SourcePath = sPath, DryRun = dry };

    [Theory]
    [InlineData("", "Backups/2026", "Backups/2026")]       // mount at remote root
    [InlineData("Backups", "Backups/2026", "2026")]        // destination inside the mount root
    [InlineData("Backups", "backups", null)]               // rclone paths are case-exact
    [InlineData("Backups", "Backups", "")]                 // destination is the mount root
    [InlineData("Backups/2026", "Backups", "")]            // destination above the mount root: refresh everything
    [InlineData("Other", "Backups/2026", null)]            // outside the mount
    [InlineData("/Backups/", "Backups\\2026\\", "2026")]   // slashes normalised
    public void RelativeDir_IsPathInsideMountRoot(string mountPath, string jobPath, string? expected)
    {
        Assert.Equal(expected, VfsRefresher.RelativeDir(mountPath, jobPath));
    }

    [Fact]
    public void Targets_MatchOnRemoteName_AndSkipDryRunAndCheck()
    {
        var mounts = new[] { M("gd"), M("s3") };
        var t = VfsRefresher.Targets(Spec(TransferOperation.Copy, "gd", "in"), mounts).ToList();
        Assert.Equal(("gd", "in"), (t.Single().mount.RemoteName, t.Single().dir));

        Assert.Empty(VfsRefresher.Targets(Spec(TransferOperation.Copy, "gd", "in", dry: true), mounts));
        Assert.Empty(VfsRefresher.Targets(Spec(TransferOperation.Check, "gd", "in"), mounts));
        Assert.Empty(VfsRefresher.Targets(Spec(TransferOperation.CryptCheck, "gd", "in"), mounts));
        Assert.Empty(VfsRefresher.Targets(Spec(TransferOperation.Copy, "other", "in"), mounts));
    }

    [Fact]
    public void Move_AndBisync_AlsoRefreshTheSourceSide()
    {
        var mounts = new[] { M("gd"), M("s3") };
        var move = VfsRefresher.Targets(Spec(TransferOperation.Move, "gd", "d", "s3", "s"), mounts).Select(x => x.mount.RemoteName).ToList();
        Assert.Equal(new[] { "gd", "s3" }, move);
        var copy = VfsRefresher.Targets(Spec(TransferOperation.Copy, "gd", "d", "s3", "s"), mounts).Select(x => x.mount.RemoteName).ToList();
        Assert.Equal(new[] { "gd" }, copy);
    }

    [Fact]
    public void Chain_ListsAncestorsRootFirst()
    {
        Assert.Equal(new[] { "", "a", "a/b" }, VfsRefresher.Chain("a/b"));
        Assert.Equal(new[] { "" }, VfsRefresher.Chain(""));
    }

    [Fact]
    public void Move_WithinOneRemote_RefreshesTheSameFolderOnce()
    {
        var mounts = new[] { M("gd") };
        var t = VfsRefresher.Targets(Spec(TransferOperation.Move, "gd", "d", "gd", "d"), mounts).ToList();
        Assert.Single(t);
        // Different folders of the same mount are both kept.
        var two = VfsRefresher.Targets(Spec(TransferOperation.Bisync, "gd", "a", "gd", "b"), mounts).ToList();
        Assert.Equal(new[] { "a", "b" }, two.Select(x => x.dir));
    }

    // Stand-in for a mount's RC server: records each vfs/refresh body, answers per request number.
    private static async Task<List<string>> RunFake(Func<int, string> reply, Func<int, string, Task> act)
    {
        var port = MountArgsTests.FreePort();
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var bodies = new List<string>();
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                using var rd = new StreamReader(ctx.Request.InputStream);
                bodies.Add(await rd.ReadToEndAsync());
                var bytes = System.Text.Encoding.UTF8.GetBytes(reply(bodies.Count));
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        try { await act(port, ""); } finally { listener.Stop(); await serve; }
        return bodies;
    }

    private const string Ok = "{\"result\":{\"x\":\"OK\"}}";
    private const string Missing = "{\"result\":{\"x\":\"file does not exist\"}}";

    [Fact]
    public async Task Refresh_IsTopDown_AndNeverRecursive()
    {
        var bodies = await RunFake(_ => Ok, (port, _) => VfsRefresher.RefreshAsync(port, "a/b"));
        Assert.Equal(new[] { "{}", "{\"dir\":\"a\"}", "{\"dir\":\"a/b\"}" }, bodies);
    }

    [Fact]
    public async Task Refresh_MissingNonRootFolder_StopsQuietly()
    {
        // root OK, then "a" does not exist: no exception, "a/b" is never asked.
        var bodies = await RunFake(n => n == 1 ? Ok : Missing, (port, _) => VfsRefresher.RefreshAsync(port, "a/b"));
        Assert.Equal(2, bodies.Count);
    }

    [Fact]
    public async Task Refresh_OtherErrors_StillThrow()
    {
        await RunFake(n => "{\"result\":{\"x\":\"permission denied\"}}", async (port, _) =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => VfsRefresher.RefreshAsync(port, "a")));
        // "does not exist" for the root itself is a real problem too.
        await RunFake(_ => Missing, async (port, _) =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => VfsRefresher.RefreshAsync(port, "a")));
    }

    [Fact]
    public async Task Refresh_MakesNewFolderVisible_InRealRclone()
    {
        var exe = RealRclone.Exe;
        if (exe is null) return;
        var dir = Directory.CreateTempSubdirectory("rm-vfsr-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "a"));
        int rc = MountArgsTests.FreePort(), dav = MountArgsTests.FreePort();
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var x in new[] { "serve", "webdav", dir, "--addr", $"127.0.0.1:{dav}", "--rc", "--rc-addr", $"127.0.0.1:{rc}", "--dir-cache-time", "1000h" })
            psi.ArgumentList.Add(x);
        RcAuth.AddServerArgs(psi.ArgumentList);
        using var p = System.Diagnostics.Process.Start(psi)!;
        try
        {
            using var http = new HttpClient();
            async Task<string> Root()
            {
                var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"http://127.0.0.1:{dav}/");
                req.Headers.Add("Depth", "1");
                return await (await http.SendAsync(req)).Content.ReadAsStringAsync();
            }
            string? first = null;
            for (var i = 0; i < 40 && first is null; i++)
            {
                try { first = await Root(); } catch { await Task.Delay(250); }
            }
            Assert.NotNull(first);
            Directory.CreateDirectory(Path.Combine(dir, "new", "sub"));
            Assert.DoesNotContain("/new/", await Root()); // cached listing is stale

            await VfsRefresher.RefreshAsync(rc, "new/sub");

            Assert.Contains("/new/", await Root());
        }
        finally { try { p.Kill(true); p.WaitForExit(); } catch { } }
    }
}
