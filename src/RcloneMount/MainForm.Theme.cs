using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using RcloneMount.UI;

namespace RcloneMount
{
    // Theme apply and live-switch. The live-switch pattern is: snapshot the palette
    // BEFORE mutating Theme, snapshot AFTER, build a Color→Color map from the deltas,
    // then walk every Control and swap colors that appeared in the map. Avoids
    // recreating any controls; the whole window flips theme in place.
    public partial class MainForm
    {
        private UserPreferenceChangedEventHandler? systemThemeHandler;

        // Wire the process to Windows' theme changes so the "Auto" preference actually
        // tracks a live OS flip (Settings → Personalisation → Colors) instead of only
        // reading the registry once at startup. UserPreferenceChanged fires on the UI
        // thread's message pump when a handle exists, but we marshal defensively.
        private void HookSystemThemeChanges()
        {
            if (systemThemeHandler is not null) return;
            systemThemeHandler = (_, ev) =>
            {
                // General covers the light/dark toggle plus most other appearance keys;
                // gating avoids re-theming on every unrelated user-pref change (locale,
                // keyboard layout, …).
                if (ev.Category != UserPreferenceCategory.General) return;
                if (!string.Equals(themePreferenceOverride, "Auto", StringComparison.OrdinalIgnoreCase)) return;
                var detected = Theme.DetectFromSystem();
                if (detected == Theme.Mode) return;
                if (IsDisposed) return;
                try
                {
                    if (InvokeRequired) BeginInvoke(new Action(() => LiveSwitchTheme(detected)));
                    else LiveSwitchTheme(detected);
                }
                catch { /* handle destroyed mid-flight — harmless */ }
            };
            SystemEvents.UserPreferenceChanged += systemThemeHandler;
        }

        private void UnhookSystemThemeChanges()
        {
            if (systemThemeHandler is null) return;
            try { SystemEvents.UserPreferenceChanged -= systemThemeHandler; } catch { }
            systemThemeHandler = null;
        }

        private void LiveSwitchTheme(ThemeMode target)
        {
            var oldColors = Theme.SnapshotAll();
            Theme.SetMode(target);
            var newColors = Theme.SnapshotAll();

            var map = new Dictionary<Color, Color>();
            for (int i = 0; i < oldColors.Length; i++)
            {
                if (oldColors[i] == newColors[i]) continue;
                if (!map.ContainsKey(oldColors[i])) map[oldColors[i]] = newColors[i];
            }

            SuspendLayout();
            RemapColorsRecursive(this, map);
            NativeTheme.ApplyTitleBarTheme(this);
            ResumeLayout(true);
            Invalidate(true);
        }

        private static void RemapColorsRecursive(Control c, Dictionary<Color, Color> map)
        {
            if (map.TryGetValue(c.BackColor, out var newBg)) c.BackColor = newBg;
            if (map.TryGetValue(c.ForeColor, out var newFg)) c.ForeColor = newFg;

            if (c is Button btn)
            {
                if (map.TryGetValue(btn.FlatAppearance.BorderColor, out var b))
                    btn.FlatAppearance.BorderColor = b;
                if (map.TryGetValue(btn.FlatAppearance.MouseOverBackColor, out var mo))
                    btn.FlatAppearance.MouseOverBackColor = mo;
                if (map.TryGetValue(btn.FlatAppearance.MouseDownBackColor, out var md))
                    btn.FlatAppearance.MouseDownBackColor = md;
            }

            foreach (Control child in c.Controls)
                RemapColorsRecursive(child, map);

            c.Invalidate();
        }

        // Hooks a button so its disabled state paints predictably in dark mode: WinForms's
        // default disabled rendering just dims the ForeColor via SystemColors, which produces
        // near-invisible text on dark surfaces. We instead move to a sunken bg + tertiary text
        // when disabled, and restore the enabled colors when re-enabled.
        private static void ApplyEnabledStyling(Button btn)
        {
            // Read palette from Theme on every sync so a live theme swap picks up correctly.
            void Sync()
            {
                if (btn.Enabled)
                {
                    btn.BackColor = Theme.SurfaceRaised;
                    btn.ForeColor = Theme.TextPrimary;
                    btn.FlatAppearance.BorderColor = Theme.BorderEmphasis;
                }
                else
                {
                    btn.BackColor = Theme.SurfaceRaised;
                    btn.ForeColor = Theme.TextSecondary;
                    btn.FlatAppearance.BorderColor = Theme.BorderSubtle;
                }
                btn.Invalidate();
            }

            // WinForms flat-button disabled paint ignores ForeColor and dims whatever it
            // computes to something near-black on dark surfaces. Override with our own paint
            // that respects TextSecondary as the disabled label color.
            btn.Paint += (_, e) =>
            {
                if (btn.Enabled) return;
                using var bg = new SolidBrush(btn.BackColor);
                e.Graphics.FillRectangle(bg, 0, 0, btn.Width, btn.Height);
                using var pen = new Pen(btn.FlatAppearance.BorderColor);
                e.Graphics.DrawRectangle(pen, 0, 0, btn.Width - 1, btn.Height - 1);
                TextRenderer.DrawText(e.Graphics, btn.Text, btn.Font,
                    new Rectangle(0, 0, btn.Width, btn.Height), Theme.TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.HidePrefix);
            };
            btn.EnabledChanged += (_, _) => Sync();
            Sync();
        }

        // Iterates the Settings tab's control tree and applies theme colors to labels,
        // checkboxes, and group boxes. Buttons keep their own explicit theming.
        private void ApplyThemeToSettingsTab(Control root)
        {
            foreach (Control c in root.Controls)
            {
                switch (c)
                {
                    case GroupBox gb:
                        gb.BackColor = Theme.SurfaceBase;
                        gb.ForeColor = Theme.TextSecondary;
                        break;
                    case CheckBox cb:
                        cb.BackColor = Theme.SurfaceBase;
                        cb.ForeColor = Theme.TextPrimary;
                        break;
                    case Label lbl:
                        lbl.BackColor = Color.Transparent;
                        lbl.ForeColor = Theme.TextPrimary;
                        break;
                    case Button btn:
                        // Only re-theme buttons that still carry the OS default look.
                        if (btn.FlatStyle != FlatStyle.Flat)
                        {
                            btn.FlatStyle = FlatStyle.Flat;
                            btn.UseVisualStyleBackColor = false;
                            btn.BackColor = Theme.SurfaceRaised;
                            btn.ForeColor = Theme.TextPrimary;
                            btn.FlatAppearance.BorderColor = Theme.BorderEmphasis;
                        }
                        ApplyEnabledStyling(btn);
                        break;
                }
                if (c.HasChildren) ApplyThemeToSettingsTab(c);
            }
        }
    }
}
