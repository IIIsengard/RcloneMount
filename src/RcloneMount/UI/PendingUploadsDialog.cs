using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using RcloneMount.Services;

namespace RcloneMount.UI
{
    // Shown before an unmount that would cut off mount uploads rclone has not finished.
    // Result: OK = the uploads finished while waiting, Yes = go ahead anyway, Cancel = keep the mounts.
    // pending null = rclone did not answer; Wait keeps waiting until it answers with 0.
    internal sealed class PendingUploadsDialog : Form
    {
        private readonly Label lblMessage;
        private readonly Button btnWait;
        private readonly Func<Task<int?>> countPending;
        private readonly string action;
        private readonly System.Windows.Forms.Timer recount = new() { Interval = 2000 };
        private bool recounting;

        public PendingUploadsDialog(string action, int? pending, bool resumes, Func<Task<int?>> countPending)
        {
            this.action = action;
            this.countPending = countPending;
            Icon = AppIcon.Get();
            Text = "Uploads still pending";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 170);

            lblMessage = new Label
            {
                Location = new Point(20, 20),
                Size = new Size(ClientSize.Width - 40, 100),
                Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9.5f),
                Text = (pending is { } n
                           ? $"{Count(n)} pending: files written to the drive are not in the cloud yet.\n\n"
                           : "RcloneMount can't reach rclone to check whether uploads are still pending.\n\n") +
                       (resumes
                           ? "Going ahead now stops them. rclone uploads them the next time that drive is mounted."
                           : "Going ahead now stops them. These files will NOT be uploaded; they stay only in rclone's local cache."),
            };
            btnWait = new Button { Text = "Wait", AutoSize = true, MinimumSize = new Size(96, 30) };
            var btnAnyway = new Button { Text = $"{action} anyway", DialogResult = DialogResult.Yes, AutoSize = true, MinimumSize = new Size(96, 30) };
            var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(96, 30) };
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Bottom,
                Height = 48,
                Padding = new Padding(0, 8, 12, 0),
            };
            buttons.Controls.AddRange(new Control[] { btnCancel, btnAnyway, btnWait });

            btnWait.Click += (_, _) =>
            {
                btnWait.Enabled = false;
                lblMessage.Text = WaitingText(pending);
                recount.Start();
            };
            recount.Tick += async (_, _) => await RecountAsync();

            AcceptButton = btnWait;
            CancelButton = btnCancel;
            Shown += (_, _) => { Activate(); btnWait.Focus(); };
            Controls.Add(lblMessage);
            Controls.Add(buttons);
            DialogTheme.Apply(this);
            ConfirmDialog.StyleDestructive(btnAnyway);
        }

        private static string Count(int n) => n == 1 ? "1 upload" : $"{n} uploads";

        private string WaitingText(int? n) =>
            (n is { } c ? $"Waiting for {Count(c)} to finish…" : "Can't reach rclone right now. Still waiting…") +
            $"\n\n{action} goes ahead by itself when they are done.";

        private async Task RecountAsync()
        {
            if (recounting) return;
            recounting = true;
            try
            {
                var n = await countPending();
                if (IsDisposed || !Visible) return;
                if (UploadsGuard.Decide(n, interactive: true) == UploadsGuardStep.Proceed) DialogResult = DialogResult.OK;
                else lblMessage.Text = WaitingText(n);
            }
            finally { recounting = false; }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            recount.Dispose();
            base.OnFormClosed(e);
        }
    }
}
