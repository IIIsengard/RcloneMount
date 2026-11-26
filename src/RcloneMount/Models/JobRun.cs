using System;
using System.Threading;
using RcloneMount.Services;

namespace RcloneMount.Models
{
    // One attempt to execute a JobSpec. Multiple runs can exist per saved
    // TransferJob over time (retries, re-fires). The queue engine owns
    // instances and mutates them under its own lock; the UI reads snapshots.
    public enum JobRunState
    {
        Queued,
        Running,
        Completed,
        Failed,
        Cancelled,
    }

    public sealed class JobRun
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        // Empty for ad-hoc runs (e.g. right-click a mount → Transfer…). Populated
        // when the run came from a saved TransferJob so the UI can link back to
        // the template and the summary row picks up the job name.
        public string JobId { get; init; } = "";
        public string DisplayName { get; init; } = "";

        public JobSpec Spec { get; internal set; } = new(); // replaced only when a remote is renamed

        public JobRunState State { get; set; } = JobRunState.Queued;

        public DateTime QueuedUtc { get; init; } = DateTime.UtcNow;
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }

        // Free-form outcome label ("Success", "Failed (exit 3)", "Cancelled",
        // etc.) — mirrors TransferJob.LastResultKind so history + queue rows
        // read the same.
        public string? Outcome { get; set; }
        public string? LastError { get; set; }

        // Latest progress snapshot pushed by RcloneJobService. Null until the
        // run flips to Running. UI reads this per redraw.
        public JobProgress? Progress { get; set; }

        // Only non-null while State == Running. The queue engine sets it
        // when it dequeues, hands it to RcloneJobService.RunAsync, and nulls
        // it on completion so the row's Cancel button knows what to hit.
        public CancellationTokenSource? Cts { get; set; }

        public bool IsActive => State == JobRunState.Queued || State == JobRunState.Running;
        public bool IsTerminal => State == JobRunState.Completed || State == JobRunState.Failed || State == JobRunState.Cancelled;
    }
}
