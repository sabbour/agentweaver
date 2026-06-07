using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence;

/// <summary>
/// Writes the OperationalRecord after a run reaches a terminal state (FR-028).
/// This is the compliance record, DISTINCT from the event log.
/// Full governance policy trace support is added in T049.
/// </summary>
public sealed class OperationalRecordWriter
{
    private readonly IOperationalRecordRepository _repo;
    private readonly ILogger<OperationalRecordWriter> _logger;

    public OperationalRecordWriter(
        IOperationalRecordRepository repo,
        ILogger<OperationalRecordWriter> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task WriteAsync(
        RunEntity run,
        int stepCount,
        CancellationToken ct = default)
    {
        var outcome = run.Status switch
        {
            RunStatus.Completed or RunStatus.AwaitingReview or RunStatus.Approved => RunOutcome.Completed,
            RunStatus.Failed => RunOutcome.Failed,
            RunStatus.Bounded => RunOutcome.Bounded,
            RunStatus.Merged => RunOutcome.Merged,
            RunStatus.Declined => RunOutcome.Declined,
            RunStatus.MergeConflict => RunOutcome.MergeConflict,
            _ => RunOutcome.Completed
        };

        var record = new OperationalRecordEntity
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            SubmittedBy = run.SubmittedBy,
            ModelSource = run.ModelSource,
            StartedAt = run.StartedAt,
            EndedAt = DateTimeOffset.UtcNow,
            StepCount = stepCount,
            Outcome = outcome,
            PolicyTrace = "[]"
        };

        await _repo.UpsertAsync(record, ct);
        _logger.LogInformation(
            "Operational record written for run {RunId}: outcome={Outcome}, steps={Steps}",
            run.Id, outcome, stepCount);
    }
}
