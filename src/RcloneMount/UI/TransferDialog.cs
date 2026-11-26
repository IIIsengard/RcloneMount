using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.UI
{
    // Setup-only dialog now. The user picks op / paths / options, optionally
    // Saves the job template, and clicks Start — which enqueues the run onto
    // the JobQueueService and closes. The old modeless in-dialog progress /
    // dry-run report pane is gone; live state is watched on the Transfers tab
    // QUEUE panel instead.
    public sealed class TransferDialog : Form
    {
        private IReadOnlyList<RcloneRemote> remotes; // replaced only by RenameRemote
        // (spec, displayName, jobId). Nullable — a caller that hides Start
        // (e.g. an edit-only flow) can pass null.
        private readonly Action<JobSpec, string, string>? enqueueCallback;

        // Setup controls.
        private readonly ThemedDropDown cbSourceRemote;
        private readonly TextBox txtSourcePath;
        private readonly Button btnSourceBrowse;
        private readonly Button btnDestBrowse;
        private readonly TextBox txtName;

        // Sentinel used in the source/dest remote dropdowns to mean "no remote —
        // path is a plain local filesystem path". rclone treats a path without a
        // "name:" prefix as local, so we emit just the path in that case.
        private const int LocalRemoteIndex = 0;
        private readonly ThemedDropDown cbOp;
        private readonly ThemedDropDown cbDestRemote;
        private readonly TextBox txtDestPath;
        private readonly CheckBox chkDryRun;
        private readonly CheckBox chkServerSide;
        private readonly CheckBox chkOneWay;
        private readonly CheckBox chkSizeOnly;
        private readonly Label lblConflict;
        private readonly ThemedDropDown cbConflict;
        private readonly CheckBox chkCheckAccess;
        private readonly Label lblMaxDelete;
        private readonly TextBox txtMaxDelete;
        private readonly Button btnFilters;
        private readonly Label lblFiltersSummary;
        private readonly Label lblBwLabel;
        private readonly TextBox txtBwLimit;
        private readonly Label lblScheduleLabel;
        private readonly TextBox txtSchedule;
        private readonly Label lblScheduleError;
        private readonly ToolTip scheduleTip = new();
        private readonly TextBox txtExtraFlags;
        private FilterRules filters = new();
        // Names this dialog was opened with; shown when one of them is not in the remote list.
        private readonly string seedSourceName;
        private readonly string seedDestName;
        private readonly bool remoteListRead; // false: a name not in `remotes` may still be configured
        private readonly Label lblMissingRemote;
        private readonly Button btnSave;
        private readonly Button btnStart;
        private readonly Button btnInitialise;
        private readonly Button btnCancel;

        // Optional persistence hook. When non-null, the Save button is enabled and
        // the caller (MainForm) writes the job into AppSettings.Jobs.
        private readonly Action<TransferJob>? saveCallback;
        // Optional identity carried over when the dialog was opened from an existing
        // saved job — used so Save updates the same record instead of creating a
        // duplicate, and so the enqueued run reports back with the right JobId.
        private string? editingJobId;
        internal string? JobId => editingJobId;
        // InputSnapshot() as of the last load or Save; a run is credited to the job only while it still matches.
        private object? savedInputs;

        public TransferDialog(
            IReadOnlyList<RcloneRemote> remotes,
            Action<JobSpec, string, string>? enqueueCallback,
            string sourceRemote,
            string sourcePath,
            TransferOperation preselectedOp = TransferOperation.Copy,
            Action<TransferJob>? saveCallback = null,
            TransferJob? existingJob = null,
            bool remoteListRead = true)
        {
            this.remotes = remotes;
            this.remoteListRead = remoteListRead;
            this.enqueueCallback = enqueueCallback;
            this.saveCallback = saveCallback;
            this.editingJobId = existingJob?.Id;

            Icon = AppIcon.Get();
            Text = existingJob is null ? "Transfer" : $"Transfer — {existingJob.Name}";
            ClientSize = new Size(560, 501);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            if (existingJob is not null)
            {
                filters = new FilterRules
                {
                    PatternsText = existingJob.FilterPatterns,
                    MinSize = existingJob.FilterMinSize,
                    MaxSize = existingJob.FilterMaxSize,
                    MinAge = existingJob.FilterMinAge,
                    MaxAge = existingJob.FilterMaxAge,
                };
            }

            var lblName = new Label { Text = "Name:", Location = new Point(16, 22), AutoSize = true, Font = BoldFont() };
            txtName = new TextBox
            {
                Location = new Point(140, 19),
                Width = 400,
                Text = existingJob?.Name ?? "",
                PlaceholderText = "Optional — required if you want to Save this job",
            };

            var lblSource = new Label { Text = "Source:", Location = new Point(16, 55), AutoSize = true, Font = BoldFont() };
            cbSourceRemote = new ThemedDropDown { Location = new Point(140, 52), Width = 200 };
            cbSourceRemote.AddItem("(Local disk)");
            foreach (var r in remotes)
                cbSourceRemote.AddItem(RemoteItem(r));
            seedSourceName = existingJob?.SourceRemote ?? sourceRemote ?? "";
            cbSourceRemote.SelectedIndex = IndexOfRemote(seedSourceName);

            var lblSourcePath = new Label { Text = "Source path:", Location = new Point(16, 91), AutoSize = true };
            txtSourcePath = new TextBox
            {
                Location = new Point(140, 88),
                Width = 366,
                Text = existingJob?.SourcePath ?? sourcePath ?? "",
            };
            btnSourceBrowse = new Button { Text = "…", Location = new Point(510, 87), Size = new Size(30, 24), Visible = false };
            btnSourceBrowse.Click += (_, _) => PickLocalFolder(txtSourcePath);

            var lblOp = new Label { Text = "Operation:", Location = new Point(16, 127), AutoSize = true, Font = BoldFont() };
            cbOp = new ThemedDropDown { Location = new Point(140, 124), Width = 260 };
            cbOp.AddItems(new object[]
            {
                "Copy — non-destructive",
                "Sync — mirror source to dest",
                "Move — copy then delete source",
                "Check — verify source vs dest (read-only)",
                "Cryptcheck — verify crypt vs plain (read-only)",
                "Bisync — two-way sync (needs initialise)",
            });
            cbOp.SelectedIndex = existingJob?.Operation switch
            {
                "Sync" => 1,
                "Move" => 2,
                "Check" => 3,
                "CryptCheck" => 4,
                "Bisync" => 5,
                "Copy" => 0,
                _ => preselectedOp switch
                {
                    TransferOperation.Sync => 1,
                    TransferOperation.Move => 2,
                    TransferOperation.Check => 3,
                    TransferOperation.CryptCheck => 4,
                    TransferOperation.Bisync => 5,
                    _ => 0,
                },
            };

            var lblDest = new Label { Text = "Destination:", Location = new Point(16, 165), AutoSize = true, Font = BoldFont() };
            cbDestRemote = new ThemedDropDown { Location = new Point(140, 162), Width = 200 };
            cbDestRemote.AddItem("(Local disk)");
            foreach (var r in remotes)
                cbDestRemote.AddItem(RemoteItem(r));
            seedDestName = existingJob?.DestRemote ?? "";
            cbDestRemote.SelectedIndex = existingJob is not null ? IndexOfRemote(seedDestName)
                : remotes.Count > 0 ? 1 : LocalRemoteIndex;

            var lblDestPath = new Label { Text = "Dest path:", Location = new Point(16, 201), AutoSize = true };
            txtDestPath = new TextBox { Location = new Point(140, 198), Width = 366, Text = existingJob?.DestPath ?? "" };
            btnDestBrowse = new Button { Text = "…", Location = new Point(510, 197), Size = new Size(30, 24), Visible = false };
            btnDestBrowse.Click += (_, _) => PickLocalFolder(txtDestPath);

            chkDryRun = new CheckBox { Text = "Dry-run (log what would happen, don't touch files)", Location = new Point(140, 234), AutoSize = true, Checked = existingJob?.DryRun ?? true };
            chkServerSide = new CheckBox { Text = "Try server-side transfer across remotes (--server-side-across-configs)", Location = new Point(140, 259), AutoSize = true, Checked = existingJob?.ServerSideAcrossConfigs ?? false };
            chkOneWay   = new CheckBox { Text = "One-way (ignore files present only on destination)", Location = new Point(140, 234), AutoSize = true, Checked = existingJob?.OneWay ?? false, Visible = false };
            chkSizeOnly = new CheckBox { Text = "Size only (skip hashing — faster, less strict)", Location = new Point(140, 259), AutoSize = true, Checked = existingJob?.SizeOnly ?? false, Visible = false };

            lblConflict = new Label { Text = "Conflict resolve:", Location = new Point(140, 262), AutoSize = true, Visible = false };
            cbConflict = new ThemedDropDown { Location = new Point(250, 259), Width = 130, Visible = false };
            cbConflict.AddItems(new object[] { "none", "newer", "older", "larger", "smaller" });
            cbConflict.SelectedIndex = (existingJob?.ConflictResolve ?? "") switch
            {
                "newer" => 1, "older" => 2, "larger" => 3, "smaller" => 4, _ => 0,
            };
            chkCheckAccess = new CheckBox { Text = "Require RCLONE_TEST marker on both sides (--check-access)", Location = new Point(140, 234), AutoSize = true, Checked = existingJob?.CheckAccess ?? false, Visible = false };
            lblMaxDelete = new Label { Text = "Max delete %:", Location = new Point(395, 262), AutoSize = true, Visible = false };
            txtMaxDelete = new TextBox
            {
                Location = new Point(485, 259),
                Width = 55,
                Text = (existingJob?.MaxDeletePct ?? 0) > 0 ? existingJob!.MaxDeletePct.ToString() : "",
                PlaceholderText = "50",
                Visible = false,
            };

            lblFiltersSummary = new Label
            {
                UseMnemonic = false,
                Text = filters.Summarise(),
                Location = new Point(115, 291),
                Size = new Size(430, 18),
                AutoEllipsis = true,
                ForeColor = Theme.TextSecondary,
            };
            btnFilters = new Button { Text = "Filters…", Location = new Point(16, 286), Size = new Size(90, 26) };
            btnFilters.Click += (_, _) =>
            {
                using var dlg = new FilterEditorDialog(filters);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    filters = dlg.Result;
                    lblFiltersSummary.Text = filters.Summarise();
                }
            };

            lblBwLabel = new Label { Text = "Bandwidth:", Location = new Point(16, 324), AutoSize = true };
            txtBwLimit = new TextBox
            {
                Location = new Point(95, 321),
                Width = 130,
                Text = existingJob?.BwLimit ?? "",
                PlaceholderText = "e.g. 10M",
            };
            lblScheduleLabel = new Label { Text = "Schedule:", Location = new Point(245, 324), AutoSize = true };
            txtSchedule = new TextBox
            {
                Location = new Point(315, 321),
                Width = 225,
                Text = existingJob?.Schedule ?? "",
                PlaceholderText = "daily 03:00 · mon,wed 21:30 · every 6h",
            };
            scheduleTip.SetToolTip(txtSchedule,
                "Local time: " + Schedule.Syntax + ". Leave empty to run manually only. The app must be running when the time arrives.");
            lblScheduleError = new Label
            {
                // Full dialog width so the format list is never ellipsised; ends at y 357, where the Extra flags box starts.
                Location = new Point(16, 344), Size = new Size(524, 13), AutoEllipsis = true, UseMnemonic = false,
                Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 7.5f),
            };
            txtSchedule.TextChanged += (_, _) => ShowScheduleError();
            FormClosed += (_, _) => scheduleTip.Dispose();
            var lblExtraFlags = new Label { Text = "Extra rclone flags:", Location = new Point(16, 360), AutoSize = true };
            txtExtraFlags = new TextBox
            {
                Location = new Point(140, 357),
                Width = 400,
                Text = existingJob?.ExtraFlags ?? "",
                PlaceholderText = "e.g. --fast-list --transfers 8",
            };

            // Two lines per missing side; the ellipsis tooltip covers longer names.
            lblMissingRemote = new Label { Location = new Point(16, 384), Size = new Size(524, 68), AutoEllipsis = true, UseMnemonic = false };

            btnSave = new Button
            {
                Text = existingJob is null ? "Save" : "Update",
                Location = new Point(16, 456),
                Size = new Size(90, 30),
                Visible = saveCallback is not null,
            };
            btnSave.Click += (_, _) => SaveJob();
            btnStart = new Button { Text = "Start", Location = new Point(360, 456), Size = new Size(90, 30) };
            btnStart.Click += (_, _) => StartAndClose(resync: false);
            btnInitialise = new Button
            {
                Text = "Initialise (resync)",
                Location = new Point(225, 456),
                Size = new Size(130, 30),
                Visible = false,
            };
            btnInitialise.Click += (_, _) => StartAndClose(resync: true);
            btnCancel = new Button { Text = "Close", Location = new Point(460, 456), Size = new Size(90, 30) };
            btnCancel.Click += (_, _) => Close();
            AcceptButton = btnStart;

            Controls.AddRange(new Control[]
            {
                lblName, txtName,
                lblSource, cbSourceRemote,
                lblSourcePath, txtSourcePath, btnSourceBrowse,
                lblOp, cbOp,
                lblDest, cbDestRemote,
                lblDestPath, txtDestPath, btnDestBrowse,
                chkDryRun, chkServerSide,
                chkOneWay, chkSizeOnly,
                chkCheckAccess, lblConflict, cbConflict, lblMaxDelete, txtMaxDelete,
                btnFilters, lblFiltersSummary,
                lblBwLabel, txtBwLimit, lblScheduleLabel, txtSchedule, lblScheduleError,
                lblExtraFlags, txtExtraFlags,
                lblMissingRemote,
                btnSave, btnInitialise, btnStart, btnCancel,
            });

            DialogTheme.Apply(this);
            RoundedButtons.Apply(this);

            cbOp.SelectedIndexChanged += (_, _) => SyncOptionVisibility();
            cbSourceRemote.SelectedIndexChanged += (_, _) => SyncOptionVisibility();
            cbDestRemote.SelectedIndexChanged += (_, _) => SyncOptionVisibility();
            SyncOptionVisibility();
            ShowScheduleError();

            if (existingJob is not null) savedInputs = InputSnapshot();
        }

        private static string RemoteItem(RcloneRemote r) => $"{r.Name} ({ProviderVisuals.FriendlyType(r.Type)})";

        // The app renamed a remote while this window is open: the selections stay, their name changes.
        internal void RenameRemote(string oldName, string newName)
        {
            remotes = RcloneRemote.Renamed(remotes, oldName, newName);
            for (var i = 0; i < remotes.Count; i++)
                cbSourceRemote.Items[i + 1] = cbDestRemote.Items[i + 1] = RemoteItem(remotes[i]);
            cbSourceRemote.Invalidate();
            cbDestRemote.Invalidate();
        }

        // -1 (nothing selected) for a name that is not in the list: never substitute another remote.
        private int IndexOfRemote(string name)
        {
            if (string.IsNullOrEmpty(name)) return LocalRemoteIndex;
            for (var i = 0; i < remotes.Count; i++)
                if (string.Equals(remotes[i].Name, name, StringComparison.Ordinal)) return i + 1;
            return -1;
        }

        // Says which remote is missing and blocks Start and Save until another one is picked.
        private void ShowMissingRemotes()
        {
            var source = MissingRemoteNote(cbSourceRemote, "Source", seedSourceName);
            var dest = MissingRemoteNote(cbDestRemote, "Destination", seedDestName);
            lblMissingRemote.ForeColor = Theme.Critical;
            lblMissingRemote.Text = (source + "\n" + dest).Trim();
            var picked = source.Length == 0 && dest.Length == 0;
            btnStart.Enabled = btnInitialise.Enabled = picked;
            btnSave.Enabled = picked && saveCallback is not null;
        }

        private string MissingRemoteNote(ThemedDropDown cb, string side, string name)
        {
            var note = cb.SelectedIndex >= 0 ? ""
                : remoteListRead ? $"{side} remote \"{name}\" is not in your rclone config. Pick another one."
                : $"{side} remote \"{name}\": the remote list could not be read. Click Refresh in the main window, then close and reopen this window.";
            cb.AccessibleDescription = note;
            return note;
        }

        // Every input that changes what rclone does. Name, schedule and the resync button do not.
        private object InputSnapshot() => (
            (cbOp.SelectedIndex, cbSourceRemote.SelectedIndex, txtSourcePath.Text, cbDestRemote.SelectedIndex, txtDestPath.Text),
            (chkDryRun.Checked, chkServerSide.Checked, chkOneWay.Checked, chkSizeOnly.Checked),
            (cbConflict.SelectedIndex, chkCheckAccess.Checked, txtMaxDelete.Text, txtBwLimit.Text, txtExtraFlags.Text),
            (filters.PatternsText, filters.MinSize, filters.MaxSize, filters.MinAge, filters.MaxAge));

        // Inline error under the Schedule box; set after DialogTheme.Apply so the colour sticks.
        private string? ShowScheduleError()
        {
            Schedule.TryParse(txtSchedule.Text, out _, out var error);
            lblScheduleError.ForeColor = Theme.Critical;
            lblScheduleError.Text = error ?? "";
            txtSchedule.AccessibleDescription = error ?? "";
            return error;
        }

        private static readonly HashSet<int> CheckOpIndexes = new() { 3, 4 };
        private const int BisyncOpIndex = 5;

        private void SyncOptionVisibility()
        {
            var isCheck = CheckOpIndexes.Contains(cbOp.SelectedIndex);
            var isBisync = cbOp.SelectedIndex == BisyncOpIndex;
            var isTransfer = !isCheck && !isBisync;
            var srcLocal = cbSourceRemote.SelectedIndex == LocalRemoteIndex;
            var dstLocal = cbDestRemote.SelectedIndex == LocalRemoteIndex;

            btnSourceBrowse.Visible = srcLocal;
            btnDestBrowse.Visible = dstLocal;

            chkDryRun.Visible = isTransfer || isBisync;
            chkServerSide.Visible = isTransfer && !srcLocal && !dstLocal;
            chkOneWay.Visible = isCheck;
            chkSizeOnly.Visible = isCheck;
            chkCheckAccess.Visible = isBisync;
            lblConflict.Visible = isBisync;
            cbConflict.Visible = isBisync;
            lblMaxDelete.Visible = isBisync;
            txtMaxDelete.Visible = isBisync;
            btnInitialise.Visible = isBisync;
            ShowMissingRemotes();
        }

        // Build the JobSpec from the current form state, hand it to the queue,
        // close. Validation errors keep the dialog open. Destructive-op warnings
        // still gate the Start so a stray click doesn't queue a Sync mirror.
        private void StartAndClose(bool resync)
        {
            if (enqueueCallback is null) return;

            var sourceRemote = ResolveRemoteName(cbSourceRemote);
            var destRemote = ResolveRemoteName(cbDestRemote);
            var sourcePath = txtSourcePath.Text ?? "";
            if (sourceRemote is null)
            {
                MessageBox.Show(this, "Pick a source remote first.", "Transfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (destRemote is null)
            {
                MessageBox.Show(this, "Pick a destination remote first.", "Transfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (sourceRemote.Length == 0 && string.IsNullOrWhiteSpace(sourcePath))
            {
                MessageBox.Show(this, "Enter a local source path.", "Transfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (destRemote.Length == 0 && string.IsNullOrWhiteSpace(txtDestPath.Text))
            {
                MessageBox.Show(this, "Enter a local destination path.", "Transfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var op = cbOp.SelectedIndex switch
            {
                1 => TransferOperation.Sync,
                2 => TransferOperation.Move,
                3 => TransferOperation.Check,
                4 => TransferOperation.CryptCheck,
                5 => TransferOperation.Bisync,
                _ => TransferOperation.Copy,
            };
            var isCheck = op is TransferOperation.Check or TransferOperation.CryptCheck;
            var isBisync = op is TransferOperation.Bisync;
            resync = resync && isBisync;

            var srcDisplay = FormatSpec(sourceRemote, sourcePath);
            var dstDisplay = FormatSpec(destRemote, txtDestPath.Text ?? "");
            if (!isCheck && !isBisync && !chkDryRun.Checked)
            {
                var warn = op switch
                {
                    TransferOperation.Sync => $"Sync will DELETE files at {dstDisplay} that don't exist at the source.\n\nContinue?",
                    TransferOperation.Move => $"Move will DELETE files from the source ({srcDisplay}) after copying.\n\nContinue?",
                    _ => null,
                };
                if (warn is not null)
                {
                    var ok = MessageBox.Show(this, warn, "Destructive operation", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (ok != DialogResult.Yes) return;
                }
            }
            if (isBisync && resync && !chkDryRun.Checked)
            {
                var msg = $"Initialise will run bisync with --resync between:\n\n" +
                          $"  {srcDisplay}\n  {dstDisplay}\n\n" +
                          "This may DELETE files on either side depending on rclone's resync policy. " +
                          "Normally only needed the first time you bisync a pair, or after a broken state.\n\n" +
                          "Continue?";
                var ok = MessageBox.Show(this, msg, "Initialise bisync pair", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ok != DialogResult.Yes) return;
            }

            var (extraArgs, tempFilterFile) = filters.EmitArgs();
            extraArgs.AddRange(RcloneMountService.SplitFlags(txtExtraFlags.Text ?? ""));
            int.TryParse((txtMaxDelete.Text ?? "").Trim(), out var maxDeletePct);
            var conflict = cbConflict.SelectedIndex > 0 && cbConflict.SelectedIndex < 5
                ? new[] { "none", "newer", "older", "larger", "smaller" }[cbConflict.SelectedIndex]
                : "";
            var spec = new JobSpec
            {
                Operation = op,
                SourceRemote = sourceRemote,
                SourcePath = sourcePath,
                DestRemote = destRemote,
                DestPath = txtDestPath.Text ?? "",
                DryRun = !isCheck && chkDryRun.Checked,
                ServerSideAcrossConfigs = !isCheck && !isBisync && chkServerSide.Checked
                    && sourceRemote.Length > 0 && destRemote.Length > 0,
                OneWay = isCheck && chkOneWay.Checked,
                SizeOnly = isCheck && chkSizeOnly.Checked,
                ConflictResolve = isBisync ? conflict : "",
                CheckAccess = isBisync && chkCheckAccess.Checked,
                MaxDeletePct = isBisync ? maxDeletePct : 0,
                Resync = resync,
                BwLimit = txtBwLimit.Text ?? "",
                ExtraArgs = extraArgs,
                CleanupTempFile = tempFilterFile,
            };

            var displayName = (txtName.Text ?? "").Trim();
            if (displayName.Length == 0)
                displayName = op.ToString() + ": " + FormatSpec(sourceRemote, sourcePath) + " → " + FormatSpec(destRemote, txtDestPath.Text ?? "");

            enqueueCallback(spec, displayName, InputSnapshot().Equals(savedInputs) ? editingJobId ?? "" : "");
            Close();
        }

        private void SaveJob()
        {
            if (saveCallback is null) return;
            var name = (txtName.Text ?? "").Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, "Give the job a name first.", "Save job", MessageBoxButtons.OK, MessageBoxIcon.Information);
                txtName.Focus();
                return;
            }
            if (ShowScheduleError() is { } scheduleError)
            {
                MessageBox.Show(this, scheduleError + "\n\nFix the schedule or clear it to save this job.", "Save job", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtSchedule.Focus();
                return;
            }
            var saveSourceRemote = ResolveRemoteName(cbSourceRemote);
            var saveDestRemote = ResolveRemoteName(cbDestRemote);
            if (saveSourceRemote is null)
            {
                MessageBox.Show(this, "Pick a source remote first.", "Save job", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (saveDestRemote is null)
            {
                MessageBox.Show(this, "Pick a destination remote first.", "Save job", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var op = cbOp.SelectedIndex switch
            {
                1 => "Sync",
                2 => "Move",
                3 => "Check",
                4 => "CryptCheck",
                5 => "Bisync",
                _ => "Copy",
            };
            int.TryParse((txtMaxDelete.Text ?? "").Trim(), out var maxDeletePct);
            var conflict = cbConflict.SelectedIndex >= 0 && cbConflict.SelectedIndex < 5
                ? new[] { "none", "newer", "older", "larger", "smaller" }[cbConflict.SelectedIndex]
                : "";
            var job = new TransferJob
            {
                Id = editingJobId ?? Guid.NewGuid().ToString("N"),
                Name = name,
                Operation = op,
                SourceRemote = saveSourceRemote,
                SourcePath = txtSourcePath.Text ?? "",
                DestRemote = saveDestRemote,
                DestPath = txtDestPath.Text ?? "",
                DryRun = chkDryRun.Checked,
                ServerSideAcrossConfigs = chkServerSide.Checked,
                OneWay = chkOneWay.Checked,
                SizeOnly = chkSizeOnly.Checked,
                ConflictResolve = conflict,
                CheckAccess = chkCheckAccess.Checked,
                MaxDeletePct = maxDeletePct,
                BwLimit = (txtBwLimit.Text ?? "").Trim(),
                Schedule = (txtSchedule.Text ?? "").Trim(),
                ExtraFlags = (txtExtraFlags.Text ?? "").Trim(),
                FilterPatterns = filters.PatternsText,
                FilterMinSize = filters.MinSize,
                FilterMaxSize = filters.MaxSize,
                FilterMinAge = filters.MinAge,
                FilterMaxAge = filters.MaxAge,
            };
            saveCallback(job);
            editingJobId = job.Id; // a second Save updates this job, and Start reports back to it
            savedInputs = InputSnapshot();
            btnSave.Text = "Saved";
            var reenable = new System.Windows.Forms.Timer { Interval = 1200 };
            reenable.Tick += (_, _) =>
            {
                reenable.Stop();
                reenable.Dispose();
                if (!IsDisposed) btnSave.Text = editingJobId is null ? "Save" : "Update";
            };
            reenable.Start();
        }

        private string? ResolveRemoteName(ThemedDropDown cb)
        {
            var i = cb.SelectedIndex;
            if (i < 0) return null;
            if (i == LocalRemoteIndex) return "";
            var remoteIndex = i - 1;
            if (remoteIndex >= remotes.Count) return null;
            return remotes[remoteIndex].Name;
        }

        private static string FormatSpec(string remoteName, string path)
        {
            var p = path ?? "";
            return remoteName.Length == 0 ? p : $"{remoteName}:{p.TrimStart('/')}";
        }

        private void PickLocalFolder(TextBox target)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Pick a folder",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                InitialDirectory = System.IO.Directory.Exists(target.Text) ? target.Text : "",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                target.Text = dlg.SelectedPath;
        }

        private Font BoldFont() => new(Font.FontFamily, Font.Size, FontStyle.Bold);
    }
}
