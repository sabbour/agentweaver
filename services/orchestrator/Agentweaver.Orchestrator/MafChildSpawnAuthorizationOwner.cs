using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Agentweaver.Orchestrator;

/// <summary>Authorizes a confirmed child-spawn intent before any child runtime is registered.</summary>
internal sealed class MafChildSpawnAuthorizationOwner(
    MafChildSpawnAuthorizationOwnerStore store,
    ProjectsRunSelectionClient projects,
    ExecutableActionGuard guard,
    ProviderCatalog catalog,
    ProviderResolver resolver,
    AgtPolicyProvider policy,
    IServiceProvider services,
    OrchestratorOptions options)
{
    internal async Task<MafChildSpawnAuthorizationEvidence> AuthorizeAsync(
        HttpContext context,
        MafChildSpawnAuthorizationIntent intent,
        CancellationToken cancellationToken)
    {
        MafChildSpawnAuthorizationContract.ValidateIntent(intent);
        var currentSelection = await ReadCurrentSelectionAsync(context, intent, cancellationToken).ConfigureAwait(false);
        var authority = currentSelection.Authority;
        var policyOptions = GetPolicyOptions();
        var policyBinding = await SourceControlEndpoints.ResolvePolicyBindingAsync(
            currentSelection.Selection.Snapshot,
            authority.RunId,
            catalog,
            resolver,
            policy,
            policyOptions,
            cancellationToken).ConfigureAwait(false);

        var existing = await store.ReadCurrentAsync(intent, authority, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var grant = await store.IssueGrantAsync(intent, authority, cancellationToken).ConfigureAwait(false);
        var receiptId = Guid.NewGuid();
        MafChildSpawnAuthorizationEvidence? evidence = null;
        var guarded = await guard.ExecuteAsync(
            new ExecutableActionInvocation(
                context.User,
                intent.ParentSessionId,
                intent.WorkflowStepId,
                MafChildSpawnAuthorizationContract.ActionId,
                MafChildSpawnAuthorizationContract.Purpose,
                grant,
                intent.ExecutionFence,
                policyBinding,
                receiptId),
            async token =>
            {
                // Re-read remote authority after policy and journal work, then bind the allow in the same owner.
                var current = await ReadCurrentSelectionAsync(context, intent, token).ConfigureAwait(false);
                if (current.Authority != authority)
                    throw new RuntimeAuthorizationException("maf_child_spawn_authority_changed");
                evidence = await store.StoreAllowAsync(
                    intent, current.Authority, receiptId, token).ConfigureAwait(false);
                return evidence;
            },
            cancellationToken).ConfigureAwait(false);

        if (!guarded.EffectInvoked ||
            guarded.Outcome != PolicyEvaluationOutcome.Allow ||
            guarded.ReasonCode != PolicyEvaluationReasonCode.Allowed ||
            guarded.EffectResult is null)
            throw new RuntimeAuthorizationException("maf_child_spawn_not_authorized");
        return evidence
            ?? throw new RuntimeAuthorizationException("maf_child_spawn_authorization_unavailable");
    }

    /// <summary>
    /// Rechecks allow evidence inside the caller's transaction without committing or changing its transaction boundary.
    /// </summary>
    internal async Task<MafChildSpawnAuthorizationConsumeResult> CheckAndConsumeWithinTransactionAsync(
        HttpContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafChildSpawnAuthorizationIntent intent,
        CancellationToken cancellationToken)
    {
        MafChildSpawnAuthorizationContract.ValidateIntent(intent);
        var currentSelection = await ReadCurrentSelectionAsync(context, intent, cancellationToken).ConfigureAwait(false);
        var authority = currentSelection.Authority;
        var policyOptions = GetPolicyOptions();
        _ = await SourceControlEndpoints.ResolvePolicyBindingAsync(
            currentSelection.Selection.Snapshot,
            authority.RunId,
            catalog,
            resolver,
            policy,
            policyOptions,
            cancellationToken).ConfigureAwait(false);
        return await store.CheckAndConsumeWithinTransactionAsync(
            connection, transaction, intent, authority, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CurrentSelection> ReadCurrentSelectionAsync(
        HttpContext context,
        MafChildSpawnAuthorizationIntent intent,
        CancellationToken cancellationToken)
    {
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        CoordinationIdentity.RequireScopes(context.User);
        var scope = CoordinationIdentity.RequireRunScope(context.User);
        var authorized = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, scope.ProjectId, scope.RunId, cancellationToken).ConfigureAwait(false);
        var selection = authorized.Selection;
        var authorization = authorized.Authorization;
        // A single current project role must authorize selection acceptance; ambiguous authority is rejected.
        var roles = authorization.EffectiveAuthority
            .Where(item => item.ResourceType == "project" && item.ResourceId == scope.ProjectId)
            .Where(item => !item.Permissions.IsDefault)
            .SelectMany(item => item.Permissions)
            .Where(item => item.Permission == "acceptRunSelection")
            .Select(item => item.RoleRevision)
            .Distinct()
            .Take(2)
            .ToArray();
        var selectionHash = CoordinationOwnerStore.HashSelection(selection);
        if (selection.ProjectId != scope.ProjectId ||
            selection.RunId != scope.RunId ||
            selection.ContextRevision != intent.ContextRevision ||
            selection.ProjectRevision < 1 ||
            selection.ProjectConfigurationRevision < 1 ||
            selection.PlatformRuntimeRevision < 1 ||
            roles.Length != 1 ||
            authorization.ActorId != actor.Subject ||
            authorization.Issuer != actor.Issuer ||
            authorization.TenantId is null ||
            authorization.MembershipRevision < 1 ||
            authorization.BoundProjectId != scope.ProjectId ||
            authorization.BoundRunId != scope.RunId)
            throw new RuntimeAuthorizationException("maf_child_spawn_selection_stale");

        var policyOptions = GetPolicyOptions();
        return new CurrentSelection(
            selection,
            new MafChildSpawnAuthorizationAuthority(
                scope.ProjectId,
                scope.RunId,
                actor.Issuer,
                actor.Subject,
                authorization.TenantId,
                selectionHash,
                selection.ContextRevision,
                selection.ProjectRevision,
                selection.ProjectConfigurationRevision,
                selection.PlatformRuntimeRevision,
                authorization.MembershipRevision,
                roles[0],
                AgtPolicyProvider.ProviderId,
                AgtPolicyProvider.AdapterVersion.ToString(),
                policyOptions.OptionsSchemaVersion,
                policyOptions.OptionsRevision));
    }

    private AgtPolicyProviderOptions GetPolicyOptions() =>
        services.GetService<AgtPolicyProviderOptions>()
        ?? throw new RuntimeAuthorizationException("maf_child_spawn_policy_unavailable");

    private sealed record CurrentSelection(
        EffectiveRunSelection Selection,
        MafChildSpawnAuthorizationAuthority Authority);
}
