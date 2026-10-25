using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.AI.Workflows;

namespace Agentweaver.Orchestrator;

/// <summary>
/// The persisted progress states for child work and non-model workflow steps.
/// </summary>
internal enum MafExecutionTaskStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Indeterminate
}

/// <summary>
/// Current progress maps for model work items, platform steps, and fixed-work associations.
/// All maps use ordinal identifiers so checkpoint comparisons are stable.
/// </summary>
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

/// <summary>
/// The immutable association between one accepted fixed-work step activation and its child session.
/// It preserves the selected agent, model, provider, selection hash, and execution fence for later dispatch checks.
/// </summary>
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

/// <summary>
/// A deterministic addressed-message request awaiting dispatch for a work association.
/// The prompt hash and message ID make retries refer to the original request rather than creating a new one.
/// </summary>
internal sealed record MafExecutionDispatchIntent(
    string AssociationId,
    string ChildSessionId,
    Guid MessageId,
    string PromptHash)
{
    /// <summary>Exact Environment generation, policy and selection verified before the owner committed this intent.</summary>
    public RuntimeEgressVerificationReceipt? EgressReceipt { get; init; }
}

/// <summary>
/// The persisted, authority-bound intent for one platform BuildTest command.
/// Workspace bytes are stored separately and referenced by hash so checkpoint metadata remains bounded.
/// </summary>
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
    [JsonIgnore]
    public ImmutableArray<byte>? WorkspaceSeed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxBuildTestWorkspaceSeedReference? WorkspaceSeedReference { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxBuildTestWorkspaceSource? WorkspaceInput { get; init; }

    /// <summary>
    /// Returns the effective workspace source and rejects a late input that would replace a confirmed source.
    /// </summary>
    [JsonIgnore]
    internal SandboxBuildTestWorkspaceSource? EffectiveWorkspaceSource
    {
        get
        {
            if (WorkspaceInput is not null && Command.WorkspaceSource is not null &&
                WorkspaceInput != Command.WorkspaceSource)
                throw new ArgumentException("The execution input cannot replace a confirmed workspace source.");
            return WorkspaceInput ?? Command.WorkspaceSource;
        }
    }

    /// <summary>
    /// Builds the checkpoint reference containing the intent's session, plan, revision, and authority fences.
    /// </summary>
    internal SandboxBuildTestCheckpointReference CheckpointReference() =>
        new(Identity.ProjectId, Identity.RunId, Identity.SessionId, CheckpointId, WorkPlanId,
            StepId, CheckpointRevision, DecisionStateVersion, ExecutionFence, AcceptedSelectionHash);

    /// <summary>
    /// Attaches and validates a workspace source and seed while preserving its origin checkpoint reference.
    /// </summary>
    /// <param name="source">The accepted source descriptor for the workspace seed.</param>
    /// <param name="seed">The package bytes whose content must match the source digest.</param>
    /// <returns>A new intent containing the source and seed reference.</returns>
    /// <exception cref="CoordinationException">Thrown when a source or seed reference was already attached inconsistently.</exception>
    internal MafExecutionBuildTestIntent AttachWorkspaceInput(
        SandboxBuildTestWorkspaceSource source,
        ImmutableArray<byte> seed)
    {
        if (WorkspaceSeedReference is not null || WorkspaceInput is not null ||
            Command.WorkspaceSource is not null && Command.WorkspaceSource != source)
            throw new CoordinationException(
                "maf_execution_build_test_intent_conflict", StatusCodes.Status409Conflict);
        _ = GitWorkspaceBuildTestSeedPackage.Parse(seed.AsSpan(), source);
        var intent = this with
        {
            WorkspaceInput = Command.WorkspaceSource is null ? source : null,
            WorkspaceSeed = seed,
            WorkspaceSeedReference = new(
                CheckpointReference(), OperationId, source.SeedSha256, source.SeedByteLength)
        };
        _ = intent.ToAcceptedCommand();
        return intent;
    }

    /// <summary>
    /// Creates the accepted command contract from persisted intent data and optionally requires seed bytes.
    /// </summary>
    /// <param name="requireSeed">When true, validates the full command including available seed content.</param>
    /// <returns>The command contract with a recomputed immutable hash.</returns>
    /// <exception cref="ArgumentException">Thrown when the pinned execution profile or workspace seed reference is invalid.</exception>
    internal SandboxBuildTestAcceptedCommand ToAcceptedCommand(bool requireSeed = true)
    {
        if (Command is null || ExecutionOptions is null ||
            ExecutionOptions.ProfileId != Command.ExecutionProfileReference)
            throw new ArgumentException("The BuildTest command must use its pinned execution profile.");
        var workspaceSource = EffectiveWorkspaceSource;
        if (workspaceSource is { } source)
        {
            _ = (WorkspaceSeedReference ?? throw new ArgumentException(
                "The BuildTest workspace seed requires its persisted origin reference."))
                .Validate(CheckpointReference(), OperationId, source);
        }
        else if (WorkspaceSeedReference is not null)
        {
            throw new ArgumentException("A legacy BuildTest intent cannot reference a workspace seed.");
        }
        var accepted = new SandboxBuildTestAcceptedCommand(
            workspaceSource is null ? 1 : 2,
            OperationId, CheckpointReference(), Command.ExecutableReference, Command.Arguments,
            Command.WorkingDirectory,
            Command.Outputs.Select(output => new SandboxBuildTestOutputObligation(
                output.Name, output.RelativePath, output.Required, output.MaximumBytes)).ToImmutableArray(),
            ExecutionOptions, string.Empty)
        {
            WorkspaceSource = workspaceSource,
            WorkspaceSeed = WorkspaceSeed
        };
        accepted = accepted with { ImmutableHash = accepted.ComputeImmutableHash() };
        return requireSeed ? accepted.Validate() : accepted.ValidateMetadata();
    }

    /// <summary>
    /// Creates the owner API request bound to this intent's checkpoint reference and expected binding.
    /// </summary>
    internal SandboxBuildTestApiRequest ToApiRequest() =>
        new SandboxBuildTestApiRequest(CheckpointReference(), ExpectedBinding).Validate();
}

