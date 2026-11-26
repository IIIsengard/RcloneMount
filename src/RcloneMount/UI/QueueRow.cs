using System;
using System.Drawing;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.UI
{
    // A single row on the QUEUE strip of the Transfers tab. One JobRun per
    // row; state chip on the left, name + route above, live progress /
    // outcome below, Cancel / Retry / Remove on the right depending on
    // state. Same visual pattern as JobRow — non-owner-drawn, small.
    public sealed class QueueRow : UserControl
    {
        private const int RowHeight = 56;

        public JobRun Run { get; private set; } = new();

        public event EventHandler? CancelRequested;
        public event EventHandler? RetryRequested;
        public event EventHandler? RemoveRequested;

        private readonly Label lblState;
        private readonly Label lblName;
        private readonly Label lblRoute;
        private readonly Label lblStatus;
        private readonly ProgressBar bar;
        private readonly FlowLayoutPanel actions;
        private readonly Button btnCancel;
        private readonly Button btnRetry;
        private readonly Button btnRemove;

        public QueueRow()
        {
            DoubleBuffered = true;
            Height = RowHeight;
            BackColor = Theme.SurfaceRaised;

            lblState = new Label
            {
                Location = new Point(10, 8),
                Size = new Size(78, 20),
                Font = new Font(Font.FontFamily, 7.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                BorderStyle = BorderStyle.FixedSingle,
                Text = "",
            };
            lblName = new Label
            {
                UseMnemonic = false,
                Location = new Point(96, 6),
                AutoSize = false,
                Size = new Size(400, 18),
                AutoEllipsis = true,
                Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                Text = "",
            };
            lblRoute = new Label
            {
                UseMnemonic = false,
                Location = new Point(96, 24),
                AutoSize = false,
                Size = new Size(400, 16),
                AutoEllipsis = true,
                Font = new Font(FontFamily.GenericMonospace, Font.Size - 0.5f),
                ForeColor = Theme.TextSecondary,
                Text = "",
            };
            bar = new ProgressBar
            {
                Location = new Point(96, 42),
                Size = new Size(300, 8),
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = 1000,
                Value = 0,
                Visible = false,
            };
            lblStatus = new Label
            {
                UseMnemonic = false,
                Location = new Point(400, 40),
                AutoSize = false,
                Size = new Size(280, 14),
                AutoEllipsis = true,
                ForeColor = Theme.TextTertiary,
                Font = new Font(Font.FontFamily, 7.5f),
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
            btnCancel = MakeMiniButton("Cancel");
            btnRetry  = MakeMiniButton("Retry");
            btnRemove = MakeMiniButton("Remove");
            btnCancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
            btnRetry.Click  += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
            btnRemove.Click += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty);
            actions.Controls.Add(btnCancel);
            actions.Controls.Add(btnRetry);
            actions.Controls.Add(btnRemove);

            Controls.Add(lblState);
            Controls.Add(lblName);
            Controls.Add(lblRoute);
            Controls.Add(bar);
            Controls.Add(lblStatus);
            Controls.Add(actions);
            RoundedButtons.Apply(this);
            Resize += (_, _) => RepositionActions();
            RepositionActions();
        }

        public void Bind(JobRun run)
        {
            Run = run;
            var stateText = run.State switch
            {
                JobRunState.Queued    => "QUEUED",
                JobRunState.Running   => "RUNNING",
                JobRunState.Completed => "DONE",
                JobRunState.Failed    => "FAILED",
                JobRunState.Cancelled => "CANCELLED",
                _ => "?",
            };
            var (chipBg, chipFg) = ChipColoursFor(run.State);
            lblState.Text = stateText;
            lblState.BackColor = chipBg;
            lblState.ForeColor = chipFg;

            lblName.Text = string.IsNullOrWhiteSpace(run.DisplayName) ? "(unnamed run)" : run.DisplayName;

            var s = run.Spec;
            var opWord = s.OperationVerb;
            var srcDisplay = FormatSpec(s.SourceRemote, s.SourcePath);
            var dstDisplay = FormatSpec(s.DestRemote, s.DestPath);
            lblRoute.Text = $"{opWord}  {srcDisplay}  →  {dstDisplay}" + (s.DryRun ? "  · dry-run" : "");

            // Progress bar + status line reflect state.
            if (run.State == JobRunState.Running && run.Progress is { } p)
            {
                bar.Visible = true;
                if (p.TotalBytes > 0)
                {
                    var ratio = Math.Clamp((double)p.BytesTransferred / p.TotalBytes, 0, 1);
                    bar.Style = ProgressBarStyle.Continuous;
                    bar.Value = (int)Math.Round(ratio * bar.Maximum);
                }
                else
                {
                    bar.Style = ProgressBarStyle.Marquee;
                }
                var etaStr = p.EtaSeconds is { } eta && double.IsFinite(eta) && eta > 0 ? FormatDuration(eta) : "—";
                lblStatus.Text =
                    $"{FormatBytes(p.BytesTransferred)}"
                    + (p.TotalBytes > 0 ? $" / {FormatBytes(p.TotalBytes)}" : "")
                    + $"  ↕ {FormatRate(p.SpeedBytesPerSecond)}"
                    + $"  ETA {etaStr}"
                    + (p.Errors > 0 ? $"  ✕ {p.Errors}" : "");
                lblStatus.ForeColor = Theme.TextSecondary;
            }
            else if (run.State == JobRunState.Queued)
            {
                bar.Visible = false;
                lblStatus.Text = "Waiting to start";
                lblStatus.ForeColor = Theme.TextTertiary;
            }
            else
            {
                bar.Visible = false;
                var outcome = run.Outcome ?? "";
                var err = run.LastError;
                lblStatus.Text = string.IsNullOrEmpty(err) ? outcome : $"{outcome} — {err}";
                lblStatus.ForeColor = run.State == JobRunState.Failed ? Theme.Critical : Theme.TextTertiary;
            }

            // Action visibility per state.
            btnCancel.Visible = run.State is JobRunState.Queued or JobRunState.Running;
            btnRetry.Visible  = run.State is JobRunState.Failed or JobRunState.Cancelled;
            btnRemove.Visible = run.IsTerminal;

            RepositionActions();
        }

        private void RepositionActions()
        {
            actions.PerformLayout();
            actions.Location = new Point(Width - actions.PreferredSize.Width - 10,
                                         (Height - actions.PreferredSize.Height) / 2);
            // Nudge the name / route / progress widths so they don't overrun the buttons.
            var maxLeftWidth = Math.Max(120, actions.Left - 96 - 8);
            lblName.Size = new Size(maxLeftWidth, lblName.Height);
            lblRoute.Size = new Size(maxLeftWidth, lblRoute.Height);
            var barWidth = Math.Max(80, (int)(maxLeftWidth * 0.65));
            bar.Size = new Size(barWidth, bar.Height);
            lblStatus.Location = new Point(bar.Right + 8, 40);
            lblStatus.Size = new Size(Math.Max(60, actions.Left - lblStatus.Left - 8), lblStatus.Height);
        }

        private static (Color bg, Color fg) ChipColoursFor(JobRunState state) => state switch
        {
            JobRunState.Queued    => (Theme.SurfaceSunken, Theme.TextSecondary),
            JobRunState.Running   => (Theme.AccentTint, Theme.Accent),
            JobRunState.Completed => (Theme.SurfaceSunken, Theme.TextSecondary),
            JobRunState.Failed    => (Theme.CriticalTint, Theme.Critical),
            JobRunState.Cancelled => (Theme.SurfaceSunken, Theme.TextTertiary),
            _ => (Theme.SurfaceSunken, Theme.TextTertiary),
        };

        private static Button MakeMiniButton(string text)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = false,
                Size = new Size(64, 26),
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

        private static string FormatSpec(string remote, string path)
        {
            var p = path ?? "";
            return string.IsNullOrEmpty(remote) ? p : $"{remote}:{p.TrimStart('/')}";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "?";
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            var i = 0;
            double v = bytes;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v >= 100 ? $"{v:F0} {u[i]}" : $"{v:F1} {u[i]}";
        }

        private static string FormatRate(double bytesPerSecond) => FormatBytes((long)Math.Max(0, bytesPerSecond)) + "/s";

        private static string FormatDuration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "—";
            var s = (int)seconds;
            if (s < 60) return $"{s}s";
            if (s < 3600) return $"{s / 60}m {s % 60}s";
            return $"{s / 3600}h {(s % 3600) / 60}m";
        }
    }
}
