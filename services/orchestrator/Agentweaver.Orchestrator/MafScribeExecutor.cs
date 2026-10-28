using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Agentweaver.Orchestrator;

internal sealed record MafExecutionScribeGateIntent(
    SessionIdentity Identity,
    string WorkPlanId,
    string StepId,
    long CheckpointRevision,
    long DecisionStateVersion,
    long ExecutionFence,
    string AcceptedSelectionHash,
    CoordinationActor Actor,
    ImmutableArray<MafExecutionScribeIntent> Outputs);

internal static class MafScribeGateContract
{
    internal static Guid OperationId(MafExecutionScribeGateIntent intent) =>
        new(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            intent.Identity.ProjectId, intent.Identity.RunId, intent.Identity.SessionId,
            intent.WorkPlanId, intent.StepId, "scribe"
        })).AsSpan(0, 16));

    internal static MafExecutionScribeGateIntent CreateIntent(
        SessionIdentity identity, WorkPlanSnapshot plan, MafExecutionCheckpoint state,
        string stepId, long executionFence, string acceptedSelectionHash, CoordinationActor actor)
    {
        RequireSelectedGate(plan, stepId);
        return new(identity, plan.Plan.Id, stepId, checked(state.Revision + 1), state.DecisionStateVersion,
            executionFence, acceptedSelectionHash, actor,
            state.Results.Keys.Order(StringComparer.Ordinal)
                .Where(id => state.Progress.WorkItems.GetValueOrDefault(id,
                    state.Progress.FixedWorkItems.GetValueOrDefault(id)) == MafExecutionTaskStatus.Succeeded)
                .Select(id => MafScribeContract.CreateIntent(identity, plan, state, stepId, id, actor))
                .ToImmutableArray());
    }

    internal static void RequireReplay(
        MafExecutionScribeGateIntent intent, SessionIdentity identity, WorkPlanSnapshot plan,
        MafExecutionCheckpoint state, long executionFence, string acceptedSelectionHash,
        CoordinationActor actor)
    {
        RequireSelectedGate(plan, intent.StepId);
        var expected = intent with
        {
            Identity = identity,
            WorkPlanId = plan.Plan.Id,
            ExecutionFence = executionFence,
            AcceptedSelectionHash = acceptedSelectionHash,
            Actor = actor,
            Outputs = intent.Outputs.Select(output => MafScribeContract.CreateIntent(
                identity, plan, state, intent.StepId, output.AssociationId, actor)).ToImmutableArray()
        };
        if (!Matches(intent, expected) ||
            !state.ScribeIntents.TryGetValue(intent.StepId, out var retained) || !Matches(intent, retained))
            throw Conflict();
    }

    internal static bool Matches(MafExecutionScribeGateIntent left, MafExecutionScribeGateIntent right) =>
        left with { Outputs = right.Outputs } == right && left.Outputs.SequenceEqual(right.Outputs);

    internal static void ValidateState(MafExecutionCheckpoint state)
    {
        if (state.ScribeIntents is null || state.ScribeReceipts is null)
            throw new ArgumentException("Scribe checkpoint maps are required.", nameof(state));
        foreach (var (stepId, intent) in state.ScribeIntents)
        {
            if (intent is null || intent.Actor is null ||
                stepId != intent.StepId || string.IsNullOrWhiteSpace(stepId) ||
                new[] { intent.Identity.ProjectId, intent.Identity.RunId, intent.Identity.SessionId,
                    intent.Actor.Issuer, intent.Actor.Subject }.Any(string.IsNullOrWhiteSpace) ||
                intent.WorkPlanId != state.WorkPlanId ||
                intent.CheckpointRevision < 1 || intent.CheckpointRevision > state.Revision ||
                intent.DecisionStateVersion < 1 || intent.DecisionStateVersion > state.DecisionStateVersion ||
                intent.ExecutionFence < 1 || intent.AcceptedSelectionHash is not { Length: 64 } ||
                !intent.AcceptedSelectionHash.All(Uri.IsHexDigit) ||
                intent.Outputs.IsDefault || intent.Outputs.Any(output => output is null) ||
                intent.Outputs.Select(output => output.AssociationId).Distinct(StringComparer.Ordinal).Count() !=
                    intent.Outputs.Length ||
                !state.Progress.NonModelSteps.TryGetValue(stepId, out var status) ||
                status is not (MafExecutionTaskStatus.Running or MafExecutionTaskStatus.Succeeded))
                throw new ArgumentException("Scribe checkpoint intent is invalid.", nameof(state));
            foreach (var output in intent.Outputs)
            {
                MafScribeContract.ValidateIntent(output);
                if (output.Identity != intent.Identity || output.WorkPlanId != intent.WorkPlanId ||
                    output.StepId != stepId || output.Actor != intent.Actor ||
                    !state.Results.TryGetValue(output.AssociationId, out var result) ||
                    state.Progress.WorkItems.GetValueOrDefault(output.AssociationId,
                        state.Progress.FixedWorkItems.GetValueOrDefault(output.AssociationId)) !=
                        MafExecutionTaskStatus.Succeeded)
                    throw new ArgumentException("Scribe output is not bound to completed work.", nameof(state));
                using var content = System.Text.Json.JsonDocument.Parse(output.Content);
                if (content.RootElement.GetProperty("result").GetString() != result)
                    throw new ArgumentException("Scribe output changed its completed result.", nameof(state));
            }
            if (status == MafExecutionTaskStatus.Succeeded &&
                !intent.Outputs.All(output => state.ScribeReceipts.ContainsKey(output.AssociationId)))
                throw new ArgumentException("A completed Scribe gate requires all Knowledge receipts.", nameof(state));
        }
        foreach (var (associationId, receipt) in state.ScribeReceipts)
        {
            if (receipt is null || receipt.AssociationId != associationId ||
                receipt.RecordId == Guid.Empty || receipt.Revision != 1 || receipt.RevisionId == Guid.Empty ||
                !state.ScribeIntents.Values.Any(intent =>
                    intent.Outputs.Any(output => output.AssociationId == associationId)))
                throw new ArgumentException("Scribe Knowledge receipt is invalid.", nameof(state));
        }
    }

    internal static void ValidateTransition(MafExecutionCheckpoint previous, MafExecutionCheckpoint next)
    {
        foreach (var (stepId, intent) in previous.ScribeIntents)
            if (!next.ScribeIntents.TryGetValue(stepId, out var retained) || !Matches(intent, retained) ||
                previous.Progress.NonModelSteps.GetValueOrDefault(stepId) == MafExecutionTaskStatus.Succeeded &&
                next.Progress.NonModelSteps.GetValueOrDefault(stepId) != MafExecutionTaskStatus.Succeeded)
                throw new ArgumentException("A persisted Scribe intent cannot be changed.", nameof(next));
        foreach (var (stepId, intent) in next.ScribeIntents)
            if (!previous.ScribeIntents.ContainsKey(stepId) &&
                (intent.CheckpointRevision != next.Revision ||
                 intent.DecisionStateVersion != next.DecisionStateVersion))
                throw new ArgumentException("A new Scribe intent must bind its originating checkpoint.", nameof(next));
        foreach (var (associationId, receipt) in previous.ScribeReceipts)
            if (!next.ScribeReceipts.TryGetValue(associationId, out var retained) || retained != receipt)
                throw new ArgumentException("A persisted Scribe receipt cannot be changed.", nameof(next));
    }

    internal static MafExecutionCheckpoint Prepare(
        MafExecutionCheckpointSnapshot latest, MafExecutionScribeGateIntent intent)
    {
        if (intent.CheckpointRevision != checked(latest.State.Revision + 1) ||
            latest.State.ScribeIntents.ContainsKey(intent.StepId) ||
            latest.State.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) != MafExecutionTaskStatus.Pending)
            throw Conflict();
        return latest.State with
        {
            Revision = intent.CheckpointRevision,
            ScribeIntents = latest.State.ScribeIntents.Add(intent.StepId, intent),
            Progress = latest.State.Progress with
            {
                NonModelSteps = latest.State.Progress.NonModelSteps.SetItem(
                    intent.StepId, MafExecutionTaskStatus.Running)
            }
        };
    }

    internal static MafExecutionCheckpoint? Complete(
        MafExecutionCheckpointSnapshot latest, MafExecutionScribeGateIntent intent,
        MafScribeTerminalReceipt? receipt)
    {
        var state = latest.State;
        if (!state.ScribeIntents.TryGetValue(intent.StepId, out var retained) || !Matches(intent, retained))
            throw Conflict();
        var receipts = state.ScribeReceipts;
        if (receipt is not null)
        {
            if (!intent.Outputs.Any(output => output.AssociationId == receipt.AssociationId) ||
                receipt.RecordId == Guid.Empty || receipt.Revision != 1 || receipt.RevisionId == Guid.Empty)
                throw Conflict();
            if (receipts.TryGetValue(receipt.AssociationId, out var previous) && previous != receipt)
                throw Conflict();
            receipts = receipts.SetItem(receipt.AssociationId, receipt);
        }
        var status = intent.Outputs.All(output => receipts.ContainsKey(output.AssociationId))
            ? MafExecutionTaskStatus.Succeeded : MafExecutionTaskStatus.Running;
        if (receipts == state.ScribeReceipts && state.Progress.NonModelSteps[intent.StepId] == status)
            return null;
        return state with
        {
            Revision = checked(state.Revision + 1),
            ScribeReceipts = receipts,
            Progress = state.Progress with
            {
                NonModelSteps = state.Progress.NonModelSteps.SetItem(intent.StepId, status)
            }
        };
    }

    private static CoordinationException Conflict() =>
        new("maf_execution_scribe_intent_conflict", StatusCodes.Status409Conflict);

    private static void RequireSelectedGate(WorkPlanSnapshot plan, string stepId)
    {
        if (!plan.Workflow.Definition.Steps.Any(step => step.Id == stepId &&
                step.Mode == WorkflowStepMode.Platform && step.PlatformGate == WorkflowPlatformGate.Scribe))
            throw Conflict();
    }
}

