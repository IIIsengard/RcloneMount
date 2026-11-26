using System.Reflection;
using System.Runtime.CompilerServices;
using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.Tests;

// These hold the save gate and redirect or block writes, so nothing else may run beside them.
[CollectionDefinition(nameof(SettingsSaveTests), DisableParallelization = true)]
public class SettingsSaveCollection { }

[Collection(nameof(SettingsSaveTests))]
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

    // The CLI pipe and the stats poller read the live mount from other threads.
    [Fact]
    public async Task OtherThreads_NeverSeeTheConfiguredLetterOnTheLiveMount()
    {
        var mount = new Mount { RemoteName = "r", DriveLetter = "Z:" };
        var form = Shell(mount, configured: "M:");
        // Enough mounts that serializing takes long enough for the reader to look in.
        var mounts = (List<Mount>)typeof(MainForm).GetField("mounts", Private)!.GetValue(form)!;
        mounts.AddRange(Enumerable.Range(0, 20_000).Select(_ => new Mount()));
        using var reading = new ManualResetEventSlim();
        var done = false;
        var sawSwap = false;
        var reader = new Thread(() =>
        {
            reading.Set();
            while (!Volatile.Read(ref done))
                if (mount.DriveLetter != "Z:") sawSwap = true;
        });
        reader.Start();
        reading.Wait();

        try
        {
            for (var i = 0; i < 5; i++) await Save(form);
        }
        finally
        {
            Volatile.Write(ref done, true);
            reader.Join();
        }

        Assert.False(sawSwap);
        Assert.Equal("M:", Saved().Mounts[0].DriveLetter);
        await SettingsService.SaveAsync(new AppSettings());
    }

    [Fact]
    public async Task FailedSave_IsLoggedNotThrown_AndAnnouncedOncePerStreak()
    {
        var form = Shell(new Mount(), configured: "M:");
        var log = new ActivityLog();
        Set(form, "activityLog", log);
        var good = SettingsService.PathOverride!;
        var file = Path.Combine(TestIsolation.Dir, "not-a-folder");
        File.WriteAllText(file, "");
        var bad = Path.Combine(file, "settings.json"); // no folder can be created under a file
        try
        {
            SettingsService.PathOverride = bad;
            await Save(form);
            await Save(form);
            SettingsService.PathOverride = good;
            await Save(form);
            SettingsService.PathOverride = bad;
            await Save(form);
        }
        finally { SettingsService.PathOverride = good; }

        var errors = log.Snapshot().Where(e => e.Level == ActivityLevel.Error).Select(e => e.Message).ToArray();
        // "could not be saved" is the entry that also raises the tray notice.
        Assert.Collection(errors,
            m => Assert.Contains("could not be saved", m),
            m => Assert.Contains("still not saved", m),
            m => Assert.Contains("could not be saved", m));
    }

    [Fact]
    public async Task ForceBackup_WaitsForTheSaveInProgress()
    {
        await SettingsService.SaveAsync(new AppSettings { ThemePreference = "Light" });
        var old = BackupFiles();

        await AssertWaitsForSaveGate(SettingsService.ForceBackup, () => Assert.Equal(old, BackupFiles()));

        Assert.NotEmpty(BackupFiles().Except(old));
    }

    [Fact]
    public async Task Restore_WaitsForTheSaveInProgress()
    {
        var backup = await BackupOf(new AppSettings { ThemePreference = "Light" });
        await SettingsService.SaveAsync(new AppSettings { ThemePreference = "Dark" });
        try
        {
            await AssertWaitsForSaveGate(() => SettingsService.RestoreFromBackup(backup),
                () => Assert.Equal("Dark", Saved().ThemePreference));

            Assert.Equal("Light", Saved().ThemePreference);
        }
        finally { SettingsService.Restored = false; }
    }

    [Fact]
    public async Task AfterRestore_NothingHeldInMemoryOverwritesTheRestoredFiles()
    {
        await TransferTotalsService.SaveAsync(new TransferTotals { DownBytes = 1 });
        var backup = await BackupOf(new AppSettings { ThemePreference = "Light" });
        var pending = SettingsService.TakeSnapshot(new AppSettings { ThemePreference = "Dark" });
        try
        {
            SettingsService.RestoreFromBackup(backup);

            await SettingsService.WriteSnapshotAsync(pending);                             // save already on its way
            await SettingsService.SaveAsync(new AppSettings { ThemePreference = "Dark" }); // scheduler, job end
            await TransferTotalsService.SaveAsync(new TransferTotals { DownBytes = 999 }); // stats timer, exit

            Assert.Equal("Light", Saved().ThemePreference);
            Assert.Equal(1, TransferTotalsService.Load().DownBytes);
        }
        finally { SettingsService.Restored = false; }
    }

    [Fact]
    public async Task FailedRestore_KeepsSavingEnabled()
    {
        var notAZip = Path.Combine(TestIsolation.Dir, "broken.zip");
        File.WriteAllText(notAZip, "not a zip");

        Assert.Throws<InvalidDataException>(() => SettingsService.RestoreFromBackup(notAZip));
        await SettingsService.SaveAsync(new AppSettings { ThemePreference = "Dark" });

        Assert.Equal("Dark", Saved().ThemePreference);
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

    private static async Task<string> BackupOf(AppSettings settings)
    {
        await SettingsService.SaveAsync(settings);
        var old = BackupFiles();
        SettingsService.ForceBackup();
        return Assert.Single(BackupFiles().Except(old));
    }

    private static string[] BackupFiles() => SettingsService.ListBackups().Select(b => b.Path).ToArray();

    private static async Task AssertWaitsForSaveGate(Action action, Action whileHeld)
    {
        var gate = SaveGate();
        await gate.WaitAsync();
        Task running;
        try
        {
            running = Task.Run(action);
            Assert.NotSame(running, await Task.WhenAny(running, Task.Delay(500)));
            whileHeld();
        }
        finally { gate.Release(); }
        await running.WaitAsync(TimeSpan.FromSeconds(10));
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
