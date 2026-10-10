namespace Agentweaver.Orchestrator.Core;

public static class WorkflowScheduleEvaluator
{
    public static WorkflowScheduleEvaluation Evaluate(
        WorkflowScheduleDefinition? schedule,
        DateTimeOffset now)
    {
        if (schedule is null)
            return WorkflowScheduleEvaluation.Invalid(
                WorkflowScheduleIssueCode.MissingSchedule,
                "schedule",
                "A workflow schedule is required.");

        if (!WorkflowValidationSupport.IsStableId(schedule.Id))
            return WorkflowScheduleEvaluation.Invalid(
                WorkflowScheduleIssueCode.InvalidTriggerId,
                "schedule.id",
                "Schedule trigger ID must be a stable, non-empty identifier.");

        if (schedule.Interval is not { } interval || !Enum.IsDefined(interval))
            return WorkflowScheduleEvaluation.Invalid(
                WorkflowScheduleIssueCode.InvalidInterval,
                "schedule.interval",
                "Schedule interval must be daily, weekly, or monthly.");

        if (schedule.TimeOfDay is not { } timeOfDay)
            return WorkflowScheduleEvaluation.Invalid(
                WorkflowScheduleIssueCode.MissingTimeOfDay,
                "schedule.timeOfDay",
                "Schedule time of day is required and is interpreted as UTC.");

        DateOnly occurrenceDate;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        switch (interval)
        {
            case WorkflowScheduleInterval.Daily:
                if (schedule.DayOfWeek is not null)
                    return Unexpected("schedule.dayOfWeek", WorkflowScheduleIssueCode.UnexpectedDayOfWeek);
                if (schedule.DayOfMonth is not null)
                    return Unexpected("schedule.dayOfMonth", WorkflowScheduleIssueCode.UnexpectedDayOfMonth);
                occurrenceDate = today;
                break;

            case WorkflowScheduleInterval.Weekly:
                if (schedule.DayOfWeek is not { } dayOfWeek ||
                    !Enum.IsDefined(dayOfWeek))
                    return WorkflowScheduleEvaluation.Invalid(
                        WorkflowScheduleIssueCode.InvalidDayOfWeek,
                        "schedule.dayOfWeek",
                        "Weekly schedules require a supported day of week.");
                if (schedule.DayOfMonth is not null)
                    return Unexpected("schedule.dayOfMonth", WorkflowScheduleIssueCode.UnexpectedDayOfMonth);

                var daysSinceOccurrence = ((int)today.DayOfWeek - (int)dayOfWeek + 7) % 7;
                if (daysSinceOccurrence > today.DayNumber)
                    return OutsideDateRange();
                occurrenceDate = today.AddDays(-daysSinceOccurrence);
                break;

            case WorkflowScheduleInterval.Monthly:
                if (schedule.DayOfMonth is not { } dayOfMonth ||
                    dayOfMonth is < 1 or > 28)
                    return WorkflowScheduleEvaluation.Invalid(
                        WorkflowScheduleIssueCode.InvalidDayOfMonth,
                        "schedule.dayOfMonth",
                        "Monthly schedules require a day of month from 1 through 28.");
                if (schedule.DayOfWeek is not null)
                    return Unexpected("schedule.dayOfWeek", WorkflowScheduleIssueCode.UnexpectedDayOfWeek);

                if (today.Day >= dayOfMonth)
                {
                    occurrenceDate = new DateOnly(today.Year, today.Month, dayOfMonth);
                }
                else if (today.Year == 1 && today.Month == 1)
                {
                    return OutsideDateRange();
                }
                else
                {
                    occurrenceDate = new DateOnly(today.Year, today.Month, dayOfMonth).AddMonths(-1);
                }
                break;

            default:
                return WorkflowScheduleEvaluation.Invalid(
                    WorkflowScheduleIssueCode.InvalidInterval,
                    "schedule.interval",
                    "Schedule interval must be daily, weekly, or monthly.");
        }

        var scheduledAtUtc = new DateTimeOffset(occurrenceDate.ToDateTime(timeOfDay), TimeSpan.Zero);
        if (now.ToUniversalTime() < scheduledAtUtc)
            return WorkflowScheduleEvaluation.NotDue();

        var periodKey = interval == WorkflowScheduleInterval.Monthly
            ? occurrenceDate.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)
            : occurrenceDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        return WorkflowScheduleEvaluation.Due(
            new WorkflowScheduleOccurrence(schedule.Id, periodKey, scheduledAtUtc));
    }

    private static WorkflowScheduleEvaluation Unexpected(
        string path,
        WorkflowScheduleIssueCode code) =>
        WorkflowScheduleEvaluation.Invalid(
            code,
            path,
            "This schedule field is not valid for the selected interval.");

    private static WorkflowScheduleEvaluation OutsideDateRange() =>
        WorkflowScheduleEvaluation.Invalid(
            WorkflowScheduleIssueCode.OccurrenceOutsideDateRange,
            "schedule",
            "The previous scheduled occurrence is outside the supported date range.");
}
