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
    // whose `Schedule` string says the target time has just passed. v1 supports
    // ONLY "daily HH:MM" (24-hour local time). Anything else is silently ignored
    // so a bad expression can't blow up the timer.
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
            if (jobService is null) return;
            var nowLocal = DateTime.Now;
            foreach (var job in jobs)
            {
                if (!TryParseDailySchedule(job.Schedule, out var target)) continue;
                if (scheduledInFlight.Contains(job.Id)) continue;

                // Today's target instant (local). If it's still in the future,
                // wait — that's what the next tick after 03:00 is for.
                var todayTarget = DateTime.Today + target;
                if (nowLocal < todayTarget) continue;

                // Has already fired today? Compare in LOCAL time — the user set
                // the time in local, and "today" is the natural boundary.
                if (job.LastScheduledFireUtc is { } last && last.ToLocalTime().Date >= DateTime.Today) continue;

                _ = FireScheduledAsync(job);
            }
        }

        private Task FireScheduledAsync(TransferJob job)
        {
            if (jobQueue is null) return Task.CompletedTask;
            scheduledInFlight.Add(job.Id);
            var spec = BuildSpecFromSaved(job);

            activityLog.Info($"Scheduled job '{job.Name}' enqueued ({spec.OperationVerb} {spec.SourceSpec} → {spec.DestSpec}).");
            job.LastScheduledFireUtc = DateTime.UtcNow;
            job.InFlight = true;
            job.InFlightStartedUtc = DateTime.UtcNow;
            _ = SaveSettingsAsync();

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
                        job.InFlight = false;
                        job.InFlightStartedUtc = null;
                        job.LastRunUtc = DateTime.UtcNow;
                        job.LastResultKind = run.Outcome ?? "Unknown";
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

        // "daily 03:00" → 03:00. Case-insensitive on the "daily " prefix; hours
        // must be 0–23 and minutes 0–59. Anything else returns false so the
        // caller can silently skip a broken expression.
        internal static bool TryParseDailySchedule(string? expression, out TimeSpan target)
        {
            target = default;
            if (string.IsNullOrWhiteSpace(expression)) return false;
            var s = expression.Trim();
            if (!s.StartsWith("daily ", StringComparison.OrdinalIgnoreCase)) return false;
            var hhmm = s.Substring(6).Trim();
            var parts = hhmm.Split(':');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out var h) || h < 0 || h > 23) return false;
            if (!int.TryParse(parts[1], out var m) || m < 0 || m > 59) return false;
            target = new TimeSpan(h, m, 0);
            return true;
        }
    }
}
