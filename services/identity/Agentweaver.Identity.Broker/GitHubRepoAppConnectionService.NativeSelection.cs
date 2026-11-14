using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Identity.Broker;

internal sealed partial class GitHubRepoAppConnectionService
{
    internal async Task<DateTimeOffset> ValidateNativeSelectionAsync(
        Guid ownerId,
        NativeRepoAppSelectionIntent intent,
        string code,
        CancellationToken cancellationToken)
    {
        if (intent.ConnectionRevision is null || intent.InstallationId is null || intent.RepositoryId is null)
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RepositoryUnavailable);
        var codeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        var now = _timeProvider.GetUtcNow();
        var selection = await _db.RepoAppRepositorySelections.AsNoTracking()
            .SingleOrDefaultAsync(row => row.CodeHash == codeHash && row.OwnerId == ownerId, cancellationToken)
            .ConfigureAwait(false);
        var connection = await _db.RepoAppConnections.AsNoTracking()
            .SingleOrDefaultAsync(row => row.OwnerId == ownerId, cancellationToken)
            .ConfigureAwait(false);
        if (selection is null ||
            connection is null ||
            selection.ProjectId is not null ||
            selection.ConsumedAt is not null ||
            selection.ExpiresAt <= now ||
            selection.ConnectionId != intent.ConnectionId ||
            selection.ConnectionRevision != intent.ConnectionRevision ||
            selection.InstallationId != intent.InstallationId ||
            selection.RepositoryId != intent.RepositoryId ||
            !string.Equals(selection.RepositoryFullName, intent.RepositoryFullName, StringComparison.Ordinal) ||
            connection.ConnectionId != intent.ConnectionId ||
            connection.ConnectionRevision != intent.ConnectionRevision ||
            connection.State != RepoAppConnectionState.Connected ||
            !await _db.RepoAppInstallations.AsNoTracking().AnyAsync(row =>
                row.ConnectionId == intent.ConnectionId &&
                row.InstallationId == intent.InstallationId &&
                row.ConnectionRevision == intent.ConnectionRevision &&
                row.RevokedAt == null,
                cancellationToken).ConfigureAwait(false))
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RepositoryUnavailable);

        return selection.ExpiresAt;
    }
}
