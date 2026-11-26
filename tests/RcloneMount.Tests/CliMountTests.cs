using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.Tests;

public class CliMountTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // The configured letter is taken (C:), so a CLI mount must fall back like the buttons do,
    // and do it on the UI thread. rclone.exe does not exist: nothing is ever mounted.
    [Fact]
    public void CliMount_PicksAFreeLetter_OnTheUiThread()
    {
        Exception? failure = null;
        var ui = new Thread(() =>
        {
            try { Run(); }
            catch (Exception ex) { failure = ex; }
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        Assert.Null(failure);
    }

    private static void Run()
    {
        Assert.StartsWith(TestIsolation.Dir, RcloneConfigPassword.FilePath);
        // As in UiTextTests: never run MainForm's constructor.
        var form = (MainForm)RuntimeHelpers.GetUninitializedObject(typeof(MainForm));
        typeof(Form).GetConstructor(Type.EmptyTypes)!.Invoke(form, null);
        GC.SuppressFinalize(form);
        FieldInfo Field(string name) => typeof(MainForm).GetField(name, Private)!;
        void Set(string name, object value) => Field(name).SetValue(form, value);

        var mount = new Mount { RemoteName = "r", DriveLetter = "C:" };
        var log = new ActivityLog();
        var logged = new List<(string Message, int Thread)>();
        log.Appended += e => logged.Add((e.Message, Environment.CurrentManagedThreadId));
        using var service = new RcloneMountService(Path.Combine(TestIsolation.Dir, "no-rclone.exe"));
        Set("mounts", new List<Mount> { mount });
        Set("remotes", new[] { new RcloneRemote { Name = "r", Type = "local" } });
        Set("configuredDriveLetters", new Dictionary<string, string>());
        Set("retryCts", new Dictionary<string, CancellationTokenSource>());
        Set("activityLog", log);
        Set("mountService", service);
        Set("winFspInfo", RuntimeHelpers.GetUninitializedObject(Field("winFspInfo").FieldType));
        _ = form.Handle;
        var uiThread = Environment.CurrentManagedThreadId;

        var method = typeof(MainForm).GetMethod("CliMountAsync", Private)!;
        var call = Task.Run(() => (Task<CliRelay.Response>)method.Invoke(form, new object[] { "r", CancellationToken.None })!);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!call.IsCompleted && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Assert.True(call.IsCompleted, "CLI mount did not finish");

        Assert.Equal(5, call.Result.ExitCode); // the missing rclone.exe, reached after letter resolution
        var fallback = Assert.Single(logged, e => e.Message.StartsWith("Drive C: was taken"));
        Assert.Equal(uiThread, fallback.Thread);
        form.Dispose();
    }
}
