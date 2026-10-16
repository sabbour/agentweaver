using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal static partial class SourceControlEndpoints
{
    internal static async Task<(SandboxBuildTestWorkspaceSource Source, ImmutableArray<byte> Seed)>
        PrepareBuildTestInputAsync(
            HttpContext context,
            CoordinationActor actor,
            SandboxBuildTestCheckpointReference reference,
            Guid operationId,
            SourceControlBuildTestWorkspaceLocator locator,
            SandboxBuildTestWorkspaceSource? confirmedSource,
            OrchestratorOptions options,
            ProjectsRunSelectionClient projects,
            CoordinatorDecisionOwnerStore decisions,
            SourceControlOwnerStore sourceControlOwner,
            ISourceControlAdapter adapter,
            GitWorkspaceManager workspaceManager,
            CancellationToken cancellationToken)
    {
        ValidateBuildTestWorkspaceLocator(locator);
        var run = await ReadPinnedSourceControlRunAsync(
            context, reference.ProjectId, reference.RunId, reference.SessionId,
            options, projects, decisions, sourceControlOwner, cancellationToken).ConfigureAwait(false);
        if (run.Actor != actor || run.DecisionStateVersion != reference.DecisionStateVersion ||
            run.Pin.AcceptedRun.Fence != reference.ExecutionFence ||
            SourceControlOwnerStore.HashSelection(run.Selection.Selection) != reference.AcceptedSelectionHash)
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
        EnsureAdapterMatchesPin(adapter, run.Pin);
        EnsureCheckoutCapability(run.Pin);
        await RequireCurrentSourceControlRunAsync(
            context, run, projects, decisions, cancellationToken).ConfigureAwait(false);
        var request = new GitWorkspaceCaptureRequest(
            run.Pin.ProviderBinding, run.Pin.Repository, locator.WorkspaceId, locator.BaseSha, locator.BranchName);
        var workspace = await workspaceManager.OpenExistingAsync(request, cancellationToken).ConfigureAwait(false);
        if (workspace.WorkspaceIncarnationId != locator.WorkspaceIncarnationId)
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
        await RequireCurrentSourceControlRunAsync(
            context, run, projects, decisions, cancellationToken).ConfigureAwait(false);
        var seed = await workspaceManager.CreateBuildTestSeedAsync(request, workspace, cancellationToken)
            .ConfigureAwait(false);
        await RequireCurrentSourceControlRunAsync(
            context, run, projects, decisions, cancellationToken).ConfigureAwait(false);
        var source = CreateBuildTestWorkspaceSource(
            run, locator, seed, confirmedSource?.SeedReference ?? "buildtest-" + operationId.ToString("N"));
        if (confirmedSource is not null && confirmedSource != source)
            throw new CoordinationException(
                "maf_execution_build_test_intent_conflict", StatusCodes.Status409Conflict);
        await RequireCurrentBuildTestInputAsync(
            context, source, reference, options, projects, decisions, sourceControlOwner, cancellationToken)
            .ConfigureAwait(false);
        return (source, seed.Bytes.ToImmutableArray());
    }

    internal static async Task RequireCurrentBuildTestInputAsync(
        HttpContext context,
        SandboxBuildTestWorkspaceSource source,
        SandboxBuildTestCheckpointReference reference,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        SourceControlOwnerStore sourceControlOwner,
        CancellationToken cancellationToken)
    {
        var run = await ReadPinnedSourceControlRunAsync(
            context, reference.ProjectId, reference.RunId, reference.SessionId,
            options, projects, decisions, sourceControlOwner, cancellationToken).ConfigureAwait(false);
        ValidateBuildTestInputRun(source, reference, run);
        await RequireCurrentSourceControlRunAsync(
            context, run, projects, decisions, cancellationToken).ConfigureAwait(false);
    }

    internal static void ValidateBuildTestInputRun(
        SandboxBuildTestWorkspaceSource source,
        SandboxBuildTestCheckpointReference reference,
        SourceControlRunContext run)
    {
        ValidateBuildTestSourceOwner(
            source, run.Actor, run.Identity, run.Selection.Authorization.TenantId,
            SourceControlOwnerStore.HashSelection(run.Selection.Selection), run.Pin, reference.ExecutionFence);
        if (run.DecisionStateVersion != reference.DecisionStateVersion ||
            source.AcceptedSelectionHash != reference.AcceptedSelectionHash)
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
    }

    internal static SandboxBuildTestWorkspaceSource CreateBuildTestWorkspaceSource(
        SourceControlRunContext run,
        SourceControlBuildTestWorkspaceLocator locator,
        GitWorkspaceBuildTestSeedDocument seed,
        string seedReference)
    {
        ValidateBuildTestWorkspaceLocator(locator);
        var manifest = seed.Baseline.Manifest;
        var source = new SandboxBuildTestWorkspaceSource(
            2, run.Identity, run.Pin.AcceptedRun.TenantId, run.Pin.AcceptedRun.Issuer, run.Pin.AcceptedRun.Subject,
            run.Pin.PinId, run.Pin.AcceptedRun.AcceptedSelectionHash, run.Pin.ProviderBinding.Resource,
            manifest.WorkspaceId, manifest.WorkspaceIncarnationId, manifest.BranchName, manifest.BaseSha,
            manifest.OutputTreeSha, seedReference, seed.Sha256, seed.Bytes.Length,
            GitWorkspaceCapturePackage.Hash(seed.BundleBytes), seed.BundleBytes.Length,
            seed.Baseline.ManifestSha256, seed.Baseline.ManifestBytes.Length);
        _ = GitWorkspaceBuildTestSeedPackage.Parse(seed.Bytes, source);
        ValidateBuildTestSourceOwner(
            source, run.Actor, run.Identity, run.Selection.Authorization.TenantId,
            SourceControlOwnerStore.HashSelection(run.Selection.Selection), run.Pin, run.Pin.AcceptedRun.Fence);
        RequireSameBuildTestWorkspace(source, locator);
        return source;
    }

    internal static void ValidateBuildTestSourceOwner(
        SandboxBuildTestWorkspaceSource source,
        CoordinationActor actor,
        SessionIdentity identity,
        string tenantId,
        string selectionHash,
        SourceControlRepositoryPin pin,
        long executionFence)
    {
        _ = source.Validate();
        var accepted = pin.AcceptedRun;
        if (source.Identity != identity || source.ActorIssuer != actor.Issuer || source.ActorSubject != actor.Subject ||
            source.TenantId != tenantId || source.AcceptedSelectionHash != selectionHash ||
            source.SourceControlPinId != pin.PinId || source.SourceControlResource != pin.ProviderBinding.Resource ||
            accepted.ProjectId != identity.ProjectId || accepted.RunId != identity.RunId ||
            accepted.RootSessionId != identity.SessionId || accepted.Issuer != actor.Issuer ||
            accepted.Subject != actor.Subject || accepted.TenantId != tenantId ||
            accepted.AcceptedSelectionHash != selectionHash || accepted.Fence != executionFence)
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
    }

    internal static void ValidateBuildTestWorkspaceLocator(SourceControlBuildTestWorkspaceLocator locator)
    {
        if (locator is null || locator.WorkspaceIncarnationId == Guid.Empty)
            throw new CoordinationException(
                "maf_execution_dispatch_invalid", StatusCodes.Status400BadRequest);
        ValidateWorkspaceRequest(locator.WorkspaceId, locator.BaseSha, locator.BranchName);
    }

    internal static void RequireSameBuildTestWorkspace(
        SandboxBuildTestWorkspaceSource? source,
        SourceControlBuildTestWorkspaceLocator locator)
    {
        ValidateBuildTestWorkspaceLocator(locator);
        if (source is null || source.WorkspaceId != locator.WorkspaceId ||
            source.WorkspaceIncarnationId != locator.WorkspaceIncarnationId ||
            !string.Equals(source.BaseSha, locator.BaseSha, StringComparison.OrdinalIgnoreCase) ||
            source.BranchName != locator.BranchName)
            throw new CoordinationException(
                "maf_execution_build_test_intent_conflict", StatusCodes.Status409Conflict);
    }
}
