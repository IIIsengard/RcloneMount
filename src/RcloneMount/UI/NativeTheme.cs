using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RcloneMount.UI
{
    // Windows 10 1809+ exposes a "DarkMode_Explorer" visual style. Applying it to a
    // scrollable control gives us dark-themed scrollbars in dark mode without needing
    // to owner-draw everything.
    internal static class NativeTheme
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int attrSize);

        // Windows 11 (build 22000+); Windows 10 20H1+ uses attr 19. Both are safe to try —
        // unsupported systems just return a non-zero HRESULT which we ignore.
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE      = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD  = 19;

        public static void ApplyDarkChromeIfDark(Control control)
        {
            if (!Theme.IsDark || control is null) return;

            void Apply()
            {
                try
                {
                    if (control.IsHandleCreated)
                        SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
                }
                catch { }
            }

            if (control.IsHandleCreated) Apply();
            else control.HandleCreated += (_, _) => Apply();
        }

        // Colors the Windows title bar / caption buttons to match the app theme.
        // On Win11 this immediately re-tints an already-visible window; on Win10 it takes
        // effect on next paint. No-op if the OS is too old to support the attribute.
        public static void ApplyTitleBarTheme(Form form)
        {
            if (form is null) return;

            void Apply()
            {
                if (!form.IsHandleCreated) return;
                var useDark = Theme.IsDark ? 1 : 0;
                try
                {
                    if (DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE,
                                              ref useDark, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD,
                                              ref useDark, sizeof(int));
                    }
                }
                catch { }
            }

            if (form.IsHandleCreated) Apply();
            else form.HandleCreated += (_, _) => Apply();
        }
    }
}
