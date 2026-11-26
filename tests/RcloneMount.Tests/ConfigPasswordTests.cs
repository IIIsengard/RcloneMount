using System.Diagnostics;
using System.Text;
using RcloneMount.Services;

namespace RcloneMount.Tests;

// Runs alone: these tests swap the stored-password path and set RCLONE_CONFIG for the whole
// process, and the user's real stored password and rclone.conf must never be touched.
[Collection(nameof(SharedStatics))]
public class ConfigPasswordTests : IDisposable
{
    // Distinctive, so a leak into stderr or failure text is unmistakable.
    private const string Password = "Rm16PwKq7Zx";
    private const string WrongPassword = "Rm16WrongJv3";

    private readonly string dir = Directory.CreateTempSubdirectory("rm-pass-").FullName;
    private readonly string savedPath = RcloneConfigPassword.FilePath;

    public ConfigPasswordTests() => RcloneConfigPassword.FilePath = Path.Combine(dir, "config-pass.bin");

    public void Dispose()
    {
        RcloneConfigPassword.FilePath = savedPath;
        RcloneConfigPassword.Pending = null;
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void Dpapi_RoundTrip_NoPlaintextOnDisk_ClearForgets()
    {
        Assert.Null(RcloneConfigPassword.Load());

        RcloneConfigPassword.Save("pä ss word ");

        Assert.Equal("pä ss word ", RcloneConfigPassword.Load());
        Assert.DoesNotContain("ss word", Encoding.UTF8.GetString(File.ReadAllBytes(RcloneConfigPassword.FilePath)));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        RcloneConfigPassword.Clear();
        Assert.Null(RcloneConfigPassword.Load());
    }

    [Fact]
    public void Pending_ReachesChildButIsNeverWritten()
    {
        RcloneConfigPassword.Save("stored");
        RcloneConfigPassword.Pending = "typed";

        Assert.Equal("typed", RcloneConfigPassword.StartInfo("rclone.exe").Environment["RCLONE_CONFIG_PASS"]);
        Assert.Equal("stored", RcloneConfigPassword.Load());
        RcloneConfigPassword.Pending = null;
        Assert.Equal("stored", RcloneConfigPassword.Current());
    }

    [Fact]
    public void IsEncrypted_UsesFirstLineThatIsNotBlankOrComment()
    {
        var enc = Path.Combine(dir, "enc.conf");
        File.WriteAllText(enc, "# Encrypted rclone configuration File\n\nRCLONE_ENCRYPT_V0:\nabc=\n");
        var plain = Path.Combine(dir, "plain.conf");
        File.WriteAllText(plain, "# RCLONE_ENCRYPT_V0:\n[foo]\ntype = local\n");

        Assert.True(RcloneConfigPassword.IsEncrypted(enc));
        Assert.False(RcloneConfigPassword.IsEncrypted(plain));
        Assert.False(RcloneConfigPassword.IsEncrypted(Path.Combine(dir, "missing.conf")));
        // Only a file positively read as plain may drop the stored password.
        Assert.False(RcloneConfigPassword.ReadEncrypted(plain));
        Assert.Null(RcloneConfigPassword.ReadEncrypted(Path.Combine(dir, "missing.conf")));
        using (new FileStream(plain, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(RcloneConfigPassword.ReadEncrypted(plain));
    }

    [Fact]
    public void StartInfo_PutsPasswordOnlyInChildEnvironment()
    {
        RcloneConfigPassword.Save("hunter2");

        var psi = RcloneConfigPassword.StartInfo("rclone.exe");
        psi.ArgumentList.Add("config");

        Assert.Equal("hunter2", psi.Environment["RCLONE_CONFIG_PASS"]);
        Assert.Equal("false", psi.Environment["RCLONE_ASK_PASSWORD"]);
        Assert.DoesNotContain("hunter2", psi.Arguments + string.Join(' ', psi.ArgumentList));
        Assert.NotEqual("hunter2", Environment.GetEnvironmentVariable("RCLONE_CONFIG_PASS"));
    }

    [Fact]
    public void IsPasswordError_MatchesRcloneDecryptErrors()
    {
        Assert.True(RcloneConfigPassword.IsPasswordError("CRITICAL: Failed to load config file \"x\": using RCLONE_CONFIG_PASS env password, unable to decrypt configuration"));
        Assert.True(RcloneConfigPassword.IsPasswordError("unable to decrypt configuration and not allowed to ask for password - set RCLONE_CONFIG_PASS to your configuration password"));
        Assert.False(RcloneConfigPassword.IsPasswordError("Failed to create file system: directory not found"));
    }

    [Theory]
    [InlineData("rclone v1.74.2", false)]
    [InlineData("rclone v1.75.0", true)]
    [InlineData("rclone v1.76.0", true)]
    [InlineData("rclone v2.0.0", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SupportsConfigUnset_NeedsRclone175(string? version, bool expected) =>
        Assert.Equal(expected, RcloneConfigService.SupportsConfigUnset(version));

    // Encrypts a temp config non-interactively, then runs the app's start info against it.
    // Right password: config dump works. Wrong or no password: rclone must fail fast with a
    // decrypt error even with stdin held open, which hangs on a prompt without the fix.
    [Fact]
    public async Task RealRclone_EncryptedConfig_DumpWorks_WrongOrMissingPasswordFailsFast()
    {
        if (RealRclone.Exe is not { } exe || !RealRclone.AtLeast("1.68")) return; // config encryption set: v1.68
        var conf = await CreateEncryptedConfigAsync(exe);

        RcloneConfigPassword.Save(Password);
        var ok = await RunAsync(RcloneConfigPassword.StartInfo(exe), "--config", conf, "config", "dump");
        Assert.Equal(0, ok.Exit);
        Assert.Contains("\"foo\"", ok.Stdout);
        Assert.DoesNotContain(Password, ok.Stderr);

        RcloneConfigPassword.Save(WrongPassword);
        var wrong = await RunAsync(RcloneConfigPassword.StartInfo(exe), "--config", conf, "config", "dump");
        Assert.NotEqual(0, wrong.Exit);
        Assert.True(RcloneConfigPassword.IsPasswordError(wrong.Stderr), wrong.Stderr);
        Assert.DoesNotContain(WrongPassword, wrong.Stderr);

        RcloneConfigPassword.Clear();
        var none = await RunAsync(RcloneConfigPassword.StartInfo(exe), "--config", conf, "config", "dump");
        Assert.NotEqual(0, none.Exit);
        Assert.True(RcloneConfigPassword.IsPasswordError(none.Stderr), none.Stderr);
    }

    // Through the real service code paths, which resolve rclone.conf like the app does.
    [Fact]
    public async Task RealRclone_EncryptedConfig_PruneUsesConfigUnset_RenameLeavesFileUntouched()
    {
        if (RealRclone.Exe is not { } exe || !RealRclone.AtLeast("1.75")) return; // config unset: v1.75
        var conf = await CreateEncryptedConfigAsync(exe);
        var version = (await RcloneLocator.ExamineAsync(exe))!.Version;
        RcloneConfigPassword.Save(Password);
        var savedEnv = Environment.GetEnvironmentVariable("RCLONE_CONFIG");
        Environment.SetEnvironmentVariable("RCLONE_CONFIG", conf);
        SettingsService.SetRcloneExePath(null); // drop any cached path
        SettingsService.SetRcloneExePath(exe);
        try
        {
            // Gate before any write: the service must be pointed at the temp file.
            Assert.Equal(Path.GetFullPath(conf), Path.GetFullPath(SettingsService.ResolveRcloneConfigPath()));
            var svc = new RcloneConfigService(exe);
            await svc.UpdateRemoteAsync("foo", new Dictionary<string, string> { ["links"] = "true", ["copy_links"] = "true" }, obscurePasswords: true);

            Assert.Null(await svc.PruneRemoteAsync("foo", new HashSet<string> { "links" }, version));

            var foo = (await svc.ListRemotesAsync()).Single();
            Assert.DoesNotContain("links", foo.Parameters.Keys);
            Assert.Contains("copy_links", foo.Parameters.Keys);
            Assert.True(RcloneConfigPassword.IsEncrypted(conf));

            var before = File.ReadAllBytes(conf);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RenameRemoteAsync("foo", "bar"));
            Assert.Equal(RcloneConfigService.EncryptedRenameMessage, ex.Message);
            Assert.Equal(before, File.ReadAllBytes(conf));

            // rclone below v1.75: keys stay and the caller gets a note, not an "Update failed".
            Assert.Equal(RcloneConfigService.EncryptedPruneNeedsNewerRclone,
                await svc.PruneRemoteAsync("foo", new HashSet<string> { "copy_links" }, "rclone v1.74.2"));
            Assert.Equal(before, File.ReadAllBytes(conf));
        }
        finally
        {
            Environment.SetEnvironmentVariable("RCLONE_CONFIG", savedEnv);
            SettingsService.SetRcloneExePath(null);
        }
    }

    // Scheduled jobs run unattended on the stored password; with a stale one the failure text
    // ends with the Refresh hint instead of a prompt.
    [Fact]
    public async Task RealRclone_EncryptedConfig_JobRunsOnStoredPassword_WrongOneAddsRefreshHint()
    {
        if (RealRclone.Exe is not { } exe || !RealRclone.AtLeast("1.68")) return;
        var conf = await CreateEncryptedConfigAsync(exe);
        var src = Directory.CreateDirectory(Path.Combine(dir, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        // Source goes through "foo", a local remote inside the encrypted config. Dry-run keeps
        // the user's transfer history clean.
        JobSpec Spec() => new()
        {
            Operation = TransferOperation.Copy,
            SourceRemote = "foo",
            SourcePath = src,
            DestPath = Path.Combine(dir, "dst"),
            DryRun = true,
            ExtraArgs = new[] { "--config", conf },
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        RcloneConfigPassword.Save(Password);
        var ok = await new RcloneJobService(exe).RunAsync(Spec(), new Progress<JobProgress>(), timeout.Token);
        Assert.Equal(0, ok.ExitCode);
        Assert.Single(ok.DryRunActions!);

        RcloneConfigPassword.Save(WrongPassword);
        var bad = await new RcloneJobService(exe).RunAsync(Spec(), new Progress<JobProgress>(), timeout.Token);
        Assert.NotEqual(0, bad.ExitCode);
        Assert.EndsWith(RcloneConfigPassword.RefreshHint, bad.StderrTail.TrimEnd());
        Assert.DoesNotContain(WrongPassword, bad.StderrTail);
    }

    private async Task<string> CreateEncryptedConfigAsync(string exe)
    {
        var conf = Path.Combine(dir, "rclone.conf");
        Assert.Equal(0, (await RunAsync(PlainStartInfo(exe), "--config", conf, "config", "create", "foo", "local", "--non-interactive")).Exit);
        Assert.Equal(0, (await RunAsync(PlainStartInfo(exe), "--config", conf, "config", "encryption", "set", "--password-command", "cmd /c echo " + Password)).Exit);
        Assert.True(RcloneConfigPassword.IsEncrypted(conf));
        return conf;
    }

    private static ProcessStartInfo PlainStartInfo(string exe) => new(exe)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(ProcessStartInfo psi, params string[] args)
    {
        psi.RedirectStandardInput = true; // held open: a password prompt would wait on it forever
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException("rclone did not exit: waiting on a password prompt?");
        }
        return (p.ExitCode, await stdout, await stderr);
    }
}
