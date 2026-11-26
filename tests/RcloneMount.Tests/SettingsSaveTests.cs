using System.Reflection;
using System.Runtime.CompilerServices;
using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.Tests;

public class SettingsSaveTests
{
    // Gate for every test below: nothing a save reads or writes may be a real user file.
    public SettingsSaveTests()
    {
        Assert.All(new[]
        {
            SettingsService.CurrentSettingsPath, SettingsService.BackupsFolder, SettingsService.DefaultBackupsFolder,
            SettingsService.ActivityLogPath, SettingsService.ResolveRcloneConfigPath(), TransferTotalsService.FilePath,
        }, path => Assert.StartsWith(TestIsolation.Dir, path));
    }

    [Fact]
    public async Task ContendedSave_WritesTheStateAtTheTimeOfTheCall()
    {
        var atCall = Path.Combine(TestIsolation.Dir, "backups-at-call");
        var settings = new AppSettings
        {
            BackupsFolderOverride = atCall,
            Jobs = { new TransferJob { Name = "at call" } },
        };
        var gate = SaveGate();
        await gate.WaitAsync(); // another save is in progress
        Task save;
        try
        {
            save = SettingsService.SaveAsync(settings);
            // What the UI thread does while the save waits.
            settings.Jobs[0].Name = "changed later";
            settings.Jobs.Add(new TransferJob { Name = "added later" });
            settings.BackupsFolderOverride = Path.Combine(TestIsolation.Dir, "backups-later");
        }
        finally { gate.Release(); }
        await save;

        var onDisk = Saved().Jobs.Select(j => j.Name).ToArray();
        var backups = SettingsService.BackupsFolder;
        await SettingsService.SaveAsync(new AppSettings()); // back to the default backups folder

        Assert.Equal(new[] { "at call" }, onDisk);
        Assert.Equal(atCall, backups);
    }

    [Fact]
    public async Task OverlappingSaves_KeepTheFallbackLetterLive_AndTheConfiguredOneOnDisk()
    {
        var mount = new Mount { RemoteName = "r", DriveLetter = "Z:" }; // running on a fallback letter
        var form = Shell(mount, configured: "M:");
        var gate = SaveGate();
        await gate.WaitAsync();
        Task first, second;
        try
        {
            first = Save(form);
            Assert.Equal("Z:", mount.DriveLetter);
            second = Save(form);
            Assert.Equal("Z:", mount.DriveLetter);
        }
        finally { gate.Release(); }
        await Task.WhenAll(first, second);

        Assert.Equal("Z:", mount.DriveLetter);
        Assert.Equal("M:", Assert.Single(Saved().Mounts).DriveLetter);
    }

    // SemaphoreSlim promises no FIFO order, so write two snapshots newest first.
    [Fact]
    public async Task OlderSnapshotWrittenLast_DoesNotOverwriteTheNewerOne()
    {
        var older = SettingsService.TakeSnapshot(new AppSettings { ThemePreference = "Light" });
        var newer = SettingsService.TakeSnapshot(new AppSettings { ThemePreference = "Dark" });

        await SettingsService.WriteSnapshotAsync(newer);
        await SettingsService.WriteSnapshotAsync(older);

        Assert.Equal("Dark", Saved().ThemePreference);
    }

    private static AppSettings Saved() =>
        SettingsService.LoadFrom(SettingsService.CurrentSettingsPath, quarantine: false, out _);

    private static SemaphoreSlim SaveGate() => (SemaphoreSlim)typeof(SettingsService)
        .GetField("saveGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    // As in UiTextTests: never run MainForm's constructor. Set only what SaveSettingsAsync reads.
    private static MainForm Shell(Mount mount, string configured)
    {
        var form = (MainForm)RuntimeHelpers.GetUninitializedObject(typeof(MainForm));
        GC.SuppressFinalize(form);
        Set(form, "mounts", new List<Mount> { mount });
        Set(form, "jobs", new List<TransferJob>());
        Set(form, "configuredDriveLetters", new Dictionary<string, string> { [mount.Id] = configured });
        return form;
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(MainForm form, string name, object value) =>
        typeof(MainForm).GetField(name, Private)!.SetValue(form, value);
    private static Task Save(MainForm form) =>
        (Task)typeof(MainForm).GetMethod("SaveSettingsAsync", Private)!.Invoke(form, null)!;
}
