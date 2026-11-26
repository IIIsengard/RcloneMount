using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;
using RcloneMount.UI;

namespace RcloneMount.Tests;

[Collection(nameof(SharedStatics))] // some actions save settings
public class UiTextTests
{
    [Fact]
    public void JobRow_LocalPathHasNoColon_AndAmpersandsShow() => Sta(() =>
    {
        using var row = new JobRow();
        row.Bind(new TransferJob
        {
            Name = "US & Canada", Operation = "Sync",
            SourcePath = @"D:\US & Canada 1", DestRemote = "US 1", DestPath = "/US & Canada 1",
        });

        Assert.Contains(All<Label>(row), l => l.Text == @"sync  D:\US & Canada 1  →  US 1:US & Canada 1");
        AssertAmpersandsShow(row, 2);
    });

    [Fact]
    public void ActiveRows_ShowAmpersands() => Sta(() =>
    {
        using var queued = new QueueRow();
        queued.Bind(new JobRun
        {
            DisplayName = "A & B", State = JobRunState.Failed, LastError = "x & y",
            Spec = new JobSpec { SourcePath = @"C:\A & B", DestRemote = "r", DestPath = "p" },
        });
        AssertAmpersandsShow(queued, 3);

        using var mounted = new MountActivityRow();
        mounted.Bind(new TransferInfo { FileName = "Tom & Jerry.mkv", RemoteName = "r", DriveLetter = "X:" });
        AssertAmpersandsShow(mounted, 2);
    });

    [Fact]
    public void Dialogs_ShowAmpersandsInUserText() => Sta(() =>
    {
        using var log = new MountLogForm("Log", "A & B at X:", "body");
        AssertAmpersandsShow(log, 1);

        using var confirm = (Form)Activator.CreateInstance(typeof(ConfirmDialog),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { "Delete job", "Delete saved job 'A & B'?", "Remove", "Cancel", true }, null)!;
        AssertAmpersandsShow(confirm, 1);
    });

    [Fact]
    public void SettingsAndAboutHeadings_ShowAmpersands() => Sta(() =>
    {
        var form = Shell();
        using var settings = new Panel();
        using var about = new Panel();
        Call(form, "BuildSettingsTab", settings);
        Call(form, "BuildAboutTab", about);

        AssertAmpersandsShow(settings, 2);
        AssertAmpersandsShow(about, 1);
    });

    [Fact]
    public void MountsTab_DetailHeaderShowsAmpersands_RailHasNoHorizontalScroll() => Sta(() =>
    {
        var form = Shell();
        using var host = Host(out var tab);
        Call(form, "BuildMountsTab", tab);
        tab.Visible = true;
        Set(form, "remotes", Enumerable.Range(1, 10)
            .Select(i => new RcloneRemote { Name = $"{i} US & Canada", Type = "mega" }).ToArray());
        Call(form, "RebuildRail");
        host.PerformLayout();

        var rail = (FlowLayoutPanel)Get(form, "railList")!;
        Assert.True(rail.VerticalScroll.Visible);
        Assert.False(rail.HorizontalScroll.Visible);
        Assert.False(((Label)Get(form, "detailTitleLabel")!).UseMnemonic);
    });

    [Fact]
    public void RailButtons_AreNamedAfterTheirAction_AndFitTheirGlyph() => Sta(() =>
    {
        var form = Shell();
        using var tab = new Panel();
        Call(form, "BuildMountsTab", tab);
        var terminal = (Button)Get(form, "railTerminalBtn")!;

        var names = terminal.Parent!.Controls.OfType<Button>().Select(b => b.AccessibleName).ToArray();

        Assert.Equal(new[]
        {
            "Add service", "Remove selected service", "Refresh service list",
            "Open rclone config (CLI)", "rclone terminal (run any rclone command)",
        }, names);
        // Narrower than this and the flat button wraps ">_" and paints only ">".
        Assert.True(terminal.Width >= terminal.GetPreferredSize(Size.Empty).Width, $"width {terminal.Width}");
    });

    [Fact]
    public void AboutCards_StayInsideTheTab() => Sta(() =>
    {
        var form = Shell();
        using var host = Host(out var tab);
        tab.AutoScroll = true;
        Call(form, "BuildAboutTab", tab);
        tab.Visible = true;
        host.PerformLayout();

        var cards = tab.Controls.OfType<Panel>().ToArray();
        Assert.Equal(6, cards.Length);
        Assert.All(cards, c => Assert.InRange(c.Right, 1000, tab.ClientSize.Width));
    });

    [Fact]
    public void TrayMenu_ShowsAmpersandsInRemoteNames() => Sta(() =>
    {
        var form = Shell();
        using var menu = new ContextMenuStrip();
        Set(form, "trayMenu", menu);
        Set(form, "mounts", new List<Mount> { new() { RemoteName = "A & B", DriveLetter = "" } });

        Call(form, "TrayMenu_Opening", null, new System.ComponentModel.CancelEventArgs());

        Assert.Contains(menu.Items.OfType<ToolStripMenuItem>(), i => i.Text!.StartsWith("A && B → "));
    });

    [Fact]
    public void TransferDialog_CheckBoxesFitInside() => Sta(() =>
    {
        using var dialog = new TransferDialog(new[] { new RcloneRemote { Name = "r", Type = "mega" } }, null, "r", "");

        var boxes = All<CheckBox>(dialog).ToArray();

        Assert.NotEmpty(boxes);
        // 16 px is the dialog's own side margin.
        Assert.All(boxes, b => Assert.True(b.Left + b.PreferredSize.Width <= dialog.ClientSize.Width - 16, b.Text));
    });

    // A new job saved from the dialog must stay one job, and Start must report back to it.
    [Fact]
    public void TransferDialog_NewJobSavedThenStarted_KeepsOneId() => Sta(() =>
    {
        var saved = new List<string>();
        var started = new List<string>();
        using var dialog = new TransferDialog(new[] { new RcloneRemote { Name = "r", Type = "mega" } },
            (_, _, id) => started.Add(id), "r", "", saveCallback: job => saved.Add(job.Id));
        object? Of(string field) => typeof(TransferDialog).GetField(field, Private)!.GetValue(dialog);
        ((TextBox)Of("txtName")!).Text = "nightly";
        void Invoke(string name, params object[] args) => typeof(TransferDialog).GetMethod(name, Private)!.Invoke(dialog, args);

        Invoke("SaveJob");
        Invoke("SaveJob");
        Invoke("StartAndClose", false); // dry-run is ticked by default, so no confirm box

        Assert.Equal(2, saved.Count);
        Assert.Equal(saved[0], saved[1]);
        Assert.Equal(new[] { saved[0] }, started);
    });

    // A run of an edited, unsaved dialog is not a run of the saved job.
    [Theory]
    [InlineData("cbOp")]
    [InlineData("cbSourceRemote")]
    [InlineData("txtSourcePath")]
    [InlineData("cbDestRemote")]
    [InlineData("txtDestPath")]
    [InlineData("chkDryRun")]
    [InlineData("chkServerSide")]
    [InlineData("chkOneWay")]
    [InlineData("chkSizeOnly")]
    [InlineData("cbConflict")]
    [InlineData("chkCheckAccess")]
    [InlineData("txtMaxDelete")]
    [InlineData("txtBwLimit")]
    [InlineData("txtExtraFlags")]
    [InlineData(nameof(FilterRules.PatternsText))]
    [InlineData(nameof(FilterRules.MinSize))]
    [InlineData(nameof(FilterRules.MaxSize))]
    [InlineData(nameof(FilterRules.MinAge))]
    [InlineData(nameof(FilterRules.MaxAge))]
    public void TransferDialog_ChangedButNotSaved_IsNotCreditedUntilSaved(string input) => Sta(() =>
    {
        var started = new List<string>();
        using var dialog = SavedJobDialog(SavedJob(), started);

        // Capitalised names are filter fields; the rest are the dialog's controls.
        switch (Of(dialog, char.IsUpper(input[0]) ? "filters" : input))
        {
            case ThemedDropDown drop: drop.SelectedIndex = input.EndsWith("Remote") ? 0 : 1; break; // 0 = local disk
            case CheckBox box: box.Checked = !box.Checked; break;
            case TextBox text: text.Text = "7"; break;
            case var filters: typeof(FilterRules).GetProperty(input)!.SetValue(filters, "7"); break;
        }

        Invoke(dialog, "StartAndClose", false); // dry-run stays ticked for Sync/Move, so no confirm box
        Invoke(dialog, "SaveJob");
        Invoke(dialog, "StartAndClose", false);

        Assert.Equal(new[] { "", "a" }, started);
    });

