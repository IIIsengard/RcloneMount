using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using RcloneMount.Models;

namespace RcloneMount.UI
{
    public class EditRemoteForm : Form
    {
        // Rclone accepts letters, digits, spaces, and most punctuation in remote names — the only
        // hard exclusions are the ones that would confuse the CLI: ':' (remote/path separator),
        // path separators, and quote/wildcard chars. Leading/trailing whitespace is trimmed.
        private static readonly Regex ValidNameRegex = new(@"^[^:\\/?*""<>|]+$", RegexOptions.Compiled);

        private readonly RcloneProvider provider;
        private readonly bool isEdit;
        private readonly IReadOnlySet<string> takenNames;
        private readonly Dictionary<string, string> initialValues;
        private readonly Dictionary<string, string> workingValues;

        private readonly TextBox txtName;
        private readonly Label lblType;
        private readonly CheckBox chkShowAdvanced;
        private readonly Panel panelFields;
        private readonly Panel? panelOAuthBanner;
        private readonly Button btnOk;
        private readonly Button btnCancel;

        private readonly Dictionary<string, FieldBinding> bindings = new(StringComparer.Ordinal);
        private string currentSubProvider = string.Empty;

        public string RemoteName => txtName.Text.Trim();
        public Dictionary<string, string> ResultValues { get; private set; } = new(StringComparer.Ordinal);
        public HashSet<string> PasswordFieldsSet { get; } = new(StringComparer.Ordinal);
        // Rclone's OAuth backends expose a `token` option (usually hidden). Detect from schema
        // so we pick up new/community backends automatically as rclone adds them.
        public bool RequiresOAuth => provider.Options.Any(o =>
            string.Equals(o.Name, "token", StringComparison.OrdinalIgnoreCase));

