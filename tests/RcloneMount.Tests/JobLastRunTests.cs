using RcloneMount.Models;

namespace RcloneMount.Tests;

// "Last run" on the Jobs tab must follow every run of a saved job, not only scheduled ones.
public class JobLastRunTests
{
    private static readonly DateTime Done = new(2026, 11, 26, 11, 40, 49, DateTimeKind.Utc);

    private static JobRun Run(string jobId, JobRunState state, bool started = true) => new()
    {
        JobId = jobId,
        State = state,
        StartedUtc = started ? Done.AddSeconds(-5) : null,
        FinishedUtc = Done,
        Outcome = state.ToString(),
    };

    [Theory]
    [InlineData(JobRunState.Completed)]
    [InlineData(JobRunState.Failed)]
    [InlineData(JobRunState.Cancelled)]
    public void FinishedRun_IsRecordedOnItsSavedJob_Once(JobRunState state)
    {
        var job = new TransferJob { Id = "a" };
        var seen = new HashSet<string>();
        var run = Run("a", state);

        Assert.True(MainForm.RecordLastRuns(new List<TransferJob> { job }, new[] { run }, seen));
        Assert.Equal(Done, job.LastRunUtc);
        Assert.Equal(state.ToString(), job.LastResultKind);
        Assert.False(MainForm.RecordLastRuns(new List<TransferJob> { job }, new[] { run }, seen));
    }

    [Fact]
    public void UnfinishedNeverStartedAndAdHocRuns_RecordNothing()
    {
        var job = new TransferJob { Id = "a" };
        var runs = new[]
        {
            Run("a", JobRunState.Queued, started: false),
            Run("a", JobRunState.Running),
            Run("a", JobRunState.Cancelled, started: false), // removed from the queue before it ran
            Run("", JobRunState.Completed),
            Run("deleted", JobRunState.Completed),
        };

        Assert.False(MainForm.RecordLastRuns(new List<TransferJob> { job }, runs, new HashSet<string>()));
        Assert.Null(job.LastRunUtc);
    }

    // The queue thread sets State a moment before Outcome; a read in between must retry later.
    [Fact]
    public void RunWithoutOutcomeYet_IsPickedUpOnTheNextRead()
    {
        var job = new TransferJob { Id = "a" };
        var jobs = new List<TransferJob> { job };
        var seen = new HashSet<string>();
        var run = Run("a", JobRunState.Completed);
        run.Outcome = null;

        Assert.False(MainForm.RecordLastRuns(jobs, new[] { run }, seen));
        run.Outcome = "Success";
        Assert.True(MainForm.RecordLastRuns(jobs, new[] { run }, seen));
        Assert.Equal("Success", job.LastResultKind);
    }

    // The scheduler notices its run up to a second late; a manual run may have finished since.
    [Fact]
    public void CompleteRun_KeepsANewerResult_AndStillClearsInFlight()
    {
        var job = new TransferJob { Id = "a", InFlight = true, LastRunUtc = Done.AddSeconds(1), LastResultKind = "Failed (exit 1)" };

        MainForm.CompleteRun(new List<TransferJob> { job }, job, "Success", Done);

        Assert.False(job.InFlight);
        Assert.Equal("Failed (exit 1)", job.LastResultKind);
    }

    // A manual run can finish while a scheduled run of the same job is still queued.
    [Fact]
    public void Recording_LeavesTheScheduledInFlightFlag()
    {
        var job = new TransferJob { Id = "a", InFlight = true, InFlightStartedUtc = Done };

        MainForm.RecordLastRuns(new List<TransferJob> { job }, new[] { Run("a", JobRunState.Completed) }, new HashSet<string>());

        Assert.True(job.InFlight);
        Assert.Equal(Done, job.InFlightStartedUtc);
    }

    // The queue lists runs oldest first; the newest finish must win whatever the order.
    [Fact]
    public void OlderRun_DoesNotOverwriteNewerOne()
    {
        var job = new TransferJob { Id = "a", LastRunUtc = Done.AddMinutes(1), LastResultKind = "Success" };

        Assert.False(MainForm.RecordLastRuns(new List<TransferJob> { job }, new[] { Run("a", JobRunState.Failed) }, new HashSet<string>()));
        Assert.Equal("Success", job.LastResultKind);
    }
}
