using System.Collections.ObjectModel;
using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.Tests;

public class MountArgsTests
{
    private static readonly RcloneRemote[] Remotes =
    {
        new() { Name = "gd", Type = "drive" },
        new() { Name = "s3", Type = "s3" },
    };

    private static Collection<string> Build(Mount m) => RcloneMountService.BuildArgs(m, 5572, Remotes);

    [Fact]
    public void PollingRemote_GetsLongDirCacheAndPollInterval()
    {
        var a = Build(new Mount { RemoteName = "gd", DriveLetter = "G:" });
        Assert.Equal(new[] { "mount", "gd:", "G:" }, a.Take(3));
        Assert.Equal("1000h", a[a.IndexOf("--dir-cache-time") + 1]);
        Assert.Equal("15s", a[a.IndexOf("--poll-interval") + 1]);
    }

    [Fact]
    public void DefaultArgs_KeepRcServerWithAuth_AndNeverDisableAuth()
    {
        var a = Build(new Mount { RemoteName = "gd", DriveLetter = "G:" });
        Assert.Equal("full", a[a.IndexOf("--vfs-cache-mode") + 1]);
        Assert.Contains("--rc", a);
        Assert.Equal("127.0.0.1:5572", a[a.IndexOf("--rc-addr") + 1]);
        var auth = new Collection<string>();
        RcAuth.AddServerArgs(auth);
        Assert.Equal(auth[1], a[a.IndexOf("--rc-user") + 1]);
        Assert.Equal(auth[3], a[a.IndexOf("--rc-pass") + 1]);
        Assert.DoesNotContain("--rc-no-auth", a);
    }

    [Fact]
    public void NonPollingRemote_Keeps15sAndNoPollInterval()
    {
        var a = Build(new Mount { RemoteName = "s3", DriveLetter = "S:" });
        Assert.Equal("15s", a[a.IndexOf("--dir-cache-time") + 1]);
        Assert.DoesNotContain("--poll-interval", a);
    }

    [Fact]
    public void ExtraFlags_AreLast_SoUserOverridesDefaults()
    {
        var a = Build(new Mount { RemoteName = "gd", DriveLetter = "G:", ExtraFlags = "--dir-cache-time 5m --poll-interval 1m" });
        Assert.Equal(new[] { "--dir-cache-time", "5m", "--poll-interval", "1m" }, a.TakeLast(4));
        Assert.True(a.ToList().LastIndexOf("--dir-cache-time") > a.IndexOf("--dir-cache-time"));
    }

    [Fact]
    public async Task LastRepeatedFlag_WinsInRealRclone() // rclone takes the last occurrence of a repeated flag
    {
        var exe = RealRclone.Exe;
        if (exe is null) return;
        var dir = Directory.CreateTempSubdirectory("rm-order-").FullName;
        int rc = FreePort(), dav = FreePort();
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var x in new[] { "serve", "webdav", dir, "--addr", $"127.0.0.1:{dav}", "--rc", "--rc-addr", $"127.0.0.1:{rc}" })
            psi.ArgumentList.Add(x);
        RcAuth.AddServerArgs(psi.ArgumentList);
        foreach (var x in new[] { "--dir-cache-time", "1000h", "--dir-cache-time", "5m" })
            psi.ArgumentList.Add(x);
        using var p = System.Diagnostics.Process.Start(psi)!;
        try
        {
            using var http = new HttpClient { DefaultRequestHeaders = { Authorization = RcAuth.Header } };
            string? body = null;
            for (var i = 0; i < 40 && body is null; i++)
            {
                try { body = await (await http.PostAsync($"http://127.0.0.1:{rc}/options/get", new StringContent("{}"))).Content.ReadAsStringAsync(); }
                catch { await Task.Delay(250); }
            }
            Assert.NotNull(body);
            Assert.Contains("\"DirCacheTime\": 300000000000", body!);
        }
        finally { try { p.Kill(true); p.WaitForExit(); } catch { } }
    }

    internal static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
    }
}
