using System;
using System.Drawing;
using System.Windows.Forms;
using RcloneMount.Services;

namespace RcloneMount.UI
{
    // Companion to QueueRow for the merged ACTIVE panel. Renders one file-read
    // or file-write currently flowing through a mounted rclone drive letter.
    // Named MountActivityRow (in a file suffixed with .Active) to avoid
    // colliding with the mount-tab MountRow — same repo, similar names.
    //
    // Actions:
    // - Unmount drive : the coarse-but-honest cancel. Calls the mount service's
    //   graceful UnmountAsync which flushes the VFS cache and releases handles
    //   before falling back to a hard kill. Confirmed by the caller.
    // - Copy locally  : spawns a real JobRun (rclone copy) so the file is
    //   fetched once instead of being re-read from the remote every time
    //   something on the machine opens it.
    public sealed class MountActivityRow : UserControl
    {
        private const int RowHeight = 56;

        public TransferInfo Transfer { get; private set; } = new();

        public event EventHandler? UnmountRequested;
        public event EventHandler? CopyLocallyRequested;

        private readonly Label lblChip;
        private readonly Label lblName;
        private readonly Label lblRoute;
        private readonly Label lblStatus;
        private readonly ProgressBar bar;
        private readonly FlowLayoutPanel actions;
        private readonly Button btnUnmount;
        private readonly Button btnCopyLocal;

        public MountActivityRow()
        {
            DoubleBuffered = true;
            Height = RowHeight;
            BackColor = Theme.SurfaceRaised;

            lblChip = new Label
            {
                Location = new Point(10, 8),
                Size = new Size(78, 20),
                Font = new Font(Font.FontFamily, 7.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                BorderStyle = BorderStyle.FixedSingle,
                Text = "MOUNT",
                BackColor = Theme.SurfaceSunken,
                ForeColor = Theme.TextSecondary,
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
            };
            lblStatus = new Label
            {
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
            btnCopyLocal = MakeMiniButton("Copy locally", 96);
            btnUnmount   = MakeMiniButton("Unmount", 74);
            btnCopyLocal.Click += (_, _) => CopyLocallyRequested?.Invoke(this, EventArgs.Empty);
            btnUnmount.Click   += (_, _) => UnmountRequested?.Invoke(this, EventArgs.Empty);
            actions.Controls.Add(btnCopyLocal);
            actions.Controls.Add(btnUnmount);

            Controls.Add(lblChip);
            Controls.Add(lblName);
            Controls.Add(lblRoute);
            Controls.Add(bar);
            Controls.Add(lblStatus);
            Controls.Add(actions);
            RoundedButtons.Apply(this);
            Resize += (_, _) => RepositionActions();
            RepositionActions();
        }

        public void Bind(TransferInfo t)
        {
            Transfer = t;
            lblName.Text = string.IsNullOrEmpty(t.FileName) ? "(unnamed)" : t.FileName;
            lblRoute.Text = $"vfs  {t.RemoteName}:{t.FileName}  (drive {t.DriveLetter})";

            if (t.TotalBytes > 0)
            {
                var ratio = Math.Clamp((double)t.TransferredBytes / t.TotalBytes, 0, 1);
                bar.Style = ProgressBarStyle.Continuous;
                bar.Value = (int)Math.Round(ratio * bar.Maximum);
            }
            else
            {
                bar.Style = ProgressBarStyle.Marquee;
            }

            var etaStr = t.EtaSeconds is { } eta && double.IsFinite(eta) && eta > 0 ? FormatDuration(eta) : "—";
            lblStatus.Text =
                $"{FormatBytes(t.TransferredBytes)}"
                + (t.TotalBytes > 0 ? $" / {FormatBytes(t.TotalBytes)}" : "")
                + $"  ↕ {FormatRate(t.BytesPerSecond)}"
                + $"  ETA {etaStr}";
            lblStatus.ForeColor = Theme.TextSecondary;

            RepositionActions();
        }

        private void RepositionActions()
        {
            actions.PerformLayout();
            actions.Location = new Point(Width - actions.PreferredSize.Width - 10,
                                         (Height - actions.PreferredSize.Height) / 2);
            var maxLeftWidth = Math.Max(120, actions.Left - 96 - 8);
            lblName.Size = new Size(maxLeftWidth, lblName.Height);
            lblRoute.Size = new Size(maxLeftWidth, lblRoute.Height);
            var barWidth = Math.Max(80, (int)(maxLeftWidth * 0.65));
            bar.Size = new Size(barWidth, bar.Height);
            lblStatus.Location = new Point(bar.Right + 8, 40);
            lblStatus.Size = new Size(Math.Max(60, actions.Left - lblStatus.Left - 8), lblStatus.Height);
        }

        private static Button MakeMiniButton(string text, int width = 64)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = false,
                Size = new Size(width, 26),
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
