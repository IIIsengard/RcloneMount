using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace RcloneMount.UI
{
    // Filter set for a transfer job. Kept as a plain data holder so it can be stored
    // on the parent dialog without pulling FilterEditorDialog itself into scope, and
    // so a future "persisted jobs" slice can serialise it directly.
    public sealed class FilterRules
    {
        // rclone filter-file syntax: each non-blank/non-comment line is `+ pattern`
        // (include), `- pattern` (exclude), or `!` (clear the current list).
        // Kept as one string so the user's line order — which rclone respects —
        // isn't lost through a round-trip via a structured list.
        public string PatternsText { get; set; } = "";
        public string MinSize { get; set; } = "";
        public string MaxSize { get; set; } = "";
        public string MinAge { get; set; } = "";
        public string MaxAge { get; set; } = "";

        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(PatternsText)
            && string.IsNullOrWhiteSpace(MinSize)
            && string.IsNullOrWhiteSpace(MaxSize)
            && string.IsNullOrWhiteSpace(MinAge)
            && string.IsNullOrWhiteSpace(MaxAge);

        // Returns extra rclone args + an optional temp file path the caller should
        // delete after the job finishes. Temp file is used for the include/exclude
        // list because it survives special chars (spaces, quotes) that quoting on
        // the argv would otherwise mangle.
        public (List<string> Args, string? TempFilterFile) EmitArgs()
        {
            var args = new List<string>();
            string? tempFile = null;

            if (!string.IsNullOrWhiteSpace(PatternsText))
            {
                var normalised = NormaliseFilterLines(PatternsText);
                if (normalised.Length > 0)
                {
                    tempFile = Path.Combine(Path.GetTempPath(), $"rclonemount-filter-{Guid.NewGuid():N}.txt");
                    File.WriteAllText(tempFile, normalised, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    args.Add("--filter-from");
                    args.Add(tempFile);
                }
            }
            if (!string.IsNullOrWhiteSpace(MinSize)) { args.Add("--min-size"); args.Add(MinSize.Trim()); }
            if (!string.IsNullOrWhiteSpace(MaxSize)) { args.Add("--max-size"); args.Add(MaxSize.Trim()); }
            if (!string.IsNullOrWhiteSpace(MinAge))  { args.Add("--min-age");  args.Add(MinAge.Trim()); }
            if (!string.IsNullOrWhiteSpace(MaxAge))  { args.Add("--max-age");  args.Add(MaxAge.Trim()); }
            return (args, tempFile);
        }

        // rclone requires each pattern line to be `+ pat`, `- pat`, `!`, `#…`, or
        // blank — with a SPACE between the sign and the pattern. Users routinely
        // type `.htaccess` or `-.htaccess`; forwarding those verbatim makes rclone
        // fail on load with "failed to reload filter". Normalise:
        //   • bare pattern with no sign → treat as exclude (`- pat`)
        //   • `+pat` / `-pat` (no space) → insert the space
        //   • `!` and comments passed through untouched
        //   • dangling `+` or `-` alone → dropped (would be a filter error)
        private static string NormaliseFilterLines(string raw)
        {
            var sb = new StringBuilder();
            foreach (var srcLine in raw.Replace("\r\n", "\n").Split('\n'))
            {
                var line = srcLine.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#' || line == "!") { sb.Append(line).Append('\n'); continue; }
                if (line[0] == '+' || line[0] == '-')
                {
                    if (line.Length == 1) continue;
                    if (line[1] == ' ') sb.Append(line).Append('\n');
                    else sb.Append(line[0]).Append(' ').Append(line.AsSpan(1)).Append('\n');
                    continue;
                }
                sb.Append("- ").Append(line).Append('\n');
            }
            return sb.ToString();
        }

        // Human-readable summary for the "Filters:" chip in the transfer dialog.
        public string Summarise()
        {
            if (IsEmpty) return "No filters";
            var bits = new List<string>();
            var lineCount = 0;
            if (!string.IsNullOrWhiteSpace(PatternsText))
            {
                foreach (var line in PatternsText.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    lineCount++;
                }
                bits.Add($"{lineCount} pattern{(lineCount == 1 ? "" : "s")}");
            }
            if (!string.IsNullOrWhiteSpace(MinSize)) bits.Add($"min {MinSize.Trim()}");
            if (!string.IsNullOrWhiteSpace(MaxSize)) bits.Add($"max {MaxSize.Trim()}");
            if (!string.IsNullOrWhiteSpace(MinAge))  bits.Add($"min-age {MinAge.Trim()}");
            if (!string.IsNullOrWhiteSpace(MaxAge))  bits.Add($"max-age {MaxAge.Trim()}");
            return string.Join(", ", bits);
        }
    }

    // Filter editor. Not sophisticated — a text area for `+ pat` / `- pat` lines
    // (matches rclone's own filter-file format so the docs apply directly) plus
    // four rclone-format text fields for size/age bounds. Deliberately un-fancy
    // because a form-per-rule editor would 3x the code for the same expressiveness.
    public sealed class FilterEditorDialog : Form
    {
        private readonly TextBox txtPatterns;
        private readonly TextBox txtMinSize;
        private readonly TextBox txtMaxSize;
        private readonly TextBox txtMinAge;
        private readonly TextBox txtMaxAge;
        private readonly Button btnOk;
        private readonly Button btnCancel;
        private readonly Button btnClear;

        public FilterRules Result { get; private set; } = new();

        public FilterEditorDialog(FilterRules seed)
        {
            Icon = AppIcon.Get();
            Text = "Filters";
            ClientSize = new Size(560, 480);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var lblPatterns = new Label
            {
                Text = "Include / exclude patterns  (one per line, prefix with + or -)",
                Location = new Point(16, 14),
                AutoSize = true,
                Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold),
            };
            var lblHelp = new Label
            {
                Text = "Examples:  + *.jpg     - /Trash/**     - **/.DS_Store     + /Docs/**\r\nA line with no + or - prefix is treated as an exclude.",
                Location = new Point(16, 34),
                AutoSize = true,
                ForeColor = Theme.TextTertiary,
            };
            txtPatterns = new TextBox
            {
                Location = new Point(16, 56),
                Size = new Size(528, 220),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                Font = new Font(FontFamily.GenericMonospace, Font.Size),
                Text = seed.PatternsText,
            };

            var lblSize = new Label { Text = "Size", Location = new Point(16, 290), AutoSize = true, Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold) };
            var lblMinSize = new Label { Text = "Min:", Location = new Point(16, 316), AutoSize = true };
            txtMinSize = new TextBox { Location = new Point(80, 313), Width = 140, Text = seed.MinSize, PlaceholderText = "e.g. 10M" };
            var lblMaxSize = new Label { Text = "Max:", Location = new Point(240, 316), AutoSize = true };
            txtMaxSize = new TextBox { Location = new Point(300, 313), Width = 140, Text = seed.MaxSize, PlaceholderText = "e.g. 2G" };

            var lblAge = new Label { Text = "Age", Location = new Point(16, 352), AutoSize = true, Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold) };
            var lblMinAge = new Label { Text = "Min:", Location = new Point(16, 378), AutoSize = true };
            txtMinAge = new TextBox { Location = new Point(80, 375), Width = 140, Text = seed.MinAge, PlaceholderText = "e.g. 1h" };
            var lblMaxAge = new Label { Text = "Max:", Location = new Point(240, 378), AutoSize = true };
            txtMaxAge = new TextBox { Location = new Point(300, 375), Width = 140, Text = seed.MaxAge, PlaceholderText = "e.g. 7d" };

            btnClear = new Button { Text = "Clear all", Location = new Point(16, 435), Size = new Size(90, 30) };
            btnClear.Click += (_, _) =>
            {
                txtPatterns.Text = "";
                txtMinSize.Text = ""; txtMaxSize.Text = "";
                txtMinAge.Text = "";  txtMaxAge.Text = "";
            };

            btnOk = new Button { Text = "OK", Location = new Point(360, 435), Size = new Size(90, 30), DialogResult = DialogResult.OK };
            btnOk.Click += (_, _) =>
            {
                Result = new FilterRules
                {
                    PatternsText = txtPatterns.Text ?? "",
                    MinSize = txtMinSize.Text?.Trim() ?? "",
                    MaxSize = txtMaxSize.Text?.Trim() ?? "",
                    MinAge = txtMinAge.Text?.Trim() ?? "",
                    MaxAge = txtMaxAge.Text?.Trim() ?? "",
                };
            };
            btnCancel = new Button { Text = "Cancel", Location = new Point(460, 435), Size = new Size(90, 30), DialogResult = DialogResult.Cancel };
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[]
            {
                lblPatterns, lblHelp, txtPatterns,
                lblSize, lblMinSize, txtMinSize, lblMaxSize, txtMaxSize,
                lblAge, lblMinAge, txtMinAge, lblMaxAge, txtMaxAge,
                btnClear, btnOk, btnCancel,
            });

            DialogTheme.Apply(this);
            RoundedButtons.Apply(this);
        }
    }
}
