using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Agentweaver.Providers;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Agentweaver.Providers.Storage.AzureFiles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentRuntimeRunStartManagerTests(EnvironmentPostgresFixture database)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Fact]
    public async Task ChildFirstStartReusesTheExactPlacementAndRejectsTheRootContract()
    {
        using var fixture = CreateFixture(database, 8, child: true);
        var operation = Guid.NewGuid();
        var credential = new SecretCredential(
            "first-start-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        var actor = new RuntimeActorAuthorization(credential, fixture.Owner.TenantId);
        try
        {
            var request = new RuntimeRunStartPlacementRequest(2, fixture.Owner, null, operation);
            var first = await fixture.Manager.ProvisionAsync(request, actor, CancellationToken.None);
            var retry = await fixture.Manager.ProvisionAsync(request, actor, CancellationToken.None);
            Assert.Equal(first, retry);
            Assert.Equal(1, fixture.Sandbox.ProvisionCalls);
            Assert.Equal(1, fixture.Storage.ProvisionCalls);
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Manager.ProvisionAsync(new(1, fixture.Owner, null, operation), actor, CancellationToken.None));
            Assert.Equal(1, fixture.Sandbox.ProvisionCalls);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("indeterminate")]
    [InlineData("cancelled")]
    public async Task TerminalCleanupUsesTheExactCurrentFenceAndRetiresOnlyTheBoundSandbox(string state)
    {
        using var fixture = CreateFixture(database, 8);
        var credential = new SecretCredential(
            "first-start-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        var actor = new RuntimeActorAuthorization(credential, fixture.Owner.TenantId);
        try
        {
            var receipt = await fixture.Manager.ProvisionAsync(
                new(1, fixture.Owner, null, Guid.NewGuid()), actor, CancellationToken.None);
            fixture.Projects.Authorization = Authorization(
                fixture.Owner, bound: false, ProjectAuthorizationPermission.CancelRun);
            var projection = Projection(fixture.Owner, receipt, Guid.NewGuid(), "cleanup_pending",
                JsonSerializer.SerializeToElement(new { placement = receipt }, JsonOptions)) with
            {
                ContractVersion = 2,
                RunState = state,
                CancellationFence = 0,
                TerminalFence = fixture.Owner.ExecutionFence + (state == "cancelled" ? 1 : 0),
                ParentRootSessionId = fixture.Owner.SessionId
            };
            fixture.OwnerHandler.CleanupProjection = projection;
            var stale = projection with { IntentVersion = projection.IntentVersion + 1 };
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Manager.CleanupAsync(new(2, null, stale), actor, CancellationToken.None));
            Assert.Equal(0, fixture.Sandbox.ReleaseCalls);
            var cleanup = await fixture.Manager.CleanupAsync(
                new(2, null, projection), actor, CancellationToken.None);
            Assert.Equal("cleanup_complete", cleanup.CleanupDisposition);
            Assert.Equal(receipt.SandboxResource, fixture.Sandbox.LastReleasedResource);
            Assert.Equal(1, fixture.Sandbox.ReleaseCalls);
            Assert.Throws<RuntimeAuthorizationException>(() =>
                (projection with { RunState = "active" }).Validate());
            if (state == "cancelled")
                Assert.Throws<RuntimeAuthorizationException>(() =>
                    (projection with { TerminalFence = projection.ExecutionFence }).Validate());
        }
        finally
        {
            credential.Invalidate();
        }
    }

    [Fact]
    public async Task FirstStartPinsTheFixedWorkspaceAndCleanupReleasesOnlyTheSavedSandbox()
    {
        using var fixture = CreateFixture(database, 8);
        var bootstrapOperationId = Guid.NewGuid();
        var credential = new SecretCredential(
            "first-start-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        var actor = new RuntimeActorAuthorization(credential, fixture.Owner.TenantId);

        RuntimeRunStartPlacementReceipt receipt;
        try
        {
            receipt = await fixture.Manager.ProvisionAsync(
                new(1, fixture.Owner, null, bootstrapOperationId),
                actor,
                CancellationToken.None);
        }
        finally
        {
            credential.Invalidate();
        }

        var fence = new EnvironmentGenerationFence(
            new(fixture.Owner.TenantId, fixture.Owner.ProjectId, fixture.Owner.RunId, receipt.EnvironmentId),
            receipt.LifecycleGeneration);
        var savedLease = Assert.IsType<SandboxLeaseSnapshot>(
            await fixture.LeaseStore.GetAsync(
                fence, receipt.SandboxResource.Generation, CancellationToken.None));
        var savedRecovery = JsonSerializer.Deserialize<EnvironmentSandboxManager.SandboxProviderRecoveryIntent>(
            savedLease.ProvisionIntent.ProviderRequest.GetRawText(), RecoveryJsonOptions);
        Assert.NotNull(savedRecovery);
        Assert.Equal(receipt.SandboxIdempotencyKey, savedRecovery.Request.IdempotencyKey);
        Assert.False(savedLease.ProvisionIntent.ProviderRequest.TryGetProperty("idempotencyKey", out _));
        var workspace = Assert.IsType<EnvironmentWorkspaceVolumeSnapshot>(
            await fixture.Store.GetWorkspaceVolumeAsync(fence, receipt.WorkspaceVolumeId, CancellationToken.None));
        var spec = workspace.Specification.Deserialize<WorkspaceVolumeSpec>(JsonOptions);
        Assert.NotNull(spec);
        Assert.Equal(1, spec.CapacityGiB);
        Assert.Equal("/workspace", fixture.Sandbox.LastRequest!.Workspace.Negotiation.MountPath);
        Assert.False(fixture.Sandbox.LastRequest.Workspace.Negotiation.ReadOnly);
        Assert.Equal(WorkspaceVolumeBindingMode.Environment, spec.BindingMode);
        Assert.Equal(WorkspaceVolumeAccessMode.ReadWriteMany, spec.AccessMode);
        Assert.Equal(WorkspaceVolumeConsistency.Strict, spec.Consistency);
        Assert.Equal(WorkspaceVolumeReclaimPolicy.Retain, spec.ReclaimPolicy);
        Assert.Equal(WorkspaceVolumeOwnerDeletionPolicy.Retain, spec.OwnerDeletionPolicy);
        Assert.Empty(spec.AuthorizedEnvironmentIds);
        Assert.Equal(0, workspace.DataGeneration);
        Assert.Equal(1, fixture.Storage.ProvisionCalls);
        Assert.Equal(receipt.WorkspaceVolumeId, fixture.Storage.LastRequest!.Spec.VolumeId);
        Assert.Equal(1, fixture.Sandbox.ProvisionCalls);
        Assert.Equal(fixture.Profiles.DefaultSourceProfileId, receipt.SourceProfileId);

        fixture.Storage.Contents = "retained-workspace-data";
        fixture.Projects.Authorization = Authorization(
            fixture.Owner,
            bound: false,
            ProjectAuthorizationPermission.CancelRun);
        var pendingProjection = Projection(fixture.Owner, receipt, Guid.NewGuid(), "cleanup_pending",
            JsonSerializer.SerializeToElement(new { placement = receipt }, JsonOptions));
        fixture.OwnerHandler.CleanupProjection = pendingProjection;
        var releaseKey = $"coordinator-release-{pendingProjection.IntentId:N}";
        var interrupted = await fixture.LeaseStore.BeginRetirementAsync(
                fence,
                receipt.SandboxResource.Generation,
                receipt.ProviderFencingGeneration,
                SandboxRetirementReason.AuthorizedAbandon,
                releaseKey,
                new SandboxRetirementAuthorization(
                    fixture.Owner.ActorIssuer,
                    fixture.Owner.ActorId,
                    1),
                terminalEvidence: null,
                CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Releasing, interrupted.State);
        Assert.Equal(receipt.ProviderFencingGeneration, interrupted.ProviderFencingGeneration);
        Assert.Equal(receipt.CurrentFencingGeneration + 1, interrupted.CurrentFencingGeneration);

        var cleanupCredential = new SecretCredential(
            "cancel-replay-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        var cleanupActor = new RuntimeActorAuthorization(cleanupCredential, fixture.Owner.TenantId);
        try
        {
            var currentProjection = await fixture.OwnerContexts.ReadCleanupAsync(
                cleanupActor, fixture.Owner.ProjectId, fixture.Owner.RunId, fixture.Owner.SessionId,
                CancellationToken.None);
            Assert.Equal(
                pendingProjection with { ReceiptLinks = default },
                currentProjection with { ReceiptLinks = default });
            Assert.True(JsonElement.DeepEquals(pendingProjection.ReceiptLinks, currentProjection.ReceiptLinks));
            pendingProjection.ValidateFor(currentProjection);
            var cleanup = await fixture.Manager.CleanupAsync(
                new(1, null, pendingProjection),
                cleanupActor,
                CancellationToken.None);

            Assert.Equal("released", cleanup.LeaseState);
            Assert.Equal("cleanup_complete", cleanup.CleanupDisposition);
            Assert.Equal(1, fixture.Sandbox.ReleaseCalls);
            Assert.Equal(receipt.SandboxResource, fixture.Sandbox.LastReleasedResource);
            Assert.Equal(1, fixture.Sandbox.ProvisionCalls);
            Assert.Equal(1, fixture.Storage.ProvisionCalls);
            Assert.Equal(0, fixture.Storage.ReleaseCalls);
            Assert.Equal("retained-workspace-data", fixture.Storage.Contents);

            var cleanupAttempt = new RuntimeRunStartCleanupAttempt(
                receipt.SandboxOperationId,
                receipt.SandboxResource.Generation,
                receipt.ProviderFencingGeneration,
                receipt.SandboxIdempotencyKey).Validate();
            var replayProjection = pendingProjection with
            {
                ReceiptLinks = JsonSerializer.SerializeToElement(
                    new { placement = receipt, cleanupAttempt }, JsonOptions)
            };
            fixture.OwnerHandler.CleanupProjection = replayProjection;
            var replay = await fixture.Manager.CleanupAsync(
                new(1, null, replayProjection),
                cleanupActor,
                CancellationToken.None);
            Assert.Equal(cleanup, replay);
            Assert.Equal(1, fixture.Sandbox.ReleaseCalls);
            Assert.Equal(0, fixture.Storage.ReleaseCalls);
            Assert.Equal("retained-workspace-data", fixture.Storage.Contents);
        }
        finally
        {
            cleanupCredential.Invalidate();
        }
    }

    [Fact]
    public async Task ReplacementRetryReusesTheAttachedWorkspaceAndReleaseKey()
    {
        using var fixture = CreateFixture(database, 1);
        var firstBootstrap = Guid.NewGuid();
        var replacementOperation = Guid.NewGuid();
        var intentId = Guid.NewGuid();
        var credential = new SecretCredential(
            "first-start-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        var actor = new RuntimeActorAuthorization(credential, fixture.Owner.TenantId);

        try
        {
            var previous = await fixture.Manager.ProvisionAsync(
                new(1, fixture.Owner, null, firstBootstrap), actor, CancellationToken.None);
            var retirement = new RuntimeRunStartRetirementRequest(
                1, fixture.Owner, firstBootstrap, Guid.NewGuid(), 1, previous).Validate();
            var released = await fixture.Manager.RetireReplacementAsync(
                new RuntimeReplacementRetirementRequest(1, replacementOperation, intentId, retirement),
                actor,
                CancellationToken.None);
            Assert.Equal("released", released.State);

            var replacementRequest = new RuntimeReplacementPlacementRequest(
                1,
                replacementOperation,
                fixture.Owner,
                firstBootstrap,
                RuntimeRunStartKeys.ReplacementOperationId(replacementOperation, "bootstrap"),
                previous);
            var firstReplacement = await fixture.Manager.ProvisionReplacementAsync(
                replacementRequest, actor, CancellationToken.None);
            var retriedReplacement = await fixture.Manager.ProvisionReplacementAsync(
                replacementRequest, actor, CancellationToken.None);

            Assert.Equal(firstReplacement.SandboxOperationId, retriedReplacement.SandboxOperationId);
            Assert.Equal(firstReplacement.SandboxResource, retriedReplacement.SandboxResource);
            Assert.Equal(firstReplacement.WorkspaceVolumeId, retriedReplacement.WorkspaceVolumeId);
            Assert.Equal(1, fixture.Storage.ProvisionCalls);
            Assert.Equal(2, fixture.Sandbox.ProvisionCalls);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    [Fact]
    public async Task FailedBootstrapRetirementUsesRunReadAuthorityAndExactPlacement()
    {
        using var fixture = CreateFixture(database, 1);
        var bootstrapOperationId = Guid.NewGuid();
        var provisionCredential = new SecretCredential(
            "first-start-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        RuntimeRunStartPlacementReceipt placement;
        try
        {
            placement = await fixture.Manager.ProvisionAsync(
                new(1, fixture.Owner, null, bootstrapOperationId),
                new RuntimeActorAuthorization(provisionCredential, fixture.Owner.TenantId),
                CancellationToken.None);
        }
        finally
        {
            provisionCredential.Invalidate();
        }

        fixture.Projects.Authorization = Authorization(
            fixture.Owner, bound: true, ProjectAuthorizationPermission.ReadRunSelection);
        var request = new RuntimeRunStartRetirementRequest(
            1, fixture.Owner, bootstrapOperationId, Guid.NewGuid(), 1, placement).Validate();
        var credential = new SecretCredential(
            "run-bound-retirement-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        try
        {
            var receipt = await fixture.Manager.RetireFailedBootstrapAsync(
                request,
                new RuntimeActorAuthorization(credential, fixture.Owner.TenantId),
                CancellationToken.None);

            Assert.Equal("released", receipt.State);
            var permissions = Assert.Single(fixture.Projects.Authorization.EffectiveAuthority).Permissions
                .Select(grant => grant.Permission);
            Assert.Contains(ProjectAuthorizationPermission.ReadRunSelection, permissions);
            Assert.DoesNotContain(ProjectAuthorizationPermission.WriteProjects, permissions);
            Assert.Equal(1, fixture.Sandbox.ReleaseCalls);
            Assert.Equal(placement.SandboxResource, fixture.Sandbox.LastReleasedResource);
            Assert.Equal(1, fixture.Sandbox.ProvisionCalls);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    [Fact]
    public async Task FailedBootstrapRetirementRejectsDeniedOrMismatchedProofBeforeRelease()
    {
        using var fixture = CreateFixture(database, 1);
        var bootstrapOperationId = Guid.NewGuid();
        var provisionCredential = new SecretCredential(
            "first-start-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        RuntimeRunStartPlacementReceipt placement;
        try
        {
            placement = await fixture.Manager.ProvisionAsync(
                new(1, fixture.Owner, null, bootstrapOperationId),
                new RuntimeActorAuthorization(provisionCredential, fixture.Owner.TenantId),
                CancellationToken.None);
        }
        finally
        {
            provisionCredential.Invalidate();
        }

        var request = new RuntimeRunStartRetirementRequest(
            1, fixture.Owner, bootstrapOperationId, Guid.NewGuid(), 1, placement).Validate();
        var credential = new SecretCredential(
            "run-bound-retirement-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        try
        {
            var mismatched = request with
            {
                Placement = request.Placement with
                {
                    SandboxResource = request.Placement.SandboxResource with { ResourceId = "other-resource" }
                }
            };
            var mismatch = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Manager.RetireFailedBootstrapAsync(
                    mismatched, new RuntimeActorAuthorization(credential, fixture.Owner.TenantId),
                    CancellationToken.None));
            Assert.Equal("runtime_start_retirement_placement_stale", mismatch.Code);
            Assert.Equal(0, fixture.Sandbox.ReleaseCalls);

            fixture.Projects.Authorization = Authorization(
                fixture.Owner, bound: true, ProjectAuthorizationPermission.ReadProjects);
            var denied = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
                fixture.Manager.RetireFailedBootstrapAsync(
                    request, new RuntimeActorAuthorization(credential, fixture.Owner.TenantId),
                    CancellationToken.None));
            Assert.Equal("run_selection_not_authorized", denied.Code);
            Assert.Equal(0, fixture.Sandbox.ReleaseCalls);

            fixture.Projects.Authorization = Authorization(
                fixture.Owner, bound: true, ProjectAuthorizationPermission.ReadRunSelection);
            var receipt = await fixture.Manager.RetireFailedBootstrapAsync(
                request, new RuntimeActorAuthorization(credential, fixture.Owner.TenantId),
                CancellationToken.None);
            Assert.Equal("released", receipt.State);
            Assert.Equal(1, fixture.Sandbox.ReleaseCalls);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstStartAuthorityLossAfterProviderReturnRecordsTheExactResourceWithoutReadiness(bool child)
    {
        using var fixture = CreateFixture(database, 1, child);
        fixture.Sandbox.AfterProvision = () => fixture.OwnerHandler.DenyOwnerContext = true;
        var environmentId = EnvironmentLifecycleManager.CreateEnvironmentId(
            fixture.Owner.TenantId, fixture.Owner.ProjectId, fixture.Owner.RunId,
            RuntimeRunStartKeys.IdempotencyKey(fixture.Owner));
        var fence = new EnvironmentGenerationFence(
            new(fixture.Owner.TenantId, fixture.Owner.ProjectId, fixture.Owner.RunId, environmentId), 1);
        SandboxLeaseState? stateAtDenial = null;
        fixture.OwnerHandler.BeforeDenial = async () =>
        {
            var current = await fixture.LeaseStore.GetAsync(
                fence, fixture.Sandbox.ProvisionedResource!.Resource.Generation, CancellationToken.None);
            stateAtDenial = current?.State;
        };
        var credential = new SecretCredential(
            "first-start-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        try
        {
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => fixture.Manager.ProvisionAsync(
                new(child ? 2 : 1, fixture.Owner, null, Guid.NewGuid()),
                new RuntimeActorAuthorization(credential, fixture.Owner.TenantId),
                CancellationToken.None));
        }
        finally
        {
            credential.Invalidate();
        }

        var resource = Assert.IsType<SandboxProvisionedResource>(fixture.Sandbox.ProvisionedResource);
        var lease = Assert.IsType<SandboxLeaseSnapshot>(
            await fixture.LeaseStore.GetAsync(fence, resource.Resource.Generation, CancellationToken.None));
        Assert.NotNull(lease.ProvisionedResource);
        Assert.Equal(resource.Resource, lease.ProvisionedResource.Resource);
        Assert.Equal(resource.Endpoint, lease.ProvisionedResource.Endpoint);
        Assert.Equal(resource.Placement, lease.ProvisionedResource.Placement);
        var attempt = Assert.Single(await fixture.LeaseStore.GetProviderAttemptsAsync(
            fence, lease.OperationId, CancellationToken.None));
        Assert.Equal(SandboxLeaseState.Provisioning, stateAtDenial);
        Assert.Equal(SandboxProviderAttemptState.Completed, attempt.State);
        Assert.Null(attempt.FailureCode);
        Assert.Equal(1, fixture.Sandbox.ProvisionCalls);
        Assert.Equal(0, fixture.Sandbox.ReleaseCalls);
    }

    [Fact]
    public async Task RejectsUnavailableCapacityBeforeNetworkOrWorkspaceProvisioning()
    {
        using var fixture = CreateFixture(database, 0);
        var credential = new SecretCredential(
            "first-start-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        var actor = new RuntimeActorAuthorization(credential, fixture.Owner.TenantId);

        RuntimeAuthorizationException failure;
        try
        {
            failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Manager.ProvisionAsync(
                    new(1, fixture.Owner, null, Guid.NewGuid()), actor, CancellationToken.None));
        }
        finally
        {
            credential.Invalidate();
        }

        Assert.Equal("runtime_start_workspace_capacity_unavailable", failure.Code);
        Assert.Empty(fixture.Network.Resources);
        Assert.Equal(0, fixture.Storage.ProvisionCalls);
        Assert.Equal(0, fixture.Sandbox.ProvisionCalls);
        var environmentId = EnvironmentLifecycleManager.CreateEnvironmentId(
            fixture.Owner.TenantId,
            fixture.Owner.ProjectId,
            fixture.Owner.RunId,
            RuntimeRunStartKeys.IdempotencyKey(fixture.Owner));
        var fence = new EnvironmentGenerationFence(
            new(fixture.Owner.TenantId, fixture.Owner.ProjectId, fixture.Owner.RunId, environmentId),
            1);
        Assert.Null(await fixture.Store.GetWorkspaceVolumeAsync(
            fence, RuntimeRunStartKeys.WorkspaceVolumeId(fixture.Owner), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupReconcilesTheExactUncertainProvisionAndNeverReprovisions(bool child)
    {
        using var fixture = CreateFixture(database, 1, child);
        fixture.Sandbox.LoseNextProvisionResponse = true;
        var bootstrapOperationId = Guid.NewGuid();
        var provisionCredential = new SecretCredential(
            "first-start-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        try
        {
            await Assert.ThrowsAsync<SandboxProviderException>(() => fixture.Manager.ProvisionAsync(
                new(child ? 2 : 1, fixture.Owner, null, bootstrapOperationId),
                new RuntimeActorAuthorization(provisionCredential, fixture.Owner.TenantId),
                CancellationToken.None));
        }
        finally
        {
            provisionCredential.Invalidate();
        }

        var projection = new RuntimeRunStartCleanupProjection(
            child ? 2 : 1,
            fixture.Owner.TenantId,
            fixture.Owner.ProjectId,
            fixture.Owner.RunId,
            fixture.Owner.SessionId,
            fixture.Owner.AcceptedSelectionHash,
            "cancelled",
            child ? 0 : fixture.Owner.ExecutionFence + 1,
            Guid.NewGuid(),
            1,
            fixture.Owner.ExecutionFence,
            bootstrapOperationId,
            RuntimeRunStartKeys.EnvironmentId(fixture.Owner),
            "cleanup_pending",
            JsonSerializer.SerializeToElement(new { }, JsonOptions))
        {
            TerminalFence = child ? fixture.Owner.ExecutionFence + 1 : null,
            ParentRootSessionId = child ? "root" : null
        }.Validate();
        fixture.OwnerHandler.CleanupProjection = projection;
        fixture.Projects.Authorization = Authorization(
            fixture.Owner, bound: false, ProjectAuthorizationPermission.CancelRun);

        var cleanupCredential = new SecretCredential(
            "cancel-replay-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        try
        {
            var actor = new RuntimeActorAuthorization(cleanupCredential, fixture.Owner.TenantId);
            var cleanup = await fixture.Manager.CleanupAsync(
                new(child ? 2 : 1, null, projection), actor, CancellationToken.None);
            Assert.Equal("released", cleanup.LeaseState);
            Assert.Equal("cleanup_complete", cleanup.CleanupDisposition);
            Assert.Equal(1, fixture.Sandbox.ListOwnedCalls);
            Assert.Equal(1, fixture.Sandbox.ReleaseCalls);
            Assert.Equal(1, fixture.Sandbox.ProvisionCalls);
            Assert.Equal(0, fixture.Sandbox.ReleasePartialCalls);
            if (child)
                Assert.Equal(fixture.Sandbox.ProvisionedResource!.Resource, cleanup.SandboxResource);

            var replay = await fixture.Manager.CleanupAsync(
                new(child ? 2 : 1, null, projection), actor, CancellationToken.None);
            Assert.Equal(cleanup, replay);
            Assert.Equal(1, fixture.Sandbox.ReleaseCalls);
            Assert.Equal(1, fixture.Sandbox.ProvisionCalls);
        }
        finally
        {
            cleanupCredential.Invalidate();
        }
    }

    [Fact]
    public async Task CleanupStaysPendingUntilHeldOrUnknownLateReleaseHasExactReceipt()
    {
        using var fixture = CreateFixture(database, 1);
        fixture.Sandbox.LoseNextProvisionResponse = true;
        var bootstrapOperationId = Guid.NewGuid();
        var provisionCredential = new SecretCredential(
            "first-start-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        try
        {
            await Assert.ThrowsAsync<SandboxProviderException>(() => fixture.Manager.ProvisionAsync(
                new(1, fixture.Owner, null, bootstrapOperationId),
                new RuntimeActorAuthorization(provisionCredential, fixture.Owner.TenantId),
                CancellationToken.None));
        }
        finally
        {
            provisionCredential.Invalidate();
        }

        var environmentId = RuntimeRunStartKeys.EnvironmentId(fixture.Owner);
        var provisionKey = RuntimeRunStartKeys.SandboxIdempotencyKey(fixture.Owner, bootstrapOperationId);
        var fence = new EnvironmentGenerationFence(
            new(fixture.Owner.TenantId, fixture.Owner.ProjectId, fixture.Owner.RunId, environmentId), 1);
        var lease = Assert.IsType<SandboxLeaseSnapshot>(
            await fixture.LeaseStore.GetByProvisionIdempotencyKeyAsync(
                fence, provisionKey, CancellationToken.None));
        var intentId = Guid.NewGuid();
        _ = await fixture.LeaseStore.BeginRetirementAsync(
            fence,
            lease.ResourceGeneration,
            lease.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon,
            $"coordinator-release-{intentId:N}",
            new SandboxRetirementAuthorization(fixture.Owner.ActorIssuer, fixture.Owner.ActorId, 1),
            terminalEvidence: null,
            CancellationToken.None);
        var provisionAttempt = Assert.Single(await fixture.LeaseStore.GetProviderAttemptsAsync(
            fence, lease.OperationId, CancellationToken.None));
        var lateLease = await SandboxLeaseProviderAttemptTestSupport.CompleteProvisionAsync(
            fixture.LeaseStore,
            lease.OperationId,
            fence,
            fixture.Sandbox.ProvisionedResource!,
            effectMayHaveApplied: true,
            CancellationToken.None,
            providerAttemptId: provisionAttempt.AttemptId);
        Assert.Equal(SandboxLeaseState.Released, lateLease.State);
        var claim = Assert.IsType<SandboxLateResourceCleanupClaim>(
            await fixture.LeaseStore.ClaimNextLateResourceCleanupAsync(
                fence, CancellationToken.None, lease.OperationId, lease.ResourceGeneration));

        var projection = new RuntimeRunStartCleanupProjection(
            1,
            fixture.Owner.TenantId,
            fixture.Owner.ProjectId,
            fixture.Owner.RunId,
            fixture.Owner.SessionId,
            fixture.Owner.AcceptedSelectionHash,
            "cancelled",
            fixture.Owner.ExecutionFence + 1,
            intentId,
            1,
            fixture.Owner.ExecutionFence,
            bootstrapOperationId,
            environmentId,
            "cleanup_pending",
            JsonSerializer.SerializeToElement(new { }, JsonOptions)).Validate();
        fixture.OwnerHandler.CleanupProjection = projection;
        fixture.Projects.Authorization = Authorization(
            fixture.Owner, bound: false, ProjectAuthorizationPermission.CancelRun);
        var cleanupCredential = new SecretCredential(
            "cancel-replay-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        try
        {
            var actor = new RuntimeActorAuthorization(cleanupCredential, fixture.Owner.TenantId);
            var held = await fixture.Manager.CleanupAsync(new(1, null, projection), actor, CancellationToken.None);
            Assert.Equal("releasing", held.LeaseState);
            Assert.Equal("cleanup_pending", held.CleanupDisposition);

            var releaseAttempt = await fixture.LeaseStore.BeginProviderAttemptAsync(
                SandboxProviderAttemptRequest.ForLateResourceRelease(claim), fence, CancellationToken.None);
            _ = await fixture.LeaseStore.RecordProviderAttemptOutcomeAsync(
                releaseAttempt.Attempt.AttemptId,
                fence,
                JsonSerializer.SerializeToElement(
                    new { errorCode = "release_timeout", effectMayHaveApplied = true }, JsonOptions),
                effectMayHaveApplied: true,
                failureCode: "release_timeout",
                CancellationToken.None);
            fixture.TimeProvider.Advance(TimeSpan.FromMinutes(3));

            var unknown = await fixture.Manager.CleanupAsync(new(1, null, projection), actor, CancellationToken.None);
            Assert.Equal("releasing", unknown.LeaseState);
            Assert.Equal("cleanup_pending", unknown.CleanupDisposition);

            await SandboxLeaseProviderAttemptTestSupport.CompleteLateResourceCleanupAsync(
                fixture.LeaseStore,
                fence,
                claim,
                new SandboxReleaseReceipt(
                    claim.ProvisionedResource.Resource,
                    claim.IdempotencyKey,
                    SandboxReleaseDisposition.Released),
                CancellationToken.None);
            var completed = await fixture.Manager.CleanupAsync(
                new(1, null, projection), actor, CancellationToken.None);
            Assert.Equal("released", completed.LeaseState);
            Assert.Equal("cleanup_complete", completed.CleanupDisposition);
            Assert.Equal(1, fixture.Sandbox.ProvisionCalls);
            Assert.Equal(0, fixture.Sandbox.ReleaseCalls);
        }
        finally
        {
            cleanupCredential.Invalidate();
        }
    }

    [Fact]
    public async Task PlacementEndpointCallsTheFirstStartManagerAndReturnsItsValidatedReceipt()
    {
        using var fixture = CreateFixture(database, 1);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, RuntimeRunStartTestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(fixture.Manager);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddScoped<EnvironmentRuntimeBootstrapDelivery>(_ =>
            throw new NotSupportedException("The placement endpoint does not use bootstrap delivery."));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEnvironmentRuntimeBootstrap();
        await app.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/runtime/run-start/placement")
        {
            Content = JsonContent.Create(
                new RuntimeRunStartPlacementRequest(1, fixture.Owner, null, Guid.NewGuid()), options: JsonOptions)
        };
        request.Headers.Authorization = new("Bearer", "first-start-token");
        request.Headers.Add("X-Agentweaver-Tenant", fixture.Owner.TenantId);
        using var response = await app.GetTestClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);

        var receipt = await response.Content.ReadFromJsonAsync<RuntimeRunStartPlacementReceipt>(JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(RuntimeRunStartKeys.EnvironmentId(fixture.Owner), receipt.EnvironmentId);
        Assert.Equal("runtime-profile", receipt.SourceProfileId);
        Assert.Equal(RuntimeRunStartKeys.WorkspaceVolumeId(fixture.Owner), receipt.WorkspaceVolumeId);
        Assert.Equal("/workspace", fixture.Sandbox.LastRequest!.Workspace.Negotiation.MountPath);
        Assert.Equal(1, fixture.Sandbox.ProvisionCalls);
        Assert.Equal(1, fixture.Storage.ProvisionCalls);
    }

    [Theory]
    [InlineData("foreign-actor-token", false)]
    [InlineData("first-start-token", true)]
    public async Task RejectsForeignActorOrTenantBeforePlacement(
        string bearer,
        bool foreignTenant)
    {
        using var fixture = CreateFixture(database, 1);
        var tenant = foreignTenant ? "foreign-tenant" : fixture.Owner.TenantId;
        var credential = new SecretCredential(
            bearer, DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        try
        {
            if (foreignTenant)
            {
                var failure = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
                    fixture.Manager.ProvisionAsync(
                        new(1, fixture.Owner, null, Guid.NewGuid()),
                        new RuntimeActorAuthorization(credential, tenant),
                        CancellationToken.None));
                Assert.False(string.IsNullOrWhiteSpace(failure.Code));
            }
            else
            {
                var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                    fixture.Manager.ProvisionAsync(
                        new(1, fixture.Owner, null, Guid.NewGuid()),
                        new RuntimeActorAuthorization(credential, tenant),
                        CancellationToken.None));
                Assert.Equal("runtime_owner_denied", failure.Code);
            }
            Assert.Empty(fixture.Network.Resources);
            Assert.Equal(0, fixture.Storage.ProvisionCalls);
            Assert.Equal(0, fixture.Sandbox.ProvisionCalls);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions RecoveryJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static Fixture CreateFixture(EnvironmentPostgresFixture database, long maximumCapacityGiB, bool child = false)
    {
        var id = Guid.NewGuid().ToString("N");
        var selectionBase = EnvironmentEgressManagerTests.Selection();
        var storageSelectionOptions = new AzureFilesCsiOptions(
            1, "storage-options-1", "agentweaver", "azure-files", 1, 60, 100);
        var sandboxOptions = new AgentSandboxOptions(
            AgentSandboxOptions.CurrentOptionsSchemaVersion,
            "sandbox-options-1",
            "agentweaver",
            AzureFilesCsiProviderMetadata.ProviderId,
            "ghcr.io/agentweaver/agenthost@sha256:" + new string('a', 64),
            "linux/amd64",
            1,
            "kata-vm",
            "kata-qemu",
            "500m",
            "512Mi",
            1,
            1,
            new(30, 90, 30, 30, 30, 180));
        var selection = selectionBase with
        {
            ProjectId = "project-" + id,
            RunId = "run-" + id,
            Providers = selectionBase.Providers
                .Add(Exclusive(AgentSandboxProviderMetadata.CreateRegistration(sandboxOptions)))
                .Add(Exclusive(AzureFilesCsiProviderMetadata.CreateRegistration(storageSelectionOptions)))
        };
        var timeProvider = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var owner = new RuntimeOwnerContext(
            1,
            "https://identity.example",
            "actor-" + id,
            "tenant-" + id,
            selection.ProjectId,
            selection.RunId,
            "root-session-" + id,
            RuntimeActionContract.CoordinatorRootStepId,
            "platform-model",
            "first-turn",
            selection.ProjectRevision,
            selection.ProjectConfigurationRevision,
            selection.PlatformRuntimeRevision,
            selection.ContextRevision,
            new string('a', 64),
            1,
            0,
            1,
            1);
        if (child)
            owner = owner with
            {
                SessionId = "child-session-" + id,
                AgentId = "implementation-agent",
                LogicalTurnOrdinal = 1,
                WorkflowStepId = "implementation"
            };
        var projects = new ProjectsClient(
            Authorization(owner, true,
                ProjectAuthorizationPermission.ReadRunSelection,
                ProjectAuthorizationPermission.WriteProjects),
            selection);
        var store = database.CreateStore();
        var networkOptions = EnvironmentEgressManagerTests.Options();
        var network = new EnvironmentEgressManagerTests.FakeCiliumPolicyResourceStore();
        var egress = new EnvironmentEgressManager(
            projects,
            new CiliumEgressPolicyAdapter(network, networkOptions),
            networkOptions,
            store);
        var ownerHandler = new OwnerContextHandler(owner);
        var ownerHttp = new HttpClient(ownerHandler);
        var ownerContexts = new EnvironmentRuntimeOwnerContextClient(
            ownerHttp,
            new(new("https://orchestrator.test/"), new("https://broker.test/")));
        var lifecycle = new EnvironmentLifecycleManager(
            egress,
            new EnvironmentLifecycleProducer(store),
            runtimeOwnerContext: ownerContexts);
        var storage = new RecordingStorageProvider(storageSelectionOptions);
        var workspaceVolumes = new WorkspaceVolumeService(store, storage);
        var sandbox = new RecordingSandboxProvider(sandboxOptions);
        var kubernetesHttp = new HttpClient(new NoKubernetesRequestsHandler())
        {
            BaseAddress = new Uri("https://kubernetes.test/")
        };
        var kubernetes = new KubernetesAgentSandboxClient(kubernetesHttp, KubernetesTestCaCertificate.Primary);
        var runtimeHealthHttp = new HttpClient(new RuntimeHealthHandler());
        var sandboxLeases = new EnvironmentSandboxLeaseStore(database.DataSource, timeProvider);
        var sandboxes = new EnvironmentSandboxManager(
            projects, store, sandboxLeases,
            sandbox, sandboxOptions, kubernetes, networkOptions, egress, timeProvider: timeProvider);
        var profiles = new EnvironmentRuntimeBootstrapProfileRegistry(
            [], "runtime-profile", new("https://orchestrator.test/"));
        var configuredStorageOptions = storageSelectionOptions with { MaximumCapacityGiB = maximumCapacityGiB };
        return new(
            owner,
            projects,
            store,
            network,
            storage,
            sandbox,
            ownerHandler,
            ownerHttp,
            ownerContexts,
            kubernetesHttp,
            runtimeHealthHttp,
            profiles,
            sandboxLeases,
            timeProvider,
            new(lifecycle, store, egress, workspaceVolumes, sandboxes, ownerContexts, profiles,
                configuredStorageOptions, new(runtimeHealthHttp), timeProvider));
    }

    private static RuntimeRunStartCleanupProjection Projection(
        RuntimeOwnerContext owner,
        RuntimeRunStartPlacementReceipt receipt,
        Guid intentId,
        string disposition,
        JsonElement receiptLinks) =>
        new(
            1,
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            owner.SessionId,
            owner.AcceptedSelectionHash,
            "cancelled",
            owner.ExecutionFence + 1,
            intentId,
            1,
            owner.ExecutionFence,
            Guid.Parse(receipt.SandboxIdempotencyKey[(receipt.SandboxIdempotencyKey.LastIndexOf(':') + 1)..]),
            receipt.EnvironmentId,
            disposition,
            receiptLinks);

    private static ProjectAuthorizationContextResponse Authorization(
        RuntimeOwnerContext owner,
        bool bound,
        params ProjectAuthorizationPermission[] permissions) =>
        new(
            ProjectAuthorizationContextContract.CurrentVersion,
            owner.ActorIssuer,
            owner.ActorId,
            owner.TenantId,
            1,
            bound ? owner.ProjectId : null,
            bound ? owner.RunId : null,
            [
                new(
                    ProjectAuthorityResourceType.Project,
                    owner.ProjectId,
                    permissions.Select(permission => new ProjectAuthorizationPermissionGrant(permission, 1))
                        .ToImmutableArray())
            ]);

    private static EffectiveProviderSelection Exclusive(ProviderRegistration registration)
    {
        var descriptor = registration.Descriptor;
        return new(
            ProviderCardinality.Exclusive,
            descriptor.Seam,
            [
                new(
                    descriptor.Seam,
                    descriptor.Id,
                    descriptor.AdapterVersion.ToString(),
                    registration.OptionsSchemaVersion,
                    registration.OptionsRevision,
                    descriptor.Hosting,
                    descriptor.AdvertisedCapabilities.ToImmutableArray(),
                    [])
            ]);
    }

    private sealed class Fixture(
        RuntimeOwnerContext owner,
        ProjectsClient projects,
        IEnvironmentLifecycleStore store,
        EnvironmentEgressManagerTests.FakeCiliumPolicyResourceStore network,
        RecordingStorageProvider storage,
        RecordingSandboxProvider sandbox,
        OwnerContextHandler ownerHandler,
        HttpClient ownerHttp,
        EnvironmentRuntimeOwnerContextClient ownerContexts,
        HttpClient kubernetesHttp,
        HttpClient runtimeHealthHttp,
        EnvironmentRuntimeBootstrapProfileRegistry profiles,
        EnvironmentSandboxLeaseStore leaseStore,
        AdjustableTimeProvider timeProvider,
        EnvironmentRuntimeRunStartManager manager) : IDisposable
    {
        public RuntimeOwnerContext Owner { get; } = owner;
        public ProjectsClient Projects { get; } = projects;
        public IEnvironmentLifecycleStore Store { get; } = store;
        public EnvironmentEgressManagerTests.FakeCiliumPolicyResourceStore Network { get; } = network;
        public RecordingStorageProvider Storage { get; } = storage;
        public RecordingSandboxProvider Sandbox { get; } = sandbox;
        public OwnerContextHandler OwnerHandler { get; } = ownerHandler;
        public EnvironmentRuntimeOwnerContextClient OwnerContexts { get; } = ownerContexts;
        public EnvironmentRuntimeBootstrapProfileRegistry Profiles { get; } = profiles;
        public EnvironmentSandboxLeaseStore LeaseStore { get; } = leaseStore;
        public AdjustableTimeProvider TimeProvider { get; } = timeProvider;
        public EnvironmentRuntimeRunStartManager Manager { get; } = manager;

        public void Dispose()
        {
            ownerHttp.Dispose();
            kubernetesHttp.Dispose();
            runtimeHealthHttp.Dispose();
        }
    }

    private sealed class ProjectsClient(
        ProjectAuthorizationContextResponse authorization,
        EffectiveNetworkPolicySelection selection) : IProjectsConfigClient
    {
        public ProjectAuthorizationContextResponse Authorization { get; set; } = authorization;

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller,
            CancellationToken cancellationToken) =>
            Task.FromResult(Authorization);

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(selection);
    }

    private sealed class OwnerContextHandler(RuntimeOwnerContext owner) : HttpMessageHandler
    {
        public RuntimeRunStartCleanupProjection? CleanupProjection { get; set; }
        public bool DenyOwnerContext { get; set; }
        public Func<Task>? BeforeDenial { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bearer = request.Headers.Authorization?.Parameter;
            var tenant = request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenants)
                ? tenants.SingleOrDefault()
                : null;
            if (DenyOwnerContext ||
                bearer is not ("first-start-token" or "cancel-replay-token" or "run-bound-retirement-token") ||
                tenant != owner.TenantId)
            {
                if (BeforeDenial is not null)
                    await BeforeDenial();
                var denied = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    RequestMessage = request,
                    Content = JsonContent.Create(new { code = "runtime_owner_denied" }, options: JsonOptions)
                };
                denied.Headers.CacheControl = new() { NoStore = true, NoCache = true };
                return denied;
            }
            if (request.RequestUri?.AbsolutePath.EndsWith(
                    "/runtime-owner-context", StringComparison.Ordinal) == true &&
                CleanupProjection is null)
                return Response(request, owner);
            if (CleanupProjection is not null)
                return Response(request, CleanupProjection);
            var missing = new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
            missing.Headers.CacheControl = new() { NoStore = true, NoCache = true };
            return missing;
        }

        private static HttpResponseMessage Response(HttpRequestMessage request, object value)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = JsonContent.Create(value, options: JsonOptions)
            };
            response.Headers.CacheControl = new() { NoStore = true, NoCache = true };
            return response;
        }
    }

    private sealed class RecordingStorageProvider(AzureFilesCsiOptions options) : IWorkspaceVolumeProvider
    {
        public int ProvisionCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public WorkspaceVolumeProvisionRequest? LastRequest { get; private set; }
        public string Contents { get; set; } = "";

        public Task<WorkspaceVolumeResource> ProvisionAsync(
            WorkspaceVolumeProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            ProvisionCalls++;
            LastRequest = request;
            Contents = "empty";
            var resource = new ProviderResourceRef(
                ProviderSeam.Storage,
                AzureFilesCsiProviderMetadata.ProviderId,
                "workspace-claim-" + Guid.NewGuid().ToString("N"),
                request.ResourceGeneration);
            var binding = new WorkspaceVolumeProviderBindingSnapshot(
                resource.ProviderId,
                "1.0.0",
                options.OptionsSchemaVersion,
                options.OptionsRevision,
                JsonSerializer.SerializeToElement(options, JsonOptions),
                JsonSerializer.SerializeToElement(new
                {
                    @namespace = options.Namespace,
                    claimName = resource.ResourceId,
                    claimUid = "claim-uid",
                    clusterIdentity = "6b333f2199e82077ee02ceffd63b1a2e6ff8becf905f3aa56167a853c36aea75"
                }, JsonOptions));
            return Task.FromResult(new WorkspaceVolumeResource(
                resource,
                ImmutableHashSet.Create(
                    StringComparer.Ordinal,
                    WorkspaceVolumeCapabilities.ReadWriteMany,
                    WorkspaceVolumeCapabilities.ReadOnlyMount),
                binding));
        }

        public Task<WorkspaceVolumeReleaseReceipt> ReleaseAsync(
            WorkspaceVolumeReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            Contents = "";
            return Task.FromResult(new WorkspaceVolumeReleaseReceipt(
                request.Resource, request.IdempotencyKey, WorkspaceVolumeReleaseDisposition.Retained));
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan amount) => _utcNow += amount;
    }

    private sealed class RecordingSandboxProvider(AgentSandboxOptions options) : ISandboxProvider
    {
        private SandboxProvisionedResource? _resource;

        public SandboxProvisionedResource? ProvisionedResource => _resource;
        public int ProvisionCalls { get; private set; }
        public int ListOwnedCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public int ReleasePartialCalls { get; private set; }
        public bool LoseNextProvisionResponse { get; set; }
        public Action? AfterProvision { get; set; }
        public ProviderResourceRef? LastReleasedResource { get; private set; }
        public SandboxProvisionRequest? LastRequest { get; private set; }

        public Task<SandboxProvisionedResource> ProvisionAsync(
            SandboxProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            ProvisionCalls++;
            LastRequest = request;
            _resource = new(
                request.Workspace.Negotiation.SandboxResource,
                new(Guid.NewGuid()),
                new("runtime-start-placement"),
                request.Candidate.AdvertisedCapabilities.ToImmutableHashSet(StringComparer.Ordinal),
                [],
                new(
                    request.Candidate.ProviderId,
                    request.Candidate.AdapterVersion.ToString(),
                    request.Candidate.OptionsSchemaVersion,
                    request.Candidate.OptionsRevision,
                    JsonSerializer.SerializeToElement(options, JsonOptions),
                    JsonSerializer.SerializeToElement(new { }, JsonOptions)));
            if (LoseNextProvisionResponse)
            {
                LoseNextProvisionResponse = false;
                throw new SandboxProviderException(
                    "provider_response_lost",
                    "The provision result was not received.",
                    effectMayHaveApplied: true);
            }

            AfterProvision?.Invoke();
            return Task.FromResult(_resource);
        }

        public Task<SandboxObservation> DescribeAsync(
            SandboxDescribeRequest request,
            CancellationToken cancellationToken = default)
        {
            var readyAt = DateTimeOffset.UtcNow;
            return Task.FromResult(new SandboxObservation(
                request.Resource,
                SandboxObservedState.Ready,
                request.FencingGeneration,
                true,
                true,
                1,
                [
                    new(SandboxStartupPhase.Scheduled, 1, readyAt),
                    new(
                        SandboxStartupPhase.ImageReady,
                        1,
                        readyAt,
                        options.ContainerImage[(options.ContainerImage.LastIndexOf('@') + 1)..],
                        options.ContainerImageCompressedPullBytes),
                    new(SandboxStartupPhase.Started, 1, readyAt),
                    new(SandboxStartupPhase.Configured, 1, readyAt),
                    new(SandboxStartupPhase.Ready, 1, readyAt)
                ],
                ProvisionedResource: _resource)
            {
                RuntimeEndpoint = new(
                    new Uri("https://agenthost.namespace.svc.cluster.local:8443/"),
                    "10.0.0.10",
                    "pod-uid",
                    "service-uid")
                {
                    ReceiverConfigureEndpoint = new Uri(
                        "https://agenthost.namespace.svc.cluster.local:8443/runtime/v1/configure")
                }
            });
        }

        public Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(
            SandboxListOwnedRequest request,
            CancellationToken cancellationToken = default)
        {
            ListOwnedCalls++;
            if (_resource is null || LastRequest is null ||
                LastRequest.Fence != request.Fence ||
                LastRequest.OperationId == Guid.Empty ||
                LastRequest.FencingGeneration < request.MinimumFencingGeneration)
                return Task.FromResult<IReadOnlyList<SandboxObservation>>([]);
            var observedAt = DateTimeOffset.UtcNow;
            IReadOnlyList<SandboxObservation> observations =
            [
                new(
                    _resource.Resource,
                    SandboxObservedState.Ready,
                    LastRequest.FencingGeneration,
                    VmIsolationVerified: true,
                    WorkspaceAttachmentVerified: true,
                    VerifiedNetworkGeneration: 1,
                    [
                        new(SandboxStartupPhase.Scheduled, 1, observedAt),
                        new(SandboxStartupPhase.Configured, 1, observedAt),
                        new(SandboxStartupPhase.Ready, 1, observedAt)
                    ],
                    ProvisionOperationId: LastRequest.OperationId,
                    ProvisionedResource: _resource)
            ];
            return Task.FromResult(observations);
        }

        public Task<SandboxReleaseReceipt> ReleaseAsync(
            SandboxReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            LastReleasedResource = request.Resource;
            return Task.FromResult(new SandboxReleaseReceipt(
                request.Resource, request.IdempotencyKey, SandboxReleaseDisposition.Released));
        }

        public Task<SandboxPartialReleaseReceipt> ReleasePartialAsync(
            SandboxPartialReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            ReleasePartialCalls++;
            throw new NotSupportedException();
        }
    }

    private sealed class NoKubernetesRequestsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The startup placement test must not call the Kubernetes API.");
    }

    private sealed class RuntimeHealthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("""{"status":"live"}""", System.Text.Encoding.UTF8, "application/json")
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }
    }

    private sealed class RuntimeRunStartTestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity("test"));
            var properties = new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(2) };
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, properties, Scheme.Name)));
        }
    }
}
