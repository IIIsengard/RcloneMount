using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.UI
{
    public class AddMountForm : Form
    {
        internal const string MaxSizeHint = "e.g. 10G, 500M, off. A bare number is KiB.";
        internal const string MinFreeHint = "e.g. 20G. A bare number is KiB.";
        internal const int HintBoxHeight = 52;

        private readonly IReadOnlyList<RcloneRemote> remotes;
        private readonly IReadOnlyList<Mount> otherMounts;
        private readonly ThemedDropDown cbRemote;
        private readonly TextBox txtRemotePath;
        private readonly ThemedDropDown cbDriveLetter;
        private readonly ThemedDropDown cbVfsMode;
        private readonly TextBox txtExtraFlags;
        private readonly TextBox txtCacheDir;
        private readonly TextBox txtCacheMax;
        private readonly TextBox txtCacheMinFree;
        private readonly Label lblError;
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
            ClientSize = new Size(460, 540);
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

            // Empty = no flag, so rclone's own default applies (uncapped cache in its default folder).
            var lblCacheDir = new Label { Text = "Cache folder:", Location = new Point(12, 203), AutoSize = true };
            txtCacheDir = new TextBox { Location = new Point(140, 200), Width = 215, Text = existing?.CacheDir ?? "" };
            var btnBrowse = new Button { Text = "Browse...", Location = new Point(362, 198), Size = new Size(78, 27) };
            btnBrowse.Click += (_, _) =>
            {
                using var dlg = new FolderBrowserDialog { Description = "Choose a folder for this mount's cache.", UseDescriptionForTitle = true };
                if (dlg.ShowDialog(this) == DialogResult.OK) txtCacheDir.Text = dlg.SelectedPath;
            };
            var lblCacheMax = new Label { Text = "Max cache size:", Location = new Point(12, 240), AutoSize = true };
            txtCacheMax = new TextBox { Location = new Point(140, 237), Width = 90, Text = existing?.VfsCacheMaxSize ?? "" };
            // Hints get their own wrapping box under the field so large text or DPI scaling cannot push them off the form.
            var lblCacheMaxHint = new Label { Text = MaxSizeHint, Location = new Point(140, 262), Size = new Size(308, HintBoxHeight), AutoSize = false };
            var lblCacheFree = new Label { Text = "Keep disk free:", Location = new Point(12, 325), AutoSize = true };
            txtCacheMinFree = new TextBox { Location = new Point(140, 322), Width = 90, Text = existing?.VfsCacheMinFreeSpace ?? "" };
            var lblCacheFreeHint = new Label { Text = MinFreeHint, Location = new Point(140, 347), Size = new Size(308, HintBoxHeight), AutoSize = false };
            lblError = new Label { UseMnemonic = false, Location = new Point(12, 436), Size = new Size(436, 50), Visible = false, AutoSize = false };
            btnBrowse.AccessibleName = "Browse for cache folder";

            chkAutoMount = new CheckBox { Text = "Auto-mount at startup", Location = new Point(140, 410), AutoSize = true, Checked = existing?.AutoMount ?? false };

            btnOk = new Button { Text = "OK", Location = new Point(250, 496), Size = new Size(90, 30), DialogResult = DialogResult.None };
            btnOk.Click += BtnOk_Click;
            btnCancel = new Button { Text = "Cancel", Location = new Point(350, 496), Size = new Size(90, 30), DialogResult = DialogResult.Cancel };

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[]
            {
                lblRemote, cbRemote,
                lblRemotePath, txtRemotePath,
                lblDrive, cbDriveLetter,
                lblVfs, cbVfsMode,
                lblExtra, txtExtraFlags,
                lblCacheDir, txtCacheDir, btnBrowse,
                lblCacheMax, txtCacheMax, lblCacheMaxHint,
                lblCacheFree, txtCacheMinFree, lblCacheFreeHint,
                lblError,
                chkAutoMount,
                btnOk, btnCancel
            });

            DialogTheme.Apply(this);
            RoundedButtons.Apply(this);
            // DialogTheme paints every label in the primary colour; recolour these after it ran.
            lblCacheMaxHint.ForeColor = lblCacheFreeHint.ForeColor = Theme.TextSecondary;
            lblError.ForeColor = Theme.Critical;

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

            // Check each field so focus and the screen-reader description go to the one that is wrong.
            var checks = new (TextBox box, string? error)[]
            {
                (txtCacheDir, VfsCacheLimits.ValidateDir(txtCacheDir.Text)),
                (txtCacheMax, VfsCacheLimits.ValidateSize(txtCacheMax.Text)),
                (txtCacheMinFree, VfsCacheLimits.ValidateSize(txtCacheMinFree.Text)),
            };
            foreach (var (box, error) in checks) box.AccessibleDescription = error ?? "";
            var bad = checks.FirstOrDefault(c => c.error is not null);
            lblError.Visible = bad.error is not null;
            if (bad.error is not null)
            {
                lblError.Text = bad.error;
                bad.box.Focus();
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
                CacheDir = txtCacheDir.Text.Trim(),
                VfsCacheMaxSize = txtCacheMax.Text.Trim(),
                VfsCacheMinFreeSpace = txtCacheMinFree.Text.Trim(),
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
