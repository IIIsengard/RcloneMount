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
