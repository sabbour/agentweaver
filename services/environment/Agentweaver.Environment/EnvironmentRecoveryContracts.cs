using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

public sealed record EnvironmentRecoveryPage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    bool HasMore);

public sealed record EnvironmentRunSummary(
    string ProjectId,
    string RunId,
    string EnvironmentId,
    long LifecycleGeneration,
    EnvironmentLifecycleState LifecycleState,
    long? NetworkPolicyGeneration,
    string? NetworkPolicyState,
    Guid? SandboxOperationId,
    string? SandboxState,
    bool HasUnresolvedCleanup);

public sealed record EnvironmentWorkspaceVolumeStatus(
    string ProjectId,
    string RunId,
    string EnvironmentId,
    string VolumeId,
    long LifecycleGeneration,
    long TransitionRevision,
    long ResourceGeneration,
    long DataGeneration,
    EnvironmentWorkspaceVolumeState Phase,
    EnvironmentWorkspaceVolumeOperation LastOperation,
    WorkspaceVolumeBindingMode BindingMode,
    WorkspaceVolumeReclaimPolicy EffectiveReclaimPolicy,
    WorkspaceVolumeOwnerDeletionPolicy OwnerDeletionPolicy,
    ProviderResourceRef? Resource,
    EnvironmentWorkspaceVolumeProviderPin? ProviderPin,
    EnvironmentWorkspaceVolumeRetryDescriptor? PendingTransition);

public sealed record EnvironmentWorkspaceVolumeProviderPin(
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision);

public sealed record EnvironmentWorkspaceVolumeRetryDescriptor(
    Guid OperationId,
    EnvironmentWorkspaceVolumeOperation Operation,
    string IdempotencyKey,
    long ExpectedTransitionRevision,
    long ExpectedResourceGeneration,
    long ExpectedDataGeneration);

public sealed record EnvironmentWorkspaceVolumeTransitionStatus(
    Guid OperationId,
    string VolumeId,
    long ExpectedTransitionRevision,
    long ExpectedResourceGeneration,
    long ExpectedDataGeneration,
    long TargetTransitionRevision,
    long TargetResourceGeneration,
    long TargetDataGeneration,
    EnvironmentWorkspaceVolumeState TargetPhase,
    EnvironmentWorkspaceVolumeOperation Operation,
    EnvironmentWorkspaceVolumeTransitionState State,
    bool Replayed,
    ProviderResourceRef? FromResource,
    ProviderResourceRef? ToResource);

public sealed record EnvironmentWorkspaceVolumeMutationStatus(
    EnvironmentWorkspaceVolumeTransitionStatus Reservation,
    EnvironmentWorkspaceVolumeTransitionStatus? Completion,
    EnvironmentWorkspaceVolumeStatus? CurrentVolume,
    EnvironmentWorkspaceVolumeRetryDescriptor? PendingTransition,
    EnvironmentWorkspaceVolumeCleanupStatus? Cleanup,
    bool CleanupPending);

internal sealed record EnvironmentRecoveryCursor(
    int Version,
    string TenantId,
    string ProjectId,
    string RunId,
    string? EnvironmentId,
    long? LifecycleGeneration,
    string AfterId)
{
    private const int MaximumLength = 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Encode(EnvironmentRecoveryCursor cursor)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor, JsonOptions);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static EnvironmentRecoveryCursor Decode(
        string value,
        string tenantId,
        string projectId,
        string runId,
        string? environmentId,
        long? lifecycleGeneration)
    {
        if (value.Length is 0 or > MaximumLength)
            throw new ArgumentException("The pagination cursor is invalid.", nameof(value));
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            var cursor = JsonSerializer.Deserialize<EnvironmentRecoveryCursor>(
                Convert.FromBase64String(base64),
                JsonOptions);
            if (cursor is null ||
                cursor.Version != 1 ||
                !string.Equals(cursor.TenantId, tenantId, StringComparison.Ordinal) ||
                !string.Equals(cursor.ProjectId, projectId, StringComparison.Ordinal) ||
                !string.Equals(cursor.RunId, runId, StringComparison.Ordinal) ||
                !string.Equals(cursor.EnvironmentId, environmentId, StringComparison.Ordinal) ||
                cursor.LifecycleGeneration != lifecycleGeneration ||
                string.IsNullOrWhiteSpace(cursor.AfterId) ||
                cursor.AfterId.Length > 256 ||
                cursor.AfterId.Any(char.IsControl))
                throw new ArgumentException("The pagination cursor does not match this owner scope.", nameof(value));
            return cursor;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The pagination cursor is invalid.", nameof(value), exception);
        }
    }
}

