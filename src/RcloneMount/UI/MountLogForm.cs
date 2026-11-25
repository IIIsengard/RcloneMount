using System.Drawing;
using System.Windows.Forms;

namespace RcloneMount.UI
{
    public class MountLogForm : Form
    {
        public MountLogForm(string title, string subject, string body)
        {
            Icon = AppIcon.Get();
            Text = title;
            ClientSize = new Size(760, 460);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(500, 300);

            var lblSubject = new Label
            {
                Text = subject,
                Dock = DockStyle.Top,
                Height = 32,
                Padding = new Padding(12, 8, 12, 0),
                Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold)
            };

            var txt = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9),
                Text = body
            };

            var btnCopy = new Button { Text = "Copy", Size = new Size(90, 30) };
            btnCopy.Click += (_, _) => { try { Clipboard.SetText(body); } catch { } };
            var btnClose = new Button { Text = "Close", Size = new Size(90, 30), DialogResult = DialogResult.OK };
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 46,
                Padding = new Padding(12, 8, 12, 8)
            };
            buttonPanel.Controls.Add(btnClose);
            buttonPanel.Controls.Add(btnCopy);

            Controls.Add(txt);
            Controls.Add(buttonPanel);
            Controls.Add(lblSubject);
            AcceptButton = btnClose;
            CancelButton = btnClose;
            DialogTheme.Apply(this);
            RoundedButtons.Apply(this);
        }
    }
}
