using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.Agents.AI.Workflows;

namespace Agentweaver.Orchestrator;

internal enum MafExecutionTaskStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Indeterminate
}

internal sealed record MafExecutionProgress(
    ImmutableDictionary<string, MafExecutionTaskStatus> WorkItems,
    ImmutableDictionary<string, MafExecutionTaskStatus> NonModelSteps)
{
    public ImmutableDictionary<string, MafExecutionTaskStatus> FixedWorkItems { get; init; } =
        ImmutableDictionary<string, MafExecutionTaskStatus>.Empty.WithComparers(StringComparer.Ordinal);

    internal static MafExecutionProgress Empty { get; } = new(
        ImmutableDictionary<string, MafExecutionTaskStatus>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, MafExecutionTaskStatus>.Empty.WithComparers(StringComparer.Ordinal))
    {
        FixedWorkItems = ImmutableDictionary<string, MafExecutionTaskStatus>.Empty
            .WithComparers(StringComparer.Ordinal)
    };
}

internal sealed record MafExecutionFixedWorkAssociation(
    string AssociationId,
    long ActivationRevision,
    string WorkPlanId,
    string WorkflowId,
    string DefinitionRevision,
    string CatalogVersion,
    string StepId,
    FixedWorkSpecification Specification,
    string AgentId,
    string ModelSelectionReference,
    string IsolationProviderId,
    string AcceptedSelectionHash,
    long ExecutionFence,
    long DecisionStateVersion,
    string ChildSessionId);

internal sealed record MafExecutionDispatchIntent(
    string AssociationId,
    string ChildSessionId,
    Guid MessageId,
    string PromptHash);

internal sealed record MafExecutionBuildTestIntent(
    Guid OperationId,
    SessionIdentity Identity,
    string CheckpointId,
    long CheckpointRevision,
    string WorkPlanId,
    string StepId,
    long DecisionStateVersion,
    long ExecutionFence,
    string AcceptedSelectionHash,
    WorkflowBuildTestCommand Command,
    SandboxBuildTestAcceptedExecutionOptions ExecutionOptions,
    SandboxBuildTestExpectedBinding ExpectedBinding)
{
    internal SandboxBuildTestCheckpointReference CheckpointReference() =>
        new(Identity.ProjectId, Identity.RunId, Identity.SessionId, CheckpointId, WorkPlanId,
            StepId, CheckpointRevision, DecisionStateVersion, ExecutionFence, AcceptedSelectionHash);

    internal SandboxBuildTestAcceptedCommand ToAcceptedCommand()
    {
        if (Command is null || ExecutionOptions is null ||
            ExecutionOptions.ProfileId != Command.ExecutionProfileReference)
            throw new ArgumentException("The BuildTest command must use its pinned execution profile.");
        var accepted = new SandboxBuildTestAcceptedCommand(
            1, OperationId, CheckpointReference(), Command.ExecutableReference, Command.Arguments,
            Command.WorkingDirectory,
            Command.Outputs.Select(output => new SandboxBuildTestOutputObligation(
                output.Name, output.RelativePath, output.Required, output.MaximumBytes)).ToImmutableArray(),
            ExecutionOptions, string.Empty);
        return (accepted with { ImmutableHash = accepted.ComputeImmutableHash() }).Validate();
    }

    internal SandboxBuildTestApiRequest ToApiRequest() =>
        new SandboxBuildTestApiRequest(CheckpointReference(), ExpectedBinding).Validate();
}

internal sealed record MafExecutionCheckpoint(
    long Revision,
    string WorkPlanId,
    long DecisionStateVersion,
    MafExecutionProgress Progress)
{
    public ImmutableDictionary<string, MafExecutionFixedWorkAssociation> FixedWorkAssociations { get; init; } =
        ImmutableDictionary<string, MafExecutionFixedWorkAssociation>.Empty.WithComparers(StringComparer.Ordinal);
    public ImmutableDictionary<string, MafExecutionDispatchIntent> PendingDispatches { get; init; } =
        ImmutableDictionary<string, MafExecutionDispatchIntent>.Empty.WithComparers(StringComparer.Ordinal);
    public ImmutableDictionary<string, string> Results { get; init; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
    public ImmutableDictionary<string, MafExecutionBuildTestIntent> BuildTestIntents { get; init; } =
        ImmutableDictionary<string, MafExecutionBuildTestIntent>.Empty.WithComparers(StringComparer.Ordinal);
    public ImmutableDictionary<string, MafBuildTestTerminalReceipt> BuildTestReceipts { get; init; } =
        ImmutableDictionary<string, MafBuildTestTerminalReceipt>.Empty.WithComparers(StringComparer.Ordinal);
}

internal sealed record MafExecutionCheckpointSnapshot(
    CheckpointInfo Info,
    MafExecutionCheckpoint State);

internal sealed record MafExecutionOwnerEvidenceBinding(
    CoordinationActor Actor,
    SessionIdentity Identity,
    string AcceptedSelectionHash,
    long ExecutionFence,
    CoordinatorDecisionCurrentState CurrentDecision,
    MafExecutionCheckpointSnapshot Checkpoint,
    WorkPlanSnapshot WorkPlan,
    MafBacklogOutputSetSnapshot OutputSet);

internal static class MafExecutionIds
{
    internal static string CreateBuildTestAssociationId(
        SessionIdentity root, string workPlanId, string stepId) =>
        $"build-test-{Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { root.ProjectId, root.RunId, root.SessionId, workPlanId, stepId })))}";

    internal static string CreateChildSessionId(
        SessionIdentity parent,
        string workPlanId,
        string associationId)
    {
        var scope = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            parent.ProjectId, parent.RunId, parent.SessionId, workPlanId, associationId
        });
        return $"maf-{Convert.ToHexStringLower(SHA256.HashData(scope))}";
    }

    internal static string CreateFixedWorkAssociationId(
        SessionIdentity parent,
        string workPlanId,
        string stepId,
        long activationRevision)
    {
        if (activationRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(activationRevision));
        var scope = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            parent.ProjectId, parent.RunId, parent.SessionId, workPlanId, stepId,
            activationRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        return $"fixed-{Convert.ToHexStringLower(SHA256.HashData(scope))}";
    }
}

