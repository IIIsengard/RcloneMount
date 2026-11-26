using System.Drawing;
using System.Windows.Forms;

namespace RcloneMount.UI
{
    // Asks for the encrypted rclone.conf password. Built like ConfirmDialog. Enter unlocks,
    // Esc cancels, Alt+P jumps to the field.
    internal sealed class ConfigPasswordDialog : Form
    {
        private readonly TextBox txtPassword;

        public string Password => txtPassword.Text;

        public ConfigPasswordDialog()
        {
            Icon = AppIcon.Get();
            Text = "Unlock rclone.conf";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            // Can open at login while the main window is still hidden in the tray.
            ShowInTaskbar = true;
            TopMost = true;
            ClientSize = new Size(440, 176);

            var font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9.5f);
            var lblMessage = new Label
            {
                Text = "Your rclone.conf is encrypted. Enter its password so mounts and scheduled jobs can run. " +
                       "RcloneMount keeps it protected for your Windows account.",
                Location = new Point(20, 18),
                Size = new Size(ClientSize.Width - 40, 54),
                Font = font,
                TabIndex = 0,
            };
            var lblPassword = new Label
            {
                Text = "&Password:",
                AutoSize = true,
                Location = new Point(20, 87),
                Font = font,
                TabIndex = 1,
            };
            txtPassword = new TextBox
            {
                UseSystemPasswordChar = true,
                Location = new Point(100, 84),
                Width = ClientSize.Width - 120,
                Font = font,
                TabIndex = 2,
                AccessibleName = "rclone.conf password",
            };
            var btnOk = new Button
            {
                Text = "Unlock",
                Size = new Size(96, 30),
                DialogResult = DialogResult.OK,
                Enabled = false,
                TabIndex = 3,
            };
            var btnCancel = new Button
            {
                Text = "Cancel",
                Size = new Size(96, 30),
                DialogResult = DialogResult.Cancel,
                TabIndex = 4,
            };
            btnCancel.Location = new Point(ClientSize.Width - btnCancel.Width - 16, ClientSize.Height - 46);
            btnOk.Location = new Point(btnCancel.Left - btnOk.Width - 8, ClientSize.Height - 46);
            txtPassword.TextChanged += (_, _) => btnOk.Enabled = txtPassword.TextLength > 0;

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            Controls.AddRange(new Control[] { lblMessage, lblPassword, txtPassword, btnOk, btnCancel });

            DialogTheme.Apply(this);
            btnOk.BackColor = Theme.Accent;
            btnOk.ForeColor = Theme.TextOnAccent;
            btnOk.FlatAppearance.BorderColor = Theme.Accent;
            btnOk.FlatAppearance.MouseOverBackColor = Theme.AccentHover;
            btnOk.FlatAppearance.MouseDownBackColor = Theme.AccentHover;
            ActiveControl = txtPassword;
        }
    }
}