/// <summary>The persisted Source Control request and exact workspace revision for one OpenPullRequest gate.</summary>
internal sealed record MafExecutionOpenPullRequestIntent(
    Guid OperationId,
    SessionIdentity Identity,
    string CheckpointId,
    long CheckpointRevision,
    string WorkPlanId,
    string StepId,
    long DecisionStateVersion,
    long ExecutionFence,
    string AcceptedSelectionHash,
    string SourceControlPinId,
    SourceControlRepositoryIdentity Repository,
    SourceControlPullRequestWorkspaceLocator Workspace,
    string TitleSource,
    string BodySource,
    string OutcomeSummary,
    SourceControlPullRequestRequest Request);

/// <summary>Small terminal proof that the pinned Source Control owner opened or reused the requested pull request.</summary>
internal sealed record MafOpenPullRequestTerminalReceipt(
    Guid OperationId,
    long PullRequestNumber,
    string HtmlUrl,
    string HeadSha,
    string RequestFingerprint);

/// <summary>
/// A versioned checkpoint state for one work plan, including progress, pending dispatches, results, and platform evidence.
/// State validation and transition checks prevent accepted intents and terminal receipts from being rewritten.
/// </summary>
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
    public ImmutableDictionary<string, MafExecutionOpenPullRequestIntent> OpenPullRequestIntents { get; init; } =
        ImmutableDictionary<string, MafExecutionOpenPullRequestIntent>.Empty.WithComparers(StringComparer.Ordinal);
    public ImmutableDictionary<string, MafOpenPullRequestTerminalReceipt> OpenPullRequestReceipts { get; init; } =
        ImmutableDictionary<string, MafOpenPullRequestTerminalReceipt>.Empty.WithComparers(StringComparer.Ordinal);
}

/// <summary>
/// A checkpoint state paired with the database checkpoint metadata that identifies its parent.
/// </summary>
internal sealed record MafExecutionCheckpointSnapshot(
    CheckpointInfo Info,
    MafExecutionCheckpoint State)
{
    public string? ParentCheckpointId { get; init; }
    public bool HasParentMetadata { get; init; }
}

/// <summary>
/// The authority and current owner evidence needed to attest that a completed plan produced its declared outputs.
/// </summary>
internal sealed record MafExecutionOwnerEvidenceBinding(
    CoordinationActor Actor,
    SessionIdentity Identity,
    string AcceptedSelectionHash,
    long ExecutionFence,
    CoordinatorDecisionCurrentState CurrentDecision,
    MafExecutionCheckpointSnapshot Checkpoint,
    WorkPlanSnapshot WorkPlan,
    MafBacklogOutputSetSnapshot OutputSet);

