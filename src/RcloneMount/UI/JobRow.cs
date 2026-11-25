using System;
using System.Drawing;
using System.Windows.Forms;
using RcloneMount.Models;

namespace RcloneMount.UI
{
    // A single row on the JOBS strip of the Transfers tab. Non-owner-drawn (unlike
    // MountRow) because the strip's fixed height doesn't need the pixel-perfect
    // layout MountRow does; regular Labels + Buttons keep this small.
    public sealed class JobRow : UserControl
    {
        private const int RowHeight = 44;

        public TransferJob Job { get; private set; } = new();

        public event EventHandler? RunRequested;
        public event EventHandler? EditRequested;
        public event EventHandler? DeleteRequested;

        private readonly Label lblName;
        private readonly Label lblRoute;
        private readonly Label lblLast;
        private readonly FlowLayoutPanel actions;
        private readonly Button btnRun;
        private readonly Button btnEdit;
        private readonly Button btnDelete;

        public JobRow()
        {
            DoubleBuffered = true;
            Height = RowHeight;
            BackColor = Theme.SurfaceRaised;

            lblName = new Label
            {
                Location = new Point(12, 6),
                AutoSize = true,
                Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                Text = "",
            };
            lblRoute = new Label
            {
                Location = new Point(12, 24),
                AutoSize = true,
                Font = new Font(FontFamily.GenericMonospace, Font.Size - 0.5f),
                ForeColor = Theme.TextSecondary,
                Text = "",
            };
            lblLast = new Label
            {
                Location = new Point(260, 24),
                AutoSize = true,
                ForeColor = Theme.TextTertiary,
                Text = "",
            };

            actions = new FlowLayoutPanel
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Padding = new Padding(0),
                Margin = new Padding(0),
                BackColor = Color.Transparent,
            };
            btnRun    = MakeMiniButton("Run");
            btnEdit   = MakeMiniButton("Edit");
            btnDelete = MakeMiniButton("Delete");
            btnRun.Click    += (_, _) => RunRequested?.Invoke(this, EventArgs.Empty);
            btnEdit.Click   += (_, _) => EditRequested?.Invoke(this, EventArgs.Empty);
            btnDelete.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);
            actions.Controls.Add(btnRun);
            actions.Controls.Add(btnEdit);
            actions.Controls.Add(btnDelete);

            Controls.Add(lblName);
            Controls.Add(lblRoute);
            Controls.Add(lblLast);
            Controls.Add(actions);
            RoundedButtons.Apply(this);
            Resize += (_, _) => RepositionActions();
            RepositionActions();
        }

        public void Bind(TransferJob job)
        {
            Job = job;
            lblName.Text = string.IsNullOrWhiteSpace(job.Name) ? "(unnamed job)" : job.Name;
            lblRoute.Text =
                $"{job.Operation.ToLowerInvariant()}  {FormatSpec(job.SourceRemote, job.SourcePath)}  →  {FormatSpec(job.DestRemote, job.DestPath)}"
                + (job.DryRun ? "  · dry-run" : "");
            if (job.LastRunUtc is { } t)
            {
                lblLast.Text = $"last run {AgoLabel(DateTime.UtcNow - t)}"
                             + (string.IsNullOrEmpty(job.LastResultKind) ? "" : $" — {job.LastResultKind}");
            }
            else
            {
                lblLast.Text = "never run";
            }
            RepositionActions();
        }

        private void RepositionActions()
        {
            actions.PerformLayout();
            actions.Location = new Point(Width - actions.PreferredSize.Width - 10,
                                         (Height - actions.PreferredSize.Height) / 2);
            // Push the last-run label leftwards if actions grew wider than expected.
            lblLast.Location = new Point(Math.Max(260, actions.Left - lblLast.Width - 16), 24);
        }

        private static Button MakeMiniButton(string text)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = false,
                Size = new Size(60, 26),
                Margin = new Padding(0, 0, 6, 0),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f),
                Cursor = Cursors.Hand,
                BackColor = Theme.SurfaceRaised,
                ForeColor = Theme.TextPrimary,
            };
            b.FlatAppearance.BorderColor = Theme.BorderEmphasis;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = Theme.SurfaceSunken;
            b.FlatAppearance.MouseDownBackColor = Theme.SurfaceSunken;
            return b;
        }

        private static string FormatSpec(string remote, string path) =>
            $"{remote}:{(path ?? "").TrimStart('/')}";

        private static string AgoLabel(TimeSpan ts)
        {
            if (ts.TotalSeconds < 60) return "just now";
            if (ts.TotalMinutes < 60) return $"{(int)ts.TotalMinutes}m ago";
            if (ts.TotalHours < 24)   return $"{(int)ts.TotalHours}h ago";
            return $"{(int)ts.TotalDays}d ago";
        }
    }
}
