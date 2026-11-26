using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using RcloneMount.Services;

namespace RcloneMount.Tests;

public class PowerGuardTests
{
    // null = idle: sleep allowed, Windows shutdown not blocked.
    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(1, 0, "A transfer job is running.")]
    [InlineData(2, 0, "2 transfer jobs are running.")]
    [InlineData(0, 1, "1 mount upload has not finished.")]
    [InlineData(0, 3, "3 mount uploads have not finished.")]
    [InlineData(1, 3, "A transfer job is running. 3 mount uploads have not finished.")]
    public void BusyReason_NamesTheWork(int jobsRunning, int uploadsPending, string? expected) =>
        Assert.Equal(expected, PowerGuard.BusyReason(jobsRunning, uploadsPending));

    // Windows terminates a window-less app that refuses shutdown, so a hidden (tray) window
    // must take the best-effort teardown path instead of holding.
    [Theory]
    [InlineData(true, "A transfer job is running.", true)]
    [InlineData(false, "A transfer job is running.", false)]
    [InlineData(true, null, false)]
    [InlineData(false, null, false)]
    public void HoldShutdown_OnlyVisibleAndBusy(bool windowVisible, string? busyReason, bool expected) =>
        Assert.Equal(expected, PowerGuard.HoldShutdown(windowVisible, busyReason));

    // Real Win32 calls on a window this test creates and owns (never the app's): the shutdown
    // reason Windows would show is the one we set, the thread asks for ES_SYSTEM_REQUIRED while
    // busy, and going idle clears both.
    [Fact]
    public void Apply_SetsThenClearsShutdownReasonAndKeepAwake()
    {
        string? busyReason = null, idleReason = "not queried";
        uint busyState = 0, idleState = 0;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                using var form = new Form();
                var h = form.Handle;
                PowerGuard.Apply(h, "A transfer job is running.");
                busyReason = QueryReason(h);
                busyState = SetThreadExecutionState(EsContinuous | EsSystemRequired); // returns the previous state
                PowerGuard.Apply(h, null);
                idleReason = QueryReason(h);
                idleState = SetThreadExecutionState(EsContinuous);
            }
            catch (Exception ex) { error = ex; } // e.g. no window station: fail this test, not the host
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("Win32 thread failed: " + error);

        Assert.Equal("A transfer job is running.", busyReason);
        Assert.Equal(EsContinuous | EsSystemRequired, busyState);
        Assert.Null(idleReason);
        Assert.Equal(EsContinuous, idleState);
    }

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private static string? QueryReason(IntPtr h)
    {
        uint size = 256;
        var buf = new StringBuilder((int)size);
        return ShutdownBlockReasonQuery(h, buf, ref size) ? buf.ToString() : null;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShutdownBlockReasonQuery(IntPtr hWnd, StringBuilder pwszBuff, ref uint pcchBuff);
}
