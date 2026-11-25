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
        private readonly IReadOnlyList<Mount> otherMounts;
        private readonly ThemedDropDown cbRemote;
        private readonly TextBox txtRemotePath;
        private readonly ThemedDropDown cbDriveLetter;
        private readonly ThemedDropDown cbVfsMode;
        private readonly TextBox txtExtraFlags;
        private readonly CheckBox chkAutoMount;
        private readonly Button btnOk;
        private readonly Button btnCancel;

        public Mount? Result { get; private set; }

        public AddMountForm(IReadOnlyList<RcloneRemote> remotes, Mount? existing = null, IEnumerable<Mount>? allMounts = null)
        {
            this.remotes = remotes;
            // Everything except the mount we're editing — those letters are the ones
            // we must NOT reoffer, or we'd let the user create two mounts fighting for
            // the same drive letter and racing on startup.
            this.otherMounts = (allMounts ?? Array.Empty<Mount>())
                .Where(m => existing is null || !ReferenceEquals(m, existing))
                .ToList();
            Icon = AppIcon.Get();
            Text = existing is null ? "Add Mount" : "Edit Mount";
            ClientSize = new Size(460, 300);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var lblRemote = new Label { Text = "Remote:", Location = new Point(12, 18), AutoSize = true };
            cbRemote = new ThemedDropDown { Location = new Point(140, 15), Width = 300 };
            foreach (var r in remotes)
                cbRemote.AddItem($"{r.Name} ({ProviderVisuals.FriendlyType(r.Type)})");

            var lblRemotePath = new Label { Text = "Remote sub-path:", Location = new Point(12, 55), AutoSize = true };
            txtRemotePath = new TextBox { Location = new Point(140, 52), Width = 300, Text = existing?.RemotePath ?? "" };

            var lblDrive = new Label { Text = "Drive letter:", Location = new Point(12, 92), AutoSize = true };
            cbDriveLetter = new ThemedDropDown { Location = new Point(140, 89), Width = 80 };
            foreach (var letter in AvailableDriveLetters())
                cbDriveLetter.AddItem(letter + ":");

            var lblVfs = new Label { Text = "VFS cache mode:", Location = new Point(12, 129), AutoSize = true };
            cbVfsMode = new ThemedDropDown { Location = new Point(140, 126), Width = 150 };
            cbVfsMode.AddItems(new object[] { "off", "minimal", "writes", "full" });
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

            DialogTheme.Apply(this);
            RoundedButtons.Apply(this);

            if (existing is not null)
            {
                var idx = -1;
                for (var i = 0; i < remotes.Count; i++)
                    if (remotes[i].Name == existing.RemoteName) { idx = i; break; }
                if (idx >= 0) cbRemote.SelectedIndex = idx;

                var driveIndex = cbDriveLetter.Items.IndexOf(existing.DriveLetter!);
                if (driveIndex < 0 && !string.IsNullOrEmpty(existing.DriveLetter))
                {
                    cbDriveLetter.AddItem(existing.DriveLetter);
                    cbDriveLetter.SelectedItem = existing.DriveLetter;
                }
                else if (driveIndex >= 0) cbDriveLetter.SelectedIndex = driveIndex;
            }
            else
            {
                if (cbRemote.Items.Count > 0) cbRemote.SelectedIndex = 0;
                if (cbDriveLetter.Items.Count > 0) cbDriveLetter.SelectedIndex = 0;
            }

            ActiveControl = txtRemotePath;
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

            var chosenLetter = cbDriveLetter.SelectedItem!.ToString()!;
            var chosenChar = char.ToUpperInvariant(chosenLetter[0]);
            var clash = otherMounts.FirstOrDefault(m =>
                !string.IsNullOrWhiteSpace(m.DriveLetter)
                && char.ToUpperInvariant(m.DriveLetter[0]) == chosenChar);
            if (clash is not null)
            {
                MessageBox.Show(
                    $"Drive letter {chosenLetter} is already assigned to mount " +
                    $"'{clash.RemoteName}{(string.IsNullOrEmpty(clash.RemotePath) ? "" : ":" + clash.RemotePath)}'. " +
                    "Pick a different letter.",
                    "Drive letter in use", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        private IEnumerable<char> AvailableDriveLetters()
        {
            var used = new HashSet<char>(
                DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
            // Also reserve letters already assigned to OTHER configured mounts even
            // when they aren't currently mounted — otherwise two rows can end up
            // fighting for the same letter on next auto-mount.
            foreach (var m in otherMounts)
            {
                if (string.IsNullOrWhiteSpace(m.DriveLetter)) continue;
                used.Add(char.ToUpperInvariant(m.DriveLetter[0]));
            }
            for (var c = 'D'; c <= 'Z'; c++)
                if (!used.Contains(c)) yield return c;
        }
    }
}
