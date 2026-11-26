using System.Diagnostics;
using System.IO.Compression;
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
        finally { ResetRestored(); }
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
        finally { ResetRestored(); }
    }

    [Fact]
    public async Task RestoreWithoutTotals_LeavesTotalSavingOn()
    {
        File.Delete(TransferTotalsService.FilePath);
        var backup = await BackupOf(new AppSettings { ThemePreference = "Light" });
        try
        {
            SettingsService.RestoreFromBackup(backup);

            await TransferTotalsService.SaveAsync(new TransferTotals { DownBytes = 999 }); // the exit save

            Assert.Equal(999, TransferTotalsService.Load().DownBytes);
        }
        finally { ResetRestored(); }
    }

    // rclone.conf cannot be replaced here (a folder sits on its path), after settings.json already was.
    [Fact]
    public async Task RestoreThatFailsPartway_ChangesNothing()
    {
        var conf = SettingsService.RcloneConfigPathOverride!;
        Directory.CreateDirectory(Path.GetDirectoryName(conf)!);
        File.WriteAllText(conf, "[a]\ntype = local\n");
        var backup = await BackupOf(new AppSettings { ThemePreference = "Light" });
        File.Delete(conf);
        await SettingsService.SaveAsync(new AppSettings { ThemePreference = "Dark" });
        await TransferTotalsService.SaveAsync(new TransferTotals { DownBytes = 7 });
        Directory.CreateDirectory(conf);
        try
        {
            Assert.ThrowsAny<Exception>(() => SettingsService.RestoreFromBackup(backup));

            Assert.Equal("Dark", Saved().ThemePreference);
            Assert.Equal(7, TransferTotalsService.Load().DownBytes);
            Assert.Empty(Directory.GetFiles(TestIsolation.Dir, "*.tmp", SearchOption.AllDirectories));
            await SettingsService.SaveAsync(new AppSettings { ThemePreference = "Auto" });
            Assert.Equal("Auto", Saved().ThemePreference); // saving stays on
        }
        finally
        {
            Directory.Delete(conf);
            ResetRestored();
        }
    }

    [Theory]
    [InlineData(".zip")]
    [InlineData(".json")]
    public async Task RestoreOfUnreadableSettings_ChangesNothing(string extension)
    {
        await SettingsService.SaveAsync(new AppSettings { ThemePreference = "Dark" });
        var bad = Path.Combine(TestIsolation.Dir, "bad-settings" + extension);
        File.Delete(bad);
        if (extension == ".zip")
        {
            using var zip = ZipFile.Open(bad, ZipArchiveMode.Create);
            using var w = new StreamWriter(zip.CreateEntry("settings.json").Open());
            w.Write("{ cut off");
        }
        else File.WriteAllText(bad, "{ cut off");
        try
        {
            Assert.Throws<InvalidDataException>(() => SettingsService.RestoreFromBackup(bad));

            Assert.Equal("Dark", Saved().ThemePreference);
        }
        finally { ResetRestored(); }
    }

    [Fact]
    public async Task Restore_RunsOnlyAfterRunningAndQueuedTransfersStopped()
    {
        var (form, queue) = RestoreShell();
        var running = AddRun(queue, JobRunState.Running);
        running.Cts!.Token.Register(() => _ = Task.Run(async () =>
        {
            await Task.Delay(300); // rclone takes a moment to quit
            running.State = JobRunState.Cancelled;
        }));
        var queued = AddRun(queue, JobRunState.Queued);
        var retry = new CancellationTokenSource();
        var token = retry.Token;
        ((Dictionary<string, CancellationTokenSource>)Get(form, "retryCts")!)["m"] = retry;
        Set(form, "schedulerTimer", new System.Windows.Forms.Timer());
        JobRunState[]? atRestore = null;

        await StopWritersThen(form, () => atRestore = new[] { running.State, queued.State });

        Assert.Equal(new[] { JobRunState.Cancelled, JobRunState.Cancelled }, atRestore);
        Assert.True(token.IsCancellationRequested, "remount retry still scheduled");
        Assert.Null(Get(form, "schedulerTimer")); // the app is about to restart
    }

    [Fact]
    public async Task Restore_IsRefusedWhenATransferWillNotStop_AndSchedulingResumes()
    {
        var (form, queue) = RestoreShell();
        AddRun(queue, JobRunState.Running); // never ends
        Set(form, "schedulerTimer", new System.Windows.Forms.Timer());
        var restored = false;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => StopWritersThen(form, () => restored = true, TimeSpan.FromMilliseconds(300)));

        Assert.False(restored);
        Assert.NotNull(Get(form, "schedulerTimer"));
    }

    [Fact]
    public async Task FailedRestore_ResumesScheduling()
    {
        var (form, _) = RestoreShell();
        Set(form, "schedulerTimer", new System.Windows.Forms.Timer());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => StopWritersThen(form, () => throw new InvalidDataException("bad backup")));

        Assert.NotNull(Get(form, "schedulerTimer"));
    }

    [Fact]
    public async Task Restore_IsRefusedWhileAnRcloneTerminalIsOpen()
    {
        var (form, _) = RestoreShell();
        // Stands in for the terminal: no window, and it exits when its input closes.
        using var terminal = Process.Start(new ProcessStartInfo("cmd.exe", "/c set /p x=")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
        })!;
        RcloneConfigService.Terminals.Add(terminal);
        var restored = false;
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StopWritersThen(form, () => restored = true));

            Assert.Contains("terminal", ex.Message);
            Assert.False(restored);

            terminal.StandardInput.Close();
            await terminal.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await StopWritersThen(form, () => restored = true); // closed: goes ahead

            Assert.True(restored);
        }
        finally
        {
            terminal.StandardInput.Close();
            RcloneConfigService.Terminals.Remove(terminal);
        }
    }

    [Fact]
    public void RestoreConfirmation_SaysWhatWillBeStopped()
    {
        var text = MainForm.RestoreWarning("Restore?", activeTransfers: 2);

        Assert.StartsWith("Restore?", text);
        Assert.Contains("2 running or queued transfer(s) will be cancelled", text);
        Assert.Contains("rclone terminal", text);
        Assert.Contains("Any transfer running by then will be cancelled", MainForm.RestoreWarning("Restore?", activeTransfers: 0));
    }

    private static (MainForm, JobQueueService) RestoreShell()
    {
        var form = Shell(new Mount(), configured: "M:");
        var queue = new JobQueueService(new RcloneJobService(Path.Combine(TestIsolation.Dir, "no-rclone.exe")));
        Set(form, "jobQueue", queue);
        Set(form, "retryCts", new Dictionary<string, CancellationTokenSource>());
        return (form, queue);
    }

    // Puts a run straight into the queue's list, so no rclone is started.
    private static JobRun AddRun(JobQueueService queue, JobRunState state)
    {
        var run = new JobRun { Spec = new JobSpec(), State = state };
        if (state == JobRunState.Running) run.Cts = new CancellationTokenSource();
        ((List<JobRun>)typeof(JobQueueService).GetField("runs", Private)!.GetValue(queue)!).Add(run);
        return run;
    }

    private static Task StopWritersThen(MainForm form, Action restore, TimeSpan? timeout = null) =>
        (Task)typeof(MainForm).GetMethod("RestoreWithWritersStoppedAsync", Private)!
            .Invoke(form, new object[] { restore, timeout ?? TimeSpan.FromSeconds(10) })!;

    private static object? Get(MainForm form, string name) => typeof(MainForm).GetField(name, Private)!.GetValue(form);

    private static void ResetRestored()
    {
        SettingsService.Restored = false;
        SettingsService.TotalsRestored = false;
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

    [Fact]
    public async Task BackupNow_FailsWhenTheBackupCannotBeWritten_ButSavingCarriesOn()
    {
        try
        {
            await SettingsService.SaveAsync(new AppSettings { BackupsFolderOverride = AFile() });
            await SettingsService.SaveAsync(new AppSettings { BackupsFolderOverride = AFile(), ThemePreference = "Dark" });

            Assert.Equal("Dark", Saved().ThemePreference); // the failed automatic backup did not stop the save
            Assert.ThrowsAny<IOException>(SettingsService.ForceBackup);
        }
        finally { await SettingsService.SaveAsync(new AppSettings()); }
    }

    [Fact]
    public void BackupNow_FailsWhenThereAreNoSettingsToBackUp()
    {
        var good = SettingsService.PathOverride!;
        try
        {
            SettingsService.PathOverride = Path.Combine(TestIsolation.Dir, "empty", "settings.json");

            Assert.Throws<FileNotFoundException>(SettingsService.ForceBackup);
        }
        finally { SettingsService.PathOverride = good; }
    }

    [Fact]
    public async Task Restore_IsRefusedWhenTheCurrentSettingsCannotBeBackedUp()
    {
        var backup = await BackupOf(new AppSettings { ThemePreference = "Light" });
        try
        {
            await SettingsService.SaveAsync(new AppSettings { BackupsFolderOverride = AFile(), ThemePreference = "Dark" });

            var ex = Assert.Throws<IOException>(() => SettingsService.RestoreFromBackup(backup));

            Assert.Contains("nothing was restored", ex.Message);
            Assert.Equal("Dark", Saved().ThemePreference);
            Assert.False(SettingsService.Restored);
        }
        finally
        {
            ResetRestored();
            await SettingsService.SaveAsync(new AppSettings());
        }
    }

    // A path that cannot be used as a folder.
    private static string AFile()
    {
        var file = Path.Combine(TestIsolation.Dir, "a-file-not-a-folder");
        File.WriteAllText(file, "");
        return file;
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
