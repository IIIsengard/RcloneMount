using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using RcloneMount.Models;

namespace RcloneMount.UI
{
    public class AddMountForm : Form
    {
        private readonly IReadOnlyList<RcloneRemote> remotes;
        private readonly ComboBox cbRemote;
        private readonly TextBox txtRemotePath;
        private readonly ComboBox cbDriveLetter;
        private readonly ComboBox cbVfsMode;
        private readonly TextBox txtExtraFlags;
        private readonly CheckBox chkAutoMount;
        private readonly Button btnOk;
        private readonly Button btnCancel;

        public Mount? Result { get; private set; }

        public AddMountForm(IReadOnlyList<RcloneRemote> remotes, Mount? existing = null)
        {
            this.remotes = remotes;
            Icon = AppIcon.Get();
            Text = existing is null ? "Add Mount" : "Edit Mount";
            ClientSize = new Size(460, 300);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var lblRemote = new Label { Text = "Remote:", Location = new Point(12, 18), AutoSize = true };
            cbRemote = new ComboBox { Location = new Point(140, 15), Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var r in remotes)
                cbRemote.Items.Add($"{r.Name} ({r.Type})");

            var lblRemotePath = new Label { Text = "Remote sub-path:", Location = new Point(12, 55), AutoSize = true };
            txtRemotePath = new TextBox { Location = new Point(140, 52), Width = 300, Text = existing?.RemotePath ?? "" };

            var lblDrive = new Label { Text = "Drive letter:", Location = new Point(12, 92), AutoSize = true };
            cbDriveLetter = new ComboBox { Location = new Point(140, 89), Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var letter in AvailableDriveLetters())
                cbDriveLetter.Items.Add(letter + ":");

            var lblVfs = new Label { Text = "VFS cache mode:", Location = new Point(12, 129), AutoSize = true };
            cbVfsMode = new ComboBox { Location = new Point(140, 126), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            cbVfsMode.Items.AddRange(new object[] { "off", "minimal", "writes", "full" });
            cbVfsMode.SelectedItem = existing?.VfsCacheMode ?? "full";

            var lblExtra = new Label { Text = "Extra flags:", Location = new Point(12, 166), AutoSize = true };
            txtExtraFlags = new TextBox { Location = new Point(140, 163), Width = 300, Text = existing?.ExtraFlags ?? "" };

            chkAutoMount = new CheckBox { Text = "Auto-mount at startup", Location = new Point(140, 195), AutoSize = true, Checked = existing?.AutoMount ?? false };

            btnOk = new Button { Text = "OK", Location = new Point(250, 250), Size = new Size(90, 30), DialogResult = DialogResult.None };
            btnOk.Click += BtnOk_Click;
            btnCancel = new Button { Text = "Cancel", Location = new Point(350, 250), Size = new Size(90, 30), DialogResult = DialogResult.Cancel };

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[]
            {
                lblRemote, cbRemote,
                lblRemotePath, txtRemotePath,
                lblDrive, cbDriveLetter,
                lblVfs, cbVfsMode,
                lblExtra, txtExtraFlags,
                chkAutoMount,
                btnOk, btnCancel
            });

            if (existing is not null)
            {
                var idx = -1;
                for (var i = 0; i < remotes.Count; i++)
                    if (remotes[i].Name == existing.RemoteName) { idx = i; break; }
                if (idx >= 0) cbRemote.SelectedIndex = idx;

                var driveIndex = cbDriveLetter.Items.IndexOf(existing.DriveLetter);
                if (driveIndex < 0 && !string.IsNullOrEmpty(existing.DriveLetter))
                {
                    cbDriveLetter.Items.Add(existing.DriveLetter);
                    cbDriveLetter.SelectedItem = existing.DriveLetter;
                }
                else if (driveIndex >= 0) cbDriveLetter.SelectedIndex = driveIndex;
            }
            else
            {
                if (cbRemote.Items.Count > 0) cbRemote.SelectedIndex = 0;
                if (cbDriveLetter.Items.Count > 0) cbDriveLetter.SelectedIndex = 0;
            }
        }

        public void PresetRemoteName(string remoteName)
        {
            for (var i = 0; i < remotes.Count; i++)
            {
                if (string.Equals(remotes[i].Name, remoteName, StringComparison.Ordinal))
                {
                    cbRemote.SelectedIndex = i;
                    return;
                }
            }
        }

        private void BtnOk_Click(object? sender, EventArgs e)
        {
            if (cbRemote.SelectedIndex < 0 || cbRemote.SelectedIndex >= remotes.Count)
            {
                MessageBox.Show("Select a remote.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (cbDriveLetter.SelectedItem is null)
            {
                MessageBox.Show("Select a drive letter.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var remoteName = remotes[cbRemote.SelectedIndex].Name;

            Result = new Mount
            {
                RemoteName = remoteName,
                RemotePath = txtRemotePath.Text.Trim(),
                DriveLetter = cbDriveLetter.SelectedItem!.ToString()!,
                VfsCacheMode = cbVfsMode.SelectedItem?.ToString() ?? "full",
                ExtraFlags = txtExtraFlags.Text.Trim(),
                AutoMount = chkAutoMount.Checked
            };
            DialogResult = DialogResult.OK;
            Close();
        }

        private static IEnumerable<char> AvailableDriveLetters()
        {
            var used = new HashSet<char>(
                DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
            for (var c = 'D'; c <= 'Z'; c++)
                if (!used.Contains(c)) yield return c;
        }
    }
}
