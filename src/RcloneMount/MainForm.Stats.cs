using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using RcloneMount.Services;
using RcloneMount.UI;

namespace RcloneMount
{
    // Stats polling and lifetime-transfer bookkeeping. Split from MainForm.cs because
    // this is a coherent subsystem with its own field cluster, timer, poll loop, and
    // display formatting — future changes to how we sample rclone belong in one file.
    public partial class MainForm
    {
        // Tracks whether a poll is currently in flight. If the timer ticks while the
        // previous poll is still running (backend is slow, /operations/about took a
        // while), we skip the new tick instead of racing on lastSamples/lastAbout.
        private bool statsPollInFlight;

        private void StartStatsPolling()
        {
            if (mountService is null) return;
            statsPoller ??= new RcloneStatsPoller(mountService, () => mounts);
            if (statsTimer is null)
            {
                statsTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                statsTimer.Tick += async (_, _) => await PollStatsOnceAsync();
                statsTimer.Start();
                // Fire an immediate first poll so the space bars appear on cold open
                // instead of waiting for the first 2s tick.
                _ = PollStatsOnceAsync();
                // Trim transfers.jsonl at startup so runtime never pays the cost. Bounded
                // at 5000 lines by default, cheap no-op when file is under the cap.
                _ = TransferHistoryService.PruneAsync();
            }
        }

        private async Task PollStatsOnceAsync()
        {
            if (statsPoller is null) return;
            if (statsPollInFlight) return;
            statsPollInFlight = true;
            AggregateStats? stats = null;
            try
            {
                stats = await statsPoller.PollAsync();
                aggregateBytesPerSecond = stats.BytesPerSecond;
                aggregateDownBytesPerSecond = stats.DownBytesPerSecond;
                aggregateUpBytesPerSecond   = stats.UpBytesPerSecond;
                lastTransfers = stats.ActiveTransfers;
                AccumulateLifetimeTotals(stats);
            }
            catch
            {
                aggregateBytesPerSecond = 0;
                aggregateDownBytesPerSecond = 0;
                aggregateUpBytesPerSecond = 0;
                lastTransfers = Array.Empty<TransferInfo>();
            }
            if (stats is not null)
            {
                ApplyPerMountStats(stats.PerMount);
                RunDetachWatchdog(stats.PerMount);
                if (stats.NewlyCompleted.Count > 0)
                {
                    // Persist to transfers.jsonl fire-and-forget; a slow disk shouldn't
                    // stall the poll loop. The in-memory prepend + list refresh runs
                    // via TransferHistoryService.EntriesAppended, so mount rows and
                    // job rows share one code path (see MainForm.Transfers.cs).
                    _ = TransferHistoryService.AppendAsync(stats.NewlyCompleted);
                    // Surface failures in the activity log; successes stay in the
                    // dedicated history view so a heavy transfer session doesn't
                    // evict mount/OAuth events from the 2000-entry rolling buffer.
                    foreach (var e in stats.NewlyCompleted)
                    {
                        if (!string.IsNullOrEmpty(e.Error))
                        {
                            var name = string.IsNullOrEmpty(e.FileName) ? "(unnamed)" : e.FileName;
                            activityLog.Error($"Transfer failed on {e.RemoteName}:{name} — {e.Error}");
                        }
                    }
                }
            }
            UpdateStatusBar();
            RefreshActiveList();
            statsPollInFlight = false;
        }

        // Push the fresh per-mount rate onto any MountRow currently visible in the mounts
        // card. Rows are transient (recreated on rebuild) so we walk the panel instead of
        // caching references.
        private void ApplyPerMountStats(IReadOnlyDictionary<string, MountSample> perMount)
        {
            foreach (Control c in mountsListFlow.Controls)
            {
                if (c is MountRow row && perMount.TryGetValue(row.Mount.Id, out var sample))
                {
                    row.UpdateLiveRate(sample.BytesPerSecond);
                    // Only push when the poller has a fresh reading. A transient failure
                    // (Google Drive's /operations/about routinely trips the 2s HTTP
                    // timeout) would otherwise blank the bar for 30s until the cache
                    // refreshes, which shows as flicker. The row zeros itself when the
                    // mount is unmounted (MountRow.Bind), so we only *need* to clear here
                    // on a real state change.
                    if (sample.HasSpaceInfo)
                        row.UpdateSpace(sample.TotalBytes, sample.UsedBytes);
                }
            }
        }