        public EditRemoteForm(RcloneProvider provider,
                              IReadOnlyDictionary<string, string>? existing,
                              string? existingName,
                              IEnumerable<string> takenRemoteNames)
        {
            this.provider = provider;
            initialValues = existing is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(existing, StringComparer.Ordinal);
            workingValues = new Dictionary<string, string>(initialValues, StringComparer.Ordinal);
            isEdit = existingName is not null;
            takenNames = takenRemoteNames.ToHashSet(StringComparer.Ordinal);

            Icon = AppIcon.Get();
            Text = isEdit ? $"Edit remote — {existingName}" : $"New remote — {provider.Name}";
            ClientSize = new Size(720, 620);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(600, 480);

            var lblName = new Label { Text = "Name:", Location = new Point(12, 18), AutoSize = true };
            txtName = new TextBox
            {
                Location = new Point(120, 15),
                Width = 260,
                Text = existingName ?? ""
            };

            var lblTypeCaption = new Label { Text = "Type:", Location = new Point(400, 18), AutoSize = true };
            lblType = new Label { Text = provider.Name, Location = new Point(440, 18), AutoSize = true, Font = new Font(Font, FontStyle.Bold) };

            var contentTop = 48;
            if (RequiresOAuth)
            {
                var bannerText = isEdit
                    ? $"{provider.Name} uses browser sign-in. All fields below are optional — use \"Reconnect (CLI)...\" on the main window to re-authorize."
                    : $"{provider.Name} uses browser sign-in. You don't need to fill anything in — just click \"Save & Sign In\" and complete the browser flow.";
                panelOAuthBanner = new Panel
                {
                    Location = new Point(12, contentTop),
                    Size = new Size(696, 56),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    BackColor = Color.FromArgb(0xFF, 0xF6, 0xE0),
                    BorderStyle = BorderStyle.FixedSingle,
                    Padding = new Padding(10)
                };
                var lblBanner = new Label
                {
                    Text = bannerText,
                    Dock = DockStyle.Fill,
                    ForeColor = Color.FromArgb(0x60, 0x40, 0x00),
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true,
                    Font = new Font(Font, FontStyle.Bold)
                };
                panelOAuthBanner.Controls.Add(lblBanner);
                contentTop += 66;
            }

            chkShowAdvanced = new CheckBox { Text = "Show advanced options", Location = new Point(12, contentTop), AutoSize = true };
            chkShowAdvanced.CheckedChanged += (_, _) => RebuildFields();

            var fieldsTop = contentTop + 30;
            panelFields = new Panel
            {
                Location = new Point(12, fieldsTop),
                Size = new Size(696, ClientSize.Height - fieldsTop - 52),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle
            };

            var okText = RequiresOAuth && !isEdit ? "Save && Sign In" : "Save";
            var okWidth = RequiresOAuth && !isEdit ? 130 : 90;
            btnOk = new Button { Text = okText, Location = new Point(ClientSize.Width - okWidth - 108, 582), Size = new Size(okWidth, 30), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnOk.Click += BtnOk_Click;
            btnCancel = new Button { Text = "Cancel", Location = new Point(ClientSize.Width - 102, 582), Size = new Size(90, 30), DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            var controls = new List<Control> { lblName, txtName, lblTypeCaption, lblType, chkShowAdvanced, panelFields, btnOk, btnCancel };
            if (panelOAuthBanner is not null) controls.Insert(4, panelOAuthBanner);
            Controls.AddRange(controls.ToArray());

            currentSubProvider = workingValues.TryGetValue("provider", out var sp) ? sp : DefaultSubProvider();
            RebuildFields();
        }

        private string DefaultSubProvider()
        {
            var opt = provider.Options.FirstOrDefault(o => string.Equals(o.Name, "provider", StringComparison.Ordinal));
            return opt?.DefaultStr ?? string.Empty;
        }

        private void RebuildFields()
        {
            CaptureCurrentValues();

            panelFields.SuspendLayout();
            panelFields.Controls.Clear();
            bindings.Clear();

            var showAdvanced = chkShowAdvanced.Checked;
            var y = 10;
            const int labelX = 8;
            const int inputX = 200;
            var inputWidth = panelFields.ClientSize.Width - inputX - 24;

            foreach (var opt in provider.Options)
            {
                if ((opt.Hide & 1) != 0) continue;
                if (opt.Advanced && !showAdvanced) continue;
                if (!MatchesSubProvider(opt.Provider, currentSubProvider)) continue;

                var label = new Label
                {
                    Text = opt.Name + (opt.Required ? " *" : ""),
                    Location = new Point(labelX, y + 4),
                    Size = new Size(inputX - labelX - 6, 20),
                    TextAlign = ContentAlignment.TopLeft
                };
                if (opt.Required) label.Font = new Font(Font, FontStyle.Bold);
                panelFields.Controls.Add(label);

                var currentValue = ResolveInitialValue(opt);
                var control = CreateInput(opt, currentValue, inputX, y, inputWidth);
                panelFields.Controls.Add(control);
                bindings[opt.Name] = new FieldBinding(opt, control);

                var helpText = opt.Help?.Trim() ?? "";
                var help = new Label
                {
                    Text = helpText,
                    Location = new Point(inputX, y + 28),
                    Size = new Size(inputWidth, 0),
                    AutoSize = false,
                    ForeColor = Color.DimGray,
                    MaximumSize = new Size(inputWidth, 0)
                };
                help.AutoSize = true;
                panelFields.Controls.Add(help);

                var rowHeight = Math.Max(help.Bottom, control.Bottom) - y;
                y += rowHeight + 14;
            }

            if (bindings.Count == 0)
            {
                panelFields.Controls.Add(new Label
                {
                    Text = "No options exposed by this provider — just save.",
                    Location = new Point(labelX, 10),
                    AutoSize = true,
                    ForeColor = Color.DimGray
                });
            }

            panelFields.ResumeLayout();
            panelFields.PerformLayout();
        }

        private string ResolveInitialValue(RcloneProviderOption opt)
        {
            if (workingValues.TryGetValue(opt.Name, out var v))
                return v;
            return opt.DefaultStr;
        }

        private Control CreateInput(RcloneProviderOption opt, string currentValue, int x, int y, int width)
        {
            var examples = opt.Examples
                .Where(ex => MatchesSubProvider(ex.Provider, currentSubProvider))
                .ToList();

            if (string.Equals(opt.Type, "bool", StringComparison.OrdinalIgnoreCase))
            {
                var cb = new CheckBox
                {
                    Location = new Point(x, y + 4),
                    Width = width,
                    Checked = string.Equals(currentValue, "true", StringComparison.OrdinalIgnoreCase),
                    Text = ""
                };
                return cb;
            }

            if (examples.Count > 0)
            {
                var combo = new ComboBox
                {
                    Location = new Point(x, y),
                    Width = width,
                    DropDownStyle = opt.Exclusive ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown
                };
                foreach (var ex in examples)
                {
                    var display = string.IsNullOrEmpty(ex.Help) ? ex.Value : $"{ex.Value}  —  {ex.Help}";
                    combo.Items.Add(new ExampleItem(ex.Value, display));
                }

                var matchIdx = -1;
                for (var i = 0; i < examples.Count; i++)
                {
                    if (string.Equals(examples[i].Value, currentValue, StringComparison.Ordinal))
                    {
                        matchIdx = i; break;
                    }
                }
                if (matchIdx >= 0) combo.SelectedIndex = matchIdx;
                else if (opt.Exclusive && examples.Count > 0) combo.SelectedIndex = 0;
                else combo.Text = currentValue;

                if (string.Equals(opt.Name, "provider", StringComparison.Ordinal))
                {
                    combo.SelectedIndexChanged += (_, _) =>
                    {
                        currentSubProvider = ReadComboValue(combo);
                        RebuildFields();
                    };
                }
                return combo;
            }

            var tb = new TextBox
            {
                Location = new Point(x, y),
                Width = width,
                Text = opt.IsPassword && isEdit ? "" : currentValue,
                UseSystemPasswordChar = opt.IsPassword
            };
            if (opt.IsPassword && isEdit)
                tb.PlaceholderText = "(leave blank to keep existing)";
            return tb;
        }

        private static string ReadComboValue(ComboBox cb)
        {
            if (cb.SelectedItem is ExampleItem ex) return ex.Value;
            return cb.Text ?? string.Empty;
        }

        private void CaptureCurrentValues()
        {
            foreach (var (name, binding) in bindings)
            {
                var val = ReadControl(binding);
                if (val is null) continue;

                if (binding.Option.IsPassword && isEdit && val.Length == 0)
                    continue;

                workingValues[name] = val;
            }
        }

        private static string? ReadControl(FieldBinding b)
        {
            return b.Control switch
            {
                CheckBox cb => cb.Checked ? "true" : "false",
                ComboBox combo => ReadComboValue(combo),
                TextBox tb => tb.Text,
                _ => null
            };
        }

        private static bool MatchesSubProvider(string filter, string current)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            var invert = filter.StartsWith("!", StringComparison.Ordinal);
            var body = invert ? filter[1..] : filter;
            var list = body.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim());
            var match = list.Contains(current, StringComparer.Ordinal);
            return invert ? !match : match;
        }

