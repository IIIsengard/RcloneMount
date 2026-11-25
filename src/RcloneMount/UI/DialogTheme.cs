using System.Drawing;
using System.Windows.Forms;

namespace RcloneMount.UI
{
    // Recursively applies the app theme (surface, text, accent) to a dialog and its
    // controls so pop-up windows match the main window instead of the OS default.
    internal static class DialogTheme
    {
        public static void Apply(Form form)
        {
            form.BackColor = Theme.SurfaceBase;
            form.ForeColor = Theme.TextPrimary;
            ApplyRecursive(form);
            NativeTheme.ApplyDarkChromeIfDark(form);
            NativeTheme.ApplyTitleBarTheme(form);
        }

        public static void ApplyRecursive(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                switch (c)
                {
                    case Label lbl:
                        lbl.BackColor = Color.Transparent;
                        lbl.ForeColor = Theme.TextPrimary;
                        break;
                    case CheckBox chk:
                        chk.BackColor = Color.Transparent;
                        chk.ForeColor = Theme.TextPrimary;
                        break;
                    case RadioButton rb:
                        rb.BackColor = Color.Transparent;
                        rb.ForeColor = Theme.TextPrimary;
                        break;
                    case TextBox tb:
                        tb.BorderStyle = BorderStyle.FixedSingle;
                        tb.BackColor = Theme.SurfaceRaised;
                        tb.ForeColor = Theme.TextPrimary;
                        if (Theme.IsDark)
                            TextBoxDarkChrome.Attach(tb);
                        break;
                    case ComboBox cb:
                        StyleComboBox(cb);
                        break;
                    case Button btn:
                        StyleButton(btn);
                        break;
                    case GroupBox gb:
                        gb.BackColor = Theme.SurfaceBase;
                        gb.ForeColor = Theme.TextSecondary;
                        break;
                    case Panel or TableLayoutPanel or FlowLayoutPanel:
                        if (c.BackColor == SystemColors.Control)
                            c.BackColor = Theme.SurfaceBase;
                        c.ForeColor = Theme.TextPrimary;
                        break;
                    case ListBox lb:
                        lb.BackColor = Theme.SurfaceRaised;
                        lb.ForeColor = Theme.TextPrimary;
                        lb.BorderStyle = BorderStyle.FixedSingle;
                        NativeTheme.ApplyDarkChromeIfDark(lb);
                        break;
                }

                if (c.HasChildren)
                    ApplyRecursive(c);
            }
        }

        private static void StyleComboBox(ComboBox cb)
        {
            cb.FlatStyle = FlatStyle.Flat;
            cb.BackColor = Theme.SurfaceRaised;
            cb.ForeColor = Theme.TextPrimary;
            NativeTheme.ApplyDarkChromeIfDark(cb);

            // Owner-draw the dropdown items so the selection highlight uses the app accent
            // instead of the OS default bright blue.
            if (cb.DrawMode != DrawMode.OwnerDrawFixed)
            {
                cb.DrawMode = DrawMode.OwnerDrawFixed;
                cb.DrawItem += ComboBox_DrawItem;
            }

            // Paint over the drop-down arrow well so it matches the dark surface.
            if (Theme.IsDark)
                ComboBoxArrowPainter.Attach(cb);
        }

        private static void ComboBox_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (sender is not ComboBox cb) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            var bg = selected ? Theme.Accent : Theme.SurfaceRaised;
            var fg = selected ? Theme.TextOnAccent : Theme.TextPrimary;
            using (var b = new SolidBrush(bg))
                e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index >= 0 && e.Index < cb.Items.Count)
            {
                var text = cb.GetItemText(cb.Items[e.Index]);
                using var tb = new SolidBrush(fg);
                e.Graphics.DrawString(text, e.Font ?? cb.Font, tb, e.Bounds.X + 2, e.Bounds.Y + 1);
            }
            e.DrawFocusRectangle();
        }

        private static void StyleButton(Button btn)
        {
            // Only re-style buttons that still carry the OS default look — leaves
            // any button already customised (e.g. accent-primary) alone.
            if (btn.FlatStyle == FlatStyle.Flat) return;
            btn.FlatStyle = FlatStyle.Flat;
            btn.UseVisualStyleBackColor = false;
            btn.BackColor = Theme.SurfaceRaised;
            btn.ForeColor = Theme.TextPrimary;
            btn.FlatAppearance.BorderColor = Theme.BorderEmphasis;
            btn.FlatAppearance.MouseOverBackColor = Theme.SurfaceSunken;
            btn.FlatAppearance.MouseDownBackColor = Theme.SurfaceSunken;
        }
    }
}
