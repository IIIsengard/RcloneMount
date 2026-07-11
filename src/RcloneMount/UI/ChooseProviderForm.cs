using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using RcloneMount.Models;

namespace RcloneMount.UI
{
    public class ChooseProviderForm : Form
    {
        private readonly IReadOnlyList<RcloneProvider> providers;
        private readonly ListBox lstProviders;
        private readonly TextBox txtSearch;
        private readonly Label lblDescription;
        private readonly Button btnOk;
        private readonly Button btnCancel;

        public RcloneProvider? Selected { get; private set; }

        public ChooseProviderForm(IReadOnlyList<RcloneProvider> providers)
        {
            this.providers = providers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Icon = AppIcon.Get();
            Text = "Choose provider";
            ClientSize = new Size(560, 480);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(500, 400);
            MaximizeBox = false;
            MinimizeBox = false;

            var lblSearch = new Label { Text = "Search:", Location = new Point(12, 15), AutoSize = true };
            txtSearch = new TextBox { Location = new Point(70, 12), Width = 470, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            txtSearch.TextChanged += (_, _) => Repopulate();

            lstProviders = new ListBox
            {
                Location = new Point(12, 42),
                Size = new Size(528, 280),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 44
            };
            lstProviders.DrawItem += LstProviders_DrawItem;
            lstProviders.SelectedIndexChanged += (_, _) => UpdateDescription();
            lstProviders.DoubleClick += (_, _) => { if (lstProviders.SelectedIndex >= 0) AcceptSelection(); };

            lblDescription = new Label
            {
                Location = new Point(12, 332),
                Size = new Size(528, 100),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(6),
                AutoEllipsis = true
            };

            btnOk = new Button { Text = "Next", Location = new Point(360, 440), Size = new Size(85, 30), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnOk.Click += (_, _) => AcceptSelection();
            btnCancel = new Button { Text = "Cancel", Location = new Point(455, 440), Size = new Size(85, 30), DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[] { lblSearch, txtSearch, lstProviders, lblDescription, btnOk, btnCancel });

            Repopulate();
        }

        private void Repopulate()
        {
            var filter = txtSearch.Text.Trim();
            lstProviders.BeginUpdate();
            lstProviders.Items.Clear();
            foreach (var p in providers)
            {
                if (filter.Length > 0
                    && p.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && p.Description.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                lstProviders.Items.Add(new ProviderItem(p));
            }
            lstProviders.EndUpdate();
            if (lstProviders.Items.Count > 0)
                lstProviders.SelectedIndex = 0;
            else
                lblDescription.Text = string.Empty;
        }

        private void UpdateDescription()
        {
            if (lstProviders.SelectedItem is ProviderItem item)
                lblDescription.Text = item.Provider.Description;
            else
                lblDescription.Text = string.Empty;
        }

        private void AcceptSelection()
        {
            if (lstProviders.SelectedItem is not ProviderItem item) return;
            Selected = item.Provider;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void LstProviders_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = e.Graphics;
            var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

            using (var bg = new SolidBrush(selected ? SystemColors.Highlight : lstProviders.BackColor))
                g.FillRectangle(bg, e.Bounds);

            var item = (ProviderItem)lstProviders.Items[e.Index]!;
            var badgeRect = new Rectangle(e.Bounds.Left + 8, e.Bounds.Top + 6, 32, 32);
            ProviderVisuals.DrawBadge(g, badgeRect, item.Provider.Name);

            var textLeft = badgeRect.Right + 10;
            var nameColor = selected ? SystemColors.HighlightText : SystemColors.ControlText;
            var descColor = selected ? SystemColors.HighlightText : Color.DimGray;

            var name = item.Provider.Name ?? "";
            var desc = item.Provider.Description ?? "";
            var descRedundant = string.Equals(desc.Trim(), name, StringComparison.OrdinalIgnoreCase);

            if (descRedundant || string.IsNullOrWhiteSpace(desc))
            {
                using var nameFont = new Font(Font, FontStyle.Bold);
                var rect = new Rectangle(textLeft, e.Bounds.Top, e.Bounds.Right - textLeft - 8, e.Bounds.Height);
                TextRenderer.DrawText(g, name, nameFont, rect, nameColor,
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
            else
            {
                using (var nameFont = new Font(Font, FontStyle.Bold))
                    TextRenderer.DrawText(g, name, nameFont,
                        new Point(textLeft, e.Bounds.Top + 4), nameColor, TextFormatFlags.NoPrefix);

                var descRect = new Rectangle(textLeft, e.Bounds.Top + 22,
                                             e.Bounds.Right - textLeft - 8, e.Bounds.Height - 22);
                TextRenderer.DrawText(g, desc, Font, descRect, descColor,
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
            }

            if ((e.State & DrawItemState.Focus) != 0)
                e.DrawFocusRectangle();
        }

        private sealed class ProviderItem
        {
            public RcloneProvider Provider { get; }
            public ProviderItem(RcloneProvider p) { Provider = p; }
            public override string ToString() => $"{Provider.Name}  —  {Provider.Description}";
        }
    }
}
