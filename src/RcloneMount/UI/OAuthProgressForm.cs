using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RcloneMount.Services;

namespace RcloneMount.UI
{
    public class OAuthProgressForm : Form
    {
        private readonly RcloneConfigService configService;
        private readonly string providerType;
        private readonly IReadOnlyDictionary<string, string>? authConfig;
        private readonly CancellationTokenSource cts = new();
        private readonly Label lblStatus;
        private readonly TextBox txtLog;
        private readonly Button btnCancel;

        public string? Token { get; private set; }
        public string? ErrorMessage { get; private set; }

        public OAuthProgressForm(RcloneConfigService configService, string providerType, IReadOnlyDictionary<string, string>? authConfig = null)
        {
            this.configService = configService;
            this.providerType = providerType;
            this.authConfig = authConfig;

            Icon = AppIcon.Get();
            Text = $"Authorizing {providerType}...";
            ClientSize = new Size(560, 320);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;

            lblStatus = new Label
            {
                Text = "Starting rclone authorize — a browser window will open. Complete the sign-in there.",
                Location = new Point(12, 12),
                Size = new Size(536, 40),
                AutoSize = false
            };

            txtLog = new TextBox
            {
                Location = new Point(12, 58),
                Size = new Size(536, 210),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font(FontFamily.GenericMonospace, 8.5f)
            };

            btnCancel = new Button
            {
                Text = "Cancel",
                Location = new Point(458, 278),
                Size = new Size(90, 30)
            };
            btnCancel.Click += (_, _) => { cts.Cancel(); btnCancel.Enabled = false; };

            Controls.Add(lblStatus);
            Controls.Add(txtLog);
            Controls.Add(btnCancel);

            DialogTheme.Apply(this);
            RoundedButtons.Apply(this);

            Shown += async (_, _) => await RunAsync();
        }

        private async Task RunAsync()
        {
            var progress = new Progress<string>(line =>
            {
                if (IsDisposed) return;
                txtLog.AppendText(line + Environment.NewLine);
            });

            try
            {
                Token = await configService.AuthorizeAsync(providerType, authConfig, progress, cts.Token);
                DialogResult = DialogResult.OK;
            }
            catch (OperationCanceledException)
            {
                DialogResult = DialogResult.Cancel;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                DialogResult = DialogResult.Abort;
            }
            finally
            {
                if (!IsDisposed) Close();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.None)
            {
                cts.Cancel();
                DialogResult = DialogResult.Cancel;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) cts.Dispose();
            base.Dispose(disposing);
        }
    }
}
