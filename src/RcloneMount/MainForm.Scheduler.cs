using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RcloneMount.Models;
using RcloneMount.Services;
using RcloneMount.UI;

namespace RcloneMount
{
    // In-process job scheduler. Ticks every 30 s and fires any saved TransferJob
    // whose `Schedule` text (see Models/Schedule.cs) says a run is due. An invalid
    // expression is logged once and skipped so it can't blow up the timer.
    //
    // Deliberately in-process (not Windows Task Scheduler) because it's a lot
    // simpler and lets the same code path handle both scheduled and manual runs.
    // Trade-off: the app has to be running (usually in tray) when the target
    // time hits; the placeholder text in the dialog says as much. Follow-up
    // upgrade to Task Scheduler is called out in [[transfer-engine]].
    public partial class MainForm
    {
        private System.Windows.Forms.Timer? schedulerTimer;
        // Jobs currently being run by the scheduler — skip on the next tick to
        // avoid piling up runs when one is slow (e.g. daily 03:00 job still
        // running when 03:30 tick fires).
        private readonly HashSet<string> scheduledInFlight = new(StringComparer.Ordinal);
        private readonly HashSet<string> invalidScheduleWarned = new(StringComparer.Ordinal);

        private void StartScheduler()
        {
            if (schedulerTimer is not null) return;
            schedulerTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
            schedulerTimer.Tick += (_, _) => SchedulerTick();
            schedulerTimer.Start();
            // Fire once immediately so a scheduled time that already passed
            // (app started after 03:00) can catch up on today's run without
            // waiting the first 30 s tick.
            SchedulerTick();
        }

        private void StopScheduler()
        {
            schedulerTimer?.Stop();
            schedulerTimer?.Dispose();
            schedulerTimer = null;
        }

        private void SchedulerTick()
        {
            // shutdownStarted: Exit is waiting on the uploads prompt or tearing down; start nothing new.
            if (jobService is null || shutdownStarted) return;
            var now = DateTimeOffset.Now;
            foreach (var job in jobs)
            {
                if (string.IsNullOrWhiteSpace(job.Schedule)) continue;
                if (!Schedule.TryParse(job.Schedule, out _, out var error))
                {
                    // Hand-edited or from a newer version: say so once instead of never running silently.
                    if (invalidScheduleWarned.Add(job.Id))
                        activityLog.Warn($"Job '{job.Name}' has an invalid schedule '{job.Schedule}' and will not run on a schedule: {error}");
                    continue;
                }
                if (scheduledInFlight.Contains(job.Id)) continue;

                // An unanchored "every Nh" job starts its clock now instead of running at once.
                if (Schedule.AnchorIfNew(job, now)) { _ = SaveSettingsAsync(); continue; }

                // Due when the next slot (after the last fire; today's missed slot catches up at
                // launch) is not in the future. Firing stamps LastScheduledFireUtc, so it never repeats.
                if (Schedule.NextRunOf(job, now) is { } next && next <= now)
                    _ = FireScheduledAsync(job, Schedule.FireStamp(job, now));
            }
        }