internal static class MafExecutionCheckpointContract
{
    internal const string StoreName = "coordinator-execution";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    internal static JsonElement Serialize(MafExecutionCheckpoint state)
    {
        ValidateState(state);
        return JsonSerializer.SerializeToElement(state, JsonOptions);
    }

    internal static MafExecutionCheckpoint Deserialize(JsonElement value)
    {
        var state = value.Deserialize<MafExecutionCheckpoint>(JsonOptions)
            ?? throw new InvalidOperationException("A stored MAF execution checkpoint is invalid.");
        ValidateState(state);
        return state;
    }

    internal static bool IsBoundTo(
        MafCheckpointBinding binding,
        SessionIdentity identity,
        long executionFence) =>
        binding.Identity == identity &&
        binding.ExecutionFence == executionFence &&
        string.Equals(binding.StoreName, StoreName, StringComparison.Ordinal);

    internal static void ValidateTransition(
        MafExecutionCheckpoint previous,
        MafExecutionCheckpoint next)
    {
        ValidateState(previous);
        ValidateState(next);
        foreach (var (stepId, intent) in previous.BuildTestIntents)
        {
            if (!next.BuildTestIntents.TryGetValue(stepId, out var retained) ||
                !BuildTestIntentsMatch(intent, retained))
                throw new ArgumentException(
                    "A persisted BuildTest intent cannot be removed or changed.", nameof(next));
        }
        if (next.BuildTestIntents.Any(item =>
                !previous.BuildTestIntents.ContainsKey(item.Key) &&
                (item.Value.CheckpointRevision != next.Revision ||
                 item.Value.DecisionStateVersion != next.DecisionStateVersion)))
            throw new ArgumentException(
                "A new BuildTest intent must bind to the checkpoint that first stores it.", nameof(next));
        foreach (var (stepId, receipt) in previous.BuildTestReceipts)
        {
            if (!next.BuildTestReceipts.TryGetValue(stepId, out var retained) ||
                !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(receipt, JsonOptions),
                    JsonSerializer.SerializeToElement(retained, JsonOptions)))
                throw new ArgumentException(
                    "A persisted BuildTest terminal receipt cannot be removed or changed.", nameof(next));
        }
    }

    internal static void ValidateBuildTestBinding(
        MafExecutionCheckpoint state,
        SessionIdentity identity,
        long executionFence,
        string checkpointId)
    {
        foreach (var intent in state.BuildTestIntents.Values)
        {
            if (intent.Identity != identity || intent.ExecutionFence != executionFence ||
                intent.CheckpointRevision == state.Revision && intent.CheckpointId != checkpointId)
                throw new ArgumentException("The BuildTest intent does not match its checkpoint binding.", nameof(state));
        }
    }

    internal static bool BuildTestIntentsMatch(
        MafExecutionBuildTestIntent left,
        MafExecutionBuildTestIntent right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left, JsonOptions),
            JsonSerializer.SerializeToElement(right, JsonOptions));

    internal static void ValidateState(MafExecutionCheckpoint state)
    {
        if (state is null || state.Revision < 1 ||
            string.IsNullOrWhiteSpace(state.WorkPlanId) || state.WorkPlanId.Length > 128 ||
            state.WorkPlanId.Any(char.IsControl) || state.DecisionStateVersion < 1 ||
            state.Progress is null || state.Progress.WorkItems is null ||
            state.Progress.NonModelSteps is null || state.Progress.FixedWorkItems is null)
            throw new ArgumentException("A bounded, bound MAF execution checkpoint is required.", nameof(state));
        if (state.FixedWorkAssociations is null)
            throw new ArgumentException("MAF execution fixed-work associations are required.", nameof(state));
        if (state.PendingDispatches is null || state.Results is null ||
            state.BuildTestIntents is null || state.BuildTestReceipts is null)
            throw new ArgumentException("MAF execution dispatch state is required.", nameof(state));
        if (state.Progress.WorkItems.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 128 ||
                item.Key.Any(char.IsControl) || !Enum.IsDefined(item.Value)) ||
            state.Progress.NonModelSteps.Any(step =>
                string.IsNullOrWhiteSpace(step.Key) || step.Key.Length > 128 ||
                step.Key.Any(char.IsControl) || !Enum.IsDefined(step.Value)) ||
            state.Progress.FixedWorkItems.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 128 ||
                item.Key.Any(char.IsControl) || !Enum.IsDefined(item.Value)))
            throw new ArgumentException("MAF execution checkpoint statuses are invalid.", nameof(state));

        foreach (var (associationId, intent) in state.PendingDispatches)
        {
            if (intent is null || associationId != intent.AssociationId ||
                !IsIdentifier(intent.AssociationId) || !IsMetadataValue(intent.ChildSessionId) ||
                intent.MessageId == Guid.Empty || !IsHash(intent.PromptHash) ||
                state.Progress.WorkItems.GetValueOrDefault(associationId,
                    state.Progress.FixedWorkItems.GetValueOrDefault(associationId)) != MafExecutionTaskStatus.Running)
                throw new ArgumentException("MAF execution dispatch intents are invalid.", nameof(state));
        }

        foreach (var (associationId, result) in state.Results)
        {
            if (!IsIdentifier(associationId) || result is null ||
                result.Length > AddressedMessageValidation.MaximumTextLength ||
                result.Any(character =>
                    char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
                throw new ArgumentException("MAF execution results are invalid.", nameof(state));
        }

        var operationIds = new HashSet<Guid>();
        foreach (var (stepId, intent) in state.BuildTestIntents)
        {
            if (intent is null || stepId != intent.StepId ||
                !IsIdentifier(intent.StepId) ||
                intent.OperationId == Guid.Empty || !operationIds.Add(intent.OperationId) ||
                !IsMetadataValue(intent.Identity.ProjectId) ||
                !IsMetadataValue(intent.Identity.RunId) ||
                !IsMetadataValue(intent.Identity.SessionId) ||
                !IsIdentifier(intent.CheckpointId) ||
                intent.CheckpointRevision < 1 || intent.CheckpointRevision > state.Revision ||
                intent.WorkPlanId != state.WorkPlanId ||
                intent.DecisionStateVersion < 1 ||
                intent.DecisionStateVersion > state.DecisionStateVersion ||
                intent.ExecutionFence < 1 || !IsHash(intent.AcceptedSelectionHash) ||
                !WorkflowDefinitionValidator.IsValidBuildTestCommand(intent.Command) ||
                !state.Progress.NonModelSteps.TryGetValue(stepId, out var status) ||
                status == MafExecutionTaskStatus.Pending)
                throw new ArgumentException("MAF BuildTest checkpoint intents are invalid.", nameof(state));
            _ = intent.ToAcceptedCommand();
            _ = intent.ToApiRequest();
            if (status != MafExecutionTaskStatus.Running &&
                !state.BuildTestReceipts.ContainsKey(stepId))
                throw new ArgumentException("A terminal BuildTest step requires an owner-verified receipt.", nameof(state));
        }
        foreach (var (stepId, receipt) in state.BuildTestReceipts)
        {
            if (receipt is null || !state.BuildTestIntents.TryGetValue(stepId, out var intent) ||
                receipt.ValidateFor(intent) != state.Progress.NonModelSteps.GetValueOrDefault(stepId))
                throw new ArgumentException("The BuildTest receipt does not match checkpoint progress.", nameof(state));
        }

        var childSessionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (associationId, association) in state.FixedWorkAssociations)
        {
            if (association is null ||
                !string.Equals(associationId, association.AssociationId, StringComparison.Ordinal) ||
                !IsIdentifier(association.AssociationId) ||
                association.ActivationRevision < 1 ||
                !IsMetadataValue(association.WorkPlanId) ||
                !IsMetadataValue(association.WorkflowId) ||
                !IsMetadataValue(association.DefinitionRevision) ||
                !IsMetadataValue(association.CatalogVersion) ||
                !IsMetadataValue(association.StepId) ||
                association.Specification is null ||
                !IsMetadataValue(association.AgentId) ||
                !IsMetadataValue(association.ModelSelectionReference) ||
                !IsMetadataValue(association.IsolationProviderId) ||
                association.AcceptedSelectionHash is not { Length: 64 } ||
                !association.AcceptedSelectionHash.All(Uri.IsHexDigit) ||
                association.ExecutionFence < 1 ||
                association.DecisionStateVersion < 1 ||
                !IsMetadataValue(association.ChildSessionId) ||
                !childSessionIds.Add(association.ChildSessionId))
                throw new ArgumentException(
                    "MAF execution fixed-work associations are invalid.", nameof(state));
        }
    }

    private static bool IsIdentifier(string? value) =>
        IsMetadataValue(value, 128) && value!.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsMetadataValue(string? value, int maximumLength = 256) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        !value.Any(char.IsControl);
}