        private void BtnOk_Click(object? sender, EventArgs e)
        {
            var name = txtName.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show("Remote name is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!ValidNameRegex.IsMatch(name))
            {
                MessageBox.Show(@"Remote name cannot contain : \ / ? * "" < > |", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (takenNames.Contains(name))
            {
                MessageBox.Show($"A remote named '{name}' already exists.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var passwordFields = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (optName, binding) in bindings)
            {
                var raw = ReadControl(binding) ?? "";
                var opt = binding.Option;

                if (opt.IsPassword)
                {
                    if (raw.Length == 0)
                    {
                        if (!isEdit && opt.Required)
                        {
                            MessageBox.Show($"'{optName}' is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        continue;
                    }
                    passwordFields.Add(optName);
                    values[optName] = raw;
                    continue;
                }

                if (opt.Required && raw.Trim().Length == 0)
                {
                    MessageBox.Show($"'{optName}' is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var isDefault = string.Equals(raw, opt.DefaultStr, StringComparison.Ordinal);
                var hadExisting = initialValues.ContainsKey(optName);
                if (isDefault && !hadExisting && !opt.Required)
                    continue;

                values[optName] = raw;
            }

            ResultValues = values;
            foreach (var p in passwordFields) PasswordFieldsSet.Add(p);
            DialogResult = DialogResult.OK;
            Close();
        }

        private sealed class FieldBinding
        {
            public RcloneProviderOption Option { get; }
            public Control Control { get; }
            public FieldBinding(RcloneProviderOption opt, Control c) { Option = opt; Control = c; }
        }

        private sealed class ExampleItem
        {
            public string Value { get; }
            public string Display { get; }
            public ExampleItem(string value, string display) { Value = value; Display = display; }
            public override string ToString() => Display;
        }
    }
}
