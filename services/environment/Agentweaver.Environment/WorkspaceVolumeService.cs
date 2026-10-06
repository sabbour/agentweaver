using System.Runtime.ExceptionServices;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

public sealed record WorkspaceVolumeLifecycleResult(
    EnvironmentWorkspaceVolumeTransitionReservation Reservation,
    EnvironmentWorkspaceVolumeTransitionResult? Completion,
    EnvironmentWorkspaceVolumeSnapshot? CurrentVolume);

public sealed class WorkspaceVolumeService
{
    private static readonly JsonSerializerOptions SpecificationJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IEnvironmentLifecycleStore _lifecycleStore;
    private readonly IWorkspaceVolumeProvider _volumeProvider;

    public WorkspaceVolumeService(
        IEnvironmentLifecycleStore lifecycleStore,
        IWorkspaceVolumeProvider volumeProvider)
    {
        _lifecycleStore = lifecycleStore ?? throw new ArgumentNullException(nameof(lifecycleStore));
        _volumeProvider = volumeProvider ?? throw new ArgumentNullException(nameof(volumeProvider));
    }

    public Task<EnvironmentWorkspaceVolumeSnapshot> CreateAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateFor(WorkspaceVolumeTransitionKind.Create);
        ValidateSpecificationScope(
            request.Specification!,
            environmentFence,
            request.VolumeId);
        return CreateAndValidateAsync(environmentFence, request, cancellationToken);
    }

    private async Task<EnvironmentWorkspaceVolumeSnapshot> CreateAndValidateAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _lifecycleStore.CreateWorkspaceVolumeAsync(
            environmentFence,
            request.VolumeId,
            JsonSerializer.SerializeToElement(request.Specification, SpecificationJsonOptions),
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        ValidateSnapshotIdentity(snapshot, environmentFence, request.VolumeId);
        _ = ReadSpecification(snapshot, environmentFence, request.VolumeId);
        return snapshot;
    }

    public Task<WorkspaceVolumeLifecycleResult> ProvisionAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken = default) =>
        ApplyProvisionAsync(environmentFence, request, cancellationToken);

    public Task<WorkspaceVolumeLifecycleResult> ReplaceAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateFor(WorkspaceVolumeTransitionKind.Replace);
        return Task.FromException<WorkspaceVolumeLifecycleResult>(
            new NotSupportedException(
                "Replace is unavailable until old-generation cleanup is tracked by the Environment owner."));
    }

    private async Task<WorkspaceVolumeLifecycleResult> ApplyProvisionAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateFor(WorkspaceVolumeTransitionKind.Provision);
        var snapshot = await GetVolumeAsync(
            environmentFence, request.VolumeId, cancellationToken).ConfigureAwait(false);
        var specification = ReadSpecification(snapshot, environmentFence, request.VolumeId);
        var reservation = await ReserveAsync(
            environmentFence, request, snapshot, cancellationToken).ConfigureAwait(false);
        if (reservation.Replayed ||
            reservation.TransitionState != EnvironmentWorkspaceVolumeTransitionState.Reserved)
            return await ReplayedAsync(
                environmentFence, request.VolumeId, reservation, cancellationToken).ConfigureAwait(false);

        WorkspaceVolumeResource? provisioned = null;
        var result = await RunEffectAsync(
            async token =>
            {
                provisioned = (await _volumeProvider.ProvisionAsync(
                    new WorkspaceVolumeProvisionRequest(
                        specification,
                        reservation.TargetResourceGeneration,
                        request.IdempotencyKey).Validate(),
                    token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Storage provider returned no resource."))
                    .Validate();
                if (provisioned.Resource.Generation != reservation.TargetResourceGeneration ||
                    !provisioned.NegotiatedCapabilities.Contains(
                        WorkspaceVolumeCapabilities.ForAccessMode(specification.AccessMode)))
                    throw new InvalidOperationException(
                        "The Storage provider did not verify the requested resource generation and access mode.");
                return provisioned;
            },
            token => CompleteProvisionAsync(
                reservation,
                environmentFence,
                GetReconciliationResource(provisioned, reservation),
                false,
                token),
            cancellationToken).ConfigureAwait(false);

        // Record a successful provider effect even if the caller cancels afterward.
        var completion = await CompleteProvisionAsync(
            reservation,
            environmentFence,
            result.Resource,
            true,
            CancellationToken.None).ConfigureAwait(false);
        return new WorkspaceVolumeLifecycleResult(reservation, completion, null);
    }

    public Task<WorkspaceVolumeLifecycleResult> BindAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken = default) =>
        ApplyMetadataTransitionAsync(
            environmentFence, request, WorkspaceVolumeTransitionKind.Bind, cancellationToken);

    public Task<WorkspaceVolumeLifecycleResult> UnbindAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken = default) =>
        ApplyMetadataTransitionAsync(
            environmentFence, request, WorkspaceVolumeTransitionKind.Unbind, cancellationToken);

    private async Task<WorkspaceVolumeLifecycleResult> ApplyMetadataTransitionAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        WorkspaceVolumeTransitionKind operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateFor(operation);
        var snapshot = await GetVolumeAsync(
            environmentFence, request.VolumeId, cancellationToken).ConfigureAwait(false);
        var specification = ReadSpecification(snapshot, environmentFence, request.VolumeId);
        var reservation = await ReserveAsync(
            environmentFence, request, snapshot, cancellationToken).ConfigureAwait(false);
        if (reservation.Replayed ||
            reservation.TransitionState != EnvironmentWorkspaceVolumeTransitionState.Reserved)
            return await ReplayedAsync(
                environmentFence, request.VolumeId, reservation, cancellationToken).ConfigureAwait(false);

        var resource = RequirePinnedResource(reservation);
        if (operation == WorkspaceVolumeTransitionKind.Bind)
            new WorkspaceVolumeBindingRequest(
                specification,
                reservation.ExpectedResourceGeneration,
                environmentFence,
                resource,
                reservation.ExpectedDataGeneration,
                request.IdempotencyKey).Validate();
        else
            new WorkspaceVolumeUnbindRequest(
                specification,
                reservation.ExpectedResourceGeneration,
                environmentFence,
                resource,
                reservation.ExpectedDataGeneration,
                request.IdempotencyKey).Validate();

        var completion = await CompleteMetadataTransitionAsync(
            operation, reservation, environmentFence, resource, cancellationToken).ConfigureAwait(false);
        return new WorkspaceVolumeLifecycleResult(reservation, completion, null);
    }

    public async Task<WorkspaceVolumeLifecycleResult> ReleaseAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateFor(WorkspaceVolumeTransitionKind.Release);
        var snapshot = await GetVolumeAsync(
            environmentFence, request.VolumeId, cancellationToken).ConfigureAwait(false);
        var specification = ReadSpecification(snapshot, environmentFence, request.VolumeId);
        var reservation = await ReserveAsync(
            environmentFence, request, snapshot, cancellationToken).ConfigureAwait(false);
        if (reservation.Replayed ||
            reservation.TransitionState != EnvironmentWorkspaceVolumeTransitionState.Reserved)
            return await ReplayedAsync(
                environmentFence, request.VolumeId, reservation, cancellationToken).ConfigureAwait(false);

        if (reservation.ExpectedResourceGeneration == 0)
        {
            var verifiedNoEffect = await _lifecycleStore.CompleteWorkspaceVolumeReleaseAsync(
                reservation.OperationId,
                environmentFence,
                true,
                true,
                cancellationToken).ConfigureAwait(false);
            return new WorkspaceVolumeLifecycleResult(reservation, verifiedNoEffect, null);
        }

        var resource = RequirePinnedResource(reservation);
        var releaseRequest = new WorkspaceVolumeReleaseRequest(
            new WorkspaceVolumeReference(
                specification.ProjectId,
                specification.VolumeId,
                reservation.ExpectedResourceGeneration),
            resource,
            specification.ReclaimPolicy,
            request.IdempotencyKey).Validate();

        _ = await RunEffectAsync(
            async token =>
            {
                var receipt = await _volumeProvider.ReleaseAsync(releaseRequest, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Storage provider returned no release receipt.");
                return receipt.ValidateFor(releaseRequest);
            },
            token => _lifecycleStore.CompleteWorkspaceVolumeReleaseAsync(
                reservation.OperationId, environmentFence, true, false, token),
            cancellationToken).ConfigureAwait(false);

        // Record a successful provider effect even if the caller cancels afterward.
        var completion = await _lifecycleStore.CompleteWorkspaceVolumeReleaseAsync(
            reservation.OperationId,
            environmentFence,
            true,
            true,
            CancellationToken.None).ConfigureAwait(false);
        return new WorkspaceVolumeLifecycleResult(reservation, completion, null);
    }

    public async Task<WorkspaceVolumeAttachmentNegotiation> NegotiateAttachmentAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeResource storageResource,
        WorkspaceVolumeMountManifest mountManifest,
        WorkspaceVolumeMountDeclaration mount,
        ProviderResourceRef sandboxResource,
        WorkspaceSandboxAttachmentProfile sandboxProfile,
        WorkspaceVolumeAttachmentProtocol protocol,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(storageResource);
        ArgumentNullException.ThrowIfNull(mountManifest);
        ArgumentNullException.ThrowIfNull(mount);
        ArgumentNullException.ThrowIfNull(sandboxResource);
        ArgumentNullException.ThrowIfNull(sandboxProfile);
        ArgumentNullException.ThrowIfNull(mount.Volume);
        var snapshot = await GetVolumeAsync(
            environmentFence, mount.Volume.VolumeId, cancellationToken).ConfigureAwait(false);
        if (snapshot.Phase != EnvironmentWorkspaceVolumeState.Bound ||
            snapshot.Resource is null ||
            storageResource.Validate().Resource != snapshot.Resource)
            throw new InvalidOperationException(
                "Attachment negotiation requires the currently bound and pinned Storage resource.");

        return WorkspaceVolumeAttachmentNegotiator.Negotiate(
            environmentFence.Owner.ProjectId,
            environmentFence.Owner.EnvironmentId,
            environmentFence.Owner.RunId,
            ReadSpecification(snapshot, environmentFence, mount.Volume.VolumeId),
            storageResource,
            mountManifest,
            mount,
            sandboxResource,
            environmentFence,
            snapshot.DataGeneration,
            sandboxProfile,
            protocol);
    }

    public async Task FlushAsync(
        WorkspaceVolumeFlushRequest request,
        WorkspaceVolumeResource pinnedResource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pinnedResource);
        request.Validate();
        pinnedResource.Validate();
        if (pinnedResource.Resource != request.Resource)
            throw new InvalidOperationException("Flush requires the pinned Storage resource.");

        var snapshot = await GetVolumeAsync(
            request.EnvironmentFence,
            request.Spec.VolumeId,
            cancellationToken).ConfigureAwait(false);
        var specification = ReadSpecification(snapshot, request.EnvironmentFence, request.Spec.VolumeId);
        new WorkspaceVolumeFlushRequest(
            specification,
            request.ResourceGeneration,
            request.EnvironmentFence,
            request.Resource,
            request.ExpectedDataGeneration,
            request.IdempotencyKey).Validate();
        if (snapshot.Resource != request.Resource ||
            snapshot.ResourceGeneration != request.ResourceGeneration ||
            snapshot.DataGeneration != request.ExpectedDataGeneration)
            throw new InvalidOperationException("Flush does not match the current owner generations.");
        if (!pinnedResource.NegotiatedCapabilities.Contains(WorkspaceVolumeCapabilities.DurableFlush))
            throw new NotSupportedException("The pinned Storage provider does not advertise durable flush.");

        throw new NotSupportedException(
            "The Storage provider contract has no verified flush operation; DataGeneration was not advanced.");
    }

    private async Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveAsync(
        EnvironmentGenerationFence fence,
        WorkspaceVolumeTransitionRequest request,
        EnvironmentWorkspaceVolumeSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var reservation = await (request.Operation switch
        {
            WorkspaceVolumeTransitionKind.Provision =>
                _lifecycleStore.ReserveWorkspaceVolumeProvisionAsync(
                    fence, request.VolumeId, request.ExpectedTransitionRevision,
                    request.ExpectedResourceGeneration, request.ExpectedDataGeneration,
                    request.IdempotencyKey, cancellationToken),
            WorkspaceVolumeTransitionKind.Bind =>
                _lifecycleStore.ReserveWorkspaceVolumeBindAsync(
                    fence, request.VolumeId, request.ExpectedTransitionRevision,
                    request.ExpectedResourceGeneration, request.ExpectedDataGeneration,
                    request.IdempotencyKey, cancellationToken),
            WorkspaceVolumeTransitionKind.Unbind =>
                _lifecycleStore.ReserveWorkspaceVolumeUnbindAsync(
                    fence, request.VolumeId, request.ExpectedTransitionRevision,
                    request.ExpectedResourceGeneration, request.ExpectedDataGeneration,
                    request.IdempotencyKey, cancellationToken),
            WorkspaceVolumeTransitionKind.Release =>
                _lifecycleStore.ReserveWorkspaceVolumeReleaseAsync(
                    fence, request.VolumeId, request.ExpectedTransitionRevision,
                    request.ExpectedResourceGeneration, request.ExpectedDataGeneration,
                    request.IdempotencyKey, cancellationToken),
            _ => throw new NotSupportedException(
                $"{request.Operation} is not an Environment-owned Storage transition in this slice.")
        }).ConfigureAwait(false);
        return ValidateReservation(snapshot, fence, request, reservation);
    }

    private Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteProvisionAsync(
        EnvironmentWorkspaceVolumeTransitionReservation reservation,
        EnvironmentGenerationFence fence,
        ProviderResourceRef? resource,
        bool verified,
        CancellationToken cancellationToken) =>
        _lifecycleStore.CompleteWorkspaceVolumeProvisionAsync(
            reservation.OperationId, fence, true, resource, verified, cancellationToken);

    private Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteMetadataTransitionAsync(
        WorkspaceVolumeTransitionKind operation,
        EnvironmentWorkspaceVolumeTransitionReservation reservation,
        EnvironmentGenerationFence fence,
        ProviderResourceRef resource,
        CancellationToken cancellationToken) =>
        operation switch
        {
            WorkspaceVolumeTransitionKind.Bind =>
                _lifecycleStore.CompleteWorkspaceVolumeBindAsync(
                    reservation.OperationId, fence, true, resource, true, cancellationToken),
            WorkspaceVolumeTransitionKind.Unbind =>
                _lifecycleStore.CompleteWorkspaceVolumeUnbindAsync(
                    reservation.OperationId, fence, true, resource, true, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private async Task<WorkspaceVolumeLifecycleResult> ReplayedAsync(
        EnvironmentGenerationFence fence,
        string volumeId,
        EnvironmentWorkspaceVolumeTransitionReservation reservation,
        CancellationToken cancellationToken) =>
        new(
            reservation,
            null,
            await GetVolumeAsync(fence, volumeId, cancellationToken).ConfigureAwait(false));

    private async Task<EnvironmentWorkspaceVolumeSnapshot> GetVolumeAsync(
        EnvironmentGenerationFence fence,
        string volumeId,
        CancellationToken cancellationToken)
    {
        var snapshot = await _lifecycleStore.GetWorkspaceVolumeAsync(
            fence, volumeId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Workspace volume '{volumeId}' was not found.");
        ValidateSnapshotIdentity(snapshot, fence, volumeId);
        return snapshot;
    }

    private static WorkspaceVolumeSpec ReadSpecification(
        EnvironmentWorkspaceVolumeSnapshot snapshot,
        EnvironmentGenerationFence fence,
        string volumeId)
    {
        var specification = JsonSerializer.Deserialize<WorkspaceVolumeSpec>(
            snapshot.Specification,
            SpecificationJsonOptions)
            ?? throw new InvalidOperationException("The Environment owner returned an empty volume specification.");
        ValidateSpecificationScope(specification, fence, volumeId);
        return specification;
    }

    private static void ValidateSnapshotIdentity(
        EnvironmentWorkspaceVolumeSnapshot snapshot,
        EnvironmentGenerationFence fence,
        string volumeId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.EnvironmentFence != fence)
            throw new InvalidOperationException("The Environment owner returned a snapshot for a different fence.");
        if (!string.Equals(snapshot.VolumeId, volumeId, StringComparison.Ordinal))
            throw new InvalidOperationException("The Environment owner returned a different workspace volume.");
    }

    private static void ValidateSpecificationScope(
        WorkspaceVolumeSpec specification,
        EnvironmentGenerationFence fence,
        string volumeId)
    {
        if (!string.Equals(specification.VolumeId, volumeId, StringComparison.Ordinal) ||
            !string.Equals(specification.ProjectId, fence.Owner.ProjectId, StringComparison.Ordinal) ||
            !specification.AllowsEnvironment(fence.Owner.ProjectId, fence.Owner.EnvironmentId))
            throw new InvalidOperationException(
                "The workspace-volume specification is not authorized for the Environment fence.");
        if (specification.BindingMode == WorkspaceVolumeBindingMode.Environment &&
            specification.Owner.Kind == WorkspaceVolumeOwnerKind.Run &&
            !string.Equals(specification.Owner.Id, fence.Owner.RunId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "A run-owned workspace volume is restricted to its owning run.");
    }

    private static EnvironmentWorkspaceVolumeTransitionReservation ValidateReservation(
        EnvironmentWorkspaceVolumeSnapshot snapshot,
        EnvironmentGenerationFence fence,
        WorkspaceVolumeTransitionRequest request,
        EnvironmentWorkspaceVolumeTransitionReservation reservation)
    {
        ValidateSnapshotIdentity(snapshot, fence, request.VolumeId);
        if (reservation.EnvironmentFence != fence ||
            !string.Equals(reservation.VolumeId, request.VolumeId, StringComparison.Ordinal) ||
            reservation.Operation != ToOwnerOperation(request.Operation) ||
            reservation.ExpectedTransitionRevision != request.ExpectedTransitionRevision ||
            reservation.ExpectedResourceGeneration != request.ExpectedResourceGeneration ||
            reservation.ExpectedDataGeneration != request.ExpectedDataGeneration ||
            reservation.TargetTransitionRevision != request.ExpectedTransitionRevision + 1 ||
            reservation.TargetResourceGeneration != request.TargetResourceGeneration ||
            reservation.TargetDataGeneration != request.TargetDataGeneration)
            throw new InvalidOperationException(
                "The Environment owner returned a transition reservation that does not match the request.");

        if (!reservation.Replayed &&
            reservation.TransitionState == EnvironmentWorkspaceVolumeTransitionState.Reserved &&
            (snapshot.TransitionRevision != request.ExpectedTransitionRevision ||
             snapshot.ResourceGeneration != request.ExpectedResourceGeneration ||
             snapshot.DataGeneration != request.ExpectedDataGeneration ||
             snapshot.Resource != reservation.CurrentResource))
            throw new InvalidOperationException(
                "The reserved transition does not match the current Environment-owned volume counters and resource.");

        return reservation;
    }

    private static EnvironmentWorkspaceVolumeOperation ToOwnerOperation(
        WorkspaceVolumeTransitionKind operation) =>
        operation switch
        {
            WorkspaceVolumeTransitionKind.Provision => EnvironmentWorkspaceVolumeOperation.Provision,
            WorkspaceVolumeTransitionKind.Bind => EnvironmentWorkspaceVolumeOperation.Bind,
            WorkspaceVolumeTransitionKind.Unbind => EnvironmentWorkspaceVolumeOperation.Unbind,
            WorkspaceVolumeTransitionKind.Release => EnvironmentWorkspaceVolumeOperation.Release,
            _ => throw new NotSupportedException($"{operation} is not executed by this Storage facade.")
        };

    private static ProviderResourceRef RequirePinnedResource(
        EnvironmentWorkspaceVolumeTransitionReservation reservation) =>
        reservation.CurrentResource
        ?? throw new InvalidOperationException("The transition requires a pinned provider resource.");

    private static ProviderResourceRef? GetReconciliationResource(
        WorkspaceVolumeResource? provisioned,
        EnvironmentWorkspaceVolumeTransitionReservation reservation)
    {
        if (provisioned is null ||
            provisioned.Resource.Generation != reservation.TargetResourceGeneration)
            return null;
        return provisioned.Resource;
    }

    private static async Task<T> RunEffectAsync<T>(
        Func<CancellationToken, Task<T>> effect,
        Func<CancellationToken, Task<EnvironmentWorkspaceVolumeTransitionResult>> markUnverified,
        CancellationToken cancellationToken)
    {
        try
        {
            return await effect(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception effectFailure)
        {
            EnvironmentWorkspaceVolumeTransitionResult reconciliation;
            try
            {
                reconciliation = await markUnverified(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception reconciliationFailure)
            {
                throw new AggregateException(
                    "The provider effect failed and the owner transition could not be marked for reconciliation.",
                    effectFailure,
                    reconciliationFailure);
            }

            if (reconciliation.TransitionState != EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired)
                throw new AggregateException(
                    "The provider effect failed but the owner transition was not left reconcilable.",
                    effectFailure,
                    new InvalidOperationException(
                        $"The owner returned transition state {reconciliation.TransitionState}."));

            ExceptionDispatchInfo.Capture(effectFailure).Throw();
            throw;
        }
    }
}
