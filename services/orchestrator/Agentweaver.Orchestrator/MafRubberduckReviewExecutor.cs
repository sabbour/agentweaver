using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Agentweaver.Orchestrator;

/// <summary>Executes one recorded-evidence RubberDuck review through the current AgentHost owner.</summary>
internal sealed class MafRubberduckReviewExecutor(
    MafExecutionCheckpointStore execution,
    NpgsqlDataSource dataSource,
    Func<MafExecutionRubberduckIntent, string, CancellationToken, Task<string>> runOwnerAsync,
    Func<MafExecutionRubberduckIntent, CancellationToken, Task> requireCurrentAsync,
    Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> requireDecisionAsync,
    Func<MafExecutionRubberduckIntent, MafExecutionCheckpoint, CancellationToken, Task> requireEvidenceAsync,
    Action<MafExecutionCheckpointSnapshot> updateCheckpoint)
{
    internal async Task<MafExecutionTaskStatus?> ExecuteAsync(
        MafExecutionRubberduckIntent intent,
        CancellationToken cancellationToken)
    {
        var latest = await execution.ReadLatestAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
        if (!latest.State.RubberduckIntents.TryGetValue(intent.StepId, out var retained) ||
            retained != intent)
            throw new CoordinationException(
                "maf_execution_rubberduck_intent_conflict", StatusCodes.Status409Conflict);
        if (latest.State.RubberduckReceipts.TryGetValue(intent.StepId, out var completed))
        {
            await requireEvidenceAsync(intent, latest.State, cancellationToken).ConfigureAwait(false);
            await requireCurrentAsync(intent, cancellationToken).ConfigureAwait(false);
            updateCheckpoint(latest);
            return MafRubberduckReviewContract.ValidateReceipt(intent, completed);
        }

        await requireEvidenceAsync(intent, latest.State, cancellationToken).ConfigureAwait(false);
        await requireCurrentAsync(intent, cancellationToken).ConfigureAwait(false);
        string output;
        try
        {
            output = await runOwnerAsync(
                intent, MafRubberduckReviewContract.CreatePrompt(intent), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CoordinationException failure) when (failure.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            await RecordFailureAsync(
                intent, failure.Code, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (HttpRequestException)
        {
            await RecordFailureAsync(
                intent, "maf_execution_rubberduck_owner_unavailable", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        await requireCurrentAsync(intent, cancellationToken).ConfigureAwait(false);
        var receipt = MafRubberduckReviewContract.ReadOwnerResult(intent, output);
        return await CompleteAsync(intent, receipt, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordFailureAsync(
        MafExecutionRubberduckIntent intent,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var recordedCode = MafRubberduckReviewContract.IsValidFailureCode(failureCode)
            ? failureCode
            : "maf_execution_rubberduck_owner_unavailable";
        try
        {
            await requireCurrentAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        catch (CoordinationException)
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await requireDecisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await execution.AcquireExecutionLockInTransactionAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        var latest = await execution.ReadLatestInTransactionAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
        if (!latest.State.RubberduckIntents.TryGetValue(intent.StepId, out var retained) ||
            retained != intent ||
            latest.State.RubberduckReceipts.ContainsKey(intent.StepId))
            throw new CoordinationException(
                "maf_execution_rubberduck_intent_conflict", StatusCodes.Status409Conflict);
        await requireEvidenceAsync(intent, latest.State, cancellationToken).ConfigureAwait(false);
        var next = CoordinationEndpoints.PrepareRubberduckFailureCheckpoint(
            latest, intent, recordedCode);
        var committed = next is null
            ? latest
            : await execution.AppendInTransactionAsync(
                connection, transaction, Guid.NewGuid().ToString("N"), latest.Info, next, cancellationToken)
                .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        updateCheckpoint(committed);
    }

    private async Task<MafExecutionTaskStatus> CompleteAsync(
        MafExecutionRubberduckIntent intent,
        MafRubberduckTerminalReceipt receipt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await requireDecisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await execution.AcquireExecutionLockInTransactionAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        var latest = await execution.ReadLatestInTransactionAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
        await requireEvidenceAsync(intent, latest.State, cancellationToken).ConfigureAwait(false);
        var next = CoordinationEndpoints.PrepareRubberduckCompletionCheckpoint(latest, intent, receipt);
        var committed = next is null
            ? latest
            : await execution.AppendInTransactionAsync(
                connection, transaction, Guid.NewGuid().ToString("N"), latest.Info, next, cancellationToken)
                .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        updateCheckpoint(committed);
        return MafRubberduckReviewContract.ValidateReceipt(intent, receipt);
    }
}
