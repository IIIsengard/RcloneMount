using System;
using System.Runtime.InteropServices;

namespace RcloneMount.Services
{
    // Tells Windows Explorer / shell tree that a drive appeared or disappeared,
    // so "This PC", the navigation pane, and open Explorer windows refresh immediately.
    public static class ShellNotifier
    {
        private const int SHCNE_DRIVEADD     = 0x00000100;
        private const int SHCNE_DRIVEREMOVED = 0x00000080;

        private const uint SHCNF_PATHW      = 0x0005;
        private const uint SHCNF_FLUSH      = 0x1000;

        public static void DriveAdded(string driveLetter) => Notify(SHCNE_DRIVEADD, driveLetter);
        public static void DriveRemoved(string driveLetter) => Notify(SHCNE_DRIVEREMOVED, driveLetter);

        private static void Notify(int eventId, string driveLetter)
        {
            if (string.IsNullOrWhiteSpace(driveLetter)) return;

            var root = driveLetter.TrimEnd(':') + ":\\";
            var ptr = Marshal.StringToHGlobalUni(root);
            try
            {
                SHChangeNotify(eventId, SHCNF_PATHW | SHCNF_FLUSH, ptr, IntPtr.Zero);
            }
            catch
            {
                // shell notification is best-effort — never let it break the app
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}