    [Fact]
    public void TransferDialog_OnlyNameAndScheduleChanged_IsStillCredited() => Sta(() =>
    {
        var started = new List<string>();
        using var dialog = SavedJobDialog(SavedJob(), started);
        ((TextBox)Of(dialog, "txtName")).Text = "renamed";
        ((TextBox)Of(dialog, "txtSchedule")).Text = "daily 03:00";

        Invoke(dialog, "StartAndClose", false);

        Assert.Equal(new[] { "a" }, started);
    });

    // A remote that no longer exists must never be swapped for another one.
    [Theory]
    [InlineData("gone", "r", "cbSourceRemote", "cbDestRemote")]
    [InlineData("r", "gone", "cbDestRemote", "cbSourceRemote")]
    public void TransferDialog_SavedRemoteMissing_BlocksUntilOneIsPicked(string source, string dest, string missing, string other) => Sta(() =>
    {
        var started = new List<string>();
        var job = SavedJob();
        job.SourceRemote = source;
        job.DestRemote = dest;
        using var dialog = SavedJobDialog(job, started);
        var drop = (ThemedDropDown)Of(dialog, missing);
        bool[] Enabled() => new[] { "btnStart", "btnSave", "btnInitialise" }.Select(b => ((Button)Of(dialog, b)).Enabled).ToArray();

        Assert.Equal(-1, drop.SelectedIndex);
        Assert.Equal(1, ((ThemedDropDown)Of(dialog, other)).SelectedIndex);
        Assert.Contains(All<Label>(dialog), l => l.Text.Contains("\"gone\""));
        Assert.Contains("\"gone\"", drop.AccessibleDescription);
        Assert.Equal(new[] { false, false, false }, Enabled());

        drop.SelectedIndex = 1;

        Assert.DoesNotContain(All<Label>(dialog), l => l.Text.Contains("\"gone\""));
        Assert.Equal("", drop.AccessibleDescription);
        Assert.Equal(new[] { true, true, true }, Enabled());
        Invoke(dialog, "StartAndClose", false); // picked, not saved: still not the saved job
        Invoke(dialog, "SaveJob");
        Invoke(dialog, "StartAndClose", false);
        Assert.Equal(new[] { "", "a" }, started);
    });

    [Fact]
    public void TransferDialog_SavedLocalDisk_IsSelected() => Sta(() =>
    {
        var job = SavedJob();
        job.SourceRemote = job.DestRemote = "";
        using var dialog = SavedJobDialog(job, new List<string>());

        Assert.Equal(0, ((ThemedDropDown)Of(dialog, "cbSourceRemote")).SelectedIndex);
        Assert.Equal(0, ((ThemedDropDown)Of(dialog, "cbDestRemote")).SelectedIndex);
        Assert.True(((Button)Of(dialog, "btnStart")).Enabled);
    });

    // E.g. "Copy from" on a mount whose remote was removed.
    [Fact]
    public void TransferDialog_SeededRemoteMissing_SelectsNothing() => Sta(() =>
    {
        using var dialog = new TransferDialog(new[] { new RcloneRemote { Name = "r", Type = "mega" } }, null, "gone", "s", saveCallback: _ => { });

        Assert.Equal(-1, ((ThemedDropDown)Of(dialog, "cbSourceRemote")).SelectedIndex);
        Assert.False(((Button)Of(dialog, "btnStart")).Enabled);
        Assert.False(((Button)Of(dialog, "btnSave")).Enabled);
    });

    // Two dialogs on one job would let the older one credit its run to settings saved by the newer one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)] // opened as a new job, then saved
    public void RunSavedJob_ReusesTheDialogAlreadyOpenForThatJob(bool openedAsNew) => Sta(() =>
    {
        var form = Shell();
        var remotes = new[] { new RcloneRemote { Name = "r", Type = "mega" } };
        Set(form, "remotes", remotes);
        Set(form, "jobService", new RcloneJobService("unused.exe"));
        var job = SavedJob();
        var open = openedAsNew
            ? new TransferDialog(remotes, null, "r", "s", saveCallback: saved => job = saved)
            : new TransferDialog(remotes, null, "r", "s", saveCallback: _ => { }, existingJob: job);
        try
        {
            open.Owner = form; // what Show(this) sets, without putting a window on screen
            if (openedAsNew)
            {
                ((TextBox)Of(open, "txtName")).Text = "nightly";
                Invoke(open, "SaveJob");
            }

            Call(form, "RunSavedJob", job);

            Assert.Same(open, Assert.Single(form.OwnedForms));
        }
        finally
        {
            foreach (var d in form.OwnedForms) d.Dispose();
            open.Dispose();
        }
    });

    // A run can leave the queue (finished-row cap) before the UI thread gets to it.
    [Fact]
    public void FinishedRunNoLongerInTheQueue_IsStillRecordedAndNotified_Once() => Sta(() =>
    {
        var form = Shell();
        var job = SavedJob();
        ((List<TransferJob>)Get(form, "jobs")!).Add(job);
        Set(form, "lastRunRecorded", new HashSet<string>()); // field initializers did not run
        var notified = new HashSet<string>();
        Set(form, "failureNotified", notified); // notifications stay off in the shell: no balloon
        var done = DateTime.UtcNow;
        var run = new JobRun
        {
            Spec = new JobSpec(), JobId = "a", State = JobRunState.Failed, Outcome = "Failed to start",
            StartedUtc = done.AddSeconds(-1), FinishedUtc = done,
        };
        _ = form.Handle;
        Call(form, "OnJobRunFinished", run);
        Assert.Null(job.LastRunUtc); // posted to the UI thread, not done on the caller's
        Application.DoEvents();
        Assert.Equal(done, job.LastRunUtc);
        Assert.Equal("Failed to start", job.LastResultKind);
        Assert.Equal(new[] { run.Id }, notified);

        job.LastRunUtc = null;
        Call(form, "OnJobRunFinished", run);
        Application.DoEvents();
        Assert.Null(job.LastRunUtc);
        typeof(Control).GetMethod("DestroyHandle", Private)!.Invoke(form, null);
    });

    [Theory]
    [InlineData(DialogResult.Yes)]
    [InlineData(DialogResult.No)]
    public void DeletingAJobWithItsDialogOpen_SaysSo_AndClosesItOnlyOnYes(DialogResult answer) => Sta(() =>
    {
        var form = Shell();
        var job = SavedJob();
        var jobs = (List<TransferJob>)Get(form, "jobs")!;
        jobs.Add(job);
        using var open = new TransferDialog(new[] { new RcloneRemote { Name = "r", Type = "mega" } }, null, "r", "s",
            saveCallback: _ => { }, existingJob: job);
        open.Owner = form;
        var asked = "";
        Set(form, "confirmJobDelete", new Func<string, DialogResult>(message => { asked = message; return answer; }));

        Call(form, "DeleteSavedJobAsync", job);

        Assert.Contains("window", asked);
        var deleted = answer == DialogResult.Yes;
        Assert.Equal(deleted, open.IsDisposed); // a closed dialog cannot press Update and add the job back
        Assert.Equal(deleted ? 0 : 1, jobs.Count);
    });

    [Fact]
    public void DeletingAJobWithNoDialogOpen_DoesNotMentionAWindow() => Sta(() =>
    {
        var form = Shell();
        var asked = "";
        Set(form, "confirmJobDelete", new Func<string, DialogResult>(message => { asked = message; return DialogResult.No; }));

        Call(form, "DeleteSavedJobAsync", SavedJob());

        Assert.Contains("nightly", asked);
        Assert.DoesNotContain("window", asked);
    });

    // Saved jobs, queued runs and open windows name remotes too: all must follow a rename.
    [Fact]
    public void RenamingARemote_MovesJobsQueuedRunsAndOpenWindowsToTheNewName() => Sta(() =>
    {
        var form = Shell();
        var remotes = new[] { new RcloneRemote { Name = "r", Type = "mega" }, new RcloneRemote { Name = "other", Type = "mega" } };
        Set(form, "remotes", remotes);
        Set(form, "configuredDriveLetters", new Dictionary<string, string>());
        var mount = new Mount { RemoteName = "r" };
        ((List<Mount>)Get(form, "mounts")!).Add(mount);
        var jobs = (List<TransferJob>)Get(form, "jobs")!;
        var job = SavedJob(); // r to r
        var destOnly = new TransferJob { Id = "b", SourceRemote = "", DestRemote = "r" };
        var unrelated = new TransferJob { Id = "c", SourceRemote = "other", DestRemote = "R" };
        jobs.AddRange(new[] { job, destOnly, unrelated });
        var queue = IdleQueue();
        Set(form, "jobQueue", queue);
        queue.Enqueue(new JobSpec { SourceRemote = "r", SourcePath = "s", DestRemote = "other", DryRun = true, BwLimit = "1M" }, "queued", "a");
        var started = new List<(JobSpec spec, string id)>();
        var saved = new List<TransferJob>();
        using var open = new TransferDialog(remotes, (spec, _, id) => started.Add((spec, id)), "r", "s",
            saveCallback: saved.Add, existingJob: job);
        open.Owner = form;

        Call(form, "ApplyRemoteRenameAsync", "r", "new");

        Assert.Equal("new", mount.RemoteName);
        Assert.Equal(("new", "new"), (job.SourceRemote, job.DestRemote));
        Assert.Equal(("", "new"), (destOnly.SourceRemote, destOnly.DestRemote));
        Assert.Equal(("other", "R"), (unrelated.SourceRemote, unrelated.DestRemote));
        var run = Assert.Single(queue.Snapshot()).Spec;
        Assert.Equal(("new", "other", "s", true, "1M"), (run.SourceRemote, run.DestRemote, run.SourcePath, run.DryRun, run.BwLimit));
        Assert.Equal(new[] { "new", "other" }, ((IReadOnlyList<RcloneRemote>)Get(form, "remotes")!).Select(x => x.Name));
        Assert.StartsWith("new (", ((ThemedDropDown)Of(open, "cbSourceRemote")).SelectedText);
        Invoke(open, "StartAndClose", false);
        Invoke(open, "SaveJob");
        Assert.Equal(("new", "new", "a"), (started[0].spec.SourceRemote, started[0].spec.DestRemote, started[0].id));
        Assert.Equal(("new", "new"), (saved[0].SourceRemote, saved[0].DestRemote));
    });