public interface IEnvironmentWorkspaceRecoveryStore
{
    Task<IReadOnlyList<EnvironmentRunSummary>> ListRunEnvironmentsAsync(
        string tenantId,
        string projectId,
        string runId,
        string afterEnvironmentId,
        int take,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EnvironmentWorkspaceVolumeStatus>> ListWorkspaceVolumesAsync(
        EnvironmentGenerationFence fence,
        string afterVolumeId,
        int take,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeStatus?> GetWorkspaceVolumeStatusAsync(
        EnvironmentGenerationFence currentFence,
        string volumeId,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeRetryDescriptor?> GetPendingWorkspaceVolumeTransitionAsync(
        EnvironmentGenerationFence fence,
        string volumeId,
        CancellationToken cancellationToken);
}

/// <summary>Reserves a first-start network effect and can re-arm a matching effect that failed before any provider call.</summary>
public interface IEnvironmentFirstStartNetworkEffectStore
{
    /// <summary>
    /// Reserves like <see cref="IEnvironmentLifecycleStore.ReserveNetworkEffectAsync"/>. A replayed <c>Failed</c>
    /// effect returns to <c>Reserved</c> only when its fence is current, no other effect for the resource is
    /// unresolved, and the last verified generation equals the expected previous generation.
    /// </summary>
    Task<EnvironmentNetworkEffectReservation> ReserveFirstStartNetworkEffectAsync(
        EnvironmentGenerationFence fence,
        string resourceId,
        long policyGeneration,
        long expectedPreviousPolicyGeneration,
        EnvironmentNetworkEffectKind kind,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

internal static class EnvironmentWorkspaceVolumeProjection
{
    public static EnvironmentWorkspaceVolumeStatus FromSnapshot(
        EnvironmentWorkspaceVolumeSnapshot snapshot,
        EnvironmentWorkspaceVolumeRetryDescriptor? pending)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var specification = JsonSerializer.Deserialize<WorkspaceVolumeSpec>(
            snapshot.Specification,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new EnvironmentLifecycleException(
                "environment_volume_specification_corrupt",
                "The immutable workspace-volume specification is invalid.");
        return FromValues(
            snapshot.EnvironmentFence,
            snapshot.VolumeId,
            snapshot.TransitionRevision,
            snapshot.ResourceGeneration,
            snapshot.DataGeneration,
            snapshot.Phase,
            snapshot.LastOperation,
            specification,
            snapshot.Resource,
            snapshot.ProviderBinding,
            pending);
    }

    public static EnvironmentWorkspaceVolumeStatus FromValues(
        EnvironmentGenerationFence fence,
        string volumeId,
        long transitionRevision,
        long resourceGeneration,
        long dataGeneration,
        EnvironmentWorkspaceVolumeState phase,
        EnvironmentWorkspaceVolumeOperation lastOperation,
        WorkspaceVolumeSpec specification,
        ProviderResourceRef? resource,
        WorkspaceVolumeProviderBindingSnapshot? providerBinding,
        EnvironmentWorkspaceVolumeRetryDescriptor? pending) =>
        new(
            fence.Owner.ProjectId,
            fence.Owner.RunId,
            fence.Owner.EnvironmentId,
            volumeId,
            fence.LifecycleGeneration,
            transitionRevision,
            resourceGeneration,
            dataGeneration,
            phase,
            lastOperation,
            specification.BindingMode,
            specification.GetEffectiveReleasePolicy(),
            specification.OwnerDeletionPolicy,
            resource,
            providerBinding is null
                ? null
                : new EnvironmentWorkspaceVolumeProviderPin(
                    providerBinding.ProviderId,
                    providerBinding.AdapterVersion,
                    providerBinding.OptionsSchemaVersion,
                    providerBinding.OptionsRevision),
            pending);
}
