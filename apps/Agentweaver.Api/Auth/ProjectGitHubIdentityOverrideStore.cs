using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Auth;

public sealed class ProjectGitHubIdentityOverrideStore(MemoryDbContext db)
{
    public async Task<string?> GetOverrideLoginAsync(
        ProjectId projectId,
        string entraUserId,
        CancellationToken ct = default)
    {
        var record = await db.ProjectGitHubIdentityOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.ProjectId == projectId.ToString() && x.EntraUserId == entraUserId,
                ct)
            .ConfigureAwait(false);
        return record?.GitHubLogin;
    }

    public async Task SetOverrideLoginAsync(
        ProjectId projectId,
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
    {
        var key = projectId.ToString();
        var existing = await db.ProjectGitHubIdentityOverrides
            .FirstOrDefaultAsync(x => x.ProjectId == key && x.EntraUserId == entraUserId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.ProjectGitHubIdentityOverrides.Add(new ProjectGitHubIdentityOverrideRecord
            {
                ProjectId = key,
                EntraUserId = entraUserId,
                GitHubLogin = githubLogin,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.GitHubLogin = githubLogin;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearOverrideLoginAsync(
        ProjectId projectId,
        string entraUserId,
        CancellationToken ct = default)
    {
        await db.ProjectGitHubIdentityOverrides
            .Where(x => x.ProjectId == projectId.ToString() && x.EntraUserId == entraUserId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task RemoveOverridesForLinkedLoginAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
    {
        await db.ProjectGitHubIdentityOverrides
            .Where(x => x.EntraUserId == entraUserId && x.GitHubLogin == githubLogin)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }
}