    // rclone has loaded the config already, so a running sync would go on deleting after the remote is gone.
    [Fact]
    public void DeletingARemote_CancelsTheTransfersThatReachIt_AlsoThroughWrappers() => Sta(() =>
    {
        var form = Shell();
        var crypt = new RcloneRemote { Name = "crypt", Type = "crypt", Parameters = { ["remote"] = "gone:enc" } };
        var outer = new RcloneRemote { Name = "outer", Type = "alias", Parameters = { ["remote"] = "crypt:" } };
        Set(form, "remotes", new[] { outer, crypt, new RcloneRemote { Name = "gone" }, new RcloneRemote { Name = "r" } });
        var queue = IdleQueue();
        Set(form, "jobQueue", queue);
        var running = queue.Enqueue(new JobSpec { Operation = TransferOperation.Sync, SourceRemote = "r", DestRemote = "gone" }, "running");
        using var cts = new CancellationTokenSource();
        running.State = JobRunState.Running;
        running.Cts = cts;
        var wrapped = queue.Enqueue(new JobSpec { SourceRemote = "outer" }, "wrapped");
        var other = queue.Enqueue(new JobSpec { SourceRemote = "r" }, "other");

        Call(form, "ApplyRemoteDeletedAsync", "gone");

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(JobRunState.Cancelled, wrapped.State);
        Assert.Equal(JobRunState.Queued, other.State);
    });

    [Fact]
    public void DeleteRemoteMessage_SaysTransfersWillBeCancelled_OnlyWhenThereAreSome()
    {
        var none = Array.Empty<TransferJob>();

        Assert.Contains("2 running or queued transfer(s) using it will be cancelled",
            MainForm.DeleteRemoteMessage("r", Array.Empty<Mount>(), none, activeTransfers: 2));
        Assert.DoesNotContain("cancelled", MainForm.DeleteRemoteMessage("r", Array.Empty<Mount>(), none));
    }

    [Fact]
    public void DeleteRemoteMessage_NamesTheSavedJobsThatUseIt()
    {
        var jobs = new List<TransferJob>
        {
            new() { Name = "from it", SourceRemote = "r" },
            new() { Name = "to it", DestRemote = "r" },
            new() { Name = "", DestRemote = "r" },
            new() { Name = "elsewhere", SourceRemote = "R", DestRemote = "other" },
        };

        var message = MainForm.DeleteRemoteMessage("r", Array.Empty<Mount>(), jobs);

        Assert.Contains("from it", message);
        Assert.Contains("to it", message);
        Assert.Contains("(unnamed)", message);
        Assert.Contains("will not run", message);
        Assert.DoesNotContain("elsewhere", message);
        Assert.DoesNotContain("job", MainForm.DeleteRemoteMessage("unused", Array.Empty<Mount>(), jobs));
    }

    [Fact]
    public void DeleteRemoteMessage_CapsALongJobList()
    {
        var jobs = Enumerable.Range(1, 25).Select(i => new TransferJob { Name = $"job-{i:00}", DestRemote = "r" }).ToList();

        var message = MainForm.DeleteRemoteMessage("r", Array.Empty<Mount>(), jobs);

        Assert.Contains("job-10", message);
        Assert.DoesNotContain("job-11", message);
        Assert.Contains("15 more", message);
    }

    // A list of job names must not be cut off at the bottom of the box.
    [Fact]
    public void ConfirmDialog_GrowsToFitItsMessage() => Sta(() =>
    {
        var message = string.Join("\n", Enumerable.Range(1, 14).Select(i => $"line {i}"));
        using var dialog = (Form)Activator.CreateInstance(typeof(ConfirmDialog), Private, null,
            new object[] { "title", message, "Remove", "Cancel", true }, null)!;

        var label = All<Label>(dialog).Single();

        Assert.True(label.Height >= label.GetPreferredSize(new Size(label.Width, 0)).Height, $"label {label.Height}");
        Assert.All(All<Button>(dialog), b => Assert.True(b.Top >= label.Bottom && b.Bottom <= dialog.ClientSize.Height, b.Text));
    });

    [Theory]
    [InlineData(DialogResult.Yes)]
    [InlineData(DialogResult.No)]
    public void DeletingAJob_SaysItsRunsWillBeCancelled_AndCancelsThemOnlyOnYes(DialogResult answer) => Sta(() =>
    {
        var form = Shell();
        var job = SavedJob();
        ((List<TransferJob>)Get(form, "jobs")!).Add(job);
        var queue = IdleQueue();
        Set(form, "jobQueue", queue);
        var running = queue.Enqueue(new JobSpec(), "running", "a");
        using var cts = new CancellationTokenSource();
        running.State = JobRunState.Running;
        running.Cts = cts;
        var queued = queue.Enqueue(new JobSpec(), "queued", "a");
        var otherJob = queue.Enqueue(new JobSpec(), "other job", "b");
        var adHoc = queue.Enqueue(new JobSpec(), "ad hoc");
        var asked = "";
        Set(form, "confirmJobDelete", new Func<string, DialogResult>(message => { asked = message; return answer; }));

        Call(form, "DeleteSavedJobAsync", job);

        Assert.Contains("cancelled", asked);
        var deleted = answer == DialogResult.Yes;
        Assert.Equal(deleted, cts.IsCancellationRequested);
        Assert.Equal(deleted ? JobRunState.Cancelled : JobRunState.Queued, queued.State);
        Assert.Equal(JobRunState.Queued, otherJob.State);
        Assert.Equal(JobRunState.Queued, adHoc.State);
    });

    // A run can start while the confirmation is on screen (the scheduler's timer still ticks).
    [Fact]
    public void DeletingAJob_AlsoCancelsARunQueuedWhileAsking() => Sta(() =>
    {
        var form = Shell();
        var job = SavedJob();
        ((List<TransferJob>)Get(form, "jobs")!).Add(job);
        var queue = IdleQueue();
        Set(form, "jobQueue", queue);
        JobRun? late = null;
        var asked = "";
        Set(form, "confirmJobDelete", new Func<string, DialogResult>(message =>
        {
            asked = message;
            late = queue.Enqueue(new JobSpec(), "scheduled", "a");
            return DialogResult.Yes;
        }));

        Call(form, "DeleteSavedJobAsync", job);

        Assert.DoesNotContain("cancelled", asked); // nothing was running when asked
        Assert.Equal(JobRunState.Cancelled, late!.State);
    });

