using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.SourceControl;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Identity.Broker;

internal sealed partial class GitHubRepoAppConnectionService
{
    internal async Task<GitHubRepoAppInstallationTokenResult> MintTeamSyncTokenAsync(
        IssueSyncProjectProof proof,
        string selectionCode,
        GitHubAppInstallationTokenIssuer tokenIssuer,
        bool writeRequested,
        CancellationToken cancellationToken)
    {
        // Bind all token authority to current project-owner evidence and one selected repository.
        if (!Guid.TryParseExact(proof.CreatorId, "D", out var ownerId) ||
            ownerId == Guid.Empty ||
            selectionCode is not { Length: >= 32 and <= 256 } ||
            string.IsNullOrWhiteSpace(proof.ProjectId) ||
            string.IsNullOrWhiteSpace(proof.TenantId) ||
            string.IsNullOrWhiteSpace(proof.ConnectionId))
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.AuthorizationInvalid);

        var selectionHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selectionCode)));
        var now = _timeProvider.GetUtcNow();
        var selection = await _db.RepoAppRepositorySelections.AsNoTracking()
            .SingleOrDefaultAsync(row => row.CodeHash == selectionHash && row.OwnerId == ownerId,
                cancellationToken).ConfigureAwait(false);
        if (selection is null ||
            selection.ProjectId is not null && selection.ProjectId != proof.ProjectId ||
            selection.ProjectId is null && (selection.ConsumedAt is not null || selection.ExpiresAt <= now) ||
            selection.ConnectionId != proof.ConnectionId ||
            !string.Equals(selection.RepositoryFullName, proof.Repository.FullName,
                StringComparison.OrdinalIgnoreCase))
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RepositoryUnavailable);

        if (selection.ProjectId is null)
        {
            // Claim the one-time selection atomically so a second project cannot reuse it.
            var claimed = await _db.RepoAppRepositorySelections
                .Where(row => row.CodeHash == selectionHash && row.OwnerId == ownerId &&
                    row.ProjectId == null && row.ConsumedAt == null && row.ExpiresAt > now)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(row => row.ProjectId, proof.ProjectId)
                    .SetProperty(row => row.ConsumedAt, now), cancellationToken)
                .ConfigureAwait(false);
            if (claimed != 1)
                throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RepositoryUnavailable);
        }

        var connection = await GetConnectionAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (connection.ConnectionId != proof.ConnectionId ||
            connection.State != RepoAppConnectionState.Connected ||
            connection.ConnectionRevision != selection.ConnectionRevision)
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.NotConnected);

        SecretCredential? userToken = null;
        SecretCredential? appKey = null;
        GitHubAppInstallationCredential? credential = null;
        try
        {
            // Confirm the selected installation and repository are still visible to the connected GitHub account.
            userToken = await GetUsableAccessTokenAsync(connection, cancellationToken).ConfigureAwait(false);
            var browse = await _provider.BrowseAsync(userToken, cancellationToken).ConfigureAwait(false);
            var repository = browse.Repositories.SingleOrDefault(item =>
                item.InstallationId == selection.InstallationId &&
                item.RepositoryId == selection.RepositoryId &&
                string.Equals(item.FullName, proof.Repository.FullName, StringComparison.OrdinalIgnoreCase));
            if (repository is null ||
                !browse.Installations.Any(item => item.InstallationId == selection.InstallationId) ||
                !await _db.RepoAppInstallations.AsNoTracking().AnyAsync(item =>
                    item.ConnectionId == connection.ConnectionId &&
                    item.ConnectionRevision == connection.ConnectionRevision &&
                    item.InstallationId == selection.InstallationId &&
                    item.RevokedAt == null,
                    cancellationToken).ConfigureAwait(false))
                throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RepositoryUnavailable);

            await EnsureConnectionCurrentAsync(connection, cancellationToken).ConfigureAwait(false);
            appKey = await _secretRedemption.RedeemAsync(
                new SecretRedemptionRequest(
                    new SecretRef(_options.PrivateKeySecretId, _options.PrivateKeySecretVersion),
                    SourceControlSecretPurposes.GitHubAppPrivateKey,
                    "source-control-team-sync:" + Guid.NewGuid().ToString("N")),
                cancellationToken).ConfigureAwait(false);
            credential = await tokenIssuer.MintTeamSyncAsync(
                selection.InstallationId,
                selection.RepositoryId,
                appKey,
                writeRequested,
                cancellationToken).ConfigureAwait(false);

            // Recheck account state after minting so a revoked connection cannot release a token.
            var current = await EnsureConnectionCurrentAsync(connection, cancellationToken).ConfigureAwait(false);
            var latestSelection = await _db.RepoAppRepositorySelections.AsNoTracking()
                .SingleOrDefaultAsync(row => row.CodeHash == selectionHash &&
                    row.OwnerId == ownerId && row.ProjectId == proof.ProjectId,
                    cancellationToken).ConfigureAwait(false);
            if (current.ConnectionRevision != selection.ConnectionRevision ||
                latestSelection is null ||
                latestSelection.ConnectionId != proof.ConnectionId ||
                latestSelection.InstallationId != selection.InstallationId ||
                latestSelection.RepositoryId != selection.RepositoryId ||
                credential.ContentsPermission != (writeRequested ? "write" : "read"))
                throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RepositoryUnavailable);

            return new GitHubRepoAppInstallationTokenResult(
                credential,
                current.ConnectionId,
                current.ConnectionRevision,
                selection.InstallationId,
                selection.RepositoryId,
                repository.FullName,
                repository.DefaultBranch,
                repository.IsPrivate,
                selectionHash);
        }
        catch
        {
            credential?.Credential.Invalidate();
            throw;
        }
        finally
        {
            userToken?.Invalidate();
            appKey?.Invalidate();
        }
    }
}
