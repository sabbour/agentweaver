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

/// <summary>
/// Internal-only authority boundary for unattended automation activation. It intentionally has
/// no HTTP endpoint: the project ID is the sole caller input, while all GitHub authority is
/// resolved server-side and captured as an immutable, fenceable tuple.
/// </summary>
public sealed class AutomationActivationSnapshotService(
    GitHubConnectionsPersistenceStore persistence,
    IProjectRoleAssignmentStore roleAssignments)
{
    public async Task<(AutomationActivationOutcome Outcome, FencedAutomationActivation? Activation)> ActivateAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        ProjectId projectId,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
        {
            await persistence.AppendAuditAsync(new GitHubAuditRecord
            {
                ActorKind = GitHubAuditActorKind.GitHubWebhook,
                Action = GitHubAuditAction.AutomationActivated,
                ResourceId = projectId.ToString(),
                AppKind = GitHubAppKind.Repo,
                CapabilityPurpose = GitHubCapabilityPurpose.UnattendedRepository,
                Outcome = GitHubAuditOutcome.Denied,
                ReasonCode = GitHubAuditReasonCode.HumanEntraSubjectRequired,
                CorrelationId = SnapshotRef.Create().Value,
                OccurredAt = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);
            return (AutomationActivationOutcome.HumanEntraSubjectRequired, null);
        }

        var assignment = await roleAssignments.GetAsync(projectId, caller.EntraObjectId!, ct).ConfigureAwait(false);
        if (assignment?.Role != ProjectRole.Owner)
        {
            await persistence.AppendAuditAsync(new GitHubAuditRecord
            {
                EntraObjectId = caller.EntraObjectId,
                ActorKind = GitHubAuditActorKind.HumanEntraSubject,
                Action = GitHubAuditAction.AutomationActivated,
                ResourceId = projectId.ToString(),
                AppKind = GitHubAppKind.Repo,
                CapabilityPurpose = GitHubCapabilityPurpose.UnattendedRepository,
                Outcome = GitHubAuditOutcome.Denied,
                ReasonCode = GitHubAuditReasonCode.ProjectOwnerRequired,
                CorrelationId = SnapshotRef.Create().Value,
                OccurredAt = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);
            return (AutomationActivationOutcome.ProjectOwnerRequired, null);
        }

        var created = await persistence.TryCreateAutomationActivationSnapshotAsync(
            projectId.ToString(), caller.EntraObjectId, GitHubAuditActorKind.HumanEntraSubject, ct).ConfigureAwait(false);
        return (ToOutcome(created.Result), created.Activation is null ? null : new(
            created.Activation.Id,
            created.Activation.ProjectId,
            created.Activation.InstallationId,
            created.Activation.RepositoryId,
            created.Activation.RepositoryGrantDigest!,
            created.Activation.CopilotBindingId!,
            created.Activation.CopilotBindingGrantDigest!));
    }

    public Task<FencedAutomationActivation?> TryFenceAsync(
        string activationId,
        CancellationToken ct = default) =>
        persistence.TryFenceAutomationActivationAsync(activationId, ct);

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
