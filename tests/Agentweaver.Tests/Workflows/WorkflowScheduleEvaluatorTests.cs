using FluentAssertions;
using Agentweaver.Api.Workflows;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Unit tests for <see cref="WorkflowScheduleEvaluator"/>: the pure "is it time to fire" calculator
/// for a schedule trigger (issue #53). Every case supplies an explicit <c>now</c> — no wall-clock
/// sleeps, no <c>DateTimeOffset.UtcNow</c> — so the whole cadence matrix (daily/weekly/monthly, before
/// vs. after the scheduled instant, repeated calls within the same occurrence) is deterministic.
/// </summary>
public sealed class WorkflowScheduleEvaluatorTests
{
    private static WorkflowTrigger Daily(TimeOnly timeOfDay) => new()
    {
        Type = WorkflowTriggerType.Schedule,
        Interval = WorkflowScheduleInterval.Daily,
        TimeOfDay = timeOfDay,
    };

    private static WorkflowTrigger Weekly(DayOfWeek dayOfWeek, TimeOnly timeOfDay) => new()
    {
        Type = WorkflowTriggerType.Schedule,
        Interval = WorkflowScheduleInterval.Weekly,
        DayOfWeek = dayOfWeek,
        TimeOfDay = timeOfDay,
    };

    private static WorkflowTrigger Monthly(int dayOfMonth, TimeOnly timeOfDay) => new()
    {
        Type = WorkflowTriggerType.Schedule,
        Interval = WorkflowScheduleInterval.Monthly,
        DayOfMonth = dayOfMonth,
        TimeOfDay = timeOfDay,
    };

    // ── Daily ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Daily_BeforeTimeOfDay_NotDue()
    {
        var trigger = Daily(new TimeOnly(9, 0));
        var now = new DateTimeOffset(2026, 7, 13, 8, 59, 0, TimeSpan.Zero);

        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, now, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Daily_AtOrAfterTimeOfDay_Due()
    {
        var trigger = Daily(new TimeOnly(9, 0));
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);

        var due = WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, now, out var periodKey, out var scheduledAt);

        due.Should().BeTrue();
        periodKey.Should().Be("2026-07-13");
        scheduledAt.Should().Be(new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Daily_LaterSameDay_SamePeriodKey_AllowsCallerToDedupe()
    {
        var trigger = Daily(new TimeOnly(9, 0));
        var firstTick = new DateTimeOffset(2026, 7, 13, 9, 0, 30, TimeSpan.Zero);
        var secondTick = new DateTimeOffset(2026, 7, 13, 14, 0, 0, TimeSpan.Zero);

        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, firstTick, out var key1, out _).Should().BeTrue();
        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, secondTick, out var key2, out _).Should().BeTrue();

