using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        // Non-null when the user picked a sub-provider row (e.g. "Wasabi" under S3-compatible).
        // Contains a single key/value the caller should seed into the new remote's parameters,
        // e.g. {"provider":"Wasabi"} for s3 or {"vendor":"fastmail"} for webdav.
        public Dictionary<string, string>? SelectedPresetParameters { get; private set; }

        // Legacy rclone aliases we don't want to expose in the picker.
        // tardigrade is the pre-rebrand name for storj; both list with the same description
        // and choosing tardigrade for a fresh remote is always wrong.
        private static readonly HashSet<string> HiddenLegacyProviders = new(StringComparer.OrdinalIgnoreCase)
        {
            "tardigrade",
        };

        // Types where a discriminating option expands into first-class picker rows,
        // paired with the option name that drives the sub-provider (s3 uses "provider",
        // webdav uses "vendor"). Add new entries here when rclone gains another sub-provider
        // shaped backend (jottacloud+onelogin? sharepoint variants? etc.).
        private static readonly Dictionary<string, string> ExpandableSubProviderTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["s3"] = "provider",
                ["webdav"] = "vendor",
            };

        public ChooseProviderForm(IReadOnlyList<RcloneProvider> providers)
        {
            // Order comes from the display label inside Repopulate — no need to sort here.
            this.providers = providers
                .Where(p => !HiddenLegacyProviders.Contains(p.Name))
                .ToList();
            Icon = AppIcon.Get();
            Text = "Choose service";
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
                ItemHeight = 36
            };
            lstProviders.DrawItem += LstProviders_DrawItem;
            lstProviders.SelectedIndexChanged += LstProviders_SelectedIndexChanged;
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

            DialogTheme.Apply(this);
            RoundedButtons.Apply(this);

            Repopulate();
        }

        private static readonly HashSet<string> VirtualProviders = new(StringComparer.OrdinalIgnoreCase)
        {
            "alias", "archive", "cache", "chunker", "combine", "compress", "crypt", "hasher", "union", "memory"
        };

        private static readonly HashSet<string> ProtocolProviders = new(StringComparer.OrdinalIgnoreCase)
        {
            "ftp", "sftp", "smb", "webdav", "http", "local"
        };

        private const string GroupCloud     = "Cloud storage";
        private const string GroupS3        = "S3-compatible services";
        private const string GroupWebdav    = "WebDAV vendors";
        private const string GroupProtocols = "Protocols & local";
        private const string GroupVirtual   = "Virtual (wrappers)";

        private static string GroupOf(RcloneProvider p) =>
            VirtualProviders.Contains(p.Name)  ? GroupVirtual :
            ProtocolProviders.Contains(p.Name) ? GroupProtocols :
                                                 GroupCloud;

        private void Repopulate()
        {
            var filter = txtSearch.Text.Trim();
            lstProviders.BeginUpdate();
            lstProviders.Items.Clear();

            // Build the full row set first (base providers + expanded sub-providers),
            // then filter, group, and emit in fixed section order.
            var rows = new List<PickerRow>();
            foreach (var p in providers)
            {
                rows.Add(PickerRow.ForProvider(p));

                if (ExpandableSubProviderTypes.TryGetValue(p.Name, out var subKey))
                {
                    var subOpt = p.Options.FirstOrDefault(o => string.Equals(o.Name, subKey, StringComparison.Ordinal));
                    if (subOpt is null) continue;
                    foreach (var ex in subOpt.Examples)
                    {
                        // "other" example values are catch-alls — skip; user can pick the base
                        // s3/webdav row and configure manually. Keep everything else, even legacy
                        // entries, since users may already have them configured.
                        if (string.Equals(ex.Value, "other", StringComparison.OrdinalIgnoreCase)) continue;
                        rows.Add(PickerRow.ForSubProvider(p, subKey, ex));
                    }
                }
            }

            var filtered = string.IsNullOrEmpty(filter)
                ? rows
                : rows.Where(r => r.Matches(filter)).ToList();

            string[] order = { GroupCloud, GroupS3, GroupWebdav, GroupProtocols, GroupVirtual };
            var byGroup = filtered.ToLookup(r => r.Group);
            foreach (var group in order)
            {
                // Sort within each section by what the user actually sees on the row.
                var items = byGroup[group].OrderBy(r => r.DisplayLabel, StringComparer.OrdinalIgnoreCase).ToList();
                if (items.Count == 0) continue;
                lstProviders.Items.Add(new SectionHeader(group));
                foreach (var r in items) lstProviders.Items.Add(r);
            }

            lstProviders.EndUpdate();
            SelectFirstProvider();
        }

        private void SelectFirstProvider()
        {
            for (int i = 0; i < lstProviders.Items.Count; i++)
            {
                if (lstProviders.Items[i] is PickerRow) { lstProviders.SelectedIndex = i; return; }
            }
            lblDescription.Text = string.Empty;
        }

        private int lastGoodSelection = -1;

        private void LstProviders_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var idx = lstProviders.SelectedIndex;
            if (idx < 0) { UpdateDescription(); return; }

            // Landing on a header (keyboard nav or click) — bounce to the nearest provider row.
            if (lstProviders.Items[idx] is SectionHeader)
            {
                var goingDown = idx > lastGoodSelection;
                var target = FindNearestProviderIndex(idx, goingDown);
                if (target >= 0) { lstProviders.SelectedIndex = target; return; }
            }
            lastGoodSelection = idx;
            UpdateDescription();
        }

        private int FindNearestProviderIndex(int from, bool downFirst)
        {
            int step = downFirst ? 1 : -1;
            for (int i = from + step; i >= 0 && i < lstProviders.Items.Count; i += step)
                if (lstProviders.Items[i] is PickerRow) return i;
            // Wrap the other direction.
            step = -step;
            for (int i = from + step; i >= 0 && i < lstProviders.Items.Count; i += step)
                if (lstProviders.Items[i] is PickerRow) return i;
            return -1;
        }

        private void UpdateDescription()
        {
            if (lstProviders.SelectedItem is PickerRow row)
                lblDescription.Text = row.DescriptionText;
            else
                lblDescription.Text = string.Empty;
        }

        private void AcceptSelection()
        {
            if (lstProviders.SelectedItem is not PickerRow row) return;
            Selected = row.Provider;
            SelectedPresetParameters = row.PresetParameters;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void LstProviders_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = e.Graphics;

            var entry = lstProviders.Items[e.Index];
            if (entry is SectionHeader header)
            {
                using var bg = new SolidBrush(lstProviders.BackColor);
                g.FillRectangle(bg, e.Bounds);
                using var headerFont = new Font(Font.FontFamily, Font.Size - 0.5f, FontStyle.Bold);
                var textRect = new Rectangle(e.Bounds.Left + 8, e.Bounds.Top, e.Bounds.Width - 16, e.Bounds.Height);
                TextRenderer.DrawText(g, header.Title.ToUpperInvariant(), headerFont, textRect,
                    Theme.TextSecondary,
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Bottom | TextFormatFlags.Left);
                using var linePen = new Pen(Theme.TextSecondary) { DashStyle = DashStyle.Dot };
                var y = e.Bounds.Bottom - 1;
                g.DrawLine(linePen, e.Bounds.Left + 8, y, e.Bounds.Right - 8, y);
                return;
            }

            var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (var bg = new SolidBrush(selected ? Theme.Accent : lstProviders.BackColor))
                g.FillRectangle(bg, e.Bounds);

            var row = (PickerRow)entry!;
            var badgeRect = new Rectangle(e.Bounds.Left + 8, e.Bounds.Top + 4, 28, 28);
            ProviderVisuals.DrawBadge(g, badgeRect, row.IconKey);

            var textLeft = badgeRect.Right + 10;
            var nameColor = selected ? Theme.TextOnAccent : Theme.TextPrimary;

            using var nameFont = new Font(Font, FontStyle.Bold);
            var rect = new Rectangle(textLeft, e.Bounds.Top, e.Bounds.Right - textLeft - 8, e.Bounds.Height);
            TextRenderer.DrawText(g, row.DisplayLabel, nameFont, rect, nameColor,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);

            if ((e.State & DrawItemState.Focus) != 0)
                e.DrawFocusRectangle();
        }

        // Unified picker row — represents either a base provider ("Amazon S3") or a
        // sub-provider variant ("Wasabi Object Storage"). Sub-provider rows carry a preset
        // parameter (e.g. provider=Wasabi) that the caller seeds into the new remote's config
        // so EditRemoteForm opens with the right sub-provider already selected.
        private sealed class PickerRow
        {
            public RcloneProvider Provider { get; }
            public string Group { get; }
            public string DisplayLabel { get; }
            public string DescriptionText { get; }
            public string IconKey { get; }
            public string SearchText { get; }
            public Dictionary<string, string>? PresetParameters { get; }

            private PickerRow(RcloneProvider provider, string group, string label, string description,
                              string iconKey, string searchText, Dictionary<string, string>? preset)
            {
                Provider = provider;
                Group = group;
                DisplayLabel = label;
                DescriptionText = description;
                IconKey = iconKey;
                SearchText = searchText;
                PresetParameters = preset;
            }

            public static PickerRow ForProvider(RcloneProvider p)
            {
                // The bare s3 row is the "any other S3-compatible endpoint" catch-all — surface
                // it inside the S3 section next to the named vendors instead of burying it in
                // the generic cloud group where users won't think to look.
                if (string.Equals(p.Name, "s3", StringComparison.OrdinalIgnoreCase))
                {
                    const string label = "Other S3 Compliant Server";
                    const string description =
                        "Configure any S3-compatible storage endpoint not listed above. " +
                        "You'll need the endpoint URL, access key, and secret key from your provider.";
                    var search = $"{p.Name} {p.Description} other s3 compliant";
                    return new PickerRow(p, GroupS3, label, description, p.Name ?? "", search, null);
                }

                var defaultLabel = !string.IsNullOrWhiteSpace(p.Description) ? p.Description : p.Name;
                var defaultSearch = $"{p.Name} {p.Description}";
                return new PickerRow(p, GroupOf(p), defaultLabel, p.Description ?? "", p.Name ?? "", defaultSearch, null);
            }

            public static PickerRow ForSubProvider(RcloneProvider p, string subKey, RcloneProviderExample ex)
            {
                var label = string.IsNullOrWhiteSpace(ex.Help) ? ex.Value : ex.Help;
                var description = string.IsNullOrWhiteSpace(ex.Help)
                    ? $"{p.Description} — {ex.Value}"
                    : $"{p.Description}\n\n{ex.Value} — {ex.Help}";
                var iconKey = ProviderVisuals.IconKeyFor(p.Name, ex.Value);
                var search = $"{ex.Value} {ex.Help} {p.Name} {p.Description}";
                var group = string.Equals(p.Name, "s3", StringComparison.OrdinalIgnoreCase)
                                ? GroupS3 : GroupWebdav;
                var preset = new Dictionary<string, string>(StringComparer.Ordinal) { [subKey] = ex.Value };
                return new PickerRow(p, group, label, description, iconKey, search, preset);
            }

            public bool Matches(string filter) =>
                SearchText.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

            public override string ToString() => DisplayLabel;
        }

        private sealed class SectionHeader
        {
            public string Title { get; }
            public SectionHeader(string title) { Title = title; }
            public override string ToString() => Title;
        }
    }
}
