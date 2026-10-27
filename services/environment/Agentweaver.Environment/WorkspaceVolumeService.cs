using System.Runtime.ExceptionServices;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Providers.Storage.AzureFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Agentweaver.Environment;

/// <summary>Coordinates durable workspace-volume state with the selected storage provider.</summary>
public sealed class WorkspaceVolumeService
{
    private static readonly JsonSerializerOptions SpecificationJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CleanupLeaseDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CleanupProviderTimeout = TimeSpan.FromSeconds(30);

    private readonly IEnvironmentLifecycleStore _lifecycleStore;
    private readonly IWorkspaceVolumeProvider _volumeProvider;
    private readonly ILogger<WorkspaceVolumeService> _logger;

    /// <summary>Creates a service over the Environment lifecycle store and selected workspace-volume provider.</summary>
    public WorkspaceVolumeService(
        IEnvironmentLifecycleStore lifecycleStore,
        IWorkspaceVolumeProvider volumeProvider,
        ILogger<WorkspaceVolumeService>? logger = null)
    {
        _lifecycleStore = lifecycleStore ?? throw new ArgumentNullException(nameof(lifecycleStore));
        _volumeProvider = volumeProvider ?? throw new ArgumentNullException(nameof(volumeProvider));
        _logger = logger ?? NullLogger<WorkspaceVolumeService>.Instance;
    }