internal sealed class MafScribeExecutor(
    MafExecutionCheckpointStore execution,
    MafScribeKnowledgeClient knowledge,
    NpgsqlDataSource dataSource,
    Func<CancellationToken, Task<RuntimeActorAuthorization>> createRuntimeActorAsync,
    Func<MafExecutionScribeGateIntent, MafExecutionCheckpoint, CancellationToken, Task> requireCurrentAsync,
    Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> requireDecisionAsync,
    Action<MafExecutionCheckpointSnapshot> updateCheckpoint)
{
    internal async Task<bool> ExecuteAsync(
        MafExecutionScribeGateIntent intent, CancellationToken cancellationToken)
    {
        var latest = await execution.ReadLatestAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
        await requireCurrentAsync(intent, latest.State, cancellationToken).ConfigureAwait(false);
        foreach (var output in intent.Outputs)
        {
            // Re-read and re-authorize even when replay already has a compact receipt.
            latest = await execution.ReadLatestAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new CoordinationException(
                    "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
            await requireCurrentAsync(intent, latest.State, cancellationToken).ConfigureAwait(false);
            if (latest.State.ScribeReceipts.ContainsKey(output.AssociationId))
                continue;
            var authorization = await createRuntimeActorAsync(cancellationToken).ConfigureAwait(false);
            MafScribeTerminalReceipt receipt;
            try
            {
                receipt = await knowledge.WriteAsync(
                    authorization, intent.Actor, output, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                authorization.Bearer.Invalidate();
            }
            latest = await CommitAsync(intent, receipt, cancellationToken).ConfigureAwait(false);
        }
        latest = await CommitAsync(intent, null, cancellationToken).ConfigureAwait(false);
        updateCheckpoint(latest);
        return latest.State.Progress.NonModelSteps[intent.StepId] == MafExecutionTaskStatus.Succeeded;
    }

    private async Task<MafExecutionCheckpointSnapshot> CommitAsync(
        MafExecutionScribeGateIntent intent, MafScribeTerminalReceipt? receipt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await requireDecisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await execution.AcquireExecutionLockInTransactionAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        var latest = await execution.ReadLatestInTransactionAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false) ?? throw new CoordinationException(
                "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
        await requireCurrentAsync(intent, latest.State, cancellationToken).ConfigureAwait(false);
        var next = MafScribeGateContract.Complete(latest, intent, receipt);
        var committed = next is null ? latest : await execution.AppendInTransactionAsync(
            connection, transaction, Guid.NewGuid().ToString("N"), latest.Info, next, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        updateCheckpoint(committed);
        return committed;
    }
}
