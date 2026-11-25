using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RcloneMount.UI
{
    // A drop-down list control we can fully paint. Sidesteps the native Win32 ComboBox
    // which won't honor dark surface colors for its arrow well, border, or focus rectangle.
    // Public surface intentionally mirrors ComboBox where sensible (Items, SelectedIndex,
    // SelectedItem, SelectedIndexChanged) so swapping is mostly mechanical.
    public class ThemedDropDown : Control
    {
        private readonly List<object> items = new();
        private int selectedIndex = -1;
        private bool isPopupOpen;
        private bool isHovered;
        private PopupForm? popup;

        public event EventHandler? SelectedIndexChanged;

        public IList<object> Items => items;

        public int SelectedIndex
        {
            get => selectedIndex;
            set
            {
                if (value < -1 || value >= items.Count) return;
                if (selectedIndex == value) return;
                selectedIndex = value;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public object? SelectedItem
        {
            get => selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : null;
            set
            {
                var idx = items.IndexOf(value!);
                if (idx >= 0) SelectedIndex = idx;
            }
        }

        public string SelectedText => SelectedItem?.ToString() ?? string.Empty;

        public ThemedDropDown()
        {
            SetStyle(ControlStyles.UserPaint
                   | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw
                   | ControlStyles.Selectable, true);
            BackColor = Theme.SurfaceRaised;
            ForeColor = Theme.TextPrimary;
            Font = SystemFonts.MessageBoxFont ?? Font;
            Height = 23;
            TabStop = true;
            Cursor = Cursors.Default;
        }

        public void ClearItems()
        {
            items.Clear();
            selectedIndex = -1;
            Invalidate();
        }

        public void AddItem(object item) => items.Add(item);

        public void AddItems(IEnumerable<object> range)
        {
            foreach (var it in range) items.Add(it);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = ClientRectangle;

            using (var bg = new SolidBrush(Theme.SurfaceRaised))
                g.FillRectangle(bg, rect);

            var borderColor = Focused || isPopupOpen ? Theme.Accent
                            : isHovered ? Theme.BorderEmphasis
                            : Theme.BorderSubtle;
            using (var pen = new Pen(borderColor))
                g.DrawRectangle(pen, 0, 0, rect.Width - 1, rect.Height - 1);

            var text = SelectedText;
            var textRect = new Rectangle(8, 0, rect.Width - 28, rect.Height);
            TextRenderer.DrawText(g, text, Font, textRect, ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            var cx = rect.Width - 14;
            var cy = rect.Height / 2;
            var chevron = new[]
            {
                new Point(cx - 4, cy - 2),
                new Point(cx + 4, cy - 2),
                new Point(cx,     cy + 3),
            };
            using (var brush = new SolidBrush(Theme.TextSecondary))
                g.FillPolygon(brush, chevron);
        }

        protected override void OnMouseEnter(EventArgs e) { isHovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { isHovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e)   { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e)  { Invalidate(); base.OnLostFocus(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                Focus();
                Toggle();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Down:
                case Keys.F4:
                case Keys.Space:
                case Keys.Enter:
                    if (!isPopupOpen) Toggle();
                    e.Handled = true;
                    break;
                case Keys.Escape:
                    if (isPopupOpen) ClosePopup();
                    e.Handled = true;
                    break;
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Down:
                case Keys.Up:
                case Keys.F4:
                case Keys.Space:
                case Keys.Enter:
                case Keys.Escape:
                    return true;
                default:
                    return base.IsInputKey(keyData);
            }
        }

        private void Toggle()
        {
            if (isPopupOpen) ClosePopup(); else OpenPopup();
        }

        private void OpenPopup()
        {
            if (items.Count == 0) return;
            popup?.Close();

            popup = new PopupForm(this);
            popup.ItemSelected += idx =>
            {
                SelectedIndex = idx;
                ClosePopup();
            };
            popup.FormClosed += (_, _) =>
            {
                isPopupOpen = false;
                popup = null;
                Invalidate();
            };
            isPopupOpen = true;
            Invalidate();

            var screen = PointToScreen(new Point(0, Height));
            popup.Show(this, screen);
        }

        private void ClosePopup()
        {
            popup?.Close();
        }

        // Borderless popup form hosting a dark-themed ListBox. Shown modeless; auto-closes on
        // deactivation (click outside) or item selection.
        private sealed class PopupForm : Form
        {
            private readonly ListBox list;
            private readonly ThemedDropDown owner;

            public event Action<int>? ItemSelected;

            public PopupForm(ThemedDropDown owner)
            {
                this.owner = owner;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Theme.BorderEmphasis;
                Padding = new Padding(1);

                list = new ListBox
                {
                    Dock = DockStyle.Fill,
                    BackColor = Theme.SurfaceRaised,
                    ForeColor = Theme.TextPrimary,
                    BorderStyle = BorderStyle.None,
                    DrawMode = DrawMode.OwnerDrawFixed,
                    IntegralHeight = false,
                    Font = owner.Font,
                    ItemHeight = 22,
                };
                list.DrawItem += OnDrawItem;
                list.MouseMove += OnListMouseMove;
                list.MouseClick += OnListMouseClick;
                list.KeyDown += OnListKeyDown;

                foreach (var it in owner.items) list.Items.Add(it);
                if (owner.selectedIndex >= 0 && owner.selectedIndex < list.Items.Count)
                    list.SelectedIndex = owner.selectedIndex;

                Controls.Add(list);
                NativeTheme.ApplyDarkChromeIfDark(list);
            }

            public void Show(Control anchor, Point screenLocation)
            {
                var width = Math.Max(anchor.Width, 120);
                var visibleRows = Math.Min(owner.items.Count, 10);
                var height = visibleRows * list.ItemHeight + 2;
                Size = new Size(width, height);
                Location = screenLocation;
                Show(anchor.FindForm());
                list.Focus();
            }

            protected override bool ShowWithoutActivation => false;

            protected override void OnDeactivate(EventArgs e)
            {
                base.OnDeactivate(e);
                Close();
            }

            private void OnDrawItem(object? sender, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                var selected = (e.State & DrawItemState.Selected) != 0;
                var bg = selected ? Theme.Accent : Theme.SurfaceRaised;
                var fg = selected ? Theme.TextOnAccent : Theme.TextPrimary;
                using (var b = new SolidBrush(bg))
                    e.Graphics.FillRectangle(b, e.Bounds);
                var text = list.Items[e.Index].ToString() ?? "";
                var textRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, text, e.Font ?? list.Font, textRect, fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            private void OnListMouseMove(object? sender, MouseEventArgs e)
            {
                var idx = list.IndexFromPoint(e.Location);
                if (idx >= 0 && idx != list.SelectedIndex) list.SelectedIndex = idx;
            }

            private void OnListMouseClick(object? sender, MouseEventArgs e)
            {
                var idx = list.IndexFromPoint(e.Location);
                if (idx >= 0) ItemSelected?.Invoke(idx);
            }

            private void OnListKeyDown(object? sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    if (list.SelectedIndex >= 0) ItemSelected?.Invoke(list.SelectedIndex);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    Close();
                    e.Handled = true;
                }
            }
        }
    }
}
