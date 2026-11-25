using System.Diagnostics;
using System.Text;
using RcloneMount.Services;

namespace RcloneMount.Tests;

public class RcAuthTests
{
    [Fact]
    public void AddServerArgs_AddsCredentials_NeverNoAuth()
    {
        var psi = new ProcessStartInfo();
        RcAuth.AddServerArgs(psi.ArgumentList);

        Assert.Equal(new[] { "--rc-user", RcAuth.User, "--rc-pass", RcAuth.Pass }, psi.ArgumentList);
        Assert.DoesNotContain("--rc-no-auth", psi.ArgumentList);
    }

    [Fact]
    public void Pass_IsLongRandomSecret()
    {
        Assert.True(RcAuth.Pass.Length >= 32);
    }

    [Fact]
    public void Header_IsBasicAuthForUserAndPass()
    {
        Assert.Equal("Basic", RcAuth.Header.Scheme);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(RcAuth.Header.Parameter!));
        Assert.Equal($"{RcAuth.User}:{RcAuth.Pass}", decoded);
    }
}

public class RcAuthRealRcloneTests
{
    [Fact]
    public async Task RealRclone_RejectsNoAuth_AcceptsRcAuth()
    {
        if (RealRclone.Exe is not { } exe) return;
        var port = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        port.Start();
        var p = ((System.Net.IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("rcd");
        psi.ArgumentList.Add("--rc-addr");
        psi.ArgumentList.Add($"127.0.0.1:{p}");
        RcAuth.AddServerArgs(psi.ArgumentList);
        using var proc = Process.Start(psi)!;
        try
        {
            using var anon = new HttpClient();
            using var authed = new HttpClient { DefaultRequestHeaders = { Authorization = RcAuth.Header } };
            var url = $"http://127.0.0.1:{p}/core/stats";
            HttpResponseMessage? ok = null;
            for (var i = 0; i < 50 && ok is null; i++)
            {
                try { ok = await authed.PostAsync(url, new StringContent("{}")); }
                catch (HttpRequestException) { await Task.Delay(100); }
            }
            Assert.NotNull(ok);
            Assert.Equal(System.Net.HttpStatusCode.OK, ok!.StatusCode);
            var denied = await anon.PostAsync($"http://127.0.0.1:{p}/operations/list?fs=/&remote=", new StringContent(""));
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        finally
        {
            try { proc.Kill(); } catch { }
        }
    }
}