    // A deleted remote's jobs must not reach a later remote of the same name until the user picks again.
    [Fact]
    public void DeletingARemote_StopsItsJobsEvenIfTheNameComesBack() => Sta(() =>
    {
        var form = Shell();
        var remotes = new[] { new RcloneRemote { Name = "gone", Type = "mega" }, new RcloneRemote { Name = "r", Type = "mega" } };
        Set(form, "remotes", remotes);
        Set(form, "jobService", new RcloneJobService(Path.Combine(TestIsolation.Dir, "no-rclone.exe")));
        var queue = IdleQueue();
        Set(form, "jobQueue", queue);
        Set(form, "scheduledInFlight", new HashSet<string>()); // field initializers did not run
        Set(form, "invalidScheduleWarned", new HashSet<string>());
        var job = SavedJob();
        job.SourceRemote = "gone";
        job.Schedule = "daily 00:00";
        job.LastScheduledFireUtc = DateTime.UtcNow.AddDays(-3); // due now
        job.InFlight = true;
        var other = new TransferJob { Id = "b", SourceRemote = "r", DestRemote = "" };
        ((List<TransferJob>)Get(form, "jobs")!).AddRange(new[] { job, other });
        queue.Enqueue(new JobSpec { SourceRemote = "gone", DestRemote = "r" }, "queued", "a");
        var started = new List<string>();
        using var open = new TransferDialog(remotes, (_, _, id) => started.Add(id), "gone", "s",
            saveCallback: _ => { }, existingJob: job);
        open.Owner = form;

        Call(form, "ApplyRemoteDeletedAsync", "gone");
        Call(form, "SchedulerTick");

        Assert.Equal("gone", job.DeletedRemote());
        Assert.Equal("daily 00:00", job.Schedule);
        Assert.False(job.InFlight); // not offered for resume
        Assert.Equal(("r", ""), (other.SourceRemote, other.DestRemote));
        Assert.Null(other.DeletedRemote());
        var queued = Assert.Single(queue.Snapshot()); // and the scheduler added nothing
        Assert.NotEqual("gone", queued.Spec.SourceRemote);
        // The open window: that side must be picked again, the other keeps its remote.
        Assert.Equal(-1, ((ThemedDropDown)Of(open, "cbSourceRemote")).SelectedIndex);
        Assert.StartsWith("r (", ((ThemedDropDown)Of(open, "cbDestRemote")).SelectedText);
        Assert.False(((Button)Of(open, "btnStart")).Enabled);
        Assert.False(((Button)Of(open, "btnSave")).Enabled);
        Assert.Contains("\"gone\" is no longer in the rclone configuration", ((Label)Of(open, "lblMissingRemote")).Text);

        // The name is created again: every way of running the saved job still refuses it.
        using var reopened = new TransferDialog(remotes, null, job.SourceRemote, "s", saveCallback: _ => { }, existingJob: job);
        Assert.Equal(-1, ((ThemedDropDown)Of(reopened, "cbSourceRemote")).SelectedIndex);
        Assert.False(((Button)Of(reopened, "btnStart")).Enabled);
        Assert.Contains("\"gone\" is no longer in the rclone configuration", ((Label)Of(reopened, "lblMissingRemote")).Text);
        var error = Assert.Throws<InvalidOperationException>(() => new RcloneJobService(Path.Combine(TestIsolation.Dir, "no-rclone.exe"))
            .RunAsync(MainForm.BuildSpecFromSaved(job), new Progress<JobProgress>(), CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains("is no longer in the rclone configuration", error.Message);

        using var row = new JobRow();
        row.Bind(job);
        Assert.Contains(All<Label>(row), l => l.Text.Contains("\"gone\" is no longer in the rclone configuration"));
        Assert.DoesNotContain(All<Label>(row), l => l.Text.Contains("next run"));

        // Picking a remote and saving brings it back.
        ((ThemedDropDown)Of(reopened, "cbSourceRemote")).SelectedIndex = 1;
        Assert.True(((Button)Of(reopened, "btnSave")).Enabled);
    });

    // Saving the repaired job must not "catch up" and sync at once.
    [Fact]
    public void PickingARemoteAgain_RestartsTheScheduleFromNow()
    {
        var old = DateTime.UtcNow.AddDays(-3);
        var stopped = new TransferJob { Id = "a", SourceRemote = "<deleted> gone", Schedule = "daily 00:00", LastScheduledFireUtc = old };
        var jobs = new List<TransferJob> { stopped };

        MainForm.UpsertInto(jobs, new TransferJob { Id = "a", SourceRemote = "r", Schedule = "daily 00:00" });

        Assert.True(jobs[0].LastScheduledFireUtc > DateTime.UtcNow.AddMinutes(-1));
        Assert.True(Schedule.NextRunOf(jobs[0], DateTimeOffset.Now) > DateTimeOffset.Now);
    }

    [Fact]
    public void RetryOfADeletedJobsRun_DoesNothing_AndTheButtonIsGone() => Sta(() =>
    {
        var form = Shell();
        var job = SavedJob();
        ((List<TransferJob>)Get(form, "jobs")!).Add(job);
        var queue = IdleQueue();
        Set(form, "jobQueue", queue);
        var finished = queue.Enqueue(new JobSpec(), "failed earlier", "a");
        finished.State = JobRunState.Failed;
        var waiting = queue.Enqueue(new JobSpec(), "waiting", "a");
        var other = queue.Enqueue(new JobSpec(), "other job", "b");
        other.State = JobRunState.Failed;
        Set(form, "confirmJobDelete", new Func<string, DialogResult>(_ => DialogResult.Yes));

        Call(form, "DeleteSavedJobAsync", job);

        Assert.Null(queue.Retry(finished.Id));
        Assert.Null(queue.Retry(waiting.Id));
        Assert.Equal(3, queue.Snapshot().Count);
        using var row = new QueueRow();
        Button Retry() => (Button)typeof(QueueRow).GetField("btnRetry", Private)!.GetValue(row)!;
        row.Bind(finished);
        Assert.False(Retry().Visible);
        row.Bind(other);
        Assert.True(Retry().Visible);
        Assert.NotNull(queue.Retry(other.Id));
    });

    [Fact]
    public void EditRemoteForm_LockedName_ListsTheRemotesToUpdateByHand() => Sta(() =>
    {
        using var form = new EditRemoteForm(new RcloneProvider { Name = "local" }, null, "old", Array.Empty<string>(),
            lockName: true, dependents: new[] { "sec", "pool" });

        var reason = All<Label>(form).Single(l => l.Text.Contains("rclone.conf is encrypted")).Text;

        Assert.Contains("sec, pool", reason);
        Assert.Contains("sec, pool", ((TextBox)typeof(EditRemoteForm).GetField("txtName", Private)!.GetValue(form)!).AccessibleDescription);
    });

    // Never starts anything, so queued runs stay queued.
    private static JobQueueService IdleQueue() => new(new RcloneJobService("unused.exe")) { MaxConcurrent = 0 };

    // "Not in your rclone config" is only true when the config was read.
    [Fact]
    public void TransferDialog_RemoteListUnread_DoesNotSayTheRemoteIsGone() => Sta(() =>
    {
        var form = Shell();
        string Note()
        {
            using var dialog = (TransferDialog)typeof(MainForm).GetMethod("NewTransferDialog", Private)!.Invoke(form,
                new object?[] { "gone", "s", TransferOperation.Copy, null, null })!;
            Assert.False(((Button)Of(dialog, "btnStart")).Enabled);
            return ((Label)Of(dialog, "lblMissingRemote")).Text;
        }

        var unread = Note();
        Set(form, "remoteListRead", true);
        var read = Note();

        Assert.Contains("\"gone\"", unread);
        Assert.Contains("could not be read", unread);
        Assert.DoesNotContain("not in your rclone config", unread);
        Assert.Contains("not in your rclone config", read);
    });

    [Fact]
    public void ThemedDropDown_TellsScreenReadersTheSelection() => Sta(() =>
    {
        using var drop = new ThemedDropDown();
        drop.AddItem("(Local disk)");

        Assert.Equal(AccessibleRole.ComboBox, drop.AccessibilityObject.Role);
        Assert.Equal("nothing selected", drop.AccessibilityObject.Value);
        drop.SelectedIndex = 0;
        Assert.Equal("(Local disk)", drop.AccessibilityObject.Value);
        drop.ClearItems();
        Assert.Equal("nothing selected", drop.AccessibilityObject.Value);
    });

    // Windows rclone reads "q:" as drive Q:, so such a name must not be saved.
    [Theory]
    [InlineData(null, "q", false, true)]   // new
    [InlineData("ab", "Q", false, true)]   // rename
    [InlineData("q", "q", false, true)]    // editing an old one: rename it
    [InlineData("q", "q", true, false)]    // name locked (encrypted config): other edits still save
    [InlineData(null, "qq", false, false)]
    [InlineData(null, "1", false, false)]
    public void EditRemoteForm_RejectsOneLetterNames(string? existing, string typed, bool locked, bool rejected) => Sta(() =>
    {
        using var form = new EditRemoteForm(new RcloneProvider { Name = "local" }, null, existing, Array.Empty<string>(), locked);
        form.PresetRemoteName(typed);

        var error = (string?)typeof(EditRemoteForm).GetMethod("NameError", Private)!.Invoke(form, null);

        if (rejected) Assert.Contains("drive", error);
        else Assert.Null(error);
    });

    private static TransferJob SavedJob() => new()
    {
        Id = "a", Name = "nightly", Operation = "Copy",
        SourceRemote = "r", SourcePath = "s", DestRemote = "r", DestPath = "d", DryRun = true,
    };

    private static TransferDialog SavedJobDialog(TransferJob job, List<string> started) =>
        new(new[] { new RcloneRemote { Name = "r", Type = "mega" } },
            (spec, _, id) =>
            {
                if (spec.CleanupTempFile is { } filterFile) File.Delete(filterFile);
                started.Add(id);
            },
            job.SourceRemote, job.SourcePath, saveCallback: _ => { }, existingJob: job);

    private static object Of(TransferDialog dialog, string field) =>
        typeof(TransferDialog).GetField(field, Private)!.GetValue(dialog)!;

    private static void Invoke(TransferDialog dialog, string name, params object[] args) =>
        typeof(TransferDialog).GetMethod(name, Private)!.Invoke(dialog, args);

    [Fact]
    public void HistoryItems_AreAnnouncedWithNameDirectionSizeAndTime() => Sta(() =>
    {
        var form = Shell();
        using var tab = new Panel();
        Call(form, "BuildTransfersTab", tab);
        var done = DateTime.Today.AddHours(14).AddMinutes(5).ToUniversalTime();
        var list = (ListBox)Get(form, "historyList")!;
        var history = (List<TransferHistoryEntry>)Get(form, "transferHistory")!;
        history.Add(new() { FileName = "a & b.zip", Direction = "up", SizeBytes = 1536, CompletedAtUtc = done });
        history.Add(new() { Direction = "down", SizeBytes = 0, CompletedAtUtc = done, Error = "quota" });
        Call(form, "RefreshTransferHistoryList");
        _ = list.Handle;

        Assert.Equal("a & b.zip, upload, 1.5 KB, 14:05", list.AccessibilityObject.GetChild(0)!.Name);
        Assert.Equal("(unnamed), download, 0.0 B, 14:05, failed: quota", list.GetItemText(list.Items[1]));
    });

    // A remote renamed or deleted outside the app must not leave its jobs free to reach a later one of that name.
    [Fact]
    public void Refresh_MarksJobsWhoseRemoteIsGone_AndSaysSoOnce() => Sta(() =>
    {
        var (form, log) = RemoteShell(Dump("r"));
        var queue = IdleQueue();
        Set(form, "jobQueue", queue);
        var job = SavedJob();
        (job.SourceRemote, job.Schedule, job.InFlight) = ("gone", "daily 03:00", true);
        var wrongCase = new TransferJob { Id = "b", Name = "upper", SourceRemote = "r,opt=1", DestRemote = "R" };
        var untouched = new[]
        {
            new TransferJob { Id = "c", SourceRemote = "", SourcePath = @"C:\x", DestRemote = ":local" },
            new TransferJob { Id = "d", SourceRemote = ":sftp,host=x", DestRemote = "r,opt=1" },
            new TransferJob { Id = "e", SourceRemote = "elsewhere", ExtraFlags = "--config other.conf" },
            new TransferJob { Id = "f", SourceRemote = "elsewhere2", ExtraFlags = "--config=other.conf" },
        };
        var before = untouched.Select(j => (j.SourceRemote, j.DestRemote)).ToArray();
        ((List<TransferJob>)Get(form, "jobs")!).AddRange(untouched.Prepend(wrongCase).Prepend(job));
        var queued = queue.Enqueue(new JobSpec { SourceRemote = "gone" }, "queued", "a");

        Call(form, "RefreshRemotesAsync");
        Call(form, "RefreshRemotesAsync");

        Assert.Equal("gone", job.DeletedRemote());
        Assert.Equal(("daily 03:00", false), (job.Schedule, job.InFlight));
        Assert.Equal(("r,opt=1", "R"), (wrongCase.SourceRemote, RcloneRemote.DeletedName(wrongCase.DestRemote)));
        Assert.Equal(before, untouched.Select(j => (j.SourceRemote, j.DestRemote)).ToArray());
        Assert.Equal(JobRunState.Cancelled, queued.State);
        var notice = Assert.Single(log, e => e.Level == ActivityLevel.Warn).Message;
        Assert.Contains("nightly (\"gone\")", notice);
        Assert.Contains("upper (\"R\")", notice);
    });

    // An unreadable or locked config says nothing; no remotes at all is more likely a wrong config path.
    [Theory]
    [InlineData("{}", "")]
    [InlineData("", "")]
    [InlineData("", "read tcp: failed")]
    [InlineData("", "unable to decrypt configuration")]
    [InlineData("not json", "")]
    public void Refresh_MarksNothing_WithoutAListOfRemotes(string stdout, string stderr) => Sta(() =>
    {
        var (form, log) = RemoteShell(stdout, stderr);
        Set(form, "unlockingConfig", true); // as if the password box were already up: none is shown here
        var job = SavedJob();
        ((List<TransferJob>)Get(form, "jobs")!).Add(job);

        Call(form, "RefreshRemotesAsync");

        Assert.Equal(("r", "r"), (job.SourceRemote, job.DestRemote));
        Assert.DoesNotContain(log, e => e.Level == ActivityLevel.Warn);
    });

    // A list read while the app renames, creates or deletes a remote can disagree with the jobs: it waits.
    [Fact]
    public void Refresh_WaitsForAnInAppRemoteChange_BeforeReading() => Sta(() =>
    {
        var (form, _) = RemoteShell(Dump("other"));
        var job = SavedJob();
        ((List<TransferJob>)Get(form, "jobs")!).Add(job);
        Set(form, "remoteWritesActive", 1);

        var refresh = Begin(form, "RefreshRemotesAsync");
        PumpFor(TimeSpan.FromMilliseconds(600));
        Assert.False(refresh.IsCompleted);
        Assert.Equal("r", job.SourceRemote);

        Set(form, "remoteWritesActive", 0);
        Finish(refresh);
        Assert.Equal("r", job.DeletedRemote());
    });

    // An older read that ends last knows less than a newer one: it may neither mark jobs nor bring its list back.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AnOlderReadThatFinishesLast_NeitherMarksNorReplaces(bool olderIsRunCheck, bool newerIsRunCheck) => Sta(() =>
    {
        var (form, log) = RemoteShell("");
        var (rclone, firstReadStarted, releaseFirstRead) = SlowFirstRclone(Dump("r"), Dump("r", "fresh"));
        Set(form, "configService", new RcloneConfigService(rclone));
        Task Read(bool runCheck) => runCheck
            ? Begin(form, "CheckRunRemotesAsync", new JobRun { Spec = new JobSpec { SourceRemote = "fresh" } })
            : Begin(form, "RefreshRemotesAsync");

        var older = Read(olderIsRunCheck);
        PumpUntil(firstReadStarted);
        var newer = Read(newerIsRunCheck);
        PumpUntil(() => newer.IsCompleted);
        Assert.False(older.IsCompleted, "the older read was meant to end last");
        var job = new TransferJob { Id = "n", SourceRemote = "fresh" };
        ((List<TransferJob>)Get(form, "jobs")!).Add(job);
        releaseFirstRead();
        Finish(older); // a run check for "fresh" must pass too
        newer.GetAwaiter().GetResult();

        Assert.Equal("fresh", job.SourceRemote);
        Assert.Equal(new[] { "r", "fresh" }, ((IReadOnlyList<RcloneRemote>)Get(form, "remotes")!).Select(r => r.Name));
        Assert.DoesNotContain(log, e => e.Level == ActivityLevel.Warn);
    });

    // A remote created (or re-created under a deleted name) while a list is being read is not in that list.
    [Fact]
    public void ARemoteCreatedWhileTheListIsRead_DoesNotGetItsNewJobMarked() => Sta(() =>
    {
        var (form, _) = RemoteShell("");
        var (rclone, firstReadStarted, releaseFirstRead) = SlowFirstRclone(Dump("r"), Dump("r", "fresh"));
        Set(form, "configService", new RcloneConfigService(rclone));

        var older = Begin(form, "RefreshRemotesAsync");
        PumpUntil(firstReadStarted);
        Call(form, "CreateRemoteAsync", "fresh", "local", new Dictionary<string, string>());
        var job = new TransferJob { Id = "n", SourceRemote = "fresh" };
        ((List<TransferJob>)Get(form, "jobs")!).Add(job);
        Assert.False(older.IsCompleted, "the read was meant to outlast the create");
        releaseFirstRead();
        Finish(older);

        Assert.Equal("fresh", job.SourceRemote);
    });

    // A job whose --config names another file is judged by that run, never by another job's check.
    [Theory]
    [InlineData("--config other.conf")]
    [InlineData("--config=other.conf --password-command=\"cmd /c echo x\"")]
    public void MarkingOneJob_LeavesOwnConfigJobsRunsAndWindowsAlone(string flags) => Sta(() =>
    {
        var (form, log) = RemoteShell(Dump("r"));
        var queue = IdleQueue();
        Set(form, "jobQueue", queue);
        var plain = new TransferJob { Id = "p", Name = "plain", SourceRemote = "gone" };
        var own = new TransferJob { Id = "o", Name = "separate", SourceRemote = "gone", ExtraFlags = flags, InFlight = true };
        ((List<TransferJob>)Get(form, "jobs")!).AddRange(new[] { plain, own });
        var ownArgs = RcloneMountService.SplitFlags(flags).ToList();
        var ownRunning = queue.Enqueue(new JobSpec { SourceRemote = "gone", ExtraArgs = ownArgs }, "own running", "o");
        using var cts = new CancellationTokenSource();
        ownRunning.State = JobRunState.Running;
        ownRunning.Cts = cts;
        var ownQueued = queue.Enqueue(new JobSpec { SourceRemote = "gone", ExtraArgs = ownArgs }, "own queued", "o");
        var plainQueued = queue.Enqueue(new JobSpec { SourceRemote = "gone" }, "plain queued", "p");
        using var open = new TransferDialog(new[] { new RcloneRemote { Name = "gone", Type = "mega" } }, null, "gone", "",
            saveCallback: _ => { }, existingJob: own);
        open.Owner = form;

        Call(form, "RefreshRemotesAsync");

        Assert.Equal("gone", plain.DeletedRemote());
        Assert.Equal(("gone", true), (own.SourceRemote, own.InFlight));
        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(JobRunState.Queued, ownQueued.State);
        Assert.Equal(("gone", "gone"), (ownRunning.Spec.SourceRemote, ownQueued.Spec.SourceRemote));
        Assert.Equal(JobRunState.Cancelled, plainQueued.State);
        Assert.StartsWith("gone (", ((ThemedDropDown)Of(open, "cbSourceRemote")).SelectedText);
        Assert.DoesNotContain("separate", Assert.Single(log, e => e.Level == ActivityLevel.Warn).Message);
    });

    // Flags that open the config the app reads: "{0}" is that file, "{1}" the same one written differently.
    public static TheoryData<string> SharedConfigFlags => new()
    {
        "--password-command \"cmd /c echo x\"",
        "--password-command=x",
        "--config \"{0}\"",
        "\"--config={1}\" --password-command=x",
        "--config other.conf --config \"{0}\"", // rclone takes the last one
    };

    private static string SharedFlags(string template)
    {
        var path = SettingsService.ResolveRcloneConfigPath();
        Assert.StartsWith(TestIsolation.Dir, path);
        var other = Path.GetRelativePath(Environment.CurrentDirectory, path).ToUpperInvariant();
        return string.Format(template, path, other);
    }

    [Theory]
    [MemberData(nameof(SharedConfigFlags))]
    public void AJobThatReadsTheAppsConfig_IsCheckedAndMarkedLikeAnyOther(string template) => Sta(() =>
    {
        var (form, log) = RemoteShell(Dump("r"));
        var flags = SharedFlags(template);
        var queue = IdleQueue();
        Set(form, "jobQueue", queue);
        var job = new TransferJob { Id = "s", Name = "shared", SourceRemote = "gone", ExtraFlags = flags };
        ((List<TransferJob>)Get(form, "jobs")!).Add(job);
        var spec = new JobSpec { SourceRemote = "gone", ExtraArgs = RcloneMountService.SplitFlags(flags).ToList() };
        var queued = queue.Enqueue(spec, "shared queued", "s");

        var error = Assert.Throws<InvalidOperationException>(() => RunCheck(form, spec with { SourceRemote = "unsaved" }));

        Assert.Contains("\"unsaved\" is not in", error.Message);
        Assert.Equal("gone", job.DeletedRemote()); // the check read the list, so it marked
        Assert.Equal(JobRunState.Cancelled, queued.State);
        Assert.Contains("shared", Assert.Single(log, e => e.Level == ActivityLevel.Warn).Message);
    });

    // The app cannot read the config, but the job brings its own way in: rclone decides, nothing is marked.
    [Theory]
    [MemberData(nameof(SharedConfigFlags))]
    public void AJobThatReadsTheAppsConfig_RunsWhenTheAppCannotReadIt(string template) => Sta(() =>
    {
        var (form, log) = RemoteShell("", "unable to decrypt configuration");
        Set(form, "unlockingConfig", true); // as if the password box were already up: none is shown here
        var flags = SharedFlags(template);
        var job = new TransferJob { Id = "s", SourceRemote = "gone", ExtraFlags = flags };
        ((List<TransferJob>)Get(form, "jobs")!).Add(job);
        var spec = new JobSpec { SourceRemote = "gone", ExtraArgs = RcloneMountService.SplitFlags(flags).ToList() };

        RunCheck(form, spec);
        Call(form, "RefreshRemotesAsync");
        Set(form, "configService", null!); // no rclone either: the run itself will say so
        RunCheck(form, spec);

        Assert.Equal("gone", job.SourceRemote);
        Assert.DoesNotContain(log, e => e.Level == ActivityLevel.Warn);
    });

    [Theory]
    [InlineData("--config other.conf")]
    [InlineData("--config=")]
    [InlineData("--config \"{0}\" --config other.conf")]
    public void AJobWhoseConfigIsAnotherFile_IsLeftToRclone(string template) => Sta(() =>
    {
        var (form, log) = RemoteShell(Dump("r"));
        var flags = SharedFlags(template);
        var job = new TransferJob { Id = "o", SourceRemote = "gone", ExtraFlags = flags };
        ((List<TransferJob>)Get(form, "jobs")!).Add(job);

        RunCheck(form, new JobSpec { SourceRemote = "gone", ExtraArgs = RcloneMountService.SplitFlags(flags).ToList() });
        Call(form, "RefreshRemotesAsync");

        Assert.Equal("gone", job.SourceRemote);
        Assert.DoesNotContain(log, e => e.Level == ActivityLevel.Warn);
    });

    // A kill that fails is said, and the caller is still not held by the process that stayed.
    [Fact]
    public async Task HungConfigDump_WhoseKillFails_IsLogged_AndStillGivesUp()
    {
        var rclone = FakeRclone("", "");
        File.WriteAllText(rclone, "@echo off\r\nping -n 21 127.0.0.1 >nul\r\n"); // ends by itself
        var warned = new List<string>();
        var service = new RcloneConfigService(rclone)
        {
            ListTimeout = TimeSpan.FromMilliseconds(500),
            Kill = _ => throw new System.ComponentModel.Win32Exception("Access is denied."),
            Warn = warned.Add,
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<TimeoutException>(service.ListRemotesAsync);

        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(15)); // the process runs for 20
        Assert.Contains("could not be stopped", error.Message);
        Assert.Contains("Access is denied.", Assert.Single(warned));
    }

    // The first list read waits for Release, then prints `first`; every later call prints `later` at once.
    private static (string Exe, Func<bool> FirstReadStarted, Action Release) SlowFirstRclone(string first, string later)
    {
        var exe = FakeRclone("", "");
        var dir = Path.GetDirectoryName(exe)!;
        File.WriteAllText(Path.Combine(dir, "first.txt"), first);
        File.WriteAllText(Path.Combine(dir, "later.txt"), later);
        File.WriteAllText(exe, "@echo off\r\nmove \"%~dp0first.txt\" \"%~dp0taken.txt\" >nul 2>&1 && goto slow\r\n"
            + "type \"%~dp0later.txt\"\r\nexit /b 0\r\n:slow\r\nif exist \"%~dp0release.txt\" goto done\r\n"
            + "ping -n 2 127.0.0.1 >nul\r\ngoto slow\r\n:done\r\ntype \"%~dp0taken.txt\"\r\n");
        return (exe, () => File.Exists(Path.Combine(dir, "taken.txt")),
            () => File.WriteAllText(Path.Combine(dir, "release.txt"), ""));
    }

    private static Task Begin(MainForm form, string name, params object?[] args)
    {
        var method = typeof(MainForm).GetMethod(name, Private)!;
        return (Task)method.Invoke(form, args)!;
    }

    private static void PumpFor(TimeSpan time)
    {
        var end = DateTime.UtcNow + time;
        PumpUntil(() => DateTime.UtcNow > end);
    }

    // The check just before rclone starts.
    [Theory]
    [InlineData("{}", "", "has no remotes")]
    [InlineData("", "boom", "boom")]
    [InlineData("", "unable to decrypt configuration", "click Refresh")]
    [InlineData(null, "", "rclone.exe was not found")]
    public void RunCheck_RefusesWithoutMarking_WhenTheRemotesCannotBeConfirmed(string? stdout, string stderr, string says) => Sta(() =>
    {
        var (form, _) = RemoteShell(stdout ?? "", stderr);
        if (stdout is null) Set(form, "configService", null!);
        var job = SavedJob();
        ((List<TransferJob>)Get(form, "jobs")!).Add(job);

        var error = Assert.Throws<InvalidOperationException>(() => RunCheck(form, new JobSpec { SourceRemote = "r" }));

        Assert.StartsWith("Not run", error.Message);
        Assert.Contains(says, error.Message);
        Assert.Equal("r", job.SourceRemote);
    });

    [Fact]
    public void RunCheck_PassesConfiguredRemotes_AndRefusesAMissingOneNoJobUses() => Sta(() =>
    {
        var (form, log) = RemoteShell(Dump("r"));

        RunCheck(form, new JobSpec { SourceRemote = "r", DestRemote = "r,opt=1" });
        var error = Assert.Throws<InvalidOperationException>(() => RunCheck(form, new JobSpec { DestRemote = "gone" }));

        Assert.Contains("\"gone\" is not in", error.Message);
        Assert.DoesNotContain(log, e => e.Level == ActivityLevel.Warn);
    });

    // Nothing in these reads the app's config, so a locked or missing one must not stop them.
    [Fact]
    public void RunCheck_NeedsNoRemoteList_ForRunsThatUseNoConfiguredRemote() => Sta(() =>
    {
        var form = Shell(); // no rclone at all

        RunCheck(form, new JobSpec { SourcePath = @"C:\a", DestPath = @"C:\b" });
        RunCheck(form, new JobSpec { SourceRemote = ":local", DestRemote = RcloneRemote.MarkDeleted("x") });
        RunCheck(form, new JobSpec { SourceRemote = "mine", ExtraArgs = new[] { "--config", "other.conf" } });
        WithEnvironment("RCLONE_CONFIG_FROM-ENV_TYPE", "local",
            () => RunCheck(form, new JobSpec { SourceRemote = "From-Env", DestRemote = "from-env,opt=1" }));
    });

    // Scheduled and resumed runs both go through FireScheduledAsync and the queue: rclone must not start.
    [Theory]
    [InlineData("r", JobRunState.Cancelled, true)]
    [InlineData(null, JobRunState.Failed, false)] // no remotes at all: refused, the job is kept
    public void ScheduledRun_OfAJobWhoseRemoteIsGone_NeverStartsRclone(string? listed, JobRunState ends, bool marked) => Sta(() =>
    {
        var (form, log) = RemoteShell("");
        var rclone = FakeRclone(listed is null ? "{}" : Dump(listed), "");
        Set(form, "configService", new RcloneConfigService(rclone));
        Set(form, "jobService", new RcloneJobService(rclone));
        Set(form, "scheduledInFlight", new HashSet<string>()); // field initializers did not run
        Set(form, "lastRunRecorded", new HashSet<string>());
        Set(form, "failureNotified", new HashSet<string>());
        _ = form.Handle;
        Call(form, "NewQueueIfIdle");
        var queue = (JobQueueService)Get(form, "jobQueue")!;
        var job = SavedJob();
        (job.SourceRemote, job.DryRun) = ("gone", false);
        ((List<TransferJob>)Get(form, "jobs")!).Add(job);

        Call(form, "FireScheduledAsync", job, null);
        var run = Assert.Single(queue.Snapshot());
        var inFlight = (HashSet<string>)Get(form, "scheduledInFlight")!;
        PumpUntil(() => inFlight.Count == 0); // the scheduler saw the run end

        Assert.Equal(ends, run.State);
        Assert.Equal(marked ? "gone" : null, job.DeletedRemote());
        if (!marked) Assert.StartsWith("Not run", run.LastError);
        Assert.Single(log, e => e.Level == ActivityLevel.Warn); // the stopped jobs, or the failed run
        Assert.All(File.ReadAllLines(Path.Combine(Path.GetDirectoryName(rclone)!, "calls.log")),
            call => Assert.StartsWith("config dump", call));
        typeof(Control).GetMethod("DestroyHandle", Private)!.Invoke(form, null);
    });

    // rclone's `config dump` leaves out remotes defined only by environment variables.
    [Fact]
    public void RealRclone_EnvironmentRemotes_AreNotTakenForMissing() => Sta(() =>
    {
        if (RealRclone.Exe is not { } exe) return;
        var (form, _) = RemoteShell("");
        Set(form, "configService", new RcloneConfigService(exe));
        var conf = Path.Combine(Directory.CreateTempSubdirectory("rm-env-").FullName, "rclone.conf");
        File.WriteAllText(conf, "[r]\ntype = local\n");
        var env = new TransferJob { Id = "e", SourceRemote = "My-Env", DestRemote = "r" };
        var gone = new TransferJob { Id = "g", SourceRemote = "gone" };
        ((List<TransferJob>)Get(form, "jobs")!).AddRange(new[] { env, gone });

        WithEnvironment("RCLONE_CONFIG", conf, () => WithEnvironment("RCLONE_CONFIG_MY-ENV_TYPE", "local", () =>
        {
            Call(form, "RefreshRemotesAsync");

            Assert.Equal("r", Assert.Single((IReadOnlyList<RcloneRemote>)Get(form, "remotes")!).Name);
            Assert.Equal("My-Env", env.SourceRemote);
            Assert.Equal("gone", gone.DeletedRemote());
        }));
    });

    [Fact]
    public void TransferWindow_TakesEnvironmentRemotes_AsConfigured() => Sta(() =>
        WithEnvironment("RCLONE_CONFIG_FROM-ENV_TYPE", "local", () =>
        {
            var job = SavedJob();
            (job.SourceRemote, job.DestRemote) = ("From-Env", "from-env,opt=1");
            var started = new List<JobSpec>();
            using var dialog = new TransferDialog(new[] { new RcloneRemote { Name = "r", Type = "mega" } },
                (spec, _, _) => started.Add(spec), job.SourceRemote, job.SourcePath, saveCallback: _ => { }, existingJob: job);

            Assert.Equal("", ((Label)Of(dialog, "lblMissingRemote")).Text);
            Assert.True(((Button)Of(dialog, "btnStart")).Enabled);
            Assert.True(((Button)Of(dialog, "btnSave")).Enabled);
            Invoke(dialog, "StartAndClose", false);
            Assert.Equal(("From-Env", "from-env,opt=1"), (started[0].SourceRemote, started[0].DestRemote));

            // Opened from a mount, with no saved job.
            using var fresh = new TransferDialog(Array.Empty<RcloneRemote>(), null, "From-Env", "");
            Assert.StartsWith("From-Env (", ((ThemedDropDown)Of(fresh, "cbSourceRemote")).SelectedText);
        }));

    [Fact]
    public void StartupPruning_KeepsAMountThatNamesAnEnvironmentRemote() =>
        WithEnvironment("RCLONE_CONFIG_PRUNE-ENV_TYPE", "local", () =>
        {
            var gone = new Mount { RemoteName = "gone" };

            var orphans = MainForm.OrphanedMounts(new[] { new Mount { RemoteName = "Prune-Env" }, gone },
                new[] { new RcloneRemote { Name = "r" } });

            Assert.Equal(new[] { gone }, orphans);
        });

    // A wrapper whose underlying remote is gone would reach whatever takes that name next.
    [Theory]
    [InlineData("crypt", "remote", "gone:secret")]
    [InlineData("alias", "remote", "gone,opt=1:")]
    [InlineData("chunker", "remote", "inner:sub")] // inner is an alias of gone
    [InlineData("union", "upstreams", "r:a \"gone:with space:ro\"")]
    [InlineData("combine", "upstreams", "a=r: b=gone:x")]
    public void RunCheck_RefusesARunThroughAWrapperOfAMissingRemote(string type, string key, string value) => Sta(() =>
    {
        var (form, log) = RemoteShell(Wrapped(type, key, value));
        var job = SavedJob();
        job.SourceRemote = "wrap";
        ((List<TransferJob>)Get(form, "jobs")!).Add(job);

        var error = Assert.Throws<InvalidOperationException>(() => RunCheck(form, new JobSpec { SourceRemote = "r", DestRemote = "wrap" }));
        Call(form, "RefreshRemotesAsync");

        Assert.Equal("Not run: remote \"wrap\" uses \"gone\", which is not in rclone's configuration.", error.Message);
        Assert.Equal("wrap", job.SourceRemote); // the job is kept: its own remote is still there
        Assert.DoesNotContain(log, e => e.Level == ActivityLevel.Warn);
    });

    [Theory]
    [InlineData("crypt", "remote", "r:secret")]
    [InlineData("alias", "remote", @"C:\data")]
    [InlineData("alias", "remote", @"\\server\share\a:b")]
    [InlineData("alias", "remote", "/srv/a:b")]
    [InlineData("alias", "remote", "folder")]
    [InlineData("alias", "remote", ":local:x")]
    [InlineData("alias", "remote", "wrap:loop")]
    [InlineData("union", "upstreams", "r:a:ro D:\\x")]
    [InlineData("combine", "upstreams", "a=r: \"b=C:\\two words\"")]
    [InlineData("webdav", "remote", "gone:")] // not a wrapper
    public void RunCheck_PassesAWrapperThatReachesNothingMissing(string type, string key, string value) => Sta(() =>
    {
        var (form, _) = RemoteShell(Wrapped(type, key, value));

        RunCheck(form, new JobSpec { SourceRemote = "wrap" });
    });

    // rclone takes a wrapper's setting from a flag or the environment before the config file.
    [Fact]
    public void RunCheck_FollowsWrapperSettingsFromFlagsAndTheEnvironment() => Sta(() =>
    {
        var (form, _) = RemoteShell(Wrapped("alias", "remote", "gone:"));
        var spec = new JobSpec { SourceRemote = "wrap" };

        RunCheck(form, spec with { ExtraArgs = new[] { "--alias-remote", "r:" } });
        RunCheck(form, spec with { ExtraArgs = new[] { "--alias-remote=r:" } });
        WithEnvironment("RCLONE_CONFIG_WRAP_REMOTE", "r:", () => RunCheck(form, spec));
        WithEnvironment("RCLONE_ALIAS_REMOTE", "r:", () => RunCheck(form, spec));
        // A wrapper defined only by environment variables is followed too.
        WithEnvironment("RCLONE_CONFIG_ENV-WRAP_TYPE", "alias", () => WithEnvironment("RCLONE_CONFIG_ENV-WRAP_REMOTE", "lost:",
            () => Assert.Contains("uses \"lost\"",
                Assert.Throws<InvalidOperationException>(() => RunCheck(form, new JobSpec { SourceRemote = "env-wrap" })).Message)));
    });

    // Marking another job cancels this run too, so the queue drops the reason: it must still be said.
    [Fact]
    public void RunCheck_SaysWhyAWrapperRunWasStopped_WhenMarkingCancelsIt() => Sta(() =>
    {
        var (form, log) = RemoteShell(Wrapped("alias", "remote", "gone:"));
        var queue = IdleQueue();
        Set(form, "jobQueue", queue);
        ((List<TransferJob>)Get(form, "jobs")!).Add(new TransferJob { Id = "a", Name = "direct", SourceRemote = "gone" });
        var run = queue.Enqueue(new JobSpec { SourceRemote = "wrap" }, "through wrapper");
        using var cts = new CancellationTokenSource();
        run.State = JobRunState.Running;
        run.Cts = cts;
        Set(form, "remotes", new[] { new RcloneRemote { Name = "wrap", Type = "alias", Parameters = { ["remote"] = "gone:" } } });

        Assert.Throws<InvalidOperationException>(() => Call(form, "CheckRunRemotesAsync", run));

        Assert.True(cts.IsCancellationRequested);
        Assert.Contains(log, e => e.Message.Contains("through wrapper") && e.Message.Contains("uses \"gone\""));
    });

    // A hung rclone must not hold the queue.
    [Fact]
    public void RunCheck_StopsAHungRclone_AndRefusesTheRun() => Sta(() =>
    {
        var (form, _) = RemoteShell("");
        var rclone = FakeRclone(Dump("r"), "");
        var done = Path.Combine(Path.GetDirectoryName(rclone)!, "done.txt");
        File.WriteAllText(rclone, "@echo off\r\nping -n 7 127.0.0.1 >nul\r\necho x>\"%~dp0done.txt\"\r\n");
        Set(form, "configService", new RcloneConfigService(rclone) { ListTimeout = TimeSpan.FromMilliseconds(500) });

        var error = Assert.Throws<InvalidOperationException>(() => RunCheck(form, new JobSpec { SourceRemote = "r" }));

        Assert.Contains("did not finish", error.Message);
        Thread.Sleep(7000); // past the ping: a process left running would write the file
        Assert.False(File.Exists(done), "rclone was left running");
    });

    // Nor may a hung in-app rename or delete, which the check waits for.
    [Fact]
    public void RunCheck_GivesUpOnAnInAppChangeThatNeverEnds() => Sta(() =>
    {
        var (form, _) = RemoteShell("");
        Set(form, "configService", new RcloneConfigService(FakeRclone(Dump("r"), "")) { ListTimeout = TimeSpan.FromMilliseconds(300) });
        Set(form, "remoteWritesActive", 1);

        var error = Assert.Throws<InvalidOperationException>(() => RunCheck(form, new JobSpec { SourceRemote = "r" }));

        Assert.StartsWith("Not run", error.Message);
    });

    // "r", and "wrap" with one setting; "inner" is an alias of "gone".
    private static string Wrapped(string type, string key, string value) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
        {
            ["r"] = new() { ["type"] = "local" },
            ["inner"] = new() { ["type"] = "alias", ["remote"] = "gone:" },
            ["wrap"] = new() { ["type"] = type, [key] = value },
        });

