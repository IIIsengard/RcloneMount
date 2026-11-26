using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.Tests;

// Wrapping remotes (crypt, alias, union, ...) name other remotes: a rename must carry them along,
// or they break, or reach a later remote that reuses the old name. Temp config files only.
public class RemoteReferenceTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("rm-refs-").FullName;
    private string Conf => Path.Combine(dir, "rclone.conf");

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    [Theory]
    [InlineData("crypt", "remote", "old:secret", "new:secret")]
    [InlineData("alias", "remote", "old:", "new:")]
    [InlineData("chunker", "remote", "old:a/old:b", "new:a/old:b")]
    [InlineData("alias", "remote", "old,description=x:sub", "new,description=x:sub")]
    [InlineData("union", "upstreams", "old:a other:b:ro old::nc", "new:a other:b:ro new::nc")]
    [InlineData("union", "upstreams", "\"old:with space:ro\"  other:x", "\"new:with space:ro\"  other:x")]
    [InlineData("union", "upstreams", "old:a=old other:old:", "new:a=old other:old:")]
    [InlineData("combine", "upstreams", "old=other: d=old:p \"my dir=old:q r\"", "old=other: d=new:p \"my dir=new:q r\"")]
    // Exact names only; local paths and other settings stay.
    [InlineData("alias", "remote", "older:x", "older:x")]
    [InlineData("alias", "remote", "Old:x", "Old:x")]
    [InlineData("alias", "remote", "my old:x", "my old:x")]
    [InlineData("alias", "remote", "old", "old")]
    [InlineData("alias", "remote", "old,backup", "old,backup")]
    [InlineData("alias", "remote", @"C:\old:x", @"C:\old:x")]
    [InlineData("union", "upstreams", "\"other:has old: inside\" x:old:", "\"other:has old: inside\" x:old:")]
    [InlineData("webdav", "url", "old:x", "old:x")]
    public void RepointValue_ChangesOnlyReferencesToThatRemote(string type, string key, string value, string expected) =>
        Assert.Equal(expected, RcloneConfigService.RepointValue(type, key, value, "old", "new"));

    [Theory]
    [InlineData("union", "old:a other:", "\"my new:a\" other:")]
    [InlineData("combine", "d=old: e=other:", "\"d=my new:\" e=other:")]
    public void RepointValue_QuotesAListItemThatGainsASpace(string type, string value, string expected) =>
        Assert.Equal(expected, RcloneConfigService.RepointValue(type, "upstreams", value, "old", "my new"));

    // rclone reads "c:..." as a drive, so it never meant the remote "c".
    [Fact]
    public void RepointValue_LeavesDrivePathsWhenTheOldNameIsOneLetter() =>
        Assert.Equal("c:/data", RcloneConfigService.RepointValue("alias", "remote", "c:/data", "c", "cloud"));

    [Fact]
    public async Task Rename_RewritesTheSectionAndEveryReference_LeavingTheRest()
    {
        File.WriteAllText(Conf, "# note = old:\r\n[old]\r\ntype = local\r\n\r\n[sec]\r\ntype = crypt\r\nremote = old:enc\r\npassword = old:\r\n\r\n"
            + "[both]\r\nupstreams = d=old: e=sec:\r\ntype = combine\r\n\r\n[older]\r\ntype = alias\r\nremote = older:\r\n");

        await RcloneConfigService.RenameInFileAsync(Conf, "old", "new");

        Assert.Equal("# note = old:\r\n[new]\r\ntype = local\r\n\r\n[sec]\r\ntype = crypt\r\nremote = new:enc\r\npassword = old:\r\n\r\n"
            + "[both]\r\nupstreams = d=new: e=sec:\r\ntype = combine\r\n\r\n[older]\r\ntype = alias\r\nremote = older:\r\n", File.ReadAllText(Conf));
    }

    // rclone wraps some values (e.g. paths with # or ;) in backticks or triple quotes when it saves.
    [Theory]
    [InlineData("`")]
    [InlineData("\"\"\"")]
    public async Task Rename_RewritesAReferenceInsideConfigFileQuotes(string quote)
    {
        File.WriteAllText(Conf, $"[old]\r\ntype = local\r\n\r\n[al]\r\ntype = alias\r\nremote = {quote}old:a#1{quote}\r\n");

        await RcloneConfigService.RenameInFileAsync(Conf, "old", "new");

        Assert.Contains($"remote = {quote}new:a#1{quote}", File.ReadAllText(Conf));
    }

    [Fact]
    public async Task Rename_EncryptedConfig_IsRefusedAndUntouched()
    {
        File.WriteAllText(Conf, "RCLONE_ENCRYPT_V0:\r\nabc\r\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RcloneConfigService.RenameInFileAsync(Conf, "old", "new"));

        Assert.Equal("RCLONE_ENCRYPT_V0:\r\nabc\r\n", File.ReadAllText(Conf));
    }

    // Two sections of one name would be left, whichever side of the renamed one the other sits.
    [Theory]
    [InlineData("[old]\r\ntype = local\r\n\r\n[new]\r\ntype = local\r\n")]
    [InlineData("[new]\r\ntype = local\r\n\r\n[old]\r\ntype = local\r\n")]
    public async Task Rename_OntoAnExistingSection_IsRefusedAndUntouched(string config)
    {
        File.WriteAllText(Conf, config);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RcloneConfigService.RenameInFileAsync(Conf, "old", "new"));

        Assert.Contains("already exists", ex.Message);
        Assert.Equal(config, File.ReadAllText(Conf));
    }

    // Encrypted config: the user renames in rclone, so the app names what else to change.
    [Fact]
    public void DependentsOf_ListsTheRemotesPointingAtOne()
    {
        static RcloneRemote Remote(string name, string type, string key, string value) =>
            new() { Name = name, Type = type, Parameters = { [key] = value } };

        var found = RcloneConfigService.DependentsOf(new[]
        {
            Remote("sec", "crypt", "remote", "old:x"),
            Remote("pool", "union", "upstreams", "a: old:y"),
            Remote("near", "alias", "remote", "older:"),
            Remote("dav", "webdav", "url", "old:"),
        }, "old");

        Assert.Equal(new[] { "sec", "pool" }, found);
    }

    [Fact]
    public async Task RealRclone_WrappersStillReachTheFilesAfterARename()
    {
        if (RealRclone.Exe is not { } exe) return;
        var files = Directory.CreateDirectory(Path.Combine(dir, "base dir", "sub")).Parent!.FullName;
        File.WriteAllText(Path.Combine(files, "sub", "a.txt"), "a");
        File.WriteAllText(Conf, $"[base]\r\ntype = alias\r\nremote = {files}\r\n\r\n"
            + "[al]\r\ntype = alias\r\nremote = base:sub\r\n\r\n"
            + "[params]\r\ntype = alias\r\nremote = base,description=x:sub\r\n\r\n"
            + "[un]\r\ntype = union\r\nupstreams = base:sub base:sub:ro\r\n\r\n"
            + "[cmb]\r\ntype = combine\r\nupstreams = one=base:sub \"two dirs=base:sub\"\r\n\r\n"
            + "[ch]\r\ntype = chunker\r\nremote = base:sub\r\n\r\n"
            + "[hs]\r\ntype = hasher\r\nremote = base:sub\r\n\r\n"
            + "[bq]\r\ntype = alias\r\nremote = `base:sub`\r\n");
        var checks = new[] { "al:", "params:", "un:", "cmb:one", "cmb:two dirs", "ch:", "hs:", "bq:" };
        foreach (var remote in checks) Assert.Contains("a.txt", await List(exe, remote));

        await RcloneConfigService.RenameInFileAsync(Conf, "base", "new base");

        foreach (var remote in checks) Assert.Contains("a.txt", await List(exe, remote));
    }

    private async Task<string> List(string exe, string remote)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "--config", Conf, "lsf", remote }) psi.ArgumentList.Add(a);
        psi.Environment["RCLONE_ASK_PASSWORD"] = "false";
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return await output + await error;
    }
}
