using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.Tests;

// Every way of mounting and the CLI commands. rclone.exe does not exist: nothing is ever mounted.
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
                if (e.Exception is NullReferenceException && e.Exception.StackTrace?.Contains("RcloneMount.") == true)
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