        // stampUtc = the LastScheduledFireUtc to record (null = now, e.g. resuming an interrupted job).
        private Task FireScheduledAsync(TransferJob job, DateTime? stampUtc = null)
        {
            if (jobQueue is null) return Task.CompletedTask;
            scheduledInFlight.Add(job.Id);
            var spec = BuildSpecFromSaved(job);

            activityLog.Info($"Scheduled job '{job.Name}' enqueued ({spec.OperationVerb} {spec.SourceSpec} → {spec.DestSpec}).");
            job.LastScheduledFireUtc = stampUtc ?? DateTime.UtcNow;
            job.InFlight = true;
            job.InFlightStartedUtc = DateTime.UtcNow;
            _ = SaveSettingsAsync();
            RefreshJobsList();

            // Hand off to the queue and clear the scheduler's in-flight guard when
            // the run finishes. The queue's own MaxConcurrent (default 1) enforces
            // sequencing across all sources — scheduled, dialog, ad-hoc alike.
            var displayName = string.IsNullOrWhiteSpace(job.Name) ? "(scheduled)" : job.Name;
            var run = jobQueue.Enqueue(spec, displayName, job.Id);
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                // Wait for terminal state via polling — the queue doesn't expose a
                // per-run completion task yet. 1s cadence is fine for a scheduler
                // whose smallest tick is 30s.
                while (!run.IsTerminal)
                    await System.Threading.Tasks.Task.Delay(1000).ConfigureAwait(false);

                // Back on the UI thread: jobs, scheduledInFlight and SaveSettingsAsync's
                // serialisation of mounts/jobs are all UI-owned.
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        scheduledInFlight.Remove(job.Id);
                        CompleteRun(jobs, job, run.Outcome ?? "Unknown", run.FinishedUtc ?? DateTime.UtcNow);
                        if (run.State == JobRunState.Failed)
                            activityLog.Warn($"Scheduled job '{job.Name}' failed: {run.LastError ?? run.Outcome}");
                        else if (run.State == JobRunState.Cancelled)
                            activityLog.Info($"Scheduled job '{job.Name}' cancelled.");
                        else
                            activityLog.Info($"Scheduled job '{job.Name}' finished.");
                        await SaveSettingsAsync();
                        RefreshJobsList();
                    }
                    catch (Exception ex) { HandleUnexpectedError("scheduled job completion", ex); }
                }));
            });
            return Task.CompletedTask;
        }

        // Record a finished run on the CURRENT saved job: the Edit dialog replaces the job object
        // while a run is in progress, so the captured one may no longer be in the list.
        internal static TransferJob CompleteRun(List<TransferJob> jobs, TransferJob captured, string outcome, DateTime finishedUtc)
        {
            var cur = jobs.Find(j => j.Id == captured.Id) ?? captured;
            cur.InFlight = false;
            cur.InFlightStartedUtc = null;
            if (cur.LastRunUtc > finishedUtc) return cur; // a later run already reported
            cur.LastRunUtc = finishedUtc;
            cur.LastResultKind = outcome;
            return cur;
        }

        // Saved job → runnable spec for scheduled and resumed runs. Applies the same
        // per-operation rules as TransferDialog's Start, so a job runs unattended
        // with exactly the safety settings the user saved (bisync --max-delete,
        // --check-access, check --one-way, …).
        internal static JobSpec BuildSpecFromSaved(TransferJob job)
        {
            var op = ParseOp(job.Operation);
            var isCheck = op is TransferOperation.Check or TransferOperation.CryptCheck;
            var isBisync = op is TransferOperation.Bisync;
            var (extraArgs, tempFile) = new FilterRules
            {
                PatternsText = job.FilterPatterns,
                MinSize = job.FilterMinSize,
                MaxSize = job.FilterMaxSize,
                MinAge = job.FilterMinAge,
                MaxAge = job.FilterMaxAge,
            }.EmitArgs();

            return new JobSpec
            {
                Operation = op,
                SourceRemote = job.SourceRemote,
                SourcePath = job.SourcePath,
                DestRemote = job.DestRemote,
                DestPath = job.DestPath,
                DryRun = !isCheck && job.DryRun,
                ServerSideAcrossConfigs = !isCheck && !isBisync && job.ServerSideAcrossConfigs
                    && job.SourceRemote.Length > 0 && job.DestRemote.Length > 0,
                OneWay = isCheck && job.OneWay,
                SizeOnly = isCheck && job.SizeOnly,
                ConflictResolve = isBisync ? job.ConflictResolve : "",
                CheckAccess = isBisync && job.CheckAccess,
                MaxDeletePct = isBisync ? job.MaxDeletePct : 0,
                BwLimit = job.BwLimit,
                ExtraArgs = extraArgs.Concat(RcloneMountService.SplitFlags(job.ExtraFlags ?? "")).ToList(),
                CleanupTempFile = tempFile,
            };
        }

        // On startup, look for jobs whose InFlight flag is still set from a
        // previous process — that means we crashed / were killed / power-cut
        // mid-run. Offer to resume them (re-runs the same rclone command, which
        // skips already-transferred files). If the user dismisses, clear the
        // flags so we don't re-prompt on the next launch. Called from
        // MainForm_LoadCore before StartScheduler so the resume kickoff
        // co-exists with the scheduler's own in-flight guard.
        private async Task ResumeInterruptedJobsAsync()
        {
            if (jobService is null) return;
            var stranded = jobs.Where(j => j.InFlight).ToList();
            if (stranded.Count == 0) return;

            var names = string.Join("\n", stranded.Select(j =>
                $"  • {(string.IsNullOrWhiteSpace(j.Name) ? "(unnamed)" : j.Name)}"
                + (j.InFlightStartedUtc is { } t ? $"  (started {t.ToLocalTime():g})" : "")));
            var msg = stranded.Count == 1
                ? $"A transfer job was still running when RcloneMount last closed:\n\n{names}\n\n"
                  + "Resume it now? (Re-runs the same operation; files already at the destination are skipped.)"
                : $"{stranded.Count} transfer jobs were still running when RcloneMount last closed:\n\n{names}\n\n"
                  + "Resume them now? (Re-runs the same operations; files already at the destination are skipped.)";
            var choice = ConfirmDialog.Show(this, "Resume interrupted jobs?", msg,
                confirmText: "Resume", cancelText: "Dismiss", destructive: false);

            if (choice != DialogResult.Yes)
            {
                foreach (var j in stranded)
                {
                    j.InFlight = false;
                    j.InFlightStartedUtc = null;
                    activityLog.Info($"Dismissed interrupted job '{j.Name}' without resuming.");
                }
                await SaveSettingsAsync();
                RefreshJobsList();
                return;
            }

            foreach (var j in stranded)
            {
                activityLog.Info($"Resuming interrupted job '{j.Name}'.");
                _ = FireScheduledAsync(j);
            }
        }
    }
}
