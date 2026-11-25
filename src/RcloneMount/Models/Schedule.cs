using System;
using System.Linq;

namespace RcloneMount.Models
{
    // Parsed TransferJob.Schedule text, local time:
    //   "daily HH:MM" | "weekdays HH:MM" | "mon,wed HH:MM" | "every Nh" (1-168). Empty = manual only.
    // Everything that reads the text (dialog, scheduler, Jobs list) goes through here, so a
    // bad expression is reported once, in one place, instead of silently never running.
    public sealed class Schedule
    {
        public const string Syntax = "daily 03:00, weekdays 03:00, mon,wed 21:30 or every 6h";

        private static readonly string[] DayNames = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

        private int dayMask;          // bit per DayOfWeek; calendar kinds only
        private TimeSpan time;
        private int everyHours;       // > 0 = "every Nh"

        private Schedule() { }

        public bool IsInterval => everyHours > 0;

        // True for valid text AND for empty text (schedule stays null = no schedule).
        // False sets `error` to a message fit for the dialog.
        public static bool TryParse(string? text, out Schedule? schedule, out string? error)
        {
            schedule = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;
            var s = text.Trim();
            var sp = s.IndexOf(' ');
            var head = sp < 0 ? s : s[..sp];
            var rest = sp < 0 ? "" : s[(sp + 1)..].Trim();

            if (head.Equals("every", StringComparison.OrdinalIgnoreCase))
            {
                if (rest.Length > 1 && (rest[^1] == 'h' || rest[^1] == 'H')
                    && int.TryParse(rest[..^1], System.Globalization.NumberStyles.None, null, out var n)
                    && n >= 1 && n <= 168)
                {
                    schedule = new Schedule { everyHours = n };
                    return true;
                }
                error = "Use 'every Nh' with N from 1 to 168, e.g. every 6h.";
                return false;
            }

            int mask;
            if (head.Equals("daily", StringComparison.OrdinalIgnoreCase)) mask = 0x7F;
            else if (head.Equals("weekdays", StringComparison.OrdinalIgnoreCase)) mask = 0b0111110;
            else
            {
                mask = 0;
                foreach (var d in head.Split(','))
                {
                    var i = Array.IndexOf(DayNames, d.ToLowerInvariant());
                    if (i < 0)
                    {
                        error = $"Unknown schedule '{Clip(head)}'. Try {Syntax}.";
                        return false;
                    }
                    mask |= 1 << i;
                }
            }

            // Same lenient time rules as v1.0.x's daily parser so saved schedules keep working.
            var parts = rest.Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var h) || h < 0 || h > 23
                || !int.TryParse(parts[1], out var m) || m < 0 || m > 59)
            {
                error = $"'{Clip(rest)}' is not a time. Use 24-hour HH:MM, e.g. {Clip(head)} 03:00.";
                return false;
            }
            schedule = new Schedule { dayMask = mask, time = new TimeSpan(h, m, 0) };
            return true;
        }

        // Next time this schedule is due that has not run yet; a value <= now means due now.
        // `last` = when the job last fired (null = never).
        //  - daily/weekdays/day list: at most one run per local calendar day (as v1.0). The search
        //    starts today, or tomorrow when the last fire was today, and never before today, so a
        //    missed slot catches up at launch only for today.
        //  - every Nh: last + N hours; with no anchor yet, now + N (the scheduler stamps the anchor
        //    on first sight via AnchorIfNew, without running).
        //  - A wall-clock time inside a spring-forward gap runs at the first valid instant after it;
        //    an ambiguous fall-back time runs at its first occurrence, once.
        public DateTimeOffset? NextRun(DateTimeOffset? last, DateTimeOffset now, TimeZoneInfo tz)
        {
            if (everyHours > 0) return (last ?? now).AddHours(everyHours);

            var today = TimeZoneInfo.ConvertTime(now, tz).Date;
            var lastDay = last is { } f ? TimeZoneInfo.ConvertTime(f, tz).Date : DateTime.MinValue;
            var first = lastDay >= today ? lastDay.AddDays(1) : today;
            for (var i = 0; i < 8; i++)
            {
                var d = first.AddDays(i);
                if ((dayMask & (1 << (int)d.DayOfWeek)) != 0) return At(d, time, tz);
            }
            return null;
        }

        // A job fired within this long after its due instant counts as on time.
        private static readonly TimeSpan OnTime = TimeSpan.FromSeconds(90);

        // First time the scheduler sees an "every Nh" job with no anchor (e.g. saved by v1.0, which
        // ignored it): start the clock now WITHOUT running, so an upgrade never starts a surprise
        // sync/move. Returns true when it stamped (caller saves settings).
        public static bool AnchorIfNew(TransferJob job, DateTimeOffset now)
        {
            if (job.LastScheduledFireUtc is not null
                || !TryParse(job.Schedule, out var s, out _) || s is not { IsInterval: true }) return false;
            job.LastScheduledFireUtc = now.UtcDateTime;
            return true;
        }

        // What to record as LastScheduledFireUtc for a run starting now. An "every Nh" run that
        // fires on time stamps its due instant, so the cadence does not drift by tick delay; a
        // late (catch-up) run stamps now and re-anchors.
        public static DateTime FireStamp(TransferJob job, DateTimeOffset now)
        {
            if (TryParse(job.Schedule, out var s, out _) && s is { IsInterval: true }
                && job.LastScheduledFireUtc is { } u)
            {
                var due = new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc)).AddHours(s.everyHours);
                if (now >= due && now - due <= OnTime) return due.UtcDateTime;
            }
            return now.UtcDateTime;
        }

        // The job's next run, or null when it has no (valid) schedule.
        public static DateTimeOffset? NextRunOf(TransferJob job, DateTimeOffset now, TimeZoneInfo? tz = null)
        {
            if (!TryParse(job.Schedule, out var s, out _) || s is null) return null;
            DateTimeOffset? last = job.LastScheduledFireUtc is { } u
                ? new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc)) : null;
            return s.NextRun(last, now, tz ?? TimeZoneInfo.Local);
        }

        private static string Clip(string s) => s.Length > 20 ? s[..20] + "…" : s;

        private static DateTimeOffset At(DateTime date, TimeSpan time, TimeZoneInfo tz)
        {
            var dt = DateTime.SpecifyKind(date.Date + time, DateTimeKind.Unspecified);
            if (tz.IsAmbiguousTime(dt))
                return new DateTimeOffset(dt, tz.GetAmbiguousTimeOffsets(dt).Max());
            if (!tz.IsInvalidTime(dt))
                return new DateTimeOffset(dt, tz.GetUtcOffset(dt));

            // Gap: read the time with the pre-gap offset (lands just after the jump), then walk
            // back to the jump itself, the first valid instant. Gaps are minutes-aligned and < 1 day.
            var x = new DateTimeOffset(dt, tz.GetUtcOffset(dt.AddDays(-1))).ToUniversalTime();
            var post = tz.GetUtcOffset(x);
            while (tz.GetUtcOffset(x.AddMinutes(-1)) == post) x = x.AddMinutes(-1);
            return TimeZoneInfo.ConvertTime(x, tz);
        }
    }
}
