using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Internal pre-run authority for #945. It can record safe, server-derived repository metadata
/// and atomically consume one selected repository. It has no run, project, snapshot, installation,
/// HTTP, MCP, or sandbox surface and never returns credential material.
/// </summary>
internal sealed class GitHubRepositoryBrowseAuthority(TwoAppPersistenceStore persistence)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    internal async Task<GitHubBrowseAuthorityHandle?> CreateAsync(
        string trustedHumanSubject,
        string authorizationId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var authority = await persistence.TryCreateBrowseAuthorityAsync(
            trustedHumanSubject, authorizationId, now, ct).ConfigureAwait(false);
        await AppendAuditAsync(
            trustedHumanSubject,
            GitHubAuditAction.BrowseAuthorityCreated,
            authority?.AuthorityRef.Value ?? "unavailable",
            authority is null ? GitHubAuditOutcome.Denied : GitHubAuditOutcome.Succeeded,
            authority is null ? GitHubAuditReasonCode.BrowseAuthorityUnavailable : GitHubAuditReasonCode.None,
            ct).ConfigureAwait(false);
        return authority;
    }

    internal async Task<BrowseSelectionRef?> RecordServerDerivedSelectionAsync(
        BrowseAuthorityRef authorityRef,
        string trustedHumanSubject,
        ServerDerivedBrowseSelection selection,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var reference = await persistence.TryCreateBrowseSelectionAsync(
            authorityRef, trustedHumanSubject, selection, now, ct).ConfigureAwait(false);
        await AppendAuditAsync(
            trustedHumanSubject,
            GitHubAuditAction.BrowseSelectionRecorded,
            authorityRef.Value,
            reference is null ? GitHubAuditOutcome.Denied : GitHubAuditOutcome.Succeeded,
            reference is null ? GitHubAuditReasonCode.BrowseAuthorityUnavailable : GitHubAuditReasonCode.None,
            ct).ConfigureAwait(false);
        return reference;
    }

    internal async Task<ConsumedBrowseSelection?> ConsumeSelectionAsync(
        BrowseAuthorityRef authorityRef,
        BrowseSelectionRef selectionRef,
        string trustedHumanSubject,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var selection = await persistence.ConsumeBrowseSelectionAsync(
            authorityRef, selectionRef, trustedHumanSubject, now, ct).ConfigureAwait(false);
        await AppendAuditAsync(
            trustedHumanSubject,
            GitHubAuditAction.BrowseSelectionConsumed,
            selectionRef.Value,
            selection is null ? GitHubAuditOutcome.Denied : GitHubAuditOutcome.Succeeded,
            selection is null ? GitHubAuditReasonCode.BrowseSelectionUnavailable : GitHubAuditReasonCode.None,
            ct).ConfigureAwait(false);
        return selection;
    }

    private Task AppendAuditAsync(
        string subject,
        GitHubAuditAction action,
        string resourceId,
        GitHubAuditOutcome outcome,
        GitHubAuditReasonCode reason,
        CancellationToken ct) =>
        persistence.AppendAuditAsync(new GitHubAuditRecord
        {
            EntraObjectId = subject,
            ActorKind = GitHubAuditActorKind.HumanEntraSubject,
            Action = action,
            ResourceId = resourceId,
            AppKind = GitHubAppKind.Repo,
            CapabilityPurpose = GitHubCapabilityPurpose.InteractiveRepository,
            Outcome = outcome,
            ReasonCode = reason,
            CorrelationId = Guid.NewGuid().ToString("N"),
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct);
}