    private static string Dump(params string[] names) =>
        "{" + string.Join(",", names.Select(n => $"\"{n}\":{{\"type\":\"local\"}}")) + "}";

    // A batch file standing in for rclone.exe: prints `stdout`, or fails with `stderr`, and records its arguments.
    private static string FakeRclone(string stdout, string stderr)
    {
        var dir = Directory.CreateTempSubdirectory("rm-fake-rclone-").FullName;
        File.WriteAllText(Path.Combine(dir, "out.txt"), stdout);
        File.WriteAllText(Path.Combine(dir, "err.txt"), stderr);
        var exe = Path.Combine(dir, "rclone.cmd");
        File.WriteAllText(exe, "@echo off\r\necho %*>>\"%~dp0calls.log\"\r\ntype \"%~dp0out.txt\"\r\n"
            + (stderr.Length == 0 ? "" : "type \"%~dp0err.txt\" 1>&2\r\nexit /b 1\r\n"));
        return exe;
    }

    // A shell with the Mounts tab, which a remote refresh repaints, and a fake rclone for its config.
    private static (MainForm Form, List<ActivityEntry> Log) RemoteShell(string stdout, string stderr = "")
    {
        var form = Shell();
        var log = new ActivityLog();
        var entries = new List<ActivityEntry>();
        log.Appended += entries.Add;
        Set(form, "activityLog", log);
        Call(form, "BuildMountsTab", new Panel());
        Set(form, "configService", new RcloneConfigService(FakeRclone(stdout, stderr)));
        return (form, entries);
    }

