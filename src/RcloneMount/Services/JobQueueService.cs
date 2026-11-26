using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RcloneMount.Models;

namespace RcloneMount.Services
{
    // The one place transfer runs live. Every Start (from TransferDialog,
    // scheduler, or ad-hoc) enqueues a JobRun here; a pump loop dequeues
    // up to MaxConcurrent runs at a time and drives each via RcloneJobService.
    //
    // MaxConcurrent defaults to 1 — user's ask, based on limited-uplink
    // scenarios where two parallel runs just half the effective bandwidth.
    // Raise it later via the (planned) Settings-tab knob.
    public sealed class JobQueueService
    {
        private readonly RcloneJobService jobService;
        private readonly ActivityLog? activityLog;
        private readonly object gate = new();
        // Ordered list rather than a Queue<T> because the UI needs to render
        // it verbatim (position N in the queue) and because we want cheap
        // O(n) cancel-by-id / remove-by-id without rebuilding a queue.
        private readonly List<JobRun> runs = new();
        // Cap on terminal rows we keep in memory. Prevents the queue growing
        // unbounded across a long session. Users who want the full record go
        // to the History tab, which is disk-backed.
        private const int TerminalRowCap = 30;

        public int MaxConcurrent { get; set; } = 1;

        // Fires on any change to the runs list — added, state flip, removed.
        // Handlers should marshal to the UI thread themselves; the queue
        // fires from whatever thread happens to hold the gate at the time.
        public event Action? RunsChanged;

        // Fires once when a started run has ended (any exit code, cancelled, or failed to start):
        // files may have changed even when it failed part-way. The run may already be trimmed from Snapshot().
        public event Action<JobRun>? RunFinished;

        public JobQueueService(RcloneJobService jobService, ActivityLog? activityLog = null)
        {
            this.jobService = jobService;
            this.activityLog = activityLog;
        }

        public IReadOnlyList<JobRun> Snapshot()
        {
            lock (gate) return runs.ToArray();
        }

        // Enqueue a spec. Returns the created JobRun so callers can hold on to
        // its Id (e.g. tray balloon "Cancel <this run>"). jobId is optional —
        // pass the saved TransferJob.Id if this run came from one.
        public JobRun Enqueue(JobSpec spec, string displayName, string jobId = "")
        {
            var run = new JobRun
            {
                Spec = spec,
                DisplayName = displayName,
                JobId = jobId,
                State = JobRunState.Queued,
            };
            lock (gate)
            {
                runs.Add(run);
                TrimTerminal_NoLock();
            }
            RunsChanged?.Invoke();
            _ = Task.Run(PumpAsync);
            return run;
        }

        // Cancel a run by id. Queued → drops it (marks Cancelled). Running →
        // signals the CTS and lets RcloneJobService's cooperative /core/quit
        // path unwind naturally. No-op if the run is already terminal.
        public void Cancel(string runId)
        {
            JobRun? found;
            lock (gate)
            {
                found = runs.FirstOrDefault(r => r.Id == runId);
                if (found is null) return;
                if (found.State == JobRunState.Queued)
                {
                    found.State = JobRunState.Cancelled;
                    found.Outcome = "Cancelled";
                    found.FinishedUtc = DateTime.UtcNow;
                }
                else if (found.State == JobRunState.Running)
                {
                    // Signal outside the lock isn't necessary here — CTS.Cancel
                    // doesn't take our gate — but be defensive about a stale ref.
                    try { found.Cts?.Cancel(); } catch { }
                }
                else
                {
                    return; // already terminal
                }
            }
            RunsChanged?.Invoke();
        }

        // Remove a terminal (or queued) run from the list. Refuses to remove
        // a Running row — cancel that first.
        public void Remove(string runId)
        {
            lock (gate)
            {
                var r = runs.FirstOrDefault(x => x.Id == runId);
                if (r is null) return;
                if (r.State == JobRunState.Running) return;
                runs.Remove(r);
            }
            RunsChanged?.Invoke();
        }

        // For a deleted saved job: bars its runs from Retry and cancels the queued and running ones.
        public void CancelJob(string jobId)
        {
            if (jobId.Length == 0) return; // "" marks ad-hoc runs, which belong to no job
            var own = Snapshot().Where(r => r.JobId == jobId).ToList();
            foreach (var run in own) run.JobDeleted = true;
            CancelRuns(own);
            RunsChanged?.Invoke(); // finished rows lose their Retry button
        }

        // The queued and running runs that read or write one of these remotes.
        public IReadOnlyList<JobRun> ActiveRunsUsing(IReadOnlyCollection<string> remoteNames) =>
            Snapshot().Where(r => r.IsActive
                && (remoteNames.Contains(r.Spec.SourceRemote) || remoteNames.Contains(r.Spec.DestRemote))).ToList();

        // Queued ones go first, so none of them starts as a running one ends.
        public void CancelRuns(IEnumerable<JobRun> targets)
        {
            foreach (var run in targets.OrderBy(r => r.State != JobRunState.Queued).ToList()) Cancel(run.Id);
        }

        // Queued runs would fail under the old name, or reach a later remote that reuses it. Finished
        // ones change too, for Retry; a running rclone already has its arguments.
        public void RenameRemote(string oldName, string newName)
        {
            lock (gate)
                foreach (var run in runs) run.Spec = run.Spec.WithRemoteRenamed(oldName, newName);
            RunsChanged?.Invoke();
        }

