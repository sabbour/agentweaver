using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

public static class SessionSuspendResumeContractVersions
{
    public const int CurrentManifestVersion = 1;
    public const string CoordinatorCheckpointStoreName = "coordinator-execution";
}

public enum SessionSuspendResumeManifestState
{
    Suspended,
    Interrupted
}

public sealed record SessionSuspendResumeCheckpointReference(
    SessionIdentity Identity,
    string StoreName,
    string CheckpointId,
    long Revision,
    string WorkPlanId,
    long DecisionStateVersion)
{
    public SessionSuspendResumeCheckpointReference Validate()
    {
        SessionSuspendResumeManifestValidation.ValidateIdentity(Identity);
        if (StoreName != SessionSuspendResumeContractVersions.CoordinatorCheckpointStoreName ||
            !SessionSuspendResumeManifestValidation.IsIdentifier(CheckpointId, 256) ||
            Revision <= 0 ||
            !SessionSuspendResumeManifestValidation.IsIdentifier(WorkPlanId, 256) ||
            DecisionStateVersion <= 0)
            throw new ArgumentException("The execution checkpoint reference is invalid.");
        return this;
    }
}

/// <summary>
/// Provider-neutral evidence only; the Core producer must resolve the actual checkpoint and drain.
/// </summary>
public sealed record SessionSuspendResumeManifest(
    int ContractVersion,
    Guid ManifestId,
    SessionIdentity Identity,
    SessionSuspendResumeManifestState State,
    EnvironmentGenerationFence? EnvironmentFence,
    long? CoreExecutionFence,
    SessionMaterialAcknowledgment? CacheAcknowledgment,
    SessionSuspendResumeCheckpointReference? Checkpoint,
    long? FlushedJournalPosition,
    WorkspaceVolumeReference? WorkspaceVolume,
    ProviderResourceRef? WorkspaceResource,
    long? WorkspaceDataGeneration,
    string? WorkspaceTreeSha256,
    string? WorkspaceProviderCheckpointId,
    ProviderResourceRef? GuestSnapshotResource,
    long? GuestSnapshotLifecycleGeneration,
    long? NetworkIntentGeneration,
    ImmutableArray<string> MissingEvidence)
{
    public SessionSuspendResumeManifest Validate()
    {
        if (ContractVersion != SessionSuspendResumeContractVersions.CurrentManifestVersion ||
            ManifestId == Guid.Empty ||
            !Enum.IsDefined(State))
            throw new ArgumentException("The session suspend/resume manifest header is invalid.");

        SessionSuspendResumeManifestValidation.ValidateIdentity(Identity);
        if (EnvironmentFence is { } environmentFence)
        {
            if (environmentFence.Owner.ProjectId != Identity.ProjectId ||
                environmentFence.Owner.RunId != Identity.RunId ||
                environmentFence.LifecycleGeneration <= 0)
                throw new ArgumentException("The environment generation fence does not match the session owner.");
        }

        if (CoreExecutionFence is <= 0)
            throw new ArgumentOutOfRangeException(nameof(CoreExecutionFence));
        if (FlushedJournalPosition is < 0)
            throw new ArgumentOutOfRangeException(nameof(FlushedJournalPosition));
        if (NetworkIntentGeneration is <= 0)
            throw new ArgumentOutOfRangeException(nameof(NetworkIntentGeneration));

        if (CacheAcknowledgment is { } cache)
            ValidateCacheAcknowledgment(cache, Identity);
        if (Checkpoint is { } checkpoint)
        {
            checkpoint.Validate();
            if (checkpoint.Identity != Identity)
                throw new ArgumentException("The execution checkpoint belongs to another session.");
        }

        ValidateWorkspace();
        ValidateGuestSnapshot();
        ValidateMissingEvidence();
        ValidateStateRequirements();
        return this;
    }

    private void ValidateWorkspace()
    {
        if (WorkspaceVolume is { } volume)
        {
            volume.Validate();
            if (volume.ProjectId != Identity.ProjectId)
                throw new ArgumentException("The workspace volume belongs to another project.");
        }

        if (WorkspaceResource is { } resource)
            SessionSuspendResumeManifestValidation.ValidateProviderResource(resource, ProviderSeam.Storage);

        if (WorkspaceResource is { } workspaceResource &&
            WorkspaceVolume is { } workspaceVolume &&
            workspaceResource.Generation != workspaceVolume.ResourceGeneration)
            throw new ArgumentException("The workspace references do not identify the same resource generation.");

        if (WorkspaceDataGeneration is <= 0)
            throw new ArgumentOutOfRangeException(nameof(WorkspaceDataGeneration));
        if (WorkspaceDataGeneration is not null &&
            (WorkspaceVolume is null || WorkspaceResource is null))
            throw new ArgumentException("Workspace data generation requires an exact Storage resource reference.");

        if (WorkspaceTreeSha256 is not null && WorkspaceProviderCheckpointId is not null)
            throw new ArgumentException("Workspace evidence must use either a tree hash or a provider checkpoint.");
        if (WorkspaceTreeSha256 is { } treeHash &&
            !SessionSuspendResumeManifestValidation.IsSha256(treeHash))
            throw new ArgumentException("The workspace tree hash must be a lowercase SHA-256 digest.");
        if (WorkspaceProviderCheckpointId is { } checkpointId &&
            !SessionSuspendResumeManifestValidation.IsIdentifier(checkpointId, 256))
            throw new ArgumentException("The workspace provider checkpoint ID is invalid.");
        if ((WorkspaceTreeSha256 is not null || WorkspaceProviderCheckpointId is not null) &&
            (WorkspaceVolume is null || WorkspaceResource is null || WorkspaceDataGeneration is null))
            throw new ArgumentException("Workspace content evidence requires its exact resource and data generation.");
    }

    private void ValidateGuestSnapshot()
    {
        if ((GuestSnapshotResource is null) != (GuestSnapshotLifecycleGeneration is null))
            throw new ArgumentException("Guest snapshot resource and lifecycle generation must be supplied together.");
        if (GuestSnapshotResource is { } resource)
            SessionSuspendResumeManifestValidation.ValidateProviderResource(resource, ProviderSeam.Snapshots);
        if (GuestSnapshotLifecycleGeneration is <= 0)
            throw new ArgumentOutOfRangeException(nameof(GuestSnapshotLifecycleGeneration));
    }

    private void ValidateMissingEvidence()
    {
        if (MissingEvidence.IsDefault || MissingEvidence.Length > 64 ||
            MissingEvidence.Any(gap => !SessionSuspendResumeManifestValidation.IsIdentifier(gap, 128)) ||
            MissingEvidence.Distinct(StringComparer.Ordinal).Count() != MissingEvidence.Length)
            throw new ArgumentException("Missing evidence must be a bounded, initialized set of unique identifiers.",
                nameof(MissingEvidence));
    }

    private void ValidateStateRequirements()
    {
        if (State == SessionSuspendResumeManifestState.Interrupted)
        {
            if (MissingEvidence.IsEmpty)
                throw new ArgumentException("An interrupted manifest must identify its evidence gaps.");
            return;
        }

        if (EnvironmentFence is null ||
            CoreExecutionFence is null ||
            CacheAcknowledgment is null ||
            Checkpoint is null ||
            FlushedJournalPosition is null ||
            WorkspaceVolume is null ||
            WorkspaceResource is null ||
            WorkspaceDataGeneration is null ||
            (WorkspaceTreeSha256 is null && WorkspaceProviderCheckpointId is null) ||
            NetworkIntentGeneration is null ||
            MissingEvidence.Length != 0)
            throw new ArgumentException("A suspended manifest must contain complete consistency evidence.");
    }

    private static void ValidateCacheAcknowledgment(
        SessionMaterialAcknowledgment acknowledgment,
        SessionIdentity identity)
    {
        if (acknowledgment.ContractVersion != 1 ||
            acknowledgment.Identity != identity ||
            acknowledgment.EventId == Guid.Empty ||
            acknowledgment.Position <= 0 ||
            acknowledgment.Reference is null ||
            acknowledgment.Reference.Material?.Kind != SessionMaterialKind.SdkCache)
            throw new ArgumentException("The cache acknowledgment does not match the session owner or SDK cache.");

        SessionMaterialValidation.Validate(acknowledgment.Reference);
    }
}

internal static class SessionSuspendResumeManifestValidation
{
    internal static void ValidateIdentity(SessionIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.ProjectId) ||
            string.IsNullOrWhiteSpace(identity.RunId) ||
            string.IsNullOrWhiteSpace(identity.SessionId))
            throw new ArgumentException("A complete session identity is required.", nameof(identity));

        _ = new SessionIdentity(identity.ProjectId, identity.RunId, identity.SessionId);
    }

    internal static void ValidateProviderResource(ProviderResourceRef resource, ProviderSeam expectedSeam)
    {
        if (resource.Seam != expectedSeam ||
            !IsIdentifier(resource.ProviderId, 256) ||
            !IsIdentifier(resource.ResourceId, 256) ||
            resource.Generation <= 0)
            throw new ArgumentException("The provider resource reference is invalid.");
    }

    internal static bool IsIdentifier(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
