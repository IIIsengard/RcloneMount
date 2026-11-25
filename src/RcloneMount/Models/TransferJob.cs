using System;

namespace RcloneMount.Models
{
    // Saved transfer job. Mirrors the fields the user picks in TransferDialog
    // plus a friendly name, last-run timestamp, and last-run outcome so the
    // Jobs list can show status without touching the ActivityLog. Kept as a
    // POCO — persistence is handled by the same System.Text.Json path as
    // AppSettings.Mounts, so no attributes are needed.
    public class TransferJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;

        // Operation stored as its string name (Copy / Sync / Move) rather than the
        // enum ordinal, so reordering TransferOperation later doesn't silently
        // reinterpret saved jobs.
        public string Operation { get; set; } = "Copy";

        public string SourceRemote { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string DestRemote { get; set; } = string.Empty;
        public string DestPath { get; set; } = string.Empty;

        public bool DryRun { get; set; }
        public bool ServerSideAcrossConfigs { get; set; }

        // Check / cryptcheck only. Persisted alongside the transfer flags so a
        // saved check job re-runs with the same coverage / speed trade-off.
        public bool OneWay { get; set; }
        public bool SizeOnly { get; set; }

        // Bisync only. Persisted so saved bisync jobs keep their conflict policy
        // and safety settings between runs. Empty ConflictResolve = rclone default.
        // MaxDeletePct == 0 = don't pass --max-delete (rclone default 50).
        public string ConflictResolve { get; set; } = string.Empty;
        public bool CheckAccess { get; set; }
        public int MaxDeletePct { get; set; }

        // Filter fields carried as plain strings (mirroring FilterRules); the
        // dialog rebuilds a FilterRules instance from these on load.
        public string FilterPatterns { get; set; } = string.Empty;
        public string FilterMinSize { get; set; } = string.Empty;
        public string FilterMaxSize { get; set; } = string.Empty;
        public string FilterMinAge { get; set; } = string.Empty;
        public string FilterMaxAge { get; set; } = string.Empty;

        // Bandwidth cap in rclone's format ("10M", "500K", or the schedule form
        // "08:00,512 12:00,10M 23:00,off"). Empty = uncapped.
        public string BwLimit { get; set; } = string.Empty;

        // Raw rclone flags appended after the filter args ("--fast-list --transfers 8").
        // Escape hatch so any rclone option works without a dedicated field. Empty = none.
        public string ExtraFlags { get; set; } = string.Empty;

        // Schedule expression, parsed by Schedule.TryParse ("daily 03:00", "weekdays 03:00",
        // "mon,wed 21:30", "every 6h"; 24-hour local time). Empty = manual only. In-process —
        // needs the app to be running when the time hits.
        public string Schedule { get; set; } = string.Empty;
        // Watermark used to avoid firing twice per interval; the scheduler updates
        // this when it kicks a run off.
        public DateTime? LastScheduledFireUtc { get; set; }

        public DateTime? LastRunUtc { get; set; }
        // "Success" | "Failed" | "Cancelled" | null. String rather than an enum so
        // future outcomes (Partial, Skipped) don't need a schema migration.
        public string? LastResultKind { get; set; }

        // Set true when a run starts and cleared in the finally. Persisted so a
        // process crash / power loss / logout mid-run leaves a marker the next
        // launch can spot and offer to resume. "Resume" here just re-invokes
        // the same rclone command — rclone's own differential scan skips files
        // already at the destination, so this works uniformly across backends.
        public bool InFlight { get; set; }
        public DateTime? InFlightStartedUtc { get; set; }
    }
}
