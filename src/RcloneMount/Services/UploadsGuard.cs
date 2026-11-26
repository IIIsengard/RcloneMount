using System.Collections.Generic;

namespace RcloneMount.Services
{
    public enum UploadsGuardStep { Proceed, Ask, Refuse } // public: xUnit theory parameter

    // Decisions behind MainForm.UploadsGuardAsync, kept pure so they can be tested.
    internal static class UploadsGuard
    {
        // Per-mount counts from vfs/stats; null = that live mount's rclone did not answer.
        // One unknown makes the total unknown: a failed call must never read as "0 pending".
        internal static int? Sum(IEnumerable<int?> counts)
        {
            var total = 0;
            foreach (var c in counts)
            {
                if (c is null) return null;
                total += c.Value;
            }
            return total;
        }

        // Interactive (first check and every Wait re-count): unknown keeps asking.
        // Automatic: unknown proceeds, so a dead or hung RC cannot block a remount or an update.
        internal static UploadsGuardStep Decide(int? pending, bool interactive) =>
            pending == 0 ? UploadsGuardStep.Proceed
            : interactive ? UploadsGuardStep.Ask
            : pending is null ? UploadsGuardStep.Proceed
            : UploadsGuardStep.Refuse;
    }
}