    private static void RunCheck(MainForm form, JobSpec spec) =>
        Call(form, "CheckRunRemotesAsync", new JobRun { Spec = spec });

    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static void WithEnvironment(string name, string value, Action body)
    {
        var saved = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try { body(); }
        finally { Environment.SetEnvironmentVariable(name, saved); }
    }

    // MainForm's own constructor loads settings, adds a tray icon and auto-mounts. Run only the
    // Form base constructor, then call single tab builders with just the fields they read.
    private static MainForm Shell()
    {
        var form = (MainForm)RuntimeHelpers.GetUninitializedObject(typeof(MainForm));
        typeof(Form).GetConstructor(Type.EmptyTypes)!.Invoke(form, null);
        GC.SuppressFinalize(form);
        Set(form, "iconToolTip", new ToolTip());
        Set(form, "mounts", new List<Mount>());
        Set(form, "jobs", new List<TransferJob>());
        Set(form, "remotes", Array.Empty<RcloneRemote>());
        Set(form, "transferHistory", new List<TransferHistoryEntry>());
        Set(form, "lastTransfers", Array.Empty<TransferInfo>());
        Set(form, "oauthProviderTypes", new HashSet<string>());
        return form;
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static FieldInfo Field(string name) => typeof(MainForm).GetField(name, Private)!;
    private static void Set(MainForm form, string name, object value) => Field(name).SetValue(form, value);
    private static object? Get(MainForm form, string name) => Field(name).GetValue(form);
    private static void Call(MainForm form, string name, params object?[] args) =>
        Finish(typeof(MainForm).GetMethod(name, Private)!.Invoke(form, args) as Task);

    // Some actions save settings (redirected by TestIsolation). Wait for the action and its saves,
    // so none is still running when a later test controls the save gate.
    private static void Finish(Task? action)
    {
        var saveGate = (SemaphoreSlim)typeof(SettingsService)
            .GetField("saveGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (action is { IsCompleted: false } || saveGate.CurrentCount == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the action or its save did not finish");
            Application.DoEvents();
            Thread.Sleep(5);
        }
        action?.GetAwaiter().GetResult();
    }

    // Same shape as the real window: hidden Dock=Fill tab panels inside a 1096 x 532 content host.
    private static Panel Host(out Panel tab)
    {
        var host = new Panel { Size = new Size(LayoutMetrics.TabHostWidth, 532) };
        tab = new Panel { Dock = DockStyle.Fill, Visible = false };
        host.Controls.Add(tab);
        return host;
    }

    private static IEnumerable<T> All<T>(Control root) where T : Control =>
        root.Controls.Cast<Control>().SelectMany(c => All<T>(c).Concat(c is T t ? new[] { t } : Array.Empty<T>()));

    private static void AssertAmpersandsShow(Control root, int atLeast)
    {
        var labels = All<Label>(root).Where(l => l.Text.Contains('&')).ToArray();
        Assert.True(labels.Length >= atLeast, $"found {labels.Length} labels with '&'");
        Assert.All(labels, l => Assert.False(l.UseMnemonic, l.Text));
    }

    private static void Sta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
                Finish(null);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
