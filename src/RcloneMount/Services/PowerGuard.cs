using System;
using System.Runtime.InteropServices;

namespace RcloneMount.Services
{
    public enum ClosingStep { TearDownNow, Hold, TearDownAtEndSession } // public: xUnit theory parameter

    // Keeps the PC from idle-sleeping and names the work on Windows' "apps are preventing
    // shutdown" screen while a job runs or mount uploads are pending. Both Win32 states belong
    // to the calling thread / window, so call Apply from the UI thread with the main form's handle.
    internal static class PowerGuard
    {
        private const uint EsContinuous = 0x80000000;
        private const uint EsSystemRequired = 0x00000001;

        // null = idle. Short on purpose: Windows shows it on the shutdown screen.
        internal static string? BusyReason(int jobsRunning, int uploadsPending)
        {
            var jobs = jobsRunning switch
            {
                0 => null,
                1 => "A transfer job is running.",
                _ => $"{jobsRunning} transfer jobs are running.",
            };
            var uploads = uploadsPending switch
            {
                0 => null,
                1 => "1 mount upload has not finished.",
                _ => $"{uploadsPending} mount uploads have not finished.",
            };
            return jobs is null ? uploads : uploads is null ? jobs : $"{jobs} {uploads}";
        }

        // Hold a Windows shutdown only with a visible window: Windows terminates a window-less app
        // that refuses shutdown, which would skip even the best-effort teardown. Otherwise leave the
        // teardown to WM_ENDSESSION(true): WM_QUERYENDSESSION only asks, and if another app or the
        // user cancels the shutdown, tearing down here would leave a process with no tray or mounts.
        internal static ClosingStep Closing(bool windowsShutDown, bool windowVisible, string? busyReason) =>
            !windowsShutDown ? ClosingStep.TearDownNow
            : windowVisible && busyReason is not null ? ClosingStep.Hold
            : ClosingStep.TearDownAtEndSession;

        // reason null clears both states; otherwise sets (or updates) them.
        internal static void Apply(IntPtr hwnd, string? reason)
        {
            if (reason is null)
            {
                SetThreadExecutionState(EsContinuous);
                ShutdownBlockReasonDestroy(hwnd);
            }
            else
            {
                SetThreadExecutionState(EsContinuous | EsSystemRequired);
                ShutdownBlockReasonCreate(hwnd, reason);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);

        [DllImport("user32.dll")]
        private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
    }
}