/// <summary>
/// Creates stable identifiers for BuildTest associations, child sessions, and fixed-work activations.
/// Hashing the full owner scope avoids exposing source identifiers and makes retries deterministic.
/// </summary>
internal static class MafExecutionIds
{
    /// <summary>
    /// Creates the deterministic association ID for a BuildTest step in a root session and plan.
    /// </summary>
    internal static string CreateBuildTestAssociationId(
        SessionIdentity root, string workPlanId, string stepId) =>
        $"build-test-{Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { root.ProjectId, root.RunId, root.SessionId, workPlanId, stepId })))}";

    /// <summary>
    /// Creates the deterministic child-session ID for a work association.
    /// </summary>
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

    /// <summary>
    /// Creates an activation-specific fixed-work association ID.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the activation revision is not positive.</exception>
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

/// <summary>
/// Defines the JSON and validation contract for persisted MAF execution checkpoints.
/// It rejects unknown JSON fields and enforces immutable intent and terminal-receipt history.
/// </summary>
internal static class MafExecutionCheckpointContract
{
    internal const string StoreName = "coordinator-execution";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    /// <summary>
    /// Validates and serializes checkpoint state using the versioned store contract.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the checkpoint violates the persisted-state contract.</exception>
    internal static JsonElement Serialize(MafExecutionCheckpoint state)
    {
        ValidateState(state);
        return JsonSerializer.SerializeToElement(state, JsonOptions);
    }

    /// <summary>
    /// Deserializes a stored checkpoint, rejecting missing data and any invalid state.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when stored JSON cannot represent a checkpoint.</exception>
    /// <exception cref="ArgumentException">Thrown when the deserialized checkpoint fails validation.</exception>
    internal static MafExecutionCheckpoint Deserialize(JsonElement value)
    {
        var state = value.Deserialize<MafExecutionCheckpoint>(JsonOptions)
            ?? throw new InvalidOperationException("A stored MAF execution checkpoint is invalid.");
        ValidateState(state);
        return state;
    }

    /// <summary>
    /// Checks that a store binding names the same session and fence as the requested execution.
    /// </summary>
    internal static bool IsBoundTo(
        MafCheckpointBinding binding,
        SessionIdentity identity,
        long executionFence) =>
        binding.Identity == identity &&
        binding.ExecutionFence == executionFence &&
        string.Equals(binding.StoreName, StoreName, StringComparison.Ordinal);

    /// <summary>
    /// Validates that the next checkpoint preserves prior platform intents and terminal receipts.
    /// </summary>
    /// <param name="previous">The currently persisted state.</param>
    /// <param name="next">The proposed state.</param>
    /// <exception cref="ArgumentException">Thrown when an intent or terminal receipt is removed, changed, or first bound to the wrong revision.</exception>
    internal static void ValidateTransition(
        MafExecutionCheckpoint previous,
        MafExecutionCheckpoint next)
    {
        ValidateState(previous);
        ValidateState(next);
        foreach (var (associationId, intent) in previous.PendingDispatches)
        {
            // Once recorded, an intent can only remain identical or be replaced by its terminal result.
            if (next.PendingDispatches.TryGetValue(associationId, out var retained)
                ? retained != intent
                : !next.Results.ContainsKey(associationId))
                throw new ArgumentException(
                    "A persisted dispatch intent cannot be changed or dropped without a result.", nameof(next));
        }
        // Terminal output is append-only, so a retry cannot rewrite a child's completed answer.
        foreach (var (associationId, result) in previous.Results)
        {
            if (!next.Results.TryGetValue(associationId, out var retained) ||
                !string.Equals(result, retained, StringComparison.Ordinal))
                throw new ArgumentException(
                    "A completed dispatch result cannot be changed or removed.", nameof(next));
        }
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
        foreach (var (stepId, intent) in previous.OpenPullRequestIntents)
        {
            if (!next.OpenPullRequestIntents.TryGetValue(stepId, out var retained) ||
                !OpenPullRequestIntentsMatch(intent, retained))
                throw new ArgumentException(
                    "A persisted OpenPullRequest intent cannot be removed or changed.", nameof(next));
        }
        if (next.OpenPullRequestIntents.Any(item =>
                !previous.OpenPullRequestIntents.ContainsKey(item.Key) &&
                (item.Value.CheckpointRevision != next.Revision ||
                 item.Value.DecisionStateVersion != next.DecisionStateVersion)))
            throw new ArgumentException(
                "A new OpenPullRequest intent must bind to the checkpoint that first stores it.", nameof(next));
        foreach (var (stepId, receipt) in previous.OpenPullRequestReceipts)
        {
            if (!next.OpenPullRequestReceipts.TryGetValue(stepId, out var retained) ||
                retained != receipt)
                throw new ArgumentException(
                    "A persisted OpenPullRequest terminal receipt cannot be removed or changed.", nameof(next));
        }
    }

