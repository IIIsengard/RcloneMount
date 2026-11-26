using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.Tests;

// Every way of mounting and the CLI commands. rclone.exe does not exist: nothing is ever mounted.
[Collection(nameof(SharedStatics))] // asserts on the stored-password path
public class CliMountTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string NoRclone = Path.Combine(TestIsolation.Dir, "no-rclone.exe");

    // The configured letter is taken (C:), so a CLI mount must fall back like the buttons do, do it
    // on the UI thread, and show the configured letter again when rclone then fails to start.
    [Fact]
    public void CliMount_PicksAFreeLetter_OnTheUiThread_AndGoesBackWhenItFails() => WithUiThread(
        new Mount { RemoteName = "r", DriveLetter = "C:" },
        (form, rig, pump) =>
        {
            var reply = pump(new CliRelay.Request(CliRelay.Command.Mount, "r"));

            Assert.Equal(5, reply.ExitCode); // the missing rclone.exe, reached after letter resolution
            var fallback = Assert.Single(rig.Logged, e => e.Message.StartsWith("Drive C: was taken"));
            Assert.Equal(Environment.CurrentManagedThreadId, fallback.Thread);
            AssertBackOnConfiguredLetter(rig, "C:");
        });

    [Theory]
    [InlineData(CliRelay.Command.List)]
    [InlineData(CliRelay.Command.Unmount)]
    public void CliListAndUnmount_ReadTheMountsOnTheUiThread(CliRelay.Command command) => WithUiThread(
        null, // a null entry makes whichever thread reads the mounts throw: that shows which one it was
        (form, rig, pump) =>
        {
            var threads = new List<int>();
            void Seen(object? _, FirstChanceExceptionEventArgs e)
            {
                // Only where the mounts are read: the event also sees every other thread's exceptions.
                if (e.Exception is NullReferenceException && e.Exception.StackTrace is { } stack
                    && (stack.Contains("RcloneMountService.IsMounted") || stack.Contains("FindMountByName")))
                    lock (threads) threads.Add(Environment.CurrentManagedThreadId);
            }
            AppDomain.CurrentDomain.FirstChanceException += Seen;
            try { Assert.ThrowsAny<Exception>(() => pump(new CliRelay.Request(command, "r"))); }
            finally { AppDomain.CurrentDomain.FirstChanceException -= Seen; }

            // First sighting is where it was thrown; later ones are the rethrow reaching the caller.
            Assert.Equal(Environment.CurrentManagedThreadId, threads.First());
        });

    [Fact]
    public async Task TrayMount_PicksAFreeLetter_AndGoesBackWhenItFails()
    {
        var rig = NewRig(new Mount { RemoteName = "r", DriveLetter = "C:" });
        using var service = rig.Service;

        await Invoke<Task>(rig.Form, "MountFromTrayAsync", rig.Mount!);

        Assert.Single(rig.Logged, e => e.Message.StartsWith("Drive C: was taken"));
        AssertBackOnConfiguredLetter(rig, "C:");
    }

    [Fact]
    public async Task AutoMount_ThatFailsOnAFallbackLetter_GoesBack()
    {
        var letter = UnusedLetter();
        var rig = NewRig(new Mount { RemoteName = "r", DriveLetter = letter, AutoMount = true });
        using var service = rig.Service;
        rig.Mounts.Add(new Mount { RemoteName = "r", DriveLetter = letter }); // takes the letter

        await Invoke<Task>(rig.Form, "AutoMountOnStartupAsync");

        Assert.Single(rig.Logged, e => e.Message.StartsWith($"Auto-mount: {letter} was taken"));
        AssertBackOnConfiguredLetter(rig, letter);
    }

    [Fact]
    public async Task Remount_ThatFailsOnAFallbackLetter_GoesBack()
    {
        var rig = NewRig(new Mount { RemoteName = "r", DriveLetter = UnusedLetter() });
        using var service = rig.Service;
        var letter = rig.Mount!.DriveLetter;
        rig.Mounts.Add(new Mount { RemoteName = "r", DriveLetter = letter });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3)); // first attempt is at 2 s

        await Invoke<Task>(rig.Form, "RetryLoopAsync", rig.Mount, stop.Token);

        AssertBackOnConfiguredLetter(rig, letter);
    }

    // C: stands for the configured letter something else holds, the reason for the fallback.
    [Fact]
    public async Task Unmount_OfAFallbackMount_ChecksTheLetterItWasOn()
    {
        var letter = UnusedLetter();
        var rig = NewRig(new Mount { RemoteName = "r", DriveLetter = letter });
        using var service = rig.Service;
        rig.Configured[rig.Mount!.Id] = "C:";

        var result = await Invoke<Task<(string Letter, bool StillMounted)>>(rig.Form, "StopMountAsync", rig.Mount);

        Assert.Equal((letter, false), result);
        AssertBackOnConfiguredLetter(rig, "C:");
    }

    // A dropped drive waiting to be remounted would otherwise come back after the script unmounted it.
    [Fact]
    public async Task CliUnmount_CancelsAPendingRemount()
    {
        var rig = NewRig(new Mount { RemoteName = "r", DriveLetter = UnusedLetter() });
        using var service = rig.Service;
        var retry = new CancellationTokenSource();
        var token = retry.Token;
        ((Dictionary<string, CancellationTokenSource>)Field("retryCts").GetValue(rig.Form)!)[rig.Mount!.Id] = retry;

        var reply = await Invoke<Task<CliRelay.Response>>(rig.Form, "CliUnmountAsync", "r");

        Assert.Equal(0, reply.ExitCode);
        Assert.True(token.IsCancellationRequested);
    }

    [Theory]
    [InlineData("UnmountAsync")]
    [InlineData("UnmountFromTrayAsync")]
    [InlineData("CliUnmountAsync")]
    public async Task UserUnmount_StaysDownUntilTheNextManualMount(string unmount)
    {
        var rig = NewRig(new Mount { RemoteName = "r", DriveLetter = UnusedLetter(), AutoMount = true });
        using var service = rig.Service;
        var retries = (Dictionary<string, CancellationTokenSource>)Field("retryCts").GetValue(rig.Form)!;

        await Invoke<Task>(rig.Form, unmount, unmount == "CliUnmountAsync" ? "r" : rig.Mount!);
        await rig.Form.KickAutoRemountsAsync("network up");
        await Invoke<Task>(rig.Form, "AutoMountOnStartupAsync"); // runs again after an rclone update

        Assert.Empty(retries);
        Assert.DoesNotContain(rig.Logged, e => e.Message.StartsWith("Auto-mounting"));

        await Invoke<Task>(rig.Form, "MountFromTrayAsync", rig.Mount!); // fails: no rclone.exe
        await rig.Form.KickAutoRemountsAsync("network up");

        Assert.Single(retries);
        retries[rig.Mount!.Id].Cancel();
    }

    // The app's own unmounts (restore, exit, a dropped drive) are not the user's.
    [Fact]
    public async Task AutoRecover_RemountsADriveTheUserDidNotUnmount()
    {
        var rig = NewRig(new Mount { RemoteName = "r", DriveLetter = UnusedLetter(), AutoMount = true });
        using var service = rig.Service;
        var retries = (Dictionary<string, CancellationTokenSource>)Field("retryCts").GetValue(rig.Form)!;
        await service.UnmountAllAsync();

        await rig.Form.KickAutoRemountsAsync("restore did not finish");

        Assert.Single(retries);
        retries[rig.Mount!.Id].Cancel();
    }

    // "C:" stands for the configured letter; the revert must not wait for rclone's exit event.
    [Fact]
    public async Task CliUnmount_OfAFallbackMount_ShowsTheConfiguredLetterAtOnce()
    {
        var rig = NewRig(new Mount { RemoteName = "r", DriveLetter = UnusedLetter() });
        using var service = rig.Service;
        rig.Configured[rig.Mount!.Id] = "C:";
        PretendMounted(service, rig.Mount);

        var reply = await Invoke<Task<CliRelay.Response>>(rig.Form, "CliUnmountAsync", "r");

        Assert.Equal(0, reply.ExitCode);
        AssertBackOnConfiguredLetter(rig, "C:");
    }

    // rclone can die just as the user clicks Unmount; its exit event is handled after the click.
    [Fact]
    public void CrashExit_ArrivingAfterAUserUnmount_DoesNotBringTheDriveBack() => WithUiThread(
        new Mount { RemoteName = "r", DriveLetter = "C:", AutoMount = true, UnmountedByUser = true },
        (form, rig, pump) =>
        {
            Field("lblStatus").SetValue(form, new Label());

            Invoke<object>(form, "MountService_MountExited", rig.Service,
                new MountExitedEventArgs(rig.Mount!.Id, 1, Array.Empty<string>(), false));
            Application.DoEvents();

            Assert.Empty((Dictionary<string, CancellationTokenSource>)Field("retryCts").GetValue(form)!);
        });

    // Asking for a mount means "I want it up", also when it cannot start right now.
    [Theory]
    [InlineData("MountAsync")]
    [InlineData("MountFromTrayAsync")]
    [InlineData("CliMountAsync")]
    public async Task ManualMount_ThatStopsEarly_NoLongerCountsAsUnmountedByTheUser(string mount)
    {
        var rig = NewRig(new Mount { RemoteName = "r", DriveLetter = UnusedLetter() });
        using var service = rig.Service;
        rig.Mount!.UnmountedByUser = true;
        // Nothing to start it with: no rclone for the button (without WinFsp it opens a dialog), no WinFsp for the others.
        Field(mount == "MountAsync" ? "mountService" : "winFspInfo").SetValue(rig.Form, null);

        await Invoke<Task>(rig.Form, mount,
            mount == "CliMountAsync" ? new object[] { "r", CancellationToken.None } : new object[] { rig.Mount });

        Assert.False(rig.Mount.UnmountedByUser);
    }

    // The UI thread takes no messages here: the request must still end, and not run once it does.
    [Fact]
    public void CliRequest_ToABusyUiThread_EndsOnCancel_AndNeverRunsLater() => WithUiThread(
        new Mount { RemoteName = "r", DriveLetter = "C:" },
        (form, rig, pump) =>
        {
            using var stop = new CancellationTokenSource(300);
            var call = Task.Run(() => Invoke<Task<CliRelay.Response>>(
                form, "HandleCliRequestAsync", new CliRelay.Request(CliRelay.Command.Mount, "r"), stop.Token));
            var ended = EndsWithoutPumping(call);
            Application.DoEvents(); // the UI thread is free again

            Assert.True(ended, "the CLI request ignored its cancellation");
            Assert.True(call.IsCanceled);
            Assert.Empty(rig.Logged);
        });

    // A rename that fails, or whose update then fails, still brings the unmounted drives back.
    [Theory]
    [InlineData("new", "new")] // renamed, then the update fails: there is no rclone.exe
    [InlineData("taken", "r")] // the rename itself is refused
    public async Task EditRemote_ThatFails_RemountsUnderTheNameInRcloneConf(string newName, string inConf)
    {
        var rig = NewRig(new Mount { RemoteName = "r", DriveLetter = UnusedLetter() });
        using var service = rig.Service;
        var mount = rig.Mount!;
        Field("jobs").SetValue(rig.Form, new List<TransferJob>());
        Field("configService").SetValue(rig.Form, new RcloneConfigService(NoRclone));
        var conf = SettingsService.ResolveRcloneConfigPath();
        Assert.StartsWith(TestIsolation.Dir, conf);
        Directory.CreateDirectory(Path.GetDirectoryName(conf)!);
        File.WriteAllText(conf, "[r]\r\ntype = local\r\n\r\n[taken]\r\ntype = local\r\n");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => Invoke<Task>(rig.Form, "RenameAndUpdateRemoteAsync",
                "r", newName, new List<Mount> { mount }, new Dictionary<string, string>(), new HashSet<string>()));

            Assert.Equal(inConf, mount.RemoteName);
            Assert.Single(rig.Logged, e => e.Message.StartsWith($"Remount of {mount.DriveLetter} failed"));
        }
        finally { File.Delete(conf); }
    }

    [Fact]
    public void CliRequest_TheUiThreadDoesNotTakeUp_IsAnsweredBusy_AndNeverRunsLater() => WithUiThread(
        null,
        (form, rig, pump) =>
        {
            var ran = false;
            Func<Task<CliRelay.Response>> command = () => { ran = true; return Task.FromResult(new CliRelay.Response(0, "")); };
            var call = Task.Run(() => Invoke<Task<CliRelay.Response>>(
                form, "OnUiThreadAsync", command, TimeSpan.FromMilliseconds(200), CancellationToken.None));
            var ended = EndsWithoutPumping(call);
            Application.DoEvents();

            Assert.True(ended, "the CLI request was not answered");
            var reply = call.GetAwaiter().GetResult();
            Assert.Equal(5, reply.ExitCode);
            Assert.Contains("busy (a dialog is open", reply.Message);
            Assert.False(ran);
        });

    // The wait is for the start only: a command that takes longer still gets its own reply.
    [Fact]
    public void CliRequest_ThatStarted_IsAnsweredHoweverLongItTakes() => WithUiThread(
        null,
        (form, rig, pump) =>
        {
            Func<Task<CliRelay.Response>> command = async () =>
            {
                await Task.Delay(2500);
                return new CliRelay.Response(0, "done");
            };
            var call = Task.Run(() => Invoke<Task<CliRelay.Response>>(
                form, "OnUiThreadAsync", command, TimeSpan.FromSeconds(1), CancellationToken.None));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!call.IsCompleted && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }

            Assert.True(call.IsCompleted, "the CLI request was not answered");
            Assert.Equal("done", call.GetAwaiter().GetResult().Message);
        });

    private static bool EndsWithoutPumping(Task call)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!call.IsCompleted && DateTime.UtcNow < deadline) Thread.Sleep(10);
        return call.IsCompleted;
    }

    // A running mount needs a process: a cmd.exe waiting on its input stands in for rclone.
    private static void PretendMounted(RcloneMountService service, Mount mount)
    {
        var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c set /p x=")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
        })!;
        var type = typeof(RcloneMountService).GetNestedType("MountProcess", BindingFlags.NonPublic)!;
        var processes = (IDictionary)typeof(RcloneMountService).GetField("processes", Private)!.GetValue(service)!;
        processes[mount.Id] = Activator.CreateInstance(type, process, 1, 0); // port 0: no rclone to ask
        Assert.True(service.IsMounted(mount));
    }

    private sealed record Rig(MainForm Form, Mount? Mount, List<Mount> Mounts, RcloneMountService Service,
        Dictionary<string, string> Configured, List<(string Message, int Thread)> Logged);

    private static void AssertBackOnConfiguredLetter(Rig rig, string letter)
    {
        Assert.Equal(letter, rig.Mount!.DriveLetter);
        Assert.Empty(rig.Configured);
    }

    private static string UnusedLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return "ZYXWVUTSRQPONMLK".First(c => !used.Contains(c)) + ":";
    }

    // As in UiTextTests: never run MainForm's constructor. Set only what the mount paths read.
    private static Rig NewRig(Mount? mount)
    {
        Assert.StartsWith(TestIsolation.Dir, RcloneConfigPassword.FilePath);
        var form = (MainForm)RuntimeHelpers.GetUninitializedObject(typeof(MainForm));
        typeof(Form).GetConstructor(Type.EmptyTypes)!.Invoke(form, null);
        GC.SuppressFinalize(form);
        void Set(string name, object value) => Field(name).SetValue(form, value);

        var log = new ActivityLog();
        var logged = new List<(string Message, int Thread)>();
        log.Appended += e => logged.Add((e.Message, Environment.CurrentManagedThreadId));
        var mounts = new List<Mount> { mount! };
        var configured = new Dictionary<string, string>();
        var service = new RcloneMountService(NoRclone);
        Set("mounts", mounts);
        Set("remotes", new[] { new RcloneRemote { Name = "r", Type = "local" } });
        Set("configuredDriveLetters", configured);
        Set("retryCts", new Dictionary<string, CancellationTokenSource>());
        Set("detachedMountIds", new HashSet<string>());
        Set("activityLog", log);
        Set("mountService", service);
        Set("winFspInfo", RuntimeHelpers.GetUninitializedObject(Field("winFspInfo").FieldType));
        return new Rig(form, mount, mounts, service, configured, logged);
    }

    private static FieldInfo Field(string name) => typeof(MainForm).GetField(name, Private)!;

    private static T Invoke<T>(MainForm form, string name, params object[] args) =>
        (T)typeof(MainForm).GetMethod(name, Private)!.Invoke(form, args)!;

    // Runs `test` on an STA thread that owns the form's (never shown) window handle. `pump` sends a
    // request from another thread, as the pipe server does, and pumps messages until it is answered.
    private static void WithUiThread(Mount? mount, Action<MainForm, Rig, Func<CliRelay.Request, CliRelay.Response>> test)
    {
        ExceptionDispatchInfo? failure = null;
        var ui = new Thread(() =>
        {
            try
            {
                var rig = NewRig(mount);
                using var service = rig.Service;
                using var form = rig.Form;
                _ = form.Handle;
                test(form, rig, request =>
                {
                    var call = Task.Run(() => Invoke<Task<CliRelay.Response>>(
                        form, "HandleCliRequestAsync", request, CancellationToken.None));
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!call.IsCompleted && DateTime.UtcNow < deadline)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }
                    Assert.True(call.IsCompleted, "the CLI request was not answered");
                    return call.GetAwaiter().GetResult();
                });
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        failure?.Throw();
    }
}