        key1.Should().Be(key2, because: "both ticks fall within the same day's single occurrence");
    }

    [Fact]
    public void Daily_NextDay_ProducesFreshPeriodKey()
    {
        var trigger = Daily(new TimeOnly(9, 0));
        var day1 = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero);

        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, day1, out var key1, out _);
        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, day2, out var key2, out _);

        key1.Should().NotBe(key2);
    }

    // ── Weekly ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Weekly_OnTargetDayBeforeTime_NotDue()
    {
        // 2026-07-13 is a Monday.
        var trigger = Weekly(DayOfWeek.Monday, new TimeOnly(9, 0));
        var now = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);

        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, now, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Weekly_OnTargetDayAtTime_Due()
    {
        var trigger = Weekly(DayOfWeek.Monday, new TimeOnly(9, 0));
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);

        var due = WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, now, out var periodKey, out var scheduledAt);

        due.Should().BeTrue();
        periodKey.Should().Be("2026-07-13");
        scheduledAt.Should().Be(new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Weekly_LaterInWeek_StillDue_SamePeriodAsMonday()
    {
        var trigger = Weekly(DayOfWeek.Monday, new TimeOnly(9, 0));
        var monday = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var wednesday = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, monday, out var mondayKey, out _).Should().BeTrue();
        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, wednesday, out var wedKey, out _).Should().BeTrue();

        wedKey.Should().Be(mondayKey, because: "the occurrence is still 'this Monday' until next Monday arrives");
    }

    [Fact]
    public void Weekly_BeforeTargetDayInWeek_NotDueForThisWeekYet_ButPreviousWeekMayStillBeDue()
    {
        // Sunday 2026-07-12, one day before the Monday target: the most recent Monday occurrence is
        // 2026-07-06 (last week), and it is long past due (already fired/dedup'd by the caller for
        // that period), so it still reports due=true with LAST week's key, not a "future" Monday.
        var trigger = Weekly(DayOfWeek.Monday, new TimeOnly(9, 0));
        var sunday = new DateTimeOffset(2026, 7, 12, 23, 0, 0, TimeSpan.Zero);

        var due = WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, sunday, out var periodKey, out _);

        due.Should().BeTrue();
        periodKey.Should().Be("2026-07-06");
    }

    [Fact]
    public void Weekly_NextWeek_ProducesFreshPeriodKey()
    {
        var trigger = Weekly(DayOfWeek.Monday, new TimeOnly(9, 0));
        var week1 = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var week2 = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);

        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, week1, out var key1, out _);
        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, week2, out var key2, out _);

        key1.Should().NotBe(key2);
    }

    // ── Monthly ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Monthly_BeforeDayOfMonth_NotDueYet_PreviousMonthStillDue()
    {
        var trigger = Monthly(1, new TimeOnly(8, 0));
        // June 15th: this month's occurrence (June 1) already passed, so it's due for June's key.
        var now = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

        var due = WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, now, out var periodKey, out _);

        due.Should().BeTrue();
        periodKey.Should().Be("2026-06");
    }

    [Fact]
    public void Monthly_OnDayOfMonthBeforeTime_NotDue()
    {
        var trigger = Monthly(15, new TimeOnly(8, 0));
        var now = new DateTimeOffset(2026, 7, 15, 7, 59, 0, TimeSpan.Zero);

        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, now, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Monthly_OnDayOfMonthAtTime_Due()
    {
        var trigger = Monthly(15, new TimeOnly(8, 0));
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

        var due = WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, now, out var periodKey, out _);

        due.Should().BeTrue();
        periodKey.Should().Be("2026-07");
    }

    [Fact]
    public void Monthly_NextMonth_ProducesFreshPeriodKey()
    {
        var trigger = Monthly(1, new TimeOnly(8, 0));
        var june = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var july = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);

        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, june, out var key1, out _);
        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, july, out var key2, out _);

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Monthly_CrossesYearBoundary_ComputesPreviousDecember()
    {
        // January 1 2026, day_of_month=1: due immediately (this month's own occurrence), so this
        // proves the "before dayOfMonth -> previous month" branch also correctly handles Jan -> Dec
        // of the PRIOR year when the target day hasn't arrived yet.
        var trigger = Monthly(15, new TimeOnly(8, 0));
        var now = new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);

        var due = WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, now, out var periodKey, out var scheduledAt);

        due.Should().BeTrue();
        periodKey.Should().Be("2025-12");
        scheduledAt.Should().Be(new DateTimeOffset(2025, 12, 15, 8, 0, 0, TimeSpan.Zero));
    }

    // ── Non-schedule / malformed triggers never report due ──────────────────────────────────

    [Fact]
    public void EventTrigger_NeverDue()
    {
        var trigger = new WorkflowTrigger { Type = WorkflowTriggerType.Event, EventName = "issue.opened" };
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);

        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, now, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void ScheduleTrigger_MissingTimeOfDay_NeverDue()
    {
        var trigger = new WorkflowTrigger
        {
            Type = WorkflowTriggerType.Schedule,
            Interval = WorkflowScheduleInterval.Daily,
            TimeOfDay = null,
        };
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);

        WorkflowScheduleEvaluator.TryGetDueOccurrence(trigger, now, out _, out _).Should().BeFalse();
    }
}
