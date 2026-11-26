using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;
using RcloneMount.UI;

namespace RcloneMount.Tests;

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
        WithoutSaving(() =>
        {
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

        WithoutSaving(() => Call(form, "DeleteSavedJobAsync", job));

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

        WithoutSaving(() => Call(form, "ApplyRemoteRenameAsync", "r", "new"));

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

        WithoutSaving(() => Call(form, "DeleteSavedJobAsync", job));

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

        WithoutSaving(() => Call(form, "DeleteSavedJobAsync", job));

        Assert.DoesNotContain("cancelled", asked); // nothing was running when asked
        Assert.Equal(JobRunState.Cancelled, late!.State);
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

    // Some actions save settings: make that a no-op so the real settings.json is never written.
    private static void WithoutSaving(Action body)
    {
        var readError = typeof(SettingsService).GetProperty(nameof(SettingsService.LoadReadError))!;
        readError.SetValue(null, "test: do not save");
        try { body(); }
        finally { readError.SetValue(null, null); }
    }

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
        typeof(MainForm).GetMethod(name, Private)!.Invoke(form, args);

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
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
