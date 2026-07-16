namespace Agentweaver.Api.Workflows;

/// <summary>
/// Pure "is it time to fire" calculator for a <see cref="WorkflowTrigger"/> schedule (issue #53).
/// Given an explicit <c>now</c> (never wall-clock — the caller always supplies it, which is what
/// keeps this fully unit-testable without sleeping), computes the most recent scheduled occurrence
/// at/before <c>now</c> for the trigger's cadence and reports whether that occurrence is due (its
/// scheduled instant has passed). The returned <c>periodKey</c> uniquely identifies THAT occurrence
/// (stable across repeated calls within the same period) so a caller can dedupe repeated firings
/// (e.g. across heartbeat ticks within the same day/week/month) without needing any additional
/// "last fired at" storage of its own.
/// </summary>
public static class WorkflowScheduleEvaluator
{
    /// <summary>
    /// Returns true when <paramref name="trigger"/> is a schedule trigger whose most recent cadence
    /// occurrence at/before <paramref name="now"/> is due. <paramref name="periodKey"/> identifies that
    /// occurrence (e.g. "2026-07-13" for daily/weekly, "2026-07" for monthly) and
    /// <paramref name="scheduledAt"/> is the occurrence's exact UTC instant.
    /// </summary>
    public static bool TryGetDueOccurrence(
        WorkflowTrigger trigger, DateTimeOffset now, out string periodKey, out DateTimeOffset scheduledAt)
    {
        periodKey = string.Empty;
        scheduledAt = default;

        if (trigger.Type != WorkflowTriggerType.Schedule) return false;
        if (trigger.Interval is not { } interval) return false;
        if (trigger.TimeOfDay is not { } timeOfDay) return false;

        var nowUtc = now.ToUniversalTime();
        var today = DateOnly.FromDateTime(nowUtc.UtcDateTime);

        DateOnly occurrenceDate;
        switch (interval)
        {
            case WorkflowScheduleInterval.Daily:
                occurrenceDate = today;
                break;

            case WorkflowScheduleInterval.Weekly:
                if (trigger.DayOfWeek is not { } dow) return false;
                // Days since the most recent (or today's) occurrence of the target weekday.
                var daysSince = ((int)today.DayOfWeek - (int)dow + 7) % 7;
                occurrenceDate = today.AddDays(-daysSince);
                break;

            case WorkflowScheduleInterval.Monthly:
                if (trigger.DayOfMonth is not { } dom || dom is < 1 or > 28) return false;
                occurrenceDate = today.Day >= dom
                    ? new DateOnly(today.Year, today.Month, dom)
                    : new DateOnly(today.Year, today.Month, dom).AddMonths(-1);
                break;

            default:
                return false;
        }

        scheduledAt = new DateTimeOffset(occurrenceDate.ToDateTime(timeOfDay), TimeSpan.Zero);
        if (nowUtc < scheduledAt) return false;

        periodKey = interval == WorkflowScheduleInterval.Monthly
            ? occurrenceDate.ToString("yyyy-MM")
            : occurrenceDate.ToString("yyyy-MM-dd");
        return true;
    }
}
