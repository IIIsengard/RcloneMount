using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;
using RcloneMount.UI;

namespace RcloneMount
{
    // Transfers tab: construction (empty label + owner-drawn ListBox), the drawer
    // for each row, the refresh pass wired to the stats poller, and the small
    // formatting helpers used only by the drawer (FormatBytes, FormatDuration).
    public partial class MainForm
    {
        // Saved-templates tab. Standalone since 2026-08-28 (v1.0.20) — used to
        // sit at the top of the Transfers tab but got cramped once QUEUE was added.
        // Templates change rarely, live transfers change constantly, so they earn
        // separate real estate.
        private void BuildJobsTab(Panel tabJobs)
        {
            tabJobs.Padding = new Padding(0);
            tabJobs.BackColor = Theme.SurfaceBase;

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Theme.SurfaceBase,
                Padding = new Padding(12),
            };
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));   // JOBS header + '+ New job'
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // jobs list (fills)

            // JOBS header + Add button.
            var jobsHeaderPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase };
            jobsHeaderLabel = new Label
            {
                Text = "JOBS",
                Font = new Font(Font.FontFamily, 8.25f, FontStyle.Bold),
                ForeColor = Theme.TextTertiary,
                Dock = DockStyle.Left,
                Width = 60,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 4, 0, 4),
            };
            btnAddJob = new Button
            {
                Text = "+ New job",
                Size = new Size(90, 26),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.SurfaceRaised,
                ForeColor = Theme.TextPrimary,
                Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f),
            };
            btnAddJob.FlatAppearance.BorderColor = Theme.BorderEmphasis;
            btnAddJob.FlatAppearance.BorderSize = 1;
            btnAddJob.Click += (_, _) => OpenNewJobDialog();
            jobsHeaderPanel.Controls.Add(jobsHeaderLabel);
            jobsHeaderPanel.Controls.Add(btnAddJob);
            jobsHeaderPanel.Resize += (_, _) =>
            {
                btnAddJob.Location = new Point(jobsHeaderPanel.Width - btnAddJob.Width - 2, 2);
            };

            var jobsHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase, BorderStyle = BorderStyle.FixedSingle };
            jobsEmpty = new Label
            {
                Text = "No saved jobs yet. Right-click a mount to build one and hit Save, or use \"+ New job\" above.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Theme.TextTertiary,
                Font = new Font(Font.FontFamily, 9.5f),
            };
            jobsFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Theme.SurfaceRaised,
                Padding = new Padding(0),
                Visible = false,
            };
            jobsFlow.Resize += (_, _) =>
            {
                var w = jobsFlow.ClientSize.Width;
                if (w <= 0) return;
                foreach (Control c in jobsFlow.Controls)
                    if (c is JobRow r) r.Width = w;
            };
            jobsHost.Controls.Add(jobsFlow);
            jobsHost.Controls.Add(jobsEmpty);

            grid.Controls.Add(jobsHeaderPanel, 0, 0);
            grid.Controls.Add(jobsHost,        0, 1);
            tabJobs.Controls.Add(grid);

            RefreshJobsList();
        }

        private void BuildTransfersTab(Panel tabTransfers)
        {
            tabTransfers.Padding = new Padding(0);
            tabTransfers.BackColor = Theme.SurfaceBase;

            // Two sections stacked: ACTIVE (every JobRun + every mount VFS read,
            // in one merged list with type chips) and HISTORY (completed rows on
            // disk). Saved-job templates live on their own Jobs tab; the old
            // MOUNT ACTIVITY panel folded into ACTIVE in v1.0.21 so users have
            // one place to see and cancel everything currently moving bytes.
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = Theme.SurfaceBase,
                Padding = new Padding(12),
            };
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // ACTIVE header
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 60f));   // active list
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // HISTORY header
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));   // history list

            // ACTIVE header — everything moving bytes right now. Both JobRuns and
            // mount VFS reads land here; per-row type chip disambiguates.
            activeHeader = new Label
            {
                Text = "ACTIVE",
                Font = new Font(Font.FontFamily, 8.25f, FontStyle.Bold),
                ForeColor = Theme.TextTertiary,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 4, 0, 4),
            };

            var queueHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase, BorderStyle = BorderStyle.FixedSingle };
            queueEmpty = new Label
            {
                Text = "Nothing moving bytes right now. Start a job or read a file from a mounted drive to see activity here.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Theme.TextTertiary,
                Font = new Font(Font.FontFamily, 9.5f),
            };
            queueFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Theme.SurfaceRaised,
                Padding = new Padding(0),
                Visible = false,
            };
            queueFlow.Resize += (_, _) =>
            {
                var w = queueFlow.ClientSize.Width;
                if (w <= 0) return;
                foreach (Control c in queueFlow.Controls)
                {
                    if (c is QueueRow qr) qr.Width = w;
                    else if (c is MountActivityRow mr) mr.Width = w;
                }
            };
            queueHost.Controls.Add(queueFlow);
            queueHost.Controls.Add(queueEmpty);

            historyHeader = new Label
            {
                Text = "HISTORY",
                Font = new Font(Font.FontFamily, 8.25f, FontStyle.Bold),
                ForeColor = Theme.TextTertiary,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 10, 0, 4),
            };

            var historyHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceBase };
            historyEmpty = new Label
            {
                Text = "No completed transfers yet — check back after a file finishes uploading or downloading.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Theme.TextTertiary,
                Font = new Font(Font.FontFamily, 9.5f)
            };
            historyList = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.SurfaceRaised,
                ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 40,
                IntegralHeight = false,
                Visible = false,
                FormattingEnabled = true,
            };
            // Rows are owner-drawn; this text is what screen readers announce.
            historyList.Format += (_, e) =>
            {
                if (e.ListItem is TransferHistoryEntry t) e.Value = DescribeHistoryEntry(t);
            };
            historyList.DrawItem += HistoryList_DrawItem;
            historyHost.Controls.Add(historyList);
            historyHost.Controls.Add(historyEmpty);

            grid.Controls.Add(activeHeader,    0, 0);
            grid.Controls.Add(queueHost,       0, 1);
            grid.Controls.Add(historyHeader,   0, 2);
            grid.Controls.Add(historyHost,     0, 3);

            tabTransfers.Controls.Add(grid);

            NativeTheme.ApplyDarkChromeIfDark(historyList);

            RefreshActiveList();

            // Prime the in-memory tail from disk. Fire-and-forget so tab construction
            // isn't blocked on I/O; RefreshTransferHistoryList paints once loaded.
            _ = LoadTransferHistoryFromDiskAsync();

            // Every appender (mount stats poller + RcloneJobService for transfer/
            // scheduled runs) now fires this event; the tab prepends the rows so
            // History updates live regardless of who wrote them. Without this,
            // job-produced rows landed on disk but the UI didn't notice until the
            // next app restart.
            TransferHistoryService.EntriesAppended += OnHistoryEntriesAppended;
            Disposed += (_, _) => TransferHistoryService.EntriesAppended -= OnHistoryEntriesAppended;
        }

        private void OnHistoryEntriesAppended(IReadOnlyList<TransferHistoryEntry> entries)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                // Newest first: prepend in reverse so the last entry written ends
                // up at index 0.
                for (var i = entries.Count - 1; i >= 0; i--)
                    transferHistory.Insert(0, entries[i]);
                if (transferHistory.Count > TransferHistoryDisplayCap)
                    transferHistory.RemoveRange(TransferHistoryDisplayCap, transferHistory.Count - TransferHistoryDisplayCap);
                RefreshTransferHistoryList();
            }));
        }

        // Rebuild the JOBS list from the in-memory `jobs` collection. Cheap — jobs
        // count is expected to stay small (dozens, not thousands) so we recreate
        // rows on every change rather than doing MountRow-style reconcile-in-place.
        private void RefreshJobsList()
        {
            if (jobsFlow is null || jobsEmpty is null) return;
            jobsFlow.SuspendLayout();
            foreach (Control c in jobsFlow.Controls) c.Dispose();
            jobsFlow.Controls.Clear();

            var any = jobs.Count > 0;
            jobsEmpty.Visible = !any;
            jobsFlow.Visible = any;

            if (any)
            {
                var w = jobsFlow.ClientSize.Width;
                foreach (var j in jobs)
                {
                    var row = new JobRow { Width = w > 0 ? w : 400, Margin = new Padding(0, 0, 0, 2) };
                    var captured = j;
                    row.RunRequested    += (_, _) => RunSavedJob(captured);
                    row.EditRequested   += (_, _) => EditSavedJob(captured);
                    row.DeleteRequested += async (_, _) => await DeleteSavedJobAsync(captured);
                    row.Bind(j);
                    jobsFlow.Controls.Add(row);
                }
            }
            jobsFlow.ResumeLayout();
        }

        private void OpenNewJobDialog()
        {
            if (jobService is null || remotes.Count == 0)
            {
                MessageBox.Show(this, "You need at least one remote before you can create a job.", "New job", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // Default source = first remote's root. User can pick another dest and
            // rename freely in the dialog.
            var seedSource = remotes[0].Name;
            var dlg = new TransferDialog(remotes, EnqueueJobRun, seedSource, "",
                preselectedOp: TransferOperation.Copy,
                saveCallback: UpsertJob,
                existingJob: null);
            dlg.Show(this);
        }

        private void RunSavedJob(TransferJob job)
        {
            if (jobService is null) return;
            var dlg = new TransferDialog(remotes, EnqueueJobRun, job.SourceRemote, job.SourcePath,
                preselectedOp: ParseOp(job.Operation),
                saveCallback: UpsertJob,
                existingJob: job);
            dlg.Show(this);
        }

        // Bridge from the dialog to the queue. Kept as a method (rather than
        // a lambda at the call site) so all three dialog entry points route
        // through one place — matters for future auditing / logging.
        private void EnqueueJobRun(JobSpec spec, string displayName, string jobId)
        {
            if (jobQueue is null) return;
            jobQueue.Enqueue(spec, displayName, jobId);
        }

        // Fired by JobQueueService whenever anything about the runs list
        // changes — a new enqueue, a state flip, a progress push, a removal.
        // Marshalled to the UI thread because the queue fires from whatever
        // background thread ran the completion.
        private void OnJobQueueChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    RefreshActiveList();
                    NotifyNewFailures();
                    UpdatePowerGuard();
                }));
            }
            catch (InvalidOperationException) { /* handle disposed mid-invoke */ }
        }

        // One tray balloon per failed run (scheduled, queued from a dialog, or retried), via
        // Notify so the Notifications toggle applies. Runs are marked seen even when the toggle
        // is off, so switching it on later does not replay old failures.
        private readonly HashSet<string> failureNotified = new(StringComparer.Ordinal);
        private void NotifyNewFailures()
        {
            if (jobQueue is null) return;
            foreach (var run in jobQueue.Snapshot())
            {
                if (run.State != Models.JobRunState.Failed || !failureNotified.Add(run.Id)) continue;
                var why = run.LastError ?? run.Outcome ?? "unknown error";
                if (why.Length > 150) why = why[..150] + "…";
                Notify("Transfer job failed", $"{run.DisplayName}: {why}", ToolTipIcon.Error, 8000);
            }
        }

        // After a job ends, make any mount of the remote it wrote to re-list that folder
        // (vfs/refresh), so new files show up without waiting for the long dir-cache to expire.
        // Runs on the UI thread because `mounts` is only touched there.
        private void OnJobRunFinished(JobRun run)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => _ = RefreshMountsAfterJobAsync(run))); }
            catch (InvalidOperationException) { /* handle disposed mid-invoke */ }
        }

        private async Task RefreshMountsAfterJobAsync(JobRun run)
        {
            if (mountService is null) return;
            foreach (var (mount, dir) in VfsRefresher.Targets(run.Spec, mounts.ToArray()))
            {
                if (mountService.GetRcPort(mount) is not int port) continue;
                try { await VfsRefresher.RefreshAsync(port, dir); }
                catch (Exception ex) { activityLog.Warn($"Could not refresh mount {mount.DriveLetter} after job '{run.DisplayName}': {ex.Message}"); }
            }
        }

        // Rebuild the ACTIVE strip from the JobQueueService snapshot + the
        // stats-poller's TransferInfo list. Called from both the queue's
        // RunsChanged event and each stats tick (~1s). Reconciles in place —
        // matches existing rows to their JobRun.Id / drive-letter key and
        // just re-binds; only new rows are constructed and only vanished
        // rows are disposed. Full-rebuild every second caused the ProgressBar
        // to flash to zero and re-fill on each tick.
        private void RefreshActiveList()
        {
            if (queueFlow is null || queueEmpty is null) return;
            var snap = jobQueue is null ? Array.Empty<Models.JobRun>() : jobQueue.Snapshot();
            var mountRows = lastTransfers ?? Array.Empty<TransferInfo>();

            // Build the desired ordered list of (key, payload). Key is stable
            // per logical row (JobRun.Id or drive+filename) so we can match to
            // the existing controls without full rebuild.
            var desired = new List<(string key, object payload)>();
            foreach (var r in snap)
                if (r.State == Models.JobRunState.Running) desired.Add(($"job:{r.Id}", r));
            foreach (var r in snap)
                if (r.State == Models.JobRunState.Queued)  desired.Add(($"job:{r.Id}", r));
            foreach (var t in mountRows)
                desired.Add(($"mnt:{t.DriveLetter}:{t.FileName}", t));
            var terminals = new List<Models.JobRun>();
            foreach (var r in snap) if (r.IsTerminal) terminals.Add(r);
            terminals.Sort((a, b) => (b.FinishedUtc ?? DateTime.MinValue).CompareTo(a.FinishedUtc ?? DateTime.MinValue));
            foreach (var r in terminals)
                desired.Add(($"job:{r.Id}", r));

            var any = desired.Count > 0;
            queueEmpty.Visible = !any;
            queueFlow.Visible = any;

            queueFlow.SuspendLayout();
            try
            {
                // Index existing controls by Tag key.
                var existing = new Dictionary<string, Control>(StringComparer.Ordinal);
                foreach (Control c in queueFlow.Controls)
                    if (c.Tag is string k) existing[k] = c;

                var w = queueFlow.ClientSize.Width;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < desired.Count; i++)
                {
                    var (key, payload) = desired[i];
                    seen.Add(key);
                    existing.TryGetValue(key, out var reuseCandidate);

                    if (payload is Models.JobRun jr)
                    {
                        QueueRow row;
                        if (reuseCandidate is QueueRow qr)
                            row = qr;
                        else
                        {
                            if (reuseCandidate is not null)
                            {
                                queueFlow.Controls.Remove(reuseCandidate);
                                reuseCandidate.Dispose();
                            }
                            row = BuildQueueRow(jr, w);
                            row.Tag = key;
                            queueFlow.Controls.Add(row);
                        }
                        if (w > 0 && row.Width != w) row.Width = w;
                        row.Bind(jr);
                        if (queueFlow.Controls.GetChildIndex(row) != i)
                            queueFlow.Controls.SetChildIndex(row, i);
                    }
                    else if (payload is TransferInfo ti)
                    {
                        MountActivityRow row;
                        if (reuseCandidate is MountActivityRow mr)
                            row = mr;
                        else
                        {
                            if (reuseCandidate is not null)
                            {
                                queueFlow.Controls.Remove(reuseCandidate);
                                reuseCandidate.Dispose();
                            }
                            row = BuildMountRow(ti, w);
                            row.Tag = key;
                            queueFlow.Controls.Add(row);
                        }
                        if (w > 0 && row.Width != w) row.Width = w;
                        row.Bind(ti);
                        if (queueFlow.Controls.GetChildIndex(row) != i)
                            queueFlow.Controls.SetChildIndex(row, i);
                    }
                }

                // Drop rows that no longer correspond to any desired entry.
                var toRemove = new List<Control>();
                foreach (Control c in queueFlow.Controls)
                    if (c.Tag is string k && !seen.Contains(k)) toRemove.Add(c);
                foreach (var c in toRemove)
                {
                    queueFlow.Controls.Remove(c);
                    c.Dispose();
                }
            }
            finally
            {
                queueFlow.ResumeLayout();
            }
        }

        private QueueRow BuildQueueRow(Models.JobRun r, int width)
        {
            var row = new QueueRow { Width = width > 0 ? width : 400, Margin = new Padding(0, 0, 0, 2) };
            var captured = r;
            row.CancelRequested += (_, _) => jobQueue?.Cancel(captured.Id);
            row.RetryRequested  += (_, _) => jobQueue?.Retry(captured.Id);
            row.RemoveRequested += (_, _) => jobQueue?.Remove(captured.Id);
            row.Bind(r);
            return row;
        }

        private MountActivityRow BuildMountRow(TransferInfo t, int width)
        {
            var row = new MountActivityRow { Width = width > 0 ? width : 400, Margin = new Padding(0, 0, 0, 2) };
            var captured = t;
            row.UnmountRequested    += (_, _) => UnmountByDriveLetter(captured.DriveLetter);
            row.CopyLocallyRequested += (_, _) => CopyMountFileLocally(captured);
            row.Bind(t);
            return row;
        }

        // Coarse "cancel" for mount VFS reads: unmount the whole drive letter
        // that owns this row. Confirms first — this kills every read/write on
        // that mount, not just the one the user clicked. Uses the graceful
        // UnmountAsync path (flushes VFS cache) rather than Stop-Process.
        private async void UnmountByDriveLetter(string driveLetter)
        {
            if (mountService is null || string.IsNullOrEmpty(driveLetter)) return;
            var mount = mounts.Find(m => string.Equals(m.DriveLetter, driveLetter, StringComparison.OrdinalIgnoreCase));
            if (mount is null) return;

            var res = MessageBox.Show(this,
                $"Unmount {driveLetter}?\n\n"
                + $"This stops EVERY file read/write currently flowing through {driveLetter}, "
                + $"not just this one row. Apps with files open on {driveLetter} will see I/O errors.",
                "Unmount drive", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (res != DialogResult.Yes) return;
            if (!await UploadsGuardAsync(new[] { mount }, "Unmount", interactive: true)) return;

            try
            {
                await mountService.UnmountAsync(mount);
                activityLog.Info($"Drive {driveLetter} unmounted (from ACTIVE row).");
            }
            catch (Exception ex)
            {
                activityLog.Error($"Unmount {driveLetter} failed: {ex.Message}");
                MessageBox.Show(this, "Unmount failed: " + ex.Message, "Unmount drive", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Convert a live mount VFS read into a real one-shot JobRun that
        // copies the file to a user-picked local folder. Useful when a file
        // keeps being re-fetched from the remote — cache it locally once.
        private void CopyMountFileLocally(TransferInfo t)
        {
            if (jobQueue is null || string.IsNullOrEmpty(t.FileName)) return;

            using var picker = new FolderBrowserDialog
            {
                Description = $"Pick a local folder to copy '{System.IO.Path.GetFileName(t.FileName)}' into",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
            };
            if (picker.ShowDialog(this) != DialogResult.OK) return;

            var spec = new JobSpec
            {
                Operation = TransferOperation.Copy,
                SourceRemote = t.RemoteName,
                SourcePath = t.FileName,
                DestRemote = "",
                DestPath = picker.SelectedPath,
                DryRun = false,
            };
            var name = $"Copy '{System.IO.Path.GetFileName(t.FileName)}' from {t.RemoteName}";
            jobQueue.Enqueue(spec, name);
        }

        private void EditSavedJob(TransferJob job) => RunSavedJob(job);

        private async System.Threading.Tasks.Task DeleteSavedJobAsync(TransferJob job)
        {
            if (ConfirmDialog.Show(this, "Delete job",
                    $"Delete saved job '{(string.IsNullOrWhiteSpace(job.Name) ? "(unnamed)" : job.Name)}'?") != DialogResult.Yes)
                return;
            jobs.RemoveAll(j => j.Id == job.Id);
            await SaveSettingsAsync();
            RefreshJobsList();
        }

        // Insert-or-replace by Id. Called by TransferDialog via saveCallback on an
        // explicit Save / Update click.
        private void UpsertJob(TransferJob incoming)
        {
            UpsertInto(jobs, incoming);
            _ = SaveSettingsAsync();
            RefreshJobsList();
        }

        internal static void UpsertInto(List<TransferJob> jobs, TransferJob incoming)
        {
            var idx = jobs.FindIndex(j => j.Id == incoming.Id);
            if (idx < 0) { jobs.Add(incoming); return; }
            // The dialog only knows the editable fields; keep the run history and the schedule
            // watermark, or editing a job would make it fire again today. A run still in progress
            // is recorded on the new object by CompleteRun.
            var old = jobs[idx];
            incoming.LastScheduledFireUtc = old.LastScheduledFireUtc;
            // A changed "every Nh" restarts its clock (AnchorIfNew, no run): counting from an old
            // daily stamp could start a sync or move within 30 s of saving.
            if (incoming.Schedule != old.Schedule && Schedule.TryParse(incoming.Schedule, out var s, out _) && s is { IsInterval: true })
                incoming.LastScheduledFireUtc = null;
            incoming.LastRunUtc = old.LastRunUtc;
            incoming.LastResultKind = old.LastResultKind;
            incoming.InFlight = old.InFlight;
            incoming.InFlightStartedUtc = old.InFlightStartedUtc;
            jobs[idx] = incoming;
        }

        private static TransferOperation ParseOp(string s) => s switch
        {
            "Sync" => TransferOperation.Sync,
            "Move" => TransferOperation.Move,
            "Check" => TransferOperation.Check,
            "CryptCheck" => TransferOperation.CryptCheck,
            "Bisync" => TransferOperation.Bisync,
            _ => TransferOperation.Copy,
        };

        private async Task LoadTransferHistoryFromDiskAsync()
        {
            var recent = await TransferHistoryService.ReadRecentAsync(TransferHistoryDisplayCap);
            if (IsDisposed) return;
            if (IsHandleCreated)
                BeginInvoke(new Action(() =>
                {
                    transferHistory.Clear();
                    transferHistory.AddRange(recent);
                    RefreshTransferHistoryList();
                }));
        }

        private void RefreshTransferHistoryList()
        {
            if (historyList is null || historyEmpty is null) return;
            var hasAny = transferHistory.Count > 0;
            historyEmpty.Visible = !hasAny;
            historyList.Visible = hasAny;
            historyList.BeginUpdate();
            historyList.Items.Clear();
            foreach (var e in transferHistory) historyList.Items.Add(e);
            historyList.EndUpdate();
        }

        private void HistoryList_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (sender is not ListBox list) return;
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            var bg = selected ? Theme.Accent : Theme.SurfaceRaised;
            var fg = selected ? Theme.TextOnAccent : Theme.TextPrimary;
            var subFg = selected ? Theme.TextOnAccent : Theme.TextSecondary;

            using (var b = new SolidBrush(bg))
                e.Graphics.FillRectangle(b, e.Bounds);
            using (var pen = new Pen(Theme.BorderSubtle))
                e.Graphics.DrawLine(pen, e.Bounds.Left + 6, e.Bounds.Bottom - 1, e.Bounds.Right - 6, e.Bounds.Bottom - 1);

            if (list.Items[e.Index] is not TransferHistoryEntry t) return;

            // Direction chip
            var arrow = t.Direction switch { "up" => "↑", "down" => "↓", _ => "•" };
            var chipRect = new Rectangle(e.Bounds.Left + 10, e.Bounds.Top + 6, 28, 24);
            var chipColour = string.IsNullOrEmpty(t.Error)
                ? (selected ? Theme.AccentHover : Theme.AccentTint)
                : Theme.CriticalTint;
            var chipFgColour = string.IsNullOrEmpty(t.Error)
                ? (selected ? Theme.TextOnAccent : Theme.Accent)
                : Theme.Critical;
            using (var chipBg = new SolidBrush(chipColour))
            using (var path = Drawing.RoundedRect(chipRect, 4))
                e.Graphics.FillPath(chipBg, path);
            using (var chipFont = new Font(FontFamily.GenericSansSerif, 11f, FontStyle.Bold))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var chipFg = new SolidBrush(chipFgColour))
                e.Graphics.DrawString(arrow, chipFont, chipFg, chipRect, fmt);

            // Title: filename
            var titleFont = new Font(Font.FontFamily, 9.5f, FontStyle.Bold);
            var titleRect = new Rectangle(chipRect.Right + 12, e.Bounds.Top + 3, e.Bounds.Width - chipRect.Width - 180, 18);
            var displayName = string.IsNullOrEmpty(t.FileName) ? "(unnamed)" : t.FileName;
            TextRenderer.DrawText(e.Graphics, displayName, titleFont, titleRect, fg,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
            titleFont.Dispose();

            // Meta line: [Job] · remote · size · duration · error?
            // Job stamp goes first so scanning the list for "which job did that?"
            // works without opening each row. Empty for mount-side rows.
            var pieces = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(t.Job))
                pieces.Add("Job: " + t.Job);
            pieces.Add($"{t.DriveLetter} {t.RemoteName}".Trim());
            pieces.Add(FormatBytes(t.SizeBytes));
            if (t.DurationMs is long d && d > 0)
                pieces.Add(FormatDuration(d / 1000.0));
            if (!string.IsNullOrEmpty(t.Error))
                pieces.Add("ERROR: " + t.Error);
            var meta = string.Join(" · ", pieces);
            var metaFont = new Font(Font.FontFamily, 8.25f);
            var metaRect = new Rectangle(chipRect.Right + 12, e.Bounds.Top + 21, e.Bounds.Width - chipRect.Width - 180, 16);
            var metaColour = string.IsNullOrEmpty(t.Error) ? subFg : Theme.Critical;
            TextRenderer.DrawText(e.Graphics, meta, metaFont, metaRect, metaColour,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
            metaFont.Dispose();

            // Timestamp on the right
            var tsText = FormatHistoryTime(t.CompletedAtUtc);
            var tsFont = new Font(Font.FontFamily, 8.25f);
            var tsRect = new Rectangle(e.Bounds.Right - 130, e.Bounds.Top + 12, 118, 18);
            TextRenderer.DrawText(e.Graphics, tsText, tsFont, tsRect, subFg,
                TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            tsFont.Dispose();
        }

        private static string DescribeHistoryEntry(TransferHistoryEntry t)
        {
            var name = string.IsNullOrEmpty(t.FileName) ? "(unnamed)" : t.FileName;
            var direction = t.Direction switch { "up" => "upload", "down" => "download", _ => "transfer" };
            var text = $"{name}, {direction}, {FormatBytes(t.SizeBytes)}, {FormatHistoryTime(t.CompletedAtUtc)}";
            return string.IsNullOrEmpty(t.Error) ? text : $"{text}, failed: {t.Error}";
        }

        private static string FormatHistoryTime(DateTime completedUtc)
        {
            var local = completedUtc.ToLocalTime();
            return local.ToString(local.Date == DateTime.Today ? "HH:mm" : "MMM d HH:mm");
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
