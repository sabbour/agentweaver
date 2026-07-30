using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

public sealed record EffectiveProjectGitHubIdentity(
    string? OverrideLogin,
    GitHubIdentityLink? EffectiveLink,
    string ResolutionSource);

public sealed class ProjectGitHubIdentityService(
    ProjectGitHubIdentityOverrideStore overrideStore,
    IGitHubTokenStore tokenStore)
{
    public async Task<EffectiveProjectGitHubIdentity> GetEffectiveIdentityAsync(
        ProjectId projectId,
        string entraUserId,
        CancellationToken ct = default)
    {
        var multi = RequireMultiIdentityStore();
        var overrideLogin = await overrideStore.GetOverrideLoginAsync(projectId, entraUserId, ct).ConfigureAwait(false);
        GitHubIdentityLink? effective = null;
        var source = "none";

        if (!string.IsNullOrWhiteSpace(overrideLogin))
        {
            effective = await multi.GetLinkedIdentityAsync(entraUserId, overrideLogin!, ct).ConfigureAwait(false);
            if (effective is not null)
                source = "project_override";
        }

        if (effective is null)
        {
            effective = await multi.GetDefaultLinkedIdentityAsync(entraUserId, ct).ConfigureAwait(false);
            if (effective is not null)
                source = "default";
        }

        return new EffectiveProjectGitHubIdentity(overrideLogin, effective, source);
    }

    public async Task SetOverrideAsync(
        ProjectId projectId,
        string entraUserId,
        string? githubLogin,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(githubLogin))
        {
            await overrideStore.ClearOverrideLoginAsync(projectId, entraUserId, ct).ConfigureAwait(false);
            return;
        }

        var multi = RequireMultiIdentityStore();
        var link = await multi.GetLinkedIdentityAsync(entraUserId, githubLogin, ct).ConfigureAwait(false);
        if (link is null)
            throw new KeyNotFoundException($"Linked GitHub account '{githubLogin}' was not found.");

        await overrideStore.SetOverrideLoginAsync(projectId, entraUserId, githubLogin, ct).ConfigureAwait(false);
    }

    private IMultiIdentityGitHubTokenStore RequireMultiIdentityStore() =>
        tokenStore as IMultiIdentityGitHubTokenStore
        ?? throw new InvalidOperationException("Configured GitHub token store does not support linked identities.");
}