        // Detect the "process alive but WinFsp detached" case. We debounce transitions in
        // both directions to avoid spurious toasts on a brief RC hiccup or drive-letter
        // enumeration failure, then flip the row's status and notify once per transition.
        private void RunDetachWatchdog(IReadOnlyDictionary<string, MountSample> perMount)
        {
            if (mountService is null) return;
            var changed = false;
            foreach (var m in mounts)
            {
                if (!mountService.IsMounted(m)) { ClearDetachState(m.Id); continue; }
                if (!perMount.TryGetValue(m.Id, out var sample)) { ClearDetachState(m.Id); continue; }

                if (sample.Healthy)
                {
                    unhealthyPollHits.Remove(m.Id);
                    if (detachedMountIds.Remove(m.Id))
                    {
                        changed = true;
                        activityLog.Info($"Mount {m.RemoteName} → {m.DriveLetter} is responsive again.");
                        Notify("Mount responsive",
                            $"{m.RemoteName} → {m.DriveLetter} is back.",
                            ToolTipIcon.Info);
                    }
                    continue;
                }

                var hits = unhealthyPollHits[m.Id] = unhealthyPollHits.GetValueOrDefault(m.Id) + 1;
                if (hits >= DetachHitThreshold && detachedMountIds.Add(m.Id))
                {
                    changed = true;
                    activityLog.Warn($"Mount {m.RemoteName} → {m.DriveLetter} appears detached (drive letter unresponsive).");
                    // AutoMount drives self-heal without user action; others get the
                    // "try Unmount + Mount" nudge so the toast still says something
                    // useful.
                    if (m.AutoMount)
                    {
                        Notify("Mount detached — auto-recovering",
                            $"{m.RemoteName} → {m.DriveLetter} isn't responding. Killing and remounting…",
                            ToolTipIcon.Warning, 5000);
                        OnMountDetached(m);
                    }
                    else
                    {
                        Notify("Mount detached",
                            $"{m.RemoteName} → {m.DriveLetter} isn't responding. Try Unmount + Mount to recover.",
                            ToolTipIcon.Warning, 5000);
                    }
                }
            }
            if (changed)
            {
                UpdateRailStatus();
                RefreshDetail();
            }
        }

        private void ClearDetachState(string mountId)
        {
            unhealthyPollHits.Remove(mountId);
            detachedMountIds.Remove(mountId);
        }

        // Adds the poller's per-tick delta to the lifetime counters, splitting by the
        // current direction-rate ratio (rclone doesn't expose direction-split cumulative
        // bytes, so this is an honest approximation). Throttled disk save.
        private void AccumulateLifetimeTotals(AggregateStats stats)
        {
            if (stats.DeltaBytesThisTick <= 0) return;
            var down = stats.DownBytesPerSecond;
            var up   = stats.UpBytesPerSecond;
            var sum  = down + up;
            long deltaDown, deltaUp;
            if (sum > 0.5)
            {
                deltaDown = (long)Math.Round(stats.DeltaBytesThisTick * (down / sum));
                deltaUp   = stats.DeltaBytesThisTick - deltaDown;
            }
            else
            {
                // No direction signal — attribute to the down bucket (mount reads dominate).
                deltaDown = stats.DeltaBytesThisTick;
                deltaUp   = 0;
            }
            lifetimeTotals.DownBytes += deltaDown;
            lifetimeTotals.UpBytes   += deltaUp;
            RefreshLifetimeTotalsLabel();

            var nowUtc = DateTime.UtcNow;
            if (nowUtc - lastTotalsSaveUtc >= TotalsSaveInterval)
            {
                lastTotalsSaveUtc = nowUtc;
                _ = TransferTotalsService.SaveAsync(lifetimeTotals);
            }
        }

        private void RefreshLifetimeTotalsLabel()
        {
            if (lifetimeTotalsLabel is null) return;
            lifetimeTotalsLabel.Text =
                $"↓ {FormatBytesLong(lifetimeTotals.DownBytes)}   ·   " +
                $"↑ {FormatBytesLong(lifetimeTotals.UpBytes)}   ·   " +
                $"Total {FormatBytesLong(lifetimeTotals.TotalBytes)}";
            if (lifetimeSinceLabel is not null)
            {
                var since = lifetimeTotals.FirstSeenUtc == default
                    ? DateTime.UtcNow
                    : lifetimeTotals.FirstSeenUtc;
                lifetimeSinceLabel.Text = $"Counting since {since.ToLocalTime():yyyy-MM-dd HH:mm}.";
            }
        }

        private async Task ResetLifetimeTotalsAsync()
        {
            var ok = MessageBox.Show(
                "Reset lifetime transfer totals to zero?",
                "Reset totals", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ok != DialogResult.Yes) return;
            lifetimeTotals = new TransferTotals { FirstSeenUtc = DateTime.UtcNow };
            RefreshLifetimeTotalsLabel();
            await TransferTotalsService.SaveAsync(lifetimeTotals);
            lastTotalsSaveUtc = DateTime.UtcNow;
        }

        private static string FormatBytesLong(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
            var i = 0;
            double v = bytes;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v >= 100 ? $"{v:F0} {u[i]}" : $"{v:F2} {u[i]}";
        }
    }
}
