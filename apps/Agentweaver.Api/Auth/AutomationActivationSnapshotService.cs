using System.Security.Claims;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

public enum AutomationActivationOutcome
{
    Activated,
    HumanEntraSubjectRequired,
    ProjectOwnerRequired,
    RepositoryGrantUnavailable,
    RepositoryGrantAmbiguous,
    CopilotBindingUnavailable,
    CopilotBindingAmbiguous,
    Conflict,
}

public enum AutomationDeactivationOutcome
{
    Deactivated,
    HumanEntraSubjectRequired,
    ProjectOwnerRequired,
    NotActive,
}

/// <summary>
/// The redacted status a project Owner sees for their project's automation activation.
/// </summary>
public sealed record AutomationActivationStatusView(
    bool IsActive,
    string? ModelProviderSource,
    DateTimeOffset? ActivatedAt);

/// <summary>
/// Authority boundary for unattended automation activation and deactivation. The project ID (plus
/// the caller's own Owner-verified identity) is the sole input: all GitHub/BYOK model-provider
/// authority is resolved server-side and captured as an immutable, fenceable tuple. Reachable only
/// through the Owner-gated <c>/api/projects/{id}/automation/*</c> endpoints
/// (<c>Endpoints.AutomationActivationEndpoints</c>).
/// </summary>
public sealed class AutomationActivationSnapshotService(
    GitHubConnectionsPersistenceStore persistence,
    IProjectRoleAssignmentStore roleAssignments,
    EffectiveModelProviderResolver modelProviderResolver)
{
    public async Task<(AutomationActivationOutcome Outcome, FencedAutomationActivation? Activation)> ActivateAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        ProjectId projectId,
        CancellationToken ct = default)
    {
        var denied = await CheckOwnerAuthorityAsync(
            caller, principal, projectId, GitHubAuditAction.AutomationActivated, ct).ConfigureAwait(false);
        if (denied is not null)
            return (denied.Value == AutomationActivationOutcome.HumanEntraSubjectRequired
                ? AutomationActivationOutcome.HumanEntraSubjectRequired
                : AutomationActivationOutcome.ProjectOwnerRequired, null);

        var created = await persistence.TryCreateAutomationActivationSnapshotAsync(
            projectId.ToString(), caller.EntraObjectId, GitHubAuditActorKind.HumanEntraSubject,
            token => modelProviderResolver.ResolveAsync(projectId, token), ct).ConfigureAwait(false);
        return (ToOutcome(created.Result), created.Activation is null ? null : new(
            created.Activation.Id,
            created.Activation.ProjectId,
            created.Activation.InstallationId,
            created.Activation.RepositoryId,
            created.Activation.RepositoryGrantDigest!,
            created.Activation.ModelProviderSource,
            created.Activation.CopilotBindingId,
            created.Activation.CopilotBindingGrantDigest,
            created.Activation.ByokProviderId));
    }

    public async Task<AutomationDeactivationOutcome> DeactivateAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        ProjectId projectId,
        CancellationToken ct = default)
    {
        var denied = await CheckOwnerAuthorityAsync(
            caller, principal, projectId, GitHubAuditAction.AutomationDeactivated, ct).ConfigureAwait(false);
        if (denied is not null)
            return denied.Value == AutomationActivationOutcome.HumanEntraSubjectRequired
                ? AutomationDeactivationOutcome.HumanEntraSubjectRequired
                : AutomationDeactivationOutcome.ProjectOwnerRequired;

        var deactivated = await persistence.TryDeactivateAutomationActivationAsync(
            projectId.ToString(), caller.EntraObjectId, GitHubAuditActorKind.HumanEntraSubject, ct).ConfigureAwait(false);
        return deactivated ? AutomationDeactivationOutcome.Deactivated : AutomationDeactivationOutcome.NotActive;
    }

    /// <summary>
    /// Redacted status for a project's most recent automation activation, for the Owner-facing
    /// settings UI. Never returns repository, installation, or credential identity.
    /// </summary>
    public async Task<AutomationActivationStatusView> GetStatusAsync(
        ProjectId projectId,
        CancellationToken ct = default)
    {
        var summary = await persistence.GetAutomationActivationSummaryAsync(projectId.ToString(), ct)
            .ConfigureAwait(false);
        if (summary is null)
            return new(IsActive: false, ModelProviderSource: null, ActivatedAt: null);

        return new(
            IsActive: summary.Status == AutomationActivationStatus.Active,
            ModelProviderSource: summary.ModelProviderSource == AutomationModelProviderSource.Byok ? "byok" : "github_copilot",
            ActivatedAt: summary.ActivatedAt);
    }

    public async Task<FencedAutomationActivation?> TryFenceAsync(
        string activationId,
        CancellationToken ct = default)
    {
        var projectId = await persistence.GetAutomationActivationProjectIdAsync(activationId, ct)
            .ConfigureAwait(false);
        if (!ProjectId.TryParse(projectId, out var parsedProjectId))
            return null;
        var selectedProvider = await modelProviderResolver.ResolveAsync(parsedProjectId, ct).ConfigureAwait(false);
        return await persistence.TryFenceAutomationActivationAsync(activationId, selectedProvider, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Shared human-Entra-subject + project-Owner authority check for both activation and
    /// deactivation, auditing a denial under <paramref name="action"/>. Returns <see langword="null"/>
    /// when the caller is authorized.
    /// </summary>
    private async Task<AutomationActivationOutcome?> CheckOwnerAuthorityAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        ProjectId projectId,
        GitHubAuditAction action,
        CancellationToken ct)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
        {
            await persistence.AppendAuditAsync(new GitHubAuditRecord
            {
                ActorKind = GitHubAuditActorKind.GitHubWebhook,
                Action = action,
                ResourceId = projectId.ToString(),
                AppKind = GitHubAppKind.Repo,
                CapabilityPurpose = GitHubCapabilityPurpose.UnattendedRepository,
                Outcome = GitHubAuditOutcome.Denied,
                ReasonCode = GitHubAuditReasonCode.HumanEntraSubjectRequired,
                CorrelationId = SnapshotRef.Create().Value,
                OccurredAt = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);
            return AutomationActivationOutcome.HumanEntraSubjectRequired;
        }

        var assignment = await roleAssignments.GetAsync(projectId, caller.EntraObjectId!, ct).ConfigureAwait(false);
        if (assignment?.Role != ProjectRole.Owner)
        {
            await persistence.AppendAuditAsync(new GitHubAuditRecord
            {
                EntraObjectId = caller.EntraObjectId,
                ActorKind = GitHubAuditActorKind.HumanEntraSubject,
                Action = action,
                ResourceId = projectId.ToString(),
                AppKind = GitHubAppKind.Repo,
                CapabilityPurpose = GitHubCapabilityPurpose.UnattendedRepository,
                Outcome = GitHubAuditOutcome.Denied,
                ReasonCode = GitHubAuditReasonCode.ProjectOwnerRequired,
                CorrelationId = SnapshotRef.Create().Value,
                OccurredAt = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);
            return AutomationActivationOutcome.ProjectOwnerRequired;
        }

        return null;
    }

    private static AutomationActivationOutcome ToOutcome(AutomationActivationWriteResult result) => result switch
    {
        AutomationActivationWriteResult.Activated => AutomationActivationOutcome.Activated,
        AutomationActivationWriteResult.RepositoryGrantUnavailable => AutomationActivationOutcome.RepositoryGrantUnavailable,
        AutomationActivationWriteResult.RepositoryGrantAmbiguous => AutomationActivationOutcome.RepositoryGrantAmbiguous,
        AutomationActivationWriteResult.CopilotBindingUnavailable => AutomationActivationOutcome.CopilotBindingUnavailable,
        AutomationActivationWriteResult.CopilotBindingAmbiguous => AutomationActivationOutcome.CopilotBindingAmbiguous,
        _ => AutomationActivationOutcome.Conflict,
    };
}
