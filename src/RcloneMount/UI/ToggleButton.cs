using System;
using System.Drawing;
using System.Windows.Forms;

namespace RcloneMount.UI
{
    // Button with a typed on/off state that carries its own visual toggle. Replaces
    // the older Button+Tag<bool>+MakeToggleButton pattern so callers can just read
    // btn.Checked or write btn.Checked = true instead of casting Tag through helpers.
    // Also fires CheckedChanged (like a CheckBox) so subscribers don't need to poll.
    public sealed class ToggleButton : Button
    {
        private bool checkedState;

        public event EventHandler? CheckedChanged;

        public bool Checked
        {
            get => checkedState;
            set
            {
                if (checkedState == value) return;
                checkedState = value;
                ApplyVisual();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        // Skips the CheckedChanged event — for initial state during Load, where we
        // don't want the persistence hook fighting the value we're restoring.
        public void SetCheckedSilent(bool value)
        {
            checkedState = value;
            ApplyVisual();
        }

        // When true (default), clicking flips Checked. Set false for radio-group members
        // where the click handler enforces group behaviour (self on, others off) and the
        // ToggleButton shouldn't independently toggle itself.
        public bool AutoToggle { get; set; } = true;

        public ToggleButton()
        {
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            ApplyVisual();
            Click += (_, _) => { if (AutoToggle) Checked = !Checked; };
        }

        private void ApplyVisual()
        {
            if (checkedState)
            {
                BackColor = Theme.AccentTint;
                ForeColor = Theme.Accent;
                FlatAppearance.BorderColor = Theme.Accent;
            }
            else
            {
                BackColor = Theme.SurfaceRaised;
                ForeColor = Theme.TextPrimary;
                FlatAppearance.BorderColor = Theme.BorderEmphasis;
            }
        }
    }
}
