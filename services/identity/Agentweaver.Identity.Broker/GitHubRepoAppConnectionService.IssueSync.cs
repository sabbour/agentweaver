using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.SourceControl;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Identity.Broker;

internal sealed partial class GitHubRepoAppConnectionService
{
    internal async Task<GitHubAppInstallationCredential> MintIssueSyncTokenAsync(IssueSyncGrant grant,
        IssueSyncGrantAuthority authority, GitHubAppInstallationTokenIssuer issuer, CancellationToken ct)
    {
        await authority.RequireAsync(grant.GrantId, ct);
        var selection = await _db.RepoAppRepositorySelections.AsNoTracking()
            .SingleOrDefaultAsync(row => row.CodeHash == grant.SelectionHash &&
                row.ProjectId == grant.ProjectId && row.OwnerId == grant.CreatorId &&
                row.ConnectionId == grant.ConnectionId && row.IssueWriteRequested, ct)
            ?? throw new RuntimeAuthorizationException("issue_sync_repository_not_selected");
        var connection = await GetConnectionAsync(grant.CreatorId, ct);
        if (connection.ConnectionId != grant.ConnectionId || connection.State != RepoAppConnectionState.Connected ||
            connection.ConnectionRevision != selection.ConnectionRevision)
            throw new RuntimeAuthorizationException("issue_sync_connection_changed");
        SecretCredential? userToken = null;
        SecretCredential? appKey = null;
        GitHubAppInstallationCredential? result = null;
        try
        {
            userToken = await GetUsableAccessTokenAsync(connection, ct);
            var browse = await _provider.BrowseAsync(userToken, ct);
            if (!browse.Repositories.Any(row => row.InstallationId == selection.InstallationId &&
                row.RepositoryId == selection.RepositoryId &&
                string.Equals(row.FullName, selection.RepositoryFullName, StringComparison.OrdinalIgnoreCase)) ||
                !browse.Installations.Any(row => row.InstallationId == selection.InstallationId))
                throw new RuntimeAuthorizationException("issue_sync_repository_not_selected");
            await EnsureConnectionCurrentAsync(connection, ct);
            await authority.RequireAsync(grant.GrantId, ct);
            appKey = await _secretRedemption.RedeemAsync(new(new(_options.PrivateKeySecretId,
                _options.PrivateKeySecretVersion), SourceControlSecretPurposes.GitHubAppPrivateKey,
                "issue-sync:" + grant.GrantId.ToString("N")), ct);
            result = await issuer.MintAsync(selection.InstallationId, selection.RepositoryId, appKey, true, ct);
            if (!result.IssueWriteGranted)
                throw new RuntimeAuthorizationException("issue_sync_issue_write_denied");
            await EnsureConnectionCurrentAsync(connection, ct);
            await authority.RequireAsync(grant.GrantId, ct);
            return result;
        }
        catch
        {
            result?.Credential.Invalidate();
            throw;
        }
        finally
        {
            userToken?.Invalidate();
            appKey?.Invalidate();
        }
    }
}
