using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount
{
    // Auto-recover for mounts that break because the OS or network dropped out from
    // under them:
    //   * PowerModeChanged.Resume  — laptop woke; every rclone child process from before
    //     suspend is a zombie because its network stack is toast.
    //   * NetworkAvailabilityChanged(true) — link came back after being unplugged /
    //     Wi-Fi bounced. rclone doesn't automatically reconnect for all backends.
    //   * DetachWatchdog flipped a mount to "Detached" — process is up but the drive
    //     letter no longer answers I/O. Kill + remount is the only real recovery.
    //
    // All three funnel into KickAutoRemountsAsync, which walks the AutoMount list and
    // schedules the existing exponential-backoff RetryLoop for anything not currently
    // healthy. Wake/network events are debounced so a burst on resume (Windows often
    // fires several NetworkAvailabilityChanged=true within a couple of seconds) only
    // triggers one recovery pass.
    public partial class MainForm
    {
        private PowerModeChangedEventHandler? powerModeHandler;
        private System.Net.NetworkInformation.NetworkAvailabilityChangedEventHandler? networkAvailHandler;
        private System.Windows.Forms.Timer? autoRecoverDebounceTimer;
        private string? autoRecoverPendingReason;

        // Windows can fire NetworkAvailabilityChanged=true a handful of times within a
        // second on wake. Coalesce with a short debounce so we don't spin the retry
        // scheduler N times for the same real-world event.
        private const int AutoRecoverDebounceMs = 1500;

        private void HookAutoRecoverSignals()
        {
            if (powerModeHandler is not null) return;

            powerModeHandler = (_, ev) =>
            {
                // Resume covers wake-from-sleep AND wake-from-hibernate. StatusChange is
                // AC↔battery transitions — not interesting for network recovery.
                if (ev.Mode != PowerModes.Resume) return;
                QueueAutoRecover("power resume");
            };
            SystemEvents.PowerModeChanged += powerModeHandler;

            networkAvailHandler = (_, ev) =>
            {
                // We only care about "network came back". A drop just means our current
                // mounts are about to start failing — the detach watchdog handles those
                // once the poll fails a few times.
                if (!ev.IsAvailable) return;
                QueueAutoRecover("network up");
            };
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += networkAvailHandler;
        }

        private void UnhookAutoRecoverSignals()
        {
            if (powerModeHandler is not null)
            {
                try { SystemEvents.PowerModeChanged -= powerModeHandler; } catch { }
                powerModeHandler = null;
            }
            if (networkAvailHandler is not null)
            {
                try { System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= networkAvailHandler; } catch { }
                networkAvailHandler = null;
            }
            if (autoRecoverDebounceTimer is not null)
            {
                autoRecoverDebounceTimer.Stop();
                autoRecoverDebounceTimer.Dispose();
                autoRecoverDebounceTimer = null;
            }
        }

        private void QueueAutoRecover(string reason)
        {
            // Reason is only used for logs — collapse to whichever fired most recently.
            autoRecoverPendingReason = reason;
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action(StartAutoRecoverDebounce)); }
                else StartAutoRecoverDebounce();
            }
            catch { /* handle destroyed mid-flight — harmless */ }
        }

        private void StartAutoRecoverDebounce()
        {
            if (autoRecoverDebounceTimer is null)
            {
                autoRecoverDebounceTimer = new System.Windows.Forms.Timer { Interval = AutoRecoverDebounceMs };
                autoRecoverDebounceTimer.Tick += (_, _) =>
                {
                    autoRecoverDebounceTimer!.Stop();
                    var reason = autoRecoverPendingReason ?? "auto";
                    autoRecoverPendingReason = null;
                    _ = KickAutoRemountsAsync(reason);
                };
            }
            // Restart on each new event so a burst collapses to one fire at the tail.
            autoRecoverDebounceTimer.Stop();
            autoRecoverDebounceTimer.Start();
        }

        // Walks the AutoMount mounts and puts everything that's off-line or detached
        // through the existing exponential-backoff retry loop. Detached mounts get an
        // explicit unmount first because the retry loop treats "process alive" as
        // "already healthy" and returns without doing anything.
        internal async Task KickAutoRemountsAsync(string reason)
        {
            if (mountService is null || winFspInfo is null) return;

            // Snapshot the list — the retry loop mutates state (cancels tokens etc.)
            // and enumeration during modification would throw.
            var candidates = mounts.Where(m => m.AutoMount).ToList();
            var scheduled = 0;

            foreach (var m in candidates)
            {
                if (!remotes.Any(r => r.Name == m.RemoteName)) continue;
                if (RcloneMountService.IsDriveMountedExternally(m.DriveLetter)) continue;

                var isMounted = mountService.IsMounted(m);
                var isDetached = detachedMountIds.Contains(m.Id);

                if (isMounted && !isDetached)
                    continue; // still healthy — leave it alone

                if (isMounted && isDetached)
                {
                    // rclone still answers and has uploads queued: killing it now stops them.
                    // The detach watchdog queues recovery again once the count reaches 0.
                    if (!await UploadsGuardAsync(new[] { m }, "Auto-recover", interactive: false))
                    {
                        if (recoverDeferred.TryGetValue(m.Id, out var d))
                            recoverDeferred[m.Id] = d with { Requeued = false };
                        else
                        {
                            recoverDeferred[m.Id] = new RecoverDeferral(DateTime.UtcNow, Requeued: false, Notified: false);
                            activityLog.Info($"Auto-recover ({reason}): {m.DriveLetter} still has uploads pending; remounting after they finish.");
                        }
                        continue;
                    }
                    recoverDeferred.Remove(m.Id);
                    // Process is alive but the drive letter has stopped answering. The
                    // watchdog can't distinguish "hung" from "will recover on its own"
                    // beyond the DetachHitThreshold it's already waited, so kill and
                    // start fresh.
                    activityLog.Info($"Auto-recover ({reason}): unmounting stale {m.RemoteName} → {m.DriveLetter}.");
                    try { await mountService.UnmountAsync(m); }
                    catch (Exception ex) { activityLog.Warn($"Auto-recover unmount of {m.DriveLetter} failed: {ex.Message}"); }
                    detachedMountIds.Remove(m.Id);
                }

                // Checked after the awaits above: the user may unmount while this pass runs.
                if (m.UnmountedByUser) continue;

                ScheduleRetry(m);
                scheduled++;
            }

            if (scheduled > 0)
            {
                RefreshMountList();
                SetStatus($"Auto-recover ({reason}): scheduling remount for {scheduled} mount(s)...");
                activityLog.Info($"Auto-recover ({reason}): scheduled {scheduled} mount(s) for retry.");
            }
        }

        // Called by the detach watchdog when a mount first flips to Detached. Kicks
        // the same recovery path so AutoMount drives self-heal without waiting for a
        // wake/network signal. Ignored when AutoMount is off — respecting the user's
        // opt-out for that mount.
        internal void OnMountDetached(Mount m)
        {
            if (!m.AutoMount) return;
            QueueAutoRecover($"stale mount {m.DriveLetter}");
        }
    }
}
