using System;
using System.Drawing;
using Microsoft.Win32;

namespace RcloneMount.UI
{
    public enum ThemeMode { Light, Dark }

    // Design tokens. Static so any custom-paint routine picks them up directly. Mode is set
    // once at process startup by DetectFromSystem(); we don't re-theme controls on the fly
    // because too many BackColor properties are captured at construction time in WinForms.
    internal static class Theme
    {
        private static ThemeMode mode = ThemeMode.Light;
        public static ThemeMode Mode => mode;
        public static bool IsDark => mode == ThemeMode.Dark;

        public static void SetMode(ThemeMode m) => mode = m;

        // Auto-detects the Windows app theme once. Falls back to Light if the registry read
        // fails (e.g. corporate lockdowns, non-Windows containers).
        public static ThemeMode DetectFromSystem()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var v = key?.GetValue("AppsUseLightTheme");
                if (v is int i && i == 0) return ThemeMode.Dark;
            }
            catch { }
            return ThemeMode.Light;
        }

        // Neutrals
        public static Color SurfaceBase    => IsDark ? RGB(0x14, 0x16, 0x1B) : RGB(0xF5, 0xF6, 0xF9);
        public static Color SurfaceRaised  => IsDark ? RGB(0x1C, 0x1F, 0x26) : RGB(0xFF, 0xFF, 0xFF);
        public static Color SurfaceSunken  => IsDark ? RGB(0x10, 0x12, 0x16) : RGB(0xEE, 0xEF, 0xF3);
        public static Color BorderSubtle   => IsDark ? RGB(0x26, 0x29, 0x32) : RGB(0xE4, 0xE6, 0xEC);
        public static Color BorderEmphasis => IsDark ? RGB(0x36, 0x39, 0x44) : RGB(0xD2, 0xD4, 0xDC);

        // Text
        public static Color TextPrimary    => IsDark ? RGB(0xEC, 0xED, 0xF1) : RGB(0x1E, 0x21, 0x2A);
        public static Color TextSecondary  => IsDark ? RGB(0xA2, 0xA6, 0xB0) : RGB(0x67, 0x6C, 0x78);
        public static Color TextTertiary   => IsDark ? RGB(0x6C, 0x70, 0x79) : RGB(0x9D, 0xA1, 0xAC);
        public static Color TextOnAccent   => Color.White;

        // Accent
        public static Color Accent         => IsDark ? RGB(0x3D, 0x82, 0xF5) : RGB(0x25, 0x63, 0xEB);
        public static Color AccentHover    => IsDark ? RGB(0x58, 0x96, 0xF7) : RGB(0x1E, 0x4F, 0xCC);
        public static Color AccentTint     => IsDark ? RGB(0x1D, 0x2A, 0x44) : RGB(0xE8, 0xED, 0xFB);

        // Semantic (status)
        public static Color Good           => IsDark ? RGB(0x22, 0xC5, 0x5E) : RGB(0x16, 0xA3, 0x4A);
        public static Color GoodTint       => IsDark ? RGB(0x12, 0x32, 0x1F) : RGB(0xE5, 0xF3, 0xEA);
        public static Color Warn           => IsDark ? RGB(0xF5, 0x9E, 0x0B) : RGB(0xC7, 0x74, 0x00);
        public static Color WarnTint       => IsDark ? RGB(0x3B, 0x2A, 0x0D) : RGB(0xFB, 0xEE, 0xDA);
        public static Color Critical       => IsDark ? RGB(0xF1, 0x59, 0x63) : RGB(0xB0, 0x21, 0x2C);
        public static Color CriticalTint   => IsDark ? RGB(0x3E, 0x14, 0x18) : RGB(0xFB, 0xE1, 0xE3);
        public static Color Idle           => IsDark ? RGB(0x8B, 0x8F, 0x98) : RGB(0x71, 0x75, 0x7F);
        public static Color IdleTint       => IsDark ? RGB(0x23, 0x26, 0x2E) : RGB(0xE9, 0xEA, 0xEE);

        // Rail selection tints
        public static Color RailActiveBg   => IsDark ? RGB(0x26, 0x2A, 0x35) : RGB(0xE8, 0xEB, 0xF4);
        public static Color RailHoverBg    => IsDark ? RGB(0x1F, 0x22, 0x29) : RGB(0xEE, 0xF0, 0xF5);

        // Snapshot of the entire semantic palette in a fixed order. Used by MainForm to build
        // an old→new color map when switching themes live.
        public static Color[] SnapshotAll() => new[]
        {
            SurfaceBase, SurfaceRaised, SurfaceSunken,
            BorderSubtle, BorderEmphasis,
            TextPrimary, TextSecondary, TextTertiary, TextOnAccent,
            Accent, AccentHover, AccentTint,
            Good, GoodTint, Warn, WarnTint, Critical, CriticalTint,
            Idle, IdleTint,
            RailActiveBg, RailHoverBg
        };

        private static Color RGB(byte r, byte g, byte b) => Color.FromArgb(r, g, b);
    }
}
