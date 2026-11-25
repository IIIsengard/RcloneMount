using RcloneMount.Models;

namespace RcloneMount.Tests;

public class ScheduleTests
{
    // Verbatim copy of v1.0.x MainForm.TryParseDailySchedule: the oracle for backward compatibility.
    private static bool LegacyParse(string? expression, out TimeSpan target)
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

    private static readonly TimeZoneInfo Uk = Zone();
    private static TimeZoneInfo Zone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
    }

    // UK 2026: clocks go forward Sun 29 Mar 01:00Z (01:00 -> 02:00), back Sun 25 Oct 01:00Z (02:00 -> 01:00).
    private static DateTimeOffset Utc(int mo, int d, int h = 0, int mi = 0, int s = 0) =>
        new(2026, mo, d, h, mi, s, TimeSpan.Zero);

    private static DateTimeOffset? Next(string text, DateTimeOffset? last, DateTimeOffset now)
    {
        Assert.True(Schedule.TryParse(text, out var s, out var err), err);
        return s!.NextRun(last, now, Uk)?.ToUniversalTime();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParse_EmptyMeansNoSchedule(string? text)
    {
        Assert.True(Schedule.TryParse(text, out var s, out var err));
        Assert.Null(s);
        Assert.Null(err);
    }

    [Theory]
    [InlineData("daily 03:00")]
    [InlineData("Daily 23:59")]
    [InlineData("  daily   3:5  ")]
    [InlineData("weekdays 08:30")]
    [InlineData("WEEKDAYS 00:00")]
    [InlineData("mon 09:00")]
    [InlineData("mon,wed 21:30")]
    [InlineData("Sat,Sun 10:00")]
    [InlineData("every 6h")]
    [InlineData("Every 1h")]
    [InlineData("every 24H")]
    [InlineData("every 168h")]
    public void TryParse_AcceptsValid(string text)
    {
        Assert.True(Schedule.TryParse(text, out var s, out var err), err);
        Assert.NotNull(s);
    }

    [Theory]
    [InlineData("daily 24:00")]
    [InlineData("daily 12:60")]
    [InlineData("daily")]
    [InlineData("daily 0300")]
    [InlineData("weekly 03:00")]
    [InlineData("weekdays")]
    [InlineData("weekdays 25:00")]
    [InlineData("funday 03:00")]
    [InlineData("mon,xyz 03:00")]
    [InlineData("mon,,wed 03:00")]
    [InlineData("every 0h")]
    [InlineData("every 169h")]
    [InlineData("every h")]
    [InlineData("every 6")]
    [InlineData("every 6m")]
    [InlineData("every 1.5h")]
    [InlineData("03:00")]
    public void TryParse_RejectsInvalidWithMessage(string text)
    {
        Assert.False(Schedule.TryParse(text, out var s, out var err));
        Assert.Null(s);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    // Every string a v1.0.x settings.json can hold: whatever the old parser accepted still
    // parses to the same time; rejected strings outside the new grammar stay rejected (empty = no schedule).
    [Theory]
    [InlineData("daily 03:00")]
    [InlineData("Daily 23:59")]
    [InlineData("DAILY 00:00")]
    [InlineData("daily 24:00")]
    [InlineData("daily 03:60")]
    [InlineData("daily 3:5")]
    [InlineData("daily 03 : 00")]
    [InlineData("daily  03:00")]
    [InlineData("  daily 03:00  ")]
    [InlineData("daily +3:00")]
    [InlineData("daily -0:00")]
    [InlineData("daily -1:00")]
    [InlineData("daily 03:00:00")]
    [InlineData("daily 0300")]
    [InlineData("daily ")]
    [InlineData("daily")]
    [InlineData("daily\t03:00")]
    [InlineData("daily 03:00 extra")]
    [InlineData("weekly 03:00")]
    [InlineData("hourly")]
    [InlineData("03:00")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_KeepsEveryLegacyOutcome(string text)
    {
        var legacy = LegacyParse(text, out var legacyTime);
        var ok = Schedule.TryParse(text, out var s, out _);
        if (legacy)
        {
            Assert.True(ok);
            Assert.NotNull(s);
            // Same fire time on an ordinary day.
            var next = s!.NextRun(null, Utc(6, 10, 12).ToOffset(TimeSpan.FromHours(1)), Uk)!.Value;
            Assert.Equal(legacyTime, TimeZoneInfo.ConvertTime(next, Uk).TimeOfDay);
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            Assert.True(ok);
            Assert.Null(s);
        }
        else Assert.False(ok);
    }

    [Fact]
    public void NextRun_Daily_AcrossMidnight()
    {
        // fired at 00:30 today, now 23:50 -> tomorrow 00:30 (winter: UTC == local)
        Assert.Equal(Utc(1, 15, 0, 30), Next("daily 00:30", Utc(1, 14, 0, 30, 5), Utc(1, 14, 23, 50)));
        // fired yesterday, time already passed today -> due (today's slot: missed run fires at launch)
        Assert.Equal(Utc(1, 14, 3, 0), Next("daily 03:00", Utc(1, 13, 3, 0, 5), Utc(1, 14, 10, 0)));
        // never fired and time passed today -> today's slot (due now), as v1.0 did
        Assert.Equal(Utc(1, 14, 3, 0), Next("daily 03:00", null, Utc(1, 14, 10, 0)));
        // never fired, time still ahead -> later today
        Assert.Equal(Utc(1, 14, 3, 0), Next("daily 03:00", null, Utc(1, 14, 1, 0)));
        // slot exactly at local midnight is not skipped
        Assert.Equal(Utc(1, 14, 0, 0), Next("daily 00:00", null, Utc(1, 14, 0, 0, 1)));
    }

    [Fact]
    public void NextRun_AfterDaysAway_OnlyCatchesUpTodaysSlot()
    {
        // away 3 days, back at 01:00 before today's 03:00 slot: wait for 03:00, no extra catch-up run.
        Assert.Equal(Utc(1, 14, 3, 0), Next("daily 03:00", Utc(1, 11, 3, 0, 5), Utc(1, 14, 1, 0)));
    }

    [Fact]
    public void NextRun_Weekdays_And_DayList()
    {
        // Fri 2026-01-16 fired, now Sat -> Mon 19th
        Assert.Equal(Utc(1, 19, 8, 30), Next("weekdays 08:30", Utc(1, 16, 8, 30, 5), Utc(1, 17, 12)));
        // mon,wed: fired Wed 14th 21:30, now Wed 23:40 -> Mon 19th
        Assert.Equal(Utc(1, 19, 21, 30), Next("mon,wed 21:30", Utc(1, 14, 21, 30, 5), Utc(1, 14, 23, 40)));
        // Tue 13th, Monday's run done -> Wed 14th
        Assert.Equal(Utc(1, 14, 21, 30), Next("mon,wed 21:30", Utc(1, 12, 21, 30, 5), Utc(1, 13, 9)));
    }

    [Fact]
    public void NextRun_Every_AnchorsOnLastRun_ElseDueNow()
    {
        var last = Utc(1, 14, 2, 17);
        Assert.Equal(Utc(1, 14, 8, 17), Next("every 6h", last, Utc(1, 14, 3)));
        // overdue -> returns the (past) due time; fires once, no catch-up of the missed slots
        Assert.Equal(Utc(1, 14, 8, 17), Next("every 6h", last, Utc(1, 15, 3)));
        // no anchor yet: shown as now + N (the scheduler anchors it without running, see AnchorIfNew)
        var now = Utc(1, 14, 3);
        Assert.Equal(Utc(1, 14, 9), Next("every 6h", null, now));
        // absolute hours, not wall clock: across spring-forward 6h later is still +6h
        Assert.Equal(Utc(3, 29, 6, 0), Next("every 6h", Utc(3, 29, 0, 0), Utc(3, 29, 1)));
    }

    [Fact]
    public void NextRun_SpringForwardGap_RunsAtFirstValidInstant()
    {
        // 29 Mar 2026: local clock jumps 01:00 -> 02:00, so 01:30 local does not exist.
        // Runs at the first valid instant after it: 02:00 BST == 01:00Z.
        Assert.Equal(Utc(3, 29, 1, 0), Next("daily 01:30", Utc(3, 28, 1, 30, 5), Utc(3, 28, 12)));
        // once fired in the gap it does not run again that day
        Assert.Equal(Utc(3, 30, 0, 30), Next("daily 01:30", Utc(3, 29, 1, 0, 5), Utc(3, 29, 1, 5)));
    }

    [Fact]
    public void NextRun_FallBackAmbiguous_RunsOnce()
    {
        // 25 Oct 2026: 02:00 BST -> 01:00 GMT, so 01:30 local happens twice (00:30Z, 01:30Z). Runs at the first.
        Assert.Equal(Utc(10, 25, 0, 30), Next("daily 01:30", Utc(10, 24, 0, 30, 5), Utc(10, 24, 12)));
        // fired at the first 01:30; in the repeated hour it must NOT run again
        Assert.Equal(Utc(10, 26, 1, 30), Next("daily 01:30", Utc(10, 25, 0, 30, 5), Utc(10, 25, 1, 35)));
    }

    [Fact]
    public void NextRun_Calendar_AtMostOncePerLocalDay()
    {
        // fired 03:00 today; schedule edited to 09:00 and it is now 10:00 -> tomorrow, not a second run today
        Assert.Equal(Utc(1, 15, 9, 0), Next("daily 09:00", Utc(1, 14, 3, 0, 5), Utc(1, 14, 10, 0)));
        // same for a day list that still matches today
        Assert.Equal(Utc(1, 21, 9, 0), Next("wed 09:00", Utc(1, 14, 3, 0, 5), Utc(1, 14, 10, 0)));
    }

    [Fact]
    public void AnchorIfNew_StampsIntervalJobWithoutRunning_OnlyOnce()
    {
        var now = Utc(1, 14, 10);
        var job = new TransferJob { Schedule = "every 6h" };
        Assert.True(Schedule.AnchorIfNew(job, now));
        Assert.Equal(now.UtcDateTime, job.LastScheduledFireUtc);
        // not due: first run is N hours after the anchor
        Assert.Equal(Utc(1, 14, 16), Schedule.NextRunOf(job, now, Uk)!.Value.ToUniversalTime());
        Assert.False(Schedule.AnchorIfNew(job, now.AddHours(1)));
        Assert.Equal(now.UtcDateTime, job.LastScheduledFireUtc);
    }

    [Theory]
    [InlineData("daily 03:00")]
    [InlineData("")]
    [InlineData("nonsense")]
    public void AnchorIfNew_LeavesOtherJobsAlone(string schedule)
    {
        var job = new TransferJob { Schedule = schedule };
        Assert.False(Schedule.AnchorIfNew(job, Utc(1, 14, 10)));
        Assert.Null(job.LastScheduledFireUtc);
    }

    [Fact]
    public void FireStamp_OnTimeKeepsCadence_LateReanchors()
    {
        var job = new TransferJob { Schedule = "every 6h", LastScheduledFireUtc = Utc(1, 14, 2, 17).UtcDateTime };
        var due = Utc(1, 14, 8, 17);

        // fired 20 s after due (tick delay): stamps the due instant, no drift
        Assert.Equal(due.UtcDateTime, Schedule.FireStamp(job, due.AddSeconds(20)));
        job.LastScheduledFireUtc = Schedule.FireStamp(job, due.AddSeconds(20));
        Assert.Equal(Utc(1, 14, 14, 17), Schedule.NextRunOf(job, due.AddSeconds(20), Uk)!.Value.ToUniversalTime());

        // woke from sleep 3 h late: stamps now
        job.LastScheduledFireUtc = Utc(1, 14, 2, 17).UtcDateTime;
        var late = due.AddHours(3);
        Assert.Equal(late.UtcDateTime, Schedule.FireStamp(job, late));

        // calendar jobs and resumed runs always stamp now
        Assert.Equal(due.UtcDateTime, Schedule.FireStamp(new TransferJob { Schedule = "daily 03:00" }, due));
    }

    [Fact]
    public void Edit_WhileRunning_RunResultLandsOnTheEditedJob()
    {
        var fired = Utc(1, 14, 3, 0, 5).UtcDateTime;
        var old = new TransferJob { Id = "a", Name = "n", Schedule = "daily 03:00", InFlight = true,
            InFlightStartedUtc = fired, LastScheduledFireUtc = fired };
        var jobs = new List<TransferJob> { old };
        var edited = new TransferJob { Id = "a", Name = "renamed", Schedule = "daily 03:00" };

        MainForm.UpsertInto(jobs, edited);
        Assert.Same(edited, jobs[0]);
        Assert.True(edited.InFlight);
        Assert.Equal(fired, edited.LastScheduledFireUtc);

        // the scheduler's completion still holds the OLD object
        var done = Utc(1, 14, 3, 30).UtcDateTime;
        Assert.Same(edited, MainForm.CompleteRun(jobs, old, "Success", done));
        Assert.False(edited.InFlight);
        Assert.Null(edited.InFlightStartedUtc);
        Assert.Equal(done, edited.LastRunUtc);
        Assert.Equal("Success", edited.LastResultKind);
    }

    [Fact]
    public void Edit_AfterFire_KeepsWatermark_SoDailyDoesNotRunTwice()
    {
        var fired = Utc(1, 14, 3, 0, 5).UtcDateTime;
        var jobs = new List<TransferJob> { new() { Id = "a", Schedule = "daily 03:00", LastScheduledFireUtc = fired,
            LastRunUtc = fired, LastResultKind = "Success" } };
        MainForm.UpsertInto(jobs, new TransferJob { Id = "a", Schedule = "daily 09:00" });

        Assert.Equal(fired, jobs[0].LastScheduledFireUtc);
        Assert.Equal("Success", jobs[0].LastResultKind);
        Assert.Equal(Utc(1, 15, 9, 0), Schedule.NextRunOf(jobs[0], Utc(1, 14, 10), Uk)!.Value.ToUniversalTime());
    }

    [Fact]
    public void CompleteRun_JobDeletedMidRun_DoesNotThrow()
    {
        var gone = new TransferJob { Id = "x", InFlight = true };
        Assert.Same(gone, MainForm.CompleteRun(new List<TransferJob>(), gone, "Failed", DateTime.UtcNow));
    }
}
