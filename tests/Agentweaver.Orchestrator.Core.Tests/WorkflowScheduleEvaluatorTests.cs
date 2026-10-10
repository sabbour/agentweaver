using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class WorkflowScheduleEvaluatorTests
{
    [Fact]
    public void DailyOccurrenceUsesUtcAndBecomesDueAtConfiguredTime()
    {
        var schedule = Daily(new TimeOnly(9, 0));

        var before = WorkflowScheduleEvaluator.Evaluate(
            schedule,
            new DateTimeOffset(2026, 7, 13, 13, 59, 59, TimeSpan.FromHours(5)));
        var at = WorkflowScheduleEvaluator.Evaluate(
            schedule,
            new DateTimeOffset(2026, 7, 13, 14, 0, 0, TimeSpan.FromHours(5)));

        Assert.True(before.IsValid);
        Assert.False(before.IsDue);
        Assert.True(at.IsValid);
        Assert.True(at.IsDue);
        Assert.Equal("2026-07-13", at.Occurrence!.PeriodKey);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero),
            at.Occurrence.ScheduledAtUtc);
    }

    [Fact]
    public void DailyRepeatedEvaluationKeepsPeriodAndIdempotencyKeyStable()
    {
        var schedule = Daily(new TimeOnly(9, 0));
        var first = WorkflowScheduleEvaluator.Evaluate(
            schedule,
            new DateTimeOffset(2026, 7, 13, 9, 0, 30, TimeSpan.Zero)).Occurrence!;
        var later = WorkflowScheduleEvaluator.Evaluate(
            schedule,
            new DateTimeOffset(2026, 7, 13, 14, 0, 0, TimeSpan.Zero)).Occurrence!;
        var nextDay = WorkflowScheduleEvaluator.Evaluate(
            schedule,
            new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero)).Occurrence!;

        Assert.Equal(first.PeriodKey, later.PeriodKey);
        Assert.Equal(first.GetIdempotencyKey("triage"), later.GetIdempotencyKey("triage"));
        Assert.NotEqual(first.PeriodKey, nextDay.PeriodKey);
        Assert.NotEqual(first.GetIdempotencyKey("triage"), nextDay.GetIdempotencyKey("triage"));
        Assert.Equal(
            "workflow-schedule-trigger:weekly%3At1:daily-t1:2026-07-13",
            first.GetIdempotencyKey("weekly:t1"));
    }

    [Fact]
    public void WeeklyOccurrenceUsesMostRecentTargetWeekday()
    {
        var schedule = new WorkflowScheduleDefinition
        {
            Id = "weekly",
            Interval = WorkflowScheduleInterval.Weekly,
            DayOfWeek = DayOfWeek.Monday,
            TimeOfDay = new TimeOnly(9, 0)
        };

        var beforeTime = WorkflowScheduleEvaluator.Evaluate(
            schedule,
            new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero));
        var laterInWeek = WorkflowScheduleEvaluator.Evaluate(
            schedule,
            new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var beforeNextTarget = WorkflowScheduleEvaluator.Evaluate(
            schedule,
            new DateTimeOffset(2026, 7, 19, 23, 0, 0, TimeSpan.Zero));

        Assert.False(beforeTime.IsDue);
        Assert.Equal("2026-07-13", laterInWeek.Occurrence!.PeriodKey);
        Assert.Equal(laterInWeek.Occurrence.PeriodKey, beforeNextTarget.Occurrence!.PeriodKey);
    }

    [Fact]
    public void MonthlyOccurrenceUsesPreviousMonthBeforeTargetAndCapsDayAtTwentyEight()
    {
        var schedule = new WorkflowScheduleDefinition
        {
            Id = "monthly",
            Interval = WorkflowScheduleInterval.Monthly,
            DayOfMonth = 15,
            TimeOfDay = new TimeOnly(8, 0)
        };

        var beforeTarget = WorkflowScheduleEvaluator.Evaluate(
            schedule,
            new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero));
        var atTarget = WorkflowScheduleEvaluator.Evaluate(
            schedule with { DayOfMonth = 28 },
            new DateTimeOffset(2026, 2, 28, 8, 0, 0, TimeSpan.Zero));

        Assert.Equal("2025-12", beforeTarget.Occurrence!.PeriodKey);
        Assert.Equal(
            new DateTimeOffset(2025, 12, 15, 8, 0, 0, TimeSpan.Zero),
            beforeTarget.Occurrence.ScheduledAtUtc);
        Assert.Equal("2026-02", atTarget.Occurrence!.PeriodKey);
        Assert.Equal(
            new DateTimeOffset(2026, 2, 28, 8, 0, 0, TimeSpan.Zero),
            atTarget.Occurrence.ScheduledAtUtc);
    }

    [Theory]
    [InlineData(null, null, WorkflowScheduleIssueCode.InvalidInterval)]
    [InlineData(17, null, WorkflowScheduleIssueCode.InvalidInterval)]
    [InlineData(1, null, WorkflowScheduleIssueCode.InvalidDayOfWeek)]
    [InlineData(2, 29, WorkflowScheduleIssueCode.InvalidDayOfMonth)]
    public void MalformedOrUnsupportedScheduleIsReported(
        int? intervalValue,
        int? dayOfMonth,
        WorkflowScheduleIssueCode expectedCode)
    {
        var schedule = new WorkflowScheduleDefinition
        {
            Id = "schedule",
            Interval = intervalValue is { } interval
                ? (WorkflowScheduleInterval)interval
                : null,
            DayOfMonth = dayOfMonth,
            TimeOfDay = new TimeOnly(9, 0)
        };

        var result = WorkflowScheduleEvaluator.Evaluate(
            schedule,
            new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero));

        Assert.False(result.IsValid);
        Assert.False(result.IsDue);
        Assert.Equal(expectedCode, result.Issue!.Code);
    }

    [Fact]
    public void DailyRejectsFieldsThatBelongToOtherCadences()
    {
        var result = WorkflowScheduleEvaluator.Evaluate(
            Daily(new TimeOnly(9, 0)) with { DayOfWeek = DayOfWeek.Monday },
            new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal(WorkflowScheduleIssueCode.UnexpectedDayOfWeek, result.Issue!.Code);
    }

    [Fact]
    public void ScheduleOccurrenceRejectsUnstableWorkflowIdentity()
    {
        var occurrence = WorkflowScheduleEvaluator.Evaluate(
            Daily(new TimeOnly(9, 0)),
            new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero)).Occurrence!;

        Assert.Throws<ArgumentException>(() => occurrence.GetIdempotencyKey("invalid workflow id"));
    }

    [Fact]
    public void PreviousOccurrenceOutsideDateRangeIsReportedWithoutThrowing()
    {
        var now = DateTimeOffset.MinValue;
        var weekly = new WorkflowScheduleDefinition
        {
            Id = "weekly",
            Interval = WorkflowScheduleInterval.Weekly,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDay = new TimeOnly(9, 0)
        };
        var monthly = new WorkflowScheduleDefinition
        {
            Id = "monthly",
            Interval = WorkflowScheduleInterval.Monthly,
            DayOfMonth = 2,
            TimeOfDay = new TimeOnly(9, 0)
        };

        Assert.Equal(
            WorkflowScheduleIssueCode.OccurrenceOutsideDateRange,
            WorkflowScheduleEvaluator.Evaluate(weekly, now).Issue!.Code);
        Assert.Equal(
            WorkflowScheduleIssueCode.OccurrenceOutsideDateRange,
            WorkflowScheduleEvaluator.Evaluate(monthly, now).Issue!.Code);
    }

    private static WorkflowScheduleDefinition Daily(TimeOnly timeOfDay) => new()
    {
        Id = "daily-t1",
        Interval = WorkflowScheduleInterval.Daily,
        TimeOfDay = timeOfDay
    };
}