    /// <summary>
    /// Ensures every BuildTest intent belongs to this identity and fence and binds new intents to this checkpoint.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when an intent's session, fence, or originating checkpoint does not match.</exception>
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

    /// <summary>Ensures OpenPullRequest intents belong to the checkpoint's root identity and fence.</summary>
    internal static void ValidateOpenPullRequestBinding(
        MafExecutionCheckpoint state,
        SessionIdentity identity,
        long executionFence,
        string checkpointId)
    {
        foreach (var intent in state.OpenPullRequestIntents.Values)
        {
            if (intent.Identity != identity || intent.ExecutionFence != executionFence ||
                intent.CheckpointRevision == state.Revision && intent.CheckpointId != checkpointId)
                throw new ArgumentException(
                    "The OpenPullRequest intent does not match its checkpoint binding.", nameof(state));
        }
    }

    /// <summary>
    /// Compares two BuildTest intents using their serialized contract, excluding transient seed bytes.
    /// </summary>
    internal static bool BuildTestIntentsMatch(
        MafExecutionBuildTestIntent left,
        MafExecutionBuildTestIntent right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left, JsonOptions),
            JsonSerializer.SerializeToElement(right, JsonOptions));

    /// <summary>Compares immutable OpenPullRequest intent data after checkpoint serialization.</summary>
    internal static bool OpenPullRequestIntentsMatch(
        MafExecutionOpenPullRequestIntent left,
        MafExecutionOpenPullRequestIntent right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left, JsonOptions),
            JsonSerializer.SerializeToElement(right, JsonOptions));

    /// <summary>
    /// Validates bounded identifiers, status maps, dispatches, results, intents, receipts, and fixed-work associations.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when any checkpoint field is missing, oversized, unknown, or inconsistently bound.</exception>
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
            state.BuildTestIntents is null || state.BuildTestReceipts is null ||
            state.OpenPullRequestIntents is null || state.OpenPullRequestReceipts is null)
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
            // Older checkpoints remain readable, but a present receipt must be a complete bound proof.
            if (intent.EgressReceipt is { } receipt)
                _ = receipt.ValidateFor(new RuntimeEgressVerificationRequest(
                    1, receipt.Fence, receipt.PolicyGeneration, receipt.ContextRevision));
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
            _ = intent.ToAcceptedCommand(requireSeed: false);
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
        foreach (var (stepId, intent) in state.OpenPullRequestIntents)
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
                !IsIdentifier(intent.SourceControlPinId) ||
                intent.Repository is null || intent.Workspace is null ||
                !MafOpenPullRequestContract.IsValidIntent(intent) ||
                !state.Progress.NonModelSteps.TryGetValue(stepId, out var status) ||
                status == MafExecutionTaskStatus.Pending)
                throw new ArgumentException("MAF OpenPullRequest checkpoint intents are invalid.", nameof(state));
            if (status != MafExecutionTaskStatus.Running &&
                !state.OpenPullRequestReceipts.ContainsKey(stepId))
                throw new ArgumentException(
                    "A terminal OpenPullRequest step requires an owner-verified receipt.", nameof(state));
        }
        foreach (var (stepId, receipt) in state.OpenPullRequestReceipts)
        {
            if (receipt is null || !state.OpenPullRequestIntents.TryGetValue(stepId, out var intent) ||
                MafOpenPullRequestContract.ValidateReceipt(intent, receipt) !=
                    state.Progress.NonModelSteps.GetValueOrDefault(stepId))
                throw new ArgumentException(
                    "The OpenPullRequest receipt does not match checkpoint progress.", nameof(state));
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

    /// <summary>
    /// Checks whether a value is a bounded identifier containing only supported identifier characters.
    /// </summary>
    private static bool IsIdentifier(string? value) =>
        IsMetadataValue(value, 128) && value!.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    /// <summary>
    /// Checks whether a value is a 64-character hexadecimal digest.
    /// </summary>
    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>
    /// Checks that metadata is present, bounded, and free of control characters.
    /// </summary>
    private static bool IsMetadataValue(string? value, int maximumLength = 256) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        !value.Any(char.IsControl);
}