        // Re-queue a failed / cancelled run with the same spec. The old row
        // is left in place (as a terminal record) so history reads honestly;
        // a new JobRun is created and enqueued.
        public JobRun? Retry(string runId)
        {
            JobRun? original;
            lock (gate) original = runs.FirstOrDefault(r => r.Id == runId);
            if (original is null || original.JobDeleted) return null;
            return Enqueue(original.Spec, original.DisplayName, original.JobId);
        }

        // Pump loop. Called after every Enqueue and after every run's completion.
        // Idempotent — multiple concurrent invocations only start what capacity
        // allows because the dequeue+state-flip is inside the gate.
        private async Task PumpAsync()
        {
            while (true)
            {
                JobRun? next = null;
                lock (gate)
                {
                    var running = runs.Count(r => r.State == JobRunState.Running);
                    if (running >= MaxConcurrent) return;
                    next = runs.FirstOrDefault(r => r.State == JobRunState.Queued);
                    if (next is null) return;
                    next.State = JobRunState.Running;
                    next.StartedUtc = DateTime.UtcNow;
                    next.Cts = new CancellationTokenSource();
                }
                RunsChanged?.Invoke();

                // Fire-and-forget the actual run; the continuation flips state
                // and re-enters the pump so the next queued run kicks off.
                var captured = next;
                _ = RunOneAsync(captured);
                // Loop back to see if we still have capacity (MaxConcurrent > 1).
            }
        }

        private async Task RunOneAsync(JobRun run)
        {
            var progress = new Progress<JobProgress>(p =>
            {
                run.Progress = p;
                RunsChanged?.Invoke();
            });
            JobResult? result = null;
            Exception? error = null;
            try
            {
                result = await jobService.RunAsync(run.Spec, progress, run.Cts!.Token, run.DisplayName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            lock (gate)
            {
                run.FinishedUtc = DateTime.UtcNow;
                // Cancel() on a Running run only signals the CTS; rclone then exits with whatever
                // code the quit/kill gives. A user cancel is never a failure, so check it first.
                var cancelRequested = run.Cts?.IsCancellationRequested == true;
                try { run.Cts?.Dispose(); } catch { }
                run.Cts = null;
                if (cancelRequested)
                {
                    run.State = JobRunState.Cancelled;
                    run.Outcome = "Cancelled";
                }
                else if (error is not null)
                {
                    run.State = JobRunState.Failed;
                    run.Outcome = "Failed to start";
                    run.LastError = error.Message;
                }
                else if (result!.ExitCode == 0)
                {
                    run.State = JobRunState.Completed;
                    run.Outcome = run.Spec.DryRun ? "Success (dry-run)" : "Success";
                }
                else
                {
                    run.State = JobRunState.Failed;
                    run.Outcome = $"Failed (exit {result.ExitCode})";
                    run.LastError = LastNonBlankLine(result.StderrTail);
                }
                TrimTerminal_NoLock();
            }
            RunsChanged?.Invoke();
            RunFinished?.Invoke(run);

            // Diagnostic: a "successful" run that moved zero bytes is either a
            // genuine no-op or a hang the completion detector cut short. Dump
            // the stderr tail into activityLog so the user (and next-session
            // Claude) can tell which — otherwise the tail is lost. Also log
            // it on failure — currently only the last line makes it to the row.
            if (result is not null && activityLog is not null && !run.Spec.IsCheck)
            {
                var moved = (run.Progress?.BytesTransferred ?? 0) > 0
                         || (run.Progress?.TransfersDone ?? 0)   > 0
                         || (run.Progress?.Checks ?? 0)          > 0;
                var shouldLog = result.ExitCode != 0 || (!run.Spec.DryRun && !moved);
                if (shouldLog && !string.IsNullOrWhiteSpace(result.StderrTail))
                {
                    var tail = TailFragment(result.StderrTail);
                    var msg = $"Job '{run.DisplayName}' finished (exit {result.ExitCode}, "
                            + $"{run.Progress?.BytesTransferred ?? 0}B, {run.Progress?.Checks ?? 0} checks). "
                            + $"rclone stderr tail:\n{tail}";
                    if (result.ExitCode == 0) activityLog.Info(msg);
                    else activityLog.Warn(msg);
                }
            }

            // Kick the pump again in case there's a queued run to start now.
            _ = Task.Run(PumpAsync);
        }

        // Keep the tail of terminal rows bounded. Preserves order — drops the
        // oldest terminals first. Active rows (Queued/Running) are never dropped.
        private void TrimTerminal_NoLock()
        {
            var terminals = runs.Where(r => r.IsTerminal).ToList();
            if (terminals.Count <= TerminalRowCap) return;
            var toDrop = terminals
                .OrderBy(r => r.FinishedUtc ?? DateTime.MinValue)
                .Take(terminals.Count - TerminalRowCap)
                .ToList();
            foreach (var r in toDrop) runs.Remove(r);
        }

        // Last ~20 lines of the stderr buffer, trimmed and joined. Used by
        // the "silent success" diagnostic above.
        private static string TailFragment(string tail)
        {
            if (string.IsNullOrWhiteSpace(tail)) return "(empty)";
            var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var start = Math.Max(0, lines.Length - 20);
            var slice = new string[lines.Length - start];
            for (var i = 0; i < slice.Length; i++) slice[i] = lines[start + i].TrimEnd('\r');
            return string.Join('\n', slice);
        }

        private static string LastNonBlankLine(string tail)
        {
            if (string.IsNullOrWhiteSpace(tail)) return "rclone exited with an error.";
            var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var t = lines[i].Trim();
                if (t.Length > 0) return t;
            }
            return "rclone exited with an error.";
        }
    }
}