    /// <summary>Creates a volume record after checking its specification matches the Environment owner scope.</summary>
    public Task<EnvironmentWorkspaceVolumeSnapshot> CreateAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? authorizationAndFenceCheck = null)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateFor(WorkspaceVolumeTransitionKind.Create);
        ValidateSpecificationScope(
            request.Specification!,
            environmentFence,
            request.VolumeId);
        return CreateAndValidateAsync(
            environmentFence, request, cancellationToken, authorizationAndFenceCheck);
    }

    /// <summary>Creates P1 volume metadata only with a retained Environment binding.</summary>
    public Task<EnvironmentWorkspaceVolumeSnapshot> CreateP1Async(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        P1InvocationBinding binding,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? authorizationAndFenceCheck,
        Func<CancellationToken, Task> p1BindingCheck)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(p1BindingCheck);
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateFor(WorkspaceVolumeTransitionKind.Create);
        ValidateSpecificationScope(request.Specification!, environmentFence, request.VolumeId);
        return CreateAndValidateAsync(
            environmentFence,
            request,
            cancellationToken,
            authorizationAndFenceCheck,
            binding,
            p1BindingCheck);
    }

    /// <summary>Reads and validates the current volume snapshot without changing provider state.</summary>
    public async Task<EnvironmentWorkspaceVolumeSnapshot?> InspectAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        _ = new WorkspaceVolumeReference(environmentFence.Owner.ProjectId, volumeId, 1).Validate();
        var snapshot = await _lifecycleStore.GetWorkspaceVolumeAsync(
            environmentFence, volumeId, cancellationToken).ConfigureAwait(false);
        if (snapshot is not null)
        {
            ValidateSnapshotIdentity(snapshot, environmentFence, volumeId);
            _ = ReadSpecification(snapshot, environmentFence, volumeId);
        }
        return snapshot;
    }

    private async Task<EnvironmentWorkspaceVolumeSnapshot> CreateAndValidateAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? authorizationAndFenceCheck,
        P1InvocationBinding? p1Binding = null,
        Func<CancellationToken, Task>? p1BindingCheck = null)
    {
        async Task CheckP1BeforeWriteAsync(CancellationToken token)
        {
            await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, token).ConfigureAwait(false);
            if (p1Binding is not null)
                await (p1BindingCheck ??
                    throw new InvalidOperationException("A P1 metadata creation requires current-binding revalidation."))(
                    token).ConfigureAwait(false);
        }

        await CheckP1BeforeWriteAsync(cancellationToken).ConfigureAwait(false);
        var specification = JsonSerializer.SerializeToElement(
            request.Specification, SpecificationJsonOptions);
        var snapshot = p1Binding is null
            ? await _lifecycleStore.CreateWorkspaceVolumeAsync(
                environmentFence,
                request.VolumeId,
                specification,
                request.IdempotencyKey,
                cancellationToken).ConfigureAwait(false)
            : await _lifecycleStore.CreateP1WorkspaceVolumeAsync(
                environmentFence,
                request.VolumeId,
                specification,
                request.IdempotencyKey,
                p1Binding,
                cancellationToken,
                CheckP1BeforeWriteAsync).ConfigureAwait(false);
        ValidateSnapshotIdentity(snapshot, environmentFence, request.VolumeId);
        _ = ReadSpecification(snapshot, environmentFence, request.VolumeId);
        return snapshot;
    }

    /// <summary>Provisions the storage resource using a durable transition reservation and owner fence.</summary>
    public Task<WorkspaceVolumeLifecycleResult> ProvisionAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? authorizationAndFenceCheck = null) =>
        ApplyProvisionAsync(environmentFence, request, cancellationToken, authorizationAndFenceCheck);

    /// <summary>Provisions a volume tied to a current Core P1 binding and retains the actual provider result.</summary>
    public Task<WorkspaceVolumeLifecycleResult> ProvisionP1Async(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        P1InvocationBinding binding,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? authorizationAndFenceCheck,
        Func<CancellationToken, Task> p1BindingCheck)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(p1BindingCheck);
        return ApplyProvisionAsync(
            environmentFence,
            request,
            cancellationToken,
            authorizationAndFenceCheck,
            binding,
            p1BindingCheck);
    }

    /// <summary>Replaces a storage resource and queues cleanup for the superseded resource.</summary>
    public Task<WorkspaceVolumeLifecycleResult> ReplaceAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? authorizationAndFenceCheck = null)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateFor(WorkspaceVolumeTransitionKind.Replace);
        return ApplyReplaceAsync(environmentFence, request, cancellationToken, authorizationAndFenceCheck);
    }

    /// <summary>Reserves replacement, provisions the new resource, commits it, then reconciles cleanup of the old one.</summary>
    private async Task<WorkspaceVolumeLifecycleResult> ApplyReplaceAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? authorizationAndFenceCheck)
    {
        var snapshot = await GetVolumeAsync(
            environmentFence, request.VolumeId, cancellationToken).ConfigureAwait(false);
        var specification = ReadSpecification(snapshot, environmentFence, request.VolumeId);
        await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, cancellationToken)
            .ConfigureAwait(false);
        var reservation = await ReserveAsync(
            environmentFence, request, snapshot, cancellationToken).ConfigureAwait(false);
        if (reservation.Replayed)
        {
            var replayedCurrent = await GetVolumeAsync(
                environmentFence, request.VolumeId, cancellationToken).ConfigureAwait(false);
            if (reservation.TransitionState != EnvironmentWorkspaceVolumeTransitionState.Completed)
                return new(reservation, null, replayedCurrent);

            CleanupProcessingResult replayedCleanup;
            try
            {
                replayedCleanup = await ProcessCleanupAsync(
                    environmentFence,
                    reservation.OperationId,
                    cancellationToken,
                    authorizationAndFenceCheck).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) when (
                !cancellationToken.IsCancellationRequested &&
                GetExpectedCleanupFailureCode(cleanupFailure) is not null)
            {
                var failureCode = GetExpectedCleanupFailureCode(cleanupFailure)!;
                LogCleanupFailure(reservation.OperationId, request.VolumeId, "replace replay", failureCode, cleanupFailure);
                return new(
                    reservation,
                    null,
                    replayedCurrent,
                    null,
                    failureCode,
                    cleanupFailure.GetType().Name);
            }
            return new(
                reservation,
                null,
                replayedCurrent,
                replayedCleanup.Status,
                replayedCleanup.FailureCode,
                replayedCleanup.FailureType);
        }

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
            token => CompleteReplaceAsync(
                reservation,
                environmentFence,
                GetReconciliationResource(provisioned, reservation),
                GetReconciliationBinding(provisioned, reservation),
                false,
                token),
            cancellationToken,
            authorizationAndFenceCheck,
            token => FailReplaceAsync(reservation, environmentFence, token)).ConfigureAwait(false);

        // Replacement is committed before cleanup, so cleanup failure must not undo its new generation.
        await CheckAuthorizationBeforeOwnerCommitAsync(
            authorizationAndFenceCheck,
            token => CompleteReplaceAsync(
                reservation,
                environmentFence,
                result.Resource,
                result.ProviderBinding,
                false,
                token)).ConfigureAwait(false);
        var completion = await CompleteReplaceAsync(
            reservation,
            environmentFence,
            result.Resource,
            result.ProviderBinding,
            true,
            CancellationToken.None).ConfigureAwait(false);
        if (completion.TransitionState != EnvironmentWorkspaceVolumeTransitionState.Completed)
            return new(reservation, completion, null);
        CleanupProcessingResult cleanupResult;
        try
        {
            cleanupResult = await ProcessCleanupAsync(
                environmentFence,
                reservation.OperationId,
                cancellationToken,
                authorizationAndFenceCheck).ConfigureAwait(false);
        }
        catch (Exception cleanupFailure) when (
            !cancellationToken.IsCancellationRequested &&
            GetExpectedCleanupFailureCode(cleanupFailure) is not null)
        {
            var failureCode = GetExpectedCleanupFailureCode(cleanupFailure)!;
            LogCleanupFailure(reservation.OperationId, request.VolumeId, "replace", failureCode, cleanupFailure);
            return new(
                reservation,
                completion,
                null,
                null,
                failureCode,
                cleanupFailure.GetType().Name);
        }
        return new(
            reservation,
            completion,
            null,
            cleanupResult.Status,
            cleanupResult.FailureCode,
            cleanupResult.FailureType);
    }

    /// <summary>Reserves provisioning before the provider call and records a completed or uncertain result.</summary>
    private async Task<WorkspaceVolumeLifecycleResult> ApplyProvisionAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? authorizationAndFenceCheck,
        P1InvocationBinding? p1Binding = null,
        Func<CancellationToken, Task>? p1BindingCheck = null)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateFor(WorkspaceVolumeTransitionKind.Provision);
        var snapshot = await GetVolumeAsync(
            environmentFence, request.VolumeId, cancellationToken).ConfigureAwait(false);
        var specification = ReadSpecification(snapshot, environmentFence, request.VolumeId);
        await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, cancellationToken)
            .ConfigureAwait(false);
        var reservation = p1Binding is null
            ? await ReserveAsync(environmentFence, request, snapshot, cancellationToken).ConfigureAwait(false)
            : await _lifecycleStore.ReserveP1WorkspaceVolumeProvisionAsync(
                environmentFence,
                request.VolumeId,
                request.ExpectedTransitionRevision,
                request.ExpectedResourceGeneration,
                request.ExpectedDataGeneration,
                request.IdempotencyKey,
                p1Binding,
                cancellationToken,
                p1BindingCheck ?? throw new RuntimeAuthorizationException(
                    "p1_invocation_binding_unavailable")).ConfigureAwait(false);
        if (reservation.Replayed ||
            reservation.TransitionState != EnvironmentWorkspaceVolumeTransitionState.Reserved)
            return await ReplayedAsync(
                environmentFence, request.VolumeId, reservation, cancellationToken).ConfigureAwait(false);

        WorkspaceVolumeResource? provisioned = null;
        async Task BeforeProviderEffectAsync(CancellationToken token)
        {
            await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, token).ConfigureAwait(false);
            if (p1BindingCheck is not null)
                await p1BindingCheck(token).ConfigureAwait(false);
        }

        Task<EnvironmentWorkspaceVolumeTransitionResult> Complete(bool mayHaveApplied, bool verified,
            ProviderResourceRef? resource, WorkspaceVolumeProviderBindingSnapshot? providerBinding,
            CancellationToken token, Func<CancellationToken, Task>? beforeCommitCheck = null) =>
            p1Binding is null
                ? CompleteProvisionAsync(
                    reservation, environmentFence, resource, providerBinding, verified, token)
                : CompleteP1ProvisionAsync(
                    reservation, environmentFence, resource, providerBinding, mayHaveApplied,
                    verified, p1Binding, token, beforeCommitCheck);

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
            token => Complete(
                mayHaveApplied: true,
                verified: false,
                GetReconciliationResource(provisioned, reservation),
                GetReconciliationBinding(provisioned, reservation),
                token),
            cancellationToken,
            BeforeProviderEffectAsync,
            token => p1Binding is null
                ? FailProvisionAsync(reservation, environmentFence, token)
                : Complete(
                    mayHaveApplied: false,
                    verified: false,
                    resource: null,
                    providerBinding: null,
                    token),
            recordNoEffectBeforeStart: p1Binding is not null).ConfigureAwait(false);

        // Record a successful provider effect even if the caller cancels afterward.
        await CheckAuthorizationBeforeOwnerCommitAsync(
            BeforeProviderEffectAsync,
            token => Complete(
                mayHaveApplied: true,
                verified: false,
                result.Resource,
                result.ProviderBinding,
                token)).ConfigureAwait(false);
        EnvironmentWorkspaceVolumeTransitionResult completion;
        try
        {
            completion = await Complete(
                mayHaveApplied: true,
                verified: true,
                result.Resource,
                result.ProviderBinding,
                CancellationToken.None,
                p1BindingCheck).ConfigureAwait(false);
        }
        catch (RuntimeAuthorizationException) when (p1Binding is not null)
        {
            _ = await Complete(
                mayHaveApplied: true,
                verified: false,
                result.Resource,
                result.ProviderBinding,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return new WorkspaceVolumeLifecycleResult(reservation, completion, null);
    }

    /// <summary>Binds a provisioned volume to a workspace after a generation-fenced provider operation.</summary>
    public Task<WorkspaceVolumeLifecycleResult> BindAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? authorizationAndFenceCheck = null) =>
        ApplyMetadataTransitionAsync(
            environmentFence, request, WorkspaceVolumeTransitionKind.Bind, cancellationToken,
            authorizationAndFenceCheck);

    /// <summary>Unbinds a volume while retaining its storage resource for later reuse or cleanup.</summary>
    public Task<WorkspaceVolumeLifecycleResult> UnbindAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? authorizationAndFenceCheck = null) =>
        ApplyMetadataTransitionAsync(
            environmentFence, request, WorkspaceVolumeTransitionKind.Unbind, cancellationToken,
            authorizationAndFenceCheck);

    /// <summary>Applies bind, unbind, or release metadata transitions through durable reservations and provider evidence.</summary>
    private async Task<WorkspaceVolumeLifecycleResult> ApplyMetadataTransitionAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        WorkspaceVolumeTransitionKind operation,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? authorizationAndFenceCheck)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateFor(operation);
        var snapshot = await GetVolumeAsync(
            environmentFence, request.VolumeId, cancellationToken).ConfigureAwait(false);
        var specification = ReadSpecification(snapshot, environmentFence, request.VolumeId);
        await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, cancellationToken)
            .ConfigureAwait(false);
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

        await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, cancellationToken)
            .ConfigureAwait(false);
        var completion = await CompleteMetadataTransitionAsync(
            operation, reservation, environmentFence, resource, cancellationToken).ConfigureAwait(false);
        return new WorkspaceVolumeLifecycleResult(reservation, completion, null);
    }

    /// <summary>Releases the volume and schedules provider cleanup when its lifecycle state allows release.</summary>
    public async Task<WorkspaceVolumeLifecycleResult> ReleaseAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? authorizationAndFenceCheck = null)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateFor(WorkspaceVolumeTransitionKind.Release);
        var snapshot = await GetVolumeAsync(
            environmentFence, request.VolumeId, cancellationToken).ConfigureAwait(false);
        var specification = ReadSpecification(snapshot, environmentFence, request.VolumeId);
        if (specification.BindingMode == WorkspaceVolumeBindingMode.Shared &&
            specification.ReclaimPolicy == WorkspaceVolumeReclaimPolicy.Delete)
            throw new NotSupportedException(
                "Shared workspace volumes cannot be deleted without an authoritative cross-owner reference registry.");
        await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, cancellationToken)
            .ConfigureAwait(false);
        var reservation = await ReserveAsync(
            environmentFence, request, snapshot, cancellationToken).ConfigureAwait(false);
        var releaseReconciliation =
            reservation.Replayed &&
            reservation.Operation == EnvironmentWorkspaceVolumeOperation.Release &&
            reservation.TransitionState == EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired;
        if (reservation.Replayed ||
            reservation.TransitionState != EnvironmentWorkspaceVolumeTransitionState.Reserved)
        {
            if (!releaseReconciliation)
                return await ReplayedAsync(
                    environmentFence, request.VolumeId, reservation, cancellationToken).ConfigureAwait(false);

            if (snapshot.TransitionRevision != reservation.ExpectedTransitionRevision ||
                snapshot.ResourceGeneration != reservation.ExpectedResourceGeneration ||
                snapshot.DataGeneration != reservation.ExpectedDataGeneration ||
                snapshot.Resource != reservation.CurrentResource ||
                !ProviderBindingsEqual(snapshot.ProviderBinding, reservation.CurrentProviderBinding))
                throw new InvalidOperationException(
                    "The pending Release no longer matches the current Environment-owned volume resource.");
        }

        if (reservation.ExpectedResourceGeneration == 0)
        {
            await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, cancellationToken)
                .ConfigureAwait(false);
            var verifiedNoEffect = await _lifecycleStore.CompleteWorkspaceVolumeReleaseAsync(
                reservation.OperationId,
                environmentFence,
                true,
                true,
                cancellationToken).ConfigureAwait(false);
            return new WorkspaceVolumeLifecycleResult(reservation, verifiedNoEffect, null);
        }

        var resource = RequirePinnedResource(reservation);
        var providerBinding = reservation.CurrentProviderBinding
            ?? throw new InvalidOperationException("The Environment owner has no pinned provider binding.");
        var releaseRequest = new WorkspaceVolumeReleaseRequest(
            new WorkspaceVolumeReference(
                specification.ProjectId,
                specification.VolumeId,
                reservation.ExpectedResourceGeneration),
            resource,
            specification.BindingMode,
            specification.GetEffectiveReleasePolicy(),
            specification.OwnerDeletionPolicy,
            providerBinding,
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
            cancellationToken,
            authorizationAndFenceCheck,
            releaseReconciliation
                ? null
                : token => _lifecycleStore.CompleteWorkspaceVolumeReleaseAsync(
                    reservation.OperationId, environmentFence, false, false, token)).ConfigureAwait(false);

        // Record a successful provider effect even if the caller cancels afterward.
        await CheckAuthorizationBeforeOwnerCommitAsync(
            authorizationAndFenceCheck,
            token => _lifecycleStore.CompleteWorkspaceVolumeReleaseAsync(
                reservation.OperationId, environmentFence, true, false, token)).ConfigureAwait(false);
        var completion = await _lifecycleStore.CompleteWorkspaceVolumeReleaseAsync(
            reservation.OperationId,
            environmentFence,
            true,
            true,
            CancellationToken.None).ConfigureAwait(false);
        return new WorkspaceVolumeLifecycleResult(reservation, completion, null);
    }

    /// <summary>Retries bounded cleanup work claimed from the durable cleanup queue.</summary>
    public async Task<EnvironmentWorkspaceVolumeCleanupStatus?> RetryCleanupAsync(
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? authorizationAndFenceCheck = null)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        return (await ProcessCleanupAsync(
            environmentFence, sourceReplaceOperationId: null, cancellationToken, authorizationAndFenceCheck)
            .ConfigureAwait(false)).Status;
    }

    /// <summary>Reconciles cleanup created by one exact replacement operation.</summary>
    public async Task<EnvironmentWorkspaceVolumeCleanupStatus?> ReconcileCleanupAsync(
        EnvironmentGenerationFence environmentFence,
        Guid sourceReplaceOperationId,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? authorizationAndFenceCheck = null)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        if (sourceReplaceOperationId == Guid.Empty)
            throw new ArgumentException("A source replace operation ID is required.", nameof(sourceReplaceOperationId));
        return (await ProcessCleanupAsync(
            environmentFence, sourceReplaceOperationId, cancellationToken, authorizationAndFenceCheck)
            .ConfigureAwait(false)).Status;
    }

    /// <summary>
    /// Claims cleanup work, bounds provider calls, and records completion or retry state.
    /// Expected provider failures remain visible as cleanup status instead of erasing the retained resource evidence.
    /// </summary>
    private async Task<CleanupProcessingResult> ProcessCleanupAsync(
        EnvironmentGenerationFence environmentFence,
        Guid? sourceReplaceOperationId,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? authorizationAndFenceCheck)
    {
        await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, cancellationToken)
            .ConfigureAwait(false);
        var lease = await _lifecycleStore.ClaimWorkspaceVolumeCleanupAsync(
            environmentFence,
            sourceReplaceOperationId,
            CleanupLeaseDuration,
            cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            var status = sourceReplaceOperationId is { } operationId
                ? await _lifecycleStore.GetWorkspaceVolumeCleanupStatusAsync(
                    environmentFence, operationId, cancellationToken).ConfigureAwait(false)
                : null;
            return new(
                status,
                sourceReplaceOperationId is not null && status is null ? "cleanup_work_not_observed" : null,
                sourceReplaceOperationId is not null && status is null
                    ? nameof(EnvironmentWorkspaceVolumeCleanupStatus)
                    : null);
        }

        try
        {
            using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            providerTimeout.CancelAfter(CleanupProviderTimeout);
            await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, providerTimeout.Token)
                .ConfigureAwait(false);
            var receipt = await _volumeProvider.ReleaseAsync(
                    lease.ReleaseRequest,
                    providerTimeout.Token)
                .WaitAsync(CleanupProviderTimeout, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Storage provider returned no cleanup receipt.");
            _ = receipt.ValidateFor(lease.ReleaseRequest);
            await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, providerTimeout.Token)
                .ConfigureAwait(false);
            return new(await _lifecycleStore.CompleteWorkspaceVolumeCleanupAsync(
                lease,
                receipt,
                CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception cleanupFailure) when (
            !cancellationToken.IsCancellationRequested &&
            GetExpectedCleanupFailureCode(cleanupFailure) is not null)
        {
            try
            {
                await _lifecycleStore.ReleaseWorkspaceVolumeCleanupLeaseAsync(
                    lease,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception leaseReleaseFailure) when (
                GetExpectedCleanupFailureCode(leaseReleaseFailure) is not null)
            {
                var releaseFailureCode = GetExpectedCleanupFailureCode(leaseReleaseFailure)!;
                LogCleanupFailure(
                    lease.SourceReplaceOperationId,
                    lease.VolumeId,
                    "lease release",
                    releaseFailureCode,
                    leaseReleaseFailure);
            }

            var failureCode = GetExpectedCleanupFailureCode(cleanupFailure)!;
            LogCleanupFailure(
                lease.SourceReplaceOperationId,
                lease.VolumeId,
                "provider cleanup",
                failureCode,
                cleanupFailure);
            var currentStatus = await _lifecycleStore.GetWorkspaceVolumeCleanupStatusAsync(
                environmentFence,
                lease.SourceReplaceOperationId,
                CancellationToken.None).ConfigureAwait(false);

            return new(currentStatus, failureCode, cleanupFailure.GetType().Name);
        }
    }

    /// <summary>Negotiates the storage provider's attachment protocol for a volume and Sandbox placement.</summary>
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

    /// <summary>Builds the exact flush request from persisted volume state and runtime workspace evidence.</summary>
    internal WorkspaceVolumeFlushRequest ResolveFlushRequest(
        EnvironmentWorkspaceVolumeSnapshot snapshot,
        EnvironmentRuntimeWorkspaceContext context,
        string idempotencyKey)
    {
        var workspace = context.Workspace.Validate();
        if (snapshot.EnvironmentFence != workspace.EnvironmentFence ||
            snapshot.VolumeId != workspace.Volume.VolumeId ||
            snapshot.ResourceGeneration != workspace.Volume.ResourceGeneration ||
            snapshot.Resource != workspace.StorageResource ||
            snapshot.DataGeneration != workspace.DataGeneration ||
            snapshot.TransitionRevision != context.TransitionRevision ||
            snapshot.Phase != EnvironmentWorkspaceVolumeState.Attached ||
            workspace.ReadOnly || workspace.Protocol != WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim)
            throw new RuntimeAuthorizationException("runtime_suspend_workspace_stale");
        return new WorkspaceVolumeFlushRequest(
            ReadSpecification(snapshot, snapshot.EnvironmentFence, snapshot.VolumeId),
            snapshot.ResourceGeneration, snapshot.EnvironmentFence,
            snapshot.Resource!, snapshot.DataGeneration, idempotencyKey)
        {
            ProviderBinding = snapshot.ProviderBinding
        }.Validate();
    }

    /// <summary>Flushes host workspace data and persists the returned durability evidence.</summary>
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
            "Flush requires the trusted mounted-workspace producer through the Environment owner path.");
    }

    /// <summary>Reserves a flush, verifies host evidence, and completes the volume transition at its next data generation.</summary>
    public async Task<WorkspaceVolumeLifecycleResult> FlushAsync(
        EnvironmentGenerationFence environmentFence,
        WorkspaceVolumeTransitionRequest request,
        Func<WorkspaceVolumeFlushRequest, CancellationToken, Task<WorkspaceVolumeFlushEvidence>> trustedMountedFlush,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? authorizationAndFenceCheck = null,
        Func<CancellationToken, Task>? beforeCommitCheck = null)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(trustedMountedFlush);
        request.ValidateFor(WorkspaceVolumeTransitionKind.Flush);
        var snapshot = await GetVolumeAsync(environmentFence, request.VolumeId, cancellationToken)
            .ConfigureAwait(false);
        var specification = ReadSpecification(snapshot, environmentFence, request.VolumeId);
        await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, cancellationToken).ConfigureAwait(false);
        var reservation = await ReserveAsync(environmentFence, request, snapshot, cancellationToken)
            .ConfigureAwait(false);
        var providerRequest = new WorkspaceVolumeFlushRequest(
            specification, reservation.ExpectedResourceGeneration, environmentFence,
            RequirePinnedResource(reservation), reservation.ExpectedDataGeneration, request.IdempotencyKey)
        {
            ProviderBinding = reservation.CurrentProviderBinding
        }.Validate();
        if (reservation.Replayed &&
            reservation.TransitionState != EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired ||
            !reservation.Replayed &&
            reservation.TransitionState != EnvironmentWorkspaceVolumeTransitionState.Reserved)
        {
            await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, cancellationToken).ConfigureAwait(false);
            return FlushResult(reservation, null,
                await GetVolumeAsync(environmentFence, request.VolumeId, cancellationToken).ConfigureAwait(false),
                providerRequest);
        }
        if (snapshot.Resource != reservation.CurrentResource ||
            snapshot.ResourceGeneration != reservation.ExpectedResourceGeneration ||
            snapshot.DataGeneration != reservation.ExpectedDataGeneration ||
            snapshot.TransitionRevision != reservation.ExpectedTransitionRevision ||
            !ProviderBindingsEqual(snapshot.ProviderBinding, reservation.CurrentProviderBinding))
            throw new InvalidOperationException("The pending flush no longer matches the exact workspace generation.");

        WorkspaceVolumeFlushEvidence? evidence = null;
        Task<EnvironmentWorkspaceVolumeTransitionResult> MarkUnverified(CancellationToken token) =>
            _lifecycleStore.CompleteWorkspaceVolumeFlushAsync(
                reservation.OperationId, environmentFence, true, evidence, false, token);
        var receipt = await RunEffectAsync(
            async token =>
            {
                var result = await _volumeProvider.FlushAsync(providerRequest, async (exactRequest, producerToken) =>
                {
                    if (!SameFlushRequest(providerRequest, exactRequest))
                        throw new InvalidOperationException("The Storage provider changed the trusted flush request.");
                    await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, producerToken)
                        .ConfigureAwait(false);
                    var produced = (await trustedMountedFlush(exactRequest, producerToken).ConfigureAwait(false)).Validate();
                    produced.Receipt.ValidateFor(providerRequest);
                    evidence = produced;
                    return evidence.Receipt;
                }, token).ConfigureAwait(false) ??
                    throw new InvalidOperationException("The Storage provider returned no workspace flush receipt.");
                result.ValidateFor(providerRequest);
                if (evidence is null || evidence.Receipt != result)
                    throw new InvalidOperationException("The Storage receipt differs from its completed trusted producer.");
                return result;
            },
            MarkUnverified,
            cancellationToken,
            authorizationAndFenceCheck,
            token => _lifecycleStore.CompleteWorkspaceVolumeFlushAsync(
                reservation.OperationId, environmentFence, false, null, false, false, token),
            recordNoEffectBeforeStart: true)
            .ConfigureAwait(false);

        EnvironmentWorkspaceVolumeTransitionResult? completion = null;
        await CheckAuthorizationBeforeOwnerCommitAsync(async token =>
        {
            await CheckAuthorizationAndFenceAsync(authorizationAndFenceCheck, token).ConfigureAwait(false);
            completion = await _lifecycleStore.CompleteWorkspaceVolumeFlushAsync(
                reservation.OperationId, environmentFence, true, evidence, true, token, beforeCommitCheck)
                .ConfigureAwait(false);
        }, MarkUnverified).ConfigureAwait(false);
        if (completion is null ||
            completion.OperationId != reservation.OperationId ||
            completion.EnvironmentFence != environmentFence ||
            completion.Operation != EnvironmentWorkspaceVolumeOperation.Flush ||
            completion.TargetResourceGeneration != reservation.TargetResourceGeneration ||
            completion.TargetDataGeneration != reservation.TargetDataGeneration ||
            completion.TargetTransitionRevision != reservation.TargetTransitionRevision ||
            completion.FlushEvidence?.Receipt != receipt)
            throw new InvalidOperationException("The Environment owner did not persist the exact workspace flush evidence.");
        return FlushResult(reservation, completion, null, providerRequest);
    }

    /// <summary>Compares the complete suspend-bound flush request by serialized value.</summary>
    private static bool SameFlushRequest(WorkspaceVolumeFlushRequest expected, WorkspaceVolumeFlushRequest actual) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(expected, SpecificationJsonOptions),
            JsonSerializer.SerializeToElement(actual, SpecificationJsonOptions));

    /// <summary>Builds a lifecycle result that includes the validated host flush evidence.</summary>
    private static WorkspaceVolumeLifecycleResult FlushResult(
        EnvironmentWorkspaceVolumeTransitionReservation reservation,
        EnvironmentWorkspaceVolumeTransitionResult? completion,
        EnvironmentWorkspaceVolumeSnapshot? current,
        WorkspaceVolumeFlushRequest request)
    {
        var evidence = (completion?.FlushEvidence ?? reservation.FlushEvidence)?.Validate();
        var state = completion?.TransitionState ?? reservation.TransitionState;
        if (evidence is null && state == EnvironmentWorkspaceVolumeTransitionState.Completed)
            throw new EnvironmentLifecycleException(
                "workspace_volume_flush_evidence_missing", "The completed flush has no saved producer evidence.");
        evidence?.Receipt.ValidateFor(request);
        RuntimeHostSuspendReceipt? host = null;
        if (evidence?.RuntimeHostReceipt is { } saved)
        {
            try
            {
                host = JsonSerializer.Deserialize<RuntimeHostSuspendReceipt>(saved, SpecificationJsonOptions) ??
                    throw new JsonException("The saved host receipt is empty.");
            }
            catch (JsonException)
            {
                throw new EnvironmentLifecycleException(
                    "workspace_volume_host_receipt_corrupt", "The saved runtime host receipt is invalid.");
            }
        }
        return new(reservation, completion, current)
        {
            FlushReceipt = evidence?.Receipt,
            HostSuspendReceipt = host
        };
    }

    /// <summary>Checks the current owner fence and reserves a durable volume transition before external effects.</summary>
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
            WorkspaceVolumeTransitionKind.Replace =>
                _lifecycleStore.ReserveWorkspaceVolumeReplaceAsync(
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
            WorkspaceVolumeTransitionKind.Flush =>
                _lifecycleStore.ReserveWorkspaceVolumeFlushAsync(
                    fence, request.VolumeId, request.ExpectedTransitionRevision,
                    request.ExpectedResourceGeneration, request.ExpectedDataGeneration,
                    request.TargetDataGeneration, request.IdempotencyKey, cancellationToken),
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
        WorkspaceVolumeProviderBindingSnapshot? providerBinding,
        bool verified,
        CancellationToken cancellationToken) =>
        _lifecycleStore.CompleteWorkspaceVolumeProvisionAsync(
            reservation.OperationId, fence, true, resource, providerBinding, verified, cancellationToken);

    private Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteP1ProvisionAsync(
        EnvironmentWorkspaceVolumeTransitionReservation reservation,
        EnvironmentGenerationFence fence,
        ProviderResourceRef? resource,
        WorkspaceVolumeProviderBindingSnapshot? providerBinding,
        bool effectMayHaveApplied,
        bool verified,
        P1InvocationBinding binding,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommitCheck) =>
        _lifecycleStore.CompleteP1WorkspaceVolumeProvisionAsync(
            reservation.OperationId,
            fence,
            effectMayHaveApplied,
            resource,
            providerBinding,
            verified,
            binding,
            cancellationToken,
            beforeCommitCheck);

    private Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteReplaceAsync(
        EnvironmentWorkspaceVolumeTransitionReservation reservation,
        EnvironmentGenerationFence fence,
        ProviderResourceRef? resource,
        WorkspaceVolumeProviderBindingSnapshot? providerBinding,
        bool verified,
        CancellationToken cancellationToken) =>
        _lifecycleStore.CompleteWorkspaceVolumeReplaceAsync(
            reservation.OperationId, fence, true, resource, providerBinding, verified, cancellationToken);

    private Task<EnvironmentWorkspaceVolumeTransitionResult> FailProvisionAsync(
        EnvironmentWorkspaceVolumeTransitionReservation reservation,
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken) =>
        _lifecycleStore.CompleteWorkspaceVolumeProvisionAsync(
            reservation.OperationId, fence, false, null, null, false, cancellationToken);

    private Task<EnvironmentWorkspaceVolumeTransitionResult> FailReplaceAsync(
        EnvironmentWorkspaceVolumeTransitionReservation reservation,
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken) =>
        _lifecycleStore.CompleteWorkspaceVolumeReplaceAsync(
            reservation.OperationId, fence, false, null, null, false, cancellationToken);

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

    /// <summary>Reconstructs an idempotent replay and resumes any cleanup associated with its completed transition.</summary>
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

    /// <summary>Deserializes and validates the stored specification against its owner and volume identifier.</summary>
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

    /// <summary>Requires a transition reservation to match the requested owner, volume, operation, and revisions.</summary>
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
            reservation.TransitionState == EnvironmentWorkspaceVolumeTransitionState.Reserved)
        {
            if (snapshot.TransitionRevision != request.ExpectedTransitionRevision ||
               snapshot.ResourceGeneration != request.ExpectedResourceGeneration ||
               snapshot.DataGeneration != request.ExpectedDataGeneration ||
               snapshot.Resource != reservation.CurrentResource ||
               !ProviderBindingsEqual(snapshot.ProviderBinding, reservation.CurrentProviderBinding))
               throw new InvalidOperationException(
                   "The reserved transition does not match the current Environment-owned volume counters and resource.");
        }

        return reservation;
    }

    private static bool ProviderBindingsEqual(
        WorkspaceVolumeProviderBindingSnapshot? left,
        WorkspaceVolumeProviderBindingSnapshot? right) =>
        ReferenceEquals(left, right) ||
        (left is not null &&
         right is not null &&
         string.Equals(left.ProviderId, right.ProviderId, StringComparison.Ordinal) &&
         string.Equals(left.AdapterVersion, right.AdapterVersion, StringComparison.Ordinal) &&
         left.OptionsSchemaVersion == right.OptionsSchemaVersion &&
         string.Equals(left.OptionsRevision, right.OptionsRevision, StringComparison.Ordinal) &&
         System.Text.Json.JsonElement.DeepEquals(left.OptionsSnapshot, right.OptionsSnapshot) &&
         System.Text.Json.JsonElement.DeepEquals(left.ReleaseDescriptor, right.ReleaseDescriptor));

    private static EnvironmentWorkspaceVolumeOperation ToOwnerOperation(
        WorkspaceVolumeTransitionKind operation) =>
        operation switch
        {
            WorkspaceVolumeTransitionKind.Provision => EnvironmentWorkspaceVolumeOperation.Provision,
            WorkspaceVolumeTransitionKind.Replace => EnvironmentWorkspaceVolumeOperation.Replace,
            WorkspaceVolumeTransitionKind.Bind => EnvironmentWorkspaceVolumeOperation.Bind,
            WorkspaceVolumeTransitionKind.Unbind => EnvironmentWorkspaceVolumeOperation.Unbind,
            WorkspaceVolumeTransitionKind.Flush => EnvironmentWorkspaceVolumeOperation.Flush,
            WorkspaceVolumeTransitionKind.Release => EnvironmentWorkspaceVolumeOperation.Release,
            _ => throw new NotSupportedException($"{operation} is not executed by this Storage facade.")
        };

    /// <summary>Returns the exact provider resource pin or fails when the volume has no current resource.</summary>
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

    private static WorkspaceVolumeProviderBindingSnapshot? GetReconciliationBinding(
        WorkspaceVolumeResource? provisioned,
        EnvironmentWorkspaceVolumeTransitionReservation reservation) =>
        provisioned is not null &&
        provisioned.Resource.Generation == reservation.TargetResourceGeneration
            ? provisioned.ProviderBinding
            : null;

    /// <summary>Runs a provider effect and records the outcome even when cancellation or provider failure occurs.</summary>
    private static async Task<T> RunEffectAsync<T>(
        Func<CancellationToken, Task<T>> effect,
        Func<CancellationToken, Task<EnvironmentWorkspaceVolumeTransitionResult>> markUnverified,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeEffectCheck,
        Func<CancellationToken, Task<EnvironmentWorkspaceVolumeTransitionResult>>? markNoEffect = null,
        bool recordNoEffectBeforeStart = false)
    {
        var effectStarted = false;
        try
        {
            await CheckAuthorizationAndFenceAsync(beforeEffectCheck, cancellationToken).ConfigureAwait(false);
            effectStarted = true;
            return await effect(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception effectFailure)
        {
            if (!effectStarted && !recordNoEffectBeforeStart)
                ExceptionDispatchInfo.Capture(effectFailure).Throw();

            if ((!effectStarted || effectFailure is AzureFilesCsiException { EffectMayHaveApplied: false }) &&
                markNoEffect is not null)
            {
                EnvironmentWorkspaceVolumeTransitionResult failed;
                try
                {
                    failed = await markNoEffect(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception failureRecordException)
                {
                    throw new AggregateException(
                        "The provider rejected the request before applying an effect, but the owner transition could not be recorded as failed.",
                        effectFailure,
                        failureRecordException);
                }

                if (failed.TransitionState is not (
                    EnvironmentWorkspaceVolumeTransitionState.Failed or
                    EnvironmentWorkspaceVolumeTransitionState.Stale))
                    throw new AggregateException(
                        "The provider rejected the request before applying an effect, but the owner transition was not safely failed.",
                        effectFailure,
                        new InvalidOperationException(
                            $"The owner returned transition state {failed.TransitionState}."));

                ExceptionDispatchInfo.Capture(effectFailure).Throw();
                throw;
            }

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

    /// <summary>Runs the optional current-authority check before beginning a provider effect.</summary>
    private static Task CheckAuthorizationAndFenceAsync(
        Func<CancellationToken, Task>? authorizationAndFenceCheck,
        CancellationToken cancellationToken) =>
        authorizationAndFenceCheck is null
            ? Task.CompletedTask
            : authorizationAndFenceCheck(cancellationToken);

    /// <summary>Rechecks caller authority and Environment fence immediately before the owner state is committed.</summary>
    private static async Task CheckAuthorizationBeforeOwnerCommitAsync(
        Func<CancellationToken, Task>? authorizationAndFenceCheck,
        Func<CancellationToken, Task<EnvironmentWorkspaceVolumeTransitionResult>> markUnverified)
    {
        try
        {
            await CheckAuthorizationAndFenceAsync(
                authorizationAndFenceCheck, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception authorizationFailure)
        {
            try
            {
                var reconciliation = await markUnverified(CancellationToken.None).ConfigureAwait(false);
                if (reconciliation.TransitionState != EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired)
                    throw new InvalidOperationException(
                        $"The owner returned transition state {reconciliation.TransitionState}.");
            }
            catch (Exception reconciliationFailure)
            {
                throw new AggregateException(
                    "Authorization changed after the provider effect and the owner transition could not be marked for reconciliation.",
                    authorizationFailure,
                    reconciliationFailure);
            }

            ExceptionDispatchInfo.Capture(authorizationFailure).Throw();
        }
    }

    private static string? GetExpectedCleanupFailureCode(Exception exception) =>
        exception switch
        {
            AzureFilesCsiException => "azure_files_cleanup_failure",
            AzureFilesKubernetesApiException => "kubernetes_cleanup_failure",
            HttpRequestException => "cleanup_transport_failure",
            TimeoutException => "cleanup_timeout",
            OperationCanceledException => "cleanup_timeout",
            NpgsqlException => "cleanup_storage_failure",
            EnvironmentLifecycleException
            {
                Code: "environment_volume_cleanup_lease_stale"
            } => "cleanup_lease_expired",
            _ => null
        };

    private void LogCleanupFailure(
        Guid sourceReplaceOperationId,
        string volumeId,
        string phase,
        string failureCode,
        Exception failure) =>
        _logger.LogWarning(
            "Workspace-volume cleanup remains pending. ReplaceOperationId: {ReplaceOperationId}; " +
            "VolumeId: {VolumeId}; Phase: {Phase}; FailureCode: {FailureCode}; FailureType: {FailureType}",
            sourceReplaceOperationId,
            volumeId,
            phase,
            failureCode,
            failure.GetType().Name);

    /// <summary>Provider cleanup outcome and safe failure details for a workspace-volume cleanup attempt.</summary>
    private sealed record CleanupProcessingResult(
        EnvironmentWorkspaceVolumeCleanupStatus? Status,
        string? FailureCode = null,
        string? FailureType = null);
}
