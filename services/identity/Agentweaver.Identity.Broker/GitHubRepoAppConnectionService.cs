using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentweaver.Identity.Broker;

internal sealed record GitHubRepoAppAuthorizationStart(Uri RedirectUri, string CallbackCookie);

internal sealed record CreatedGitHubRepoAppAuthorization(string State, string CallbackCookie, string Verifier);

internal sealed record GitHubRepoAppRepositoryOption(
    long InstallationId,
    long RepositoryId,
    string FullName,
    string OwnerLogin,
    bool IsPrivate,
    string DefaultBranch);

internal sealed record GitHubRepoAppRepositoryBrowser(
    string ConnectionId,
    long ConnectionRevision,
    string GitHubLogin,
    IReadOnlyList<GitHubRepoAppRepositoryOption> Repositories);

internal sealed record GitHubRepoAppRepositorySelection(
    string Code,
    string ConnectionId,
    long ConnectionRevision,
    long InstallationId,
    long RepositoryId,
    string RepositoryFullName);

internal sealed record GitHubRepoAppInstallationTokenRequest(
    string? SelectionCode,
    string? SelectionHash,
    string? ConnectionId,
    long? ConnectionRevision,
    long? InstallationId,
    long? RepositoryId,
    string? PermissionDigest,
    string ExpectedRepositoryFullName);

internal sealed record GitHubRepoAppInstallationTokenResult(
    GitHubAppInstallationCredential Credential,
    string ConnectionId,
    long ConnectionRevision,
    long InstallationId,
    long RepositoryId,
    string RepositoryFullName,
    string DefaultBranch,
    bool IsPrivate,
    string SelectionHash);

internal enum GitHubRepoAppConnectionFailure
{
    NotConnected,
    AuthorizationInvalid,
    Revoked,
    RefreshInProgress,
    RotationUncertain,
    ProviderUnavailable,
    RepositoryUnavailable,
    RunBindingInvalid,
    PermissionsChanged
}

internal sealed class GitHubRepoAppConnectionException(
    GitHubRepoAppConnectionFailure failure)
    : Exception($"GitHub Repo App connection failed ({failure}).")
{
    public GitHubRepoAppConnectionFailure Failure { get; } = failure;
}

internal sealed class GitHubRepoAppConnectionService
{
    private static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AccessTokenRefreshWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RefreshLeaseLifetime = TimeSpan.FromMinutes(2);
    private const string CallbackPath = "/auth/github/repo-app/callback";
    private const string CallbackCookieName = "__Host-agentweaver-github-repo-app";
    private readonly IdentityBrokerDbContext _db;
    private readonly GitHubRepoAppOptions _options;
    private readonly GitHubRepoAppProviderClient _provider;
    private readonly ISecretVersionWriter _secretWriter;
    private readonly ISecretRedemption _secretRedemption;
    private readonly IDataProtector _verifierProtector;
    private readonly TimeProvider _timeProvider;

    public GitHubRepoAppConnectionService(
        IdentityBrokerDbContext db,
        GitHubRepoAppOptions options,
        GitHubRepoAppProviderClient provider,
        ISecretVersionWriter secretWriter,
        ISecretRedemption secretRedemption,
        IDataProtectionProvider dataProtection,
        TimeProvider timeProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _secretWriter = secretWriter ?? throw new ArgumentNullException(nameof(secretWriter));
        _secretRedemption = secretRedemption ?? throw new ArgumentNullException(nameof(secretRedemption));
        _verifierProtector = (dataProtection ?? throw new ArgumentNullException(nameof(dataProtection)))
            .CreateProtector("Agentweaver.Identity.Broker.GitHubRepoApp.PkceVerifier.v1");
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (!Uri.TryCreate(options.CallbackUri, UriKind.Absolute, out var callbackUri) ||
            callbackUri.Scheme != Uri.UriSchemeHttps ||
            callbackUri.AbsolutePath != CallbackPath ||
            !string.IsNullOrEmpty(callbackUri.Query) ||
            !string.IsNullOrEmpty(callbackUri.Fragment))
            throw new ArgumentException("The GitHub Repo App callback URI is invalid.", nameof(options));
    }

    internal const string CallbackCookie = CallbackCookieName;

    internal async Task<GitHubRepoAppAuthorizationStart> BeginUserAuthorizationAsync(
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        var transaction = await CreateAuthorizationTransactionAsync(
            ownerId, RepoAppAuthorizationPurpose.UserAuthorization, cancellationToken).ConfigureAwait(false);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(transaction.Verifier)));
        var authorizationUri = QueryHelpers.AddQueryString(
            "https://github.com/login/oauth/authorize",
            new Dictionary<string, string?>
            {
                ["client_id"] = _options.OAuthClientId,
                ["redirect_uri"] = _options.CallbackUri,
                ["state"] = transaction.State,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256"
            });
        return new(new Uri(authorizationUri), transaction.CallbackCookie);
    }

    internal async Task<GitHubRepoAppAuthorizationStart> BeginInstallationSetupAsync(
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (connection.State != RepoAppConnectionState.Connected)
            throw FailureForState(connection.State);

        var transaction = await CreateAuthorizationTransactionAsync(
            ownerId, RepoAppAuthorizationPurpose.InstallationSetup, cancellationToken).ConfigureAwait(false);
        var setupUri = QueryHelpers.AddQueryString(
            $"https://github.com/apps/{Uri.EscapeDataString(_options.AppSlug)}/installations/new",
            "state",
            transaction.State);
        return new(new Uri(setupUri), transaction.CallbackCookie);
    }

    internal async Task CompleteCallbackAsync(
        Guid ownerId,
        string? state,
        string? callbackCookie,
        string? code,
        long? installationId,
        string? setupAction,
        CancellationToken cancellationToken)
    {
        if (!TryHashOpaque(state, out var stateHash) ||
            !TryHashOpaque(callbackCookie, out var callbackCookieHash))
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.AuthorizationInvalid);

        var now = _timeProvider.GetUtcNow();
        var transaction = await _db.RepoAppAuthorizationTransactions
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.StateHash == stateHash &&
                item.OwnerId == ownerId &&
                item.State == RepoAppAuthorizationState.Pending &&
                item.ExpiresAt > now,
                cancellationToken)
            .ConfigureAwait(false);
        if (transaction is null || !FixedTimeHashEquals(transaction.CallbackCookieHash, callbackCookieHash))
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.AuthorizationInvalid);

        var claimed = await _db.RepoAppAuthorizationTransactions
            .Where(item =>
                item.StateHash == stateHash &&
                item.OwnerId == ownerId &&
                item.State == RepoAppAuthorizationState.Pending &&
                item.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, RepoAppAuthorizationState.Processing),
                cancellationToken)
            .ConfigureAwait(false);
        if (claimed != 1)
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.AuthorizationInvalid);

        try
        {
            switch (transaction.Purpose)
            {
                case RepoAppAuthorizationPurpose.UserAuthorization when
                    !string.IsNullOrWhiteSpace(code) && installationId is null:
                    await CompleteUserAuthorizationAsync(
                        transaction, code, cancellationToken).ConfigureAwait(false);
                    break;
                case RepoAppAuthorizationPurpose.InstallationSetup when
                    string.IsNullOrWhiteSpace(code) && installationId is > 0 &&
                    setupAction is "install" or "update":
                    await CompleteInstallationSetupAsync(
                        transaction, installationId.Value, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new GitHubRepoAppConnectionException(
                        GitHubRepoAppConnectionFailure.AuthorizationInvalid);
            }

            await _db.RepoAppAuthorizationTransactions
                .Where(item => item.StateHash == stateHash &&
                    item.State == RepoAppAuthorizationState.Processing)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, RepoAppAuthorizationState.Completed)
                    .SetProperty(item => item.CompletedAt, _timeProvider.GetUtcNow())
                    .SetProperty(item => item.InstallationId, installationId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await _db.RepoAppAuthorizationTransactions
                .Where(item => item.StateHash == stateHash &&
                    item.State == RepoAppAuthorizationState.Processing)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, RepoAppAuthorizationState.Failed),
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<GitHubRepoAppRepositoryBrowser> ListRepositoriesAsync(
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (connection.State != RepoAppConnectionState.Connected)
            throw FailureForState(connection.State);

        SecretCredential? accessToken = null;
        try
        {
            accessToken = await GetUsableAccessTokenAsync(connection, cancellationToken).ConfigureAwait(false);
            var browse = await _provider.BrowseAsync(accessToken, cancellationToken).ConfigureAwait(false);
            var current = await EnsureConnectionCurrentAsync(
                connection, cancellationToken).ConfigureAwait(false);
            await SynchronizeInstallationsAsync(current, browse.Installations, cancellationToken)
                .ConfigureAwait(false);
            var repositories = browse.Repositories.Select(repository => new GitHubRepoAppRepositoryOption(
                repository.InstallationId,
                repository.RepositoryId,
                repository.FullName,
                repository.OwnerLogin,
                repository.IsPrivate,
                repository.DefaultBranch)).ToArray();
            return new(current.ConnectionId, current.ConnectionRevision, current.GitHubLogin, repositories);
        }
        catch (GitHubRepoAppProviderException error)
        {
            if (error.Failure == GitHubRepoAppProviderFailure.Revoked)
                await MarkConnectionStateAsync(connection, RepoAppConnectionState.Revoked)
                    .ConfigureAwait(false);
            throw MapProviderFailure(error);
        }
        finally
        {
            accessToken?.Invalidate();
        }
    }

    internal async Task<GitHubRepoAppRepositorySelection> CreateRepositorySelectionAsync(
        Guid ownerId,
        long installationId,
        long repositoryId,
        CancellationToken cancellationToken)
    {
        if (installationId <= 0 || repositoryId <= 0)
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RepositoryUnavailable);

        var connection = await GetConnectionAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (connection.State != RepoAppConnectionState.Connected)
            throw FailureForState(connection.State);

        SecretCredential? accessToken = null;
        try
        {
            accessToken = await GetUsableAccessTokenAsync(connection, cancellationToken).ConfigureAwait(false);
            var browse = await _provider.BrowseAsync(accessToken, cancellationToken).ConfigureAwait(false);
            var current = await EnsureConnectionCurrentAsync(
                connection, cancellationToken).ConfigureAwait(false);
            await SynchronizeInstallationsAsync(current, browse.Installations, cancellationToken)
                .ConfigureAwait(false);
            var repository = browse.Repositories.SingleOrDefault(candidate =>
                candidate.RepositoryId == repositoryId &&
                candidate.InstallationId == installationId);
            if (repository is null)
                throw new GitHubRepoAppConnectionException(
                    GitHubRepoAppConnectionFailure.RepositoryUnavailable);

            var code = NewSelectionCode();
            var now = _timeProvider.GetUtcNow();
            _db.RepoAppRepositorySelections.Add(new RepoAppRepositorySelectionRecord
            {
                CodeHash = Hash(code),
                OwnerId = ownerId,
                ConnectionId = current.ConnectionId,
                ConnectionRevision = current.ConnectionRevision,
                InstallationId = installationId,
                RepositoryId = repositoryId,
                RepositoryFullName = repository.FullName,
                CreatedAt = now,
                ExpiresAt = now.Add(AuthorizationLifetime)
            });
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new(code, connection.ConnectionId, connection.ConnectionRevision,
                installationId, repositoryId, repository.FullName);
        }
        catch (GitHubRepoAppProviderException error)
        {
            if (error.Failure == GitHubRepoAppProviderFailure.Revoked)
                await MarkConnectionStateAsync(connection, RepoAppConnectionState.Revoked)
                    .ConfigureAwait(false);
            throw MapProviderFailure(error);
        }
        finally
        {
            accessToken?.Invalidate();
        }
    }

    internal async Task<GitHubRepoAppInstallationTokenResult> MintInstallationTokenAsync(
        Guid ownerId,
        string projectId,
        string runId,
        GitHubRepoAppInstallationTokenRequest request,
        IdentityGrantAuthority grantAuthority,
        GitHubAppInstallationTokenIssuer tokenIssuer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(grantAuthority);
        ArgumentNullException.ThrowIfNull(tokenIssuer);
        if (ownerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(projectId) || projectId.Length > 256 ||
            string.IsNullOrWhiteSpace(runId) || runId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.ExpectedRepositoryFullName) ||
            request.ExpectedRepositoryFullName.Length > 201 ||
            request.ExpectedRepositoryFullName.Any(char.IsControl))
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RunBindingInvalid);

        var codeHash = string.Empty;
        var hasCode = request.SelectionCode is not null &&
            TryHashOpaque(request.SelectionCode, out codeHash);
        var hasSelectionHash = request.SelectionHash is not null &&
            IsSha256Hash(request.SelectionHash);
        if (request.SelectionCode is not null && !hasCode ||
            request.SelectionHash is not null && !hasSelectionHash ||
            hasCode == hasSelectionHash ||
            (!hasCode && (string.IsNullOrWhiteSpace(request.ConnectionId) ||
                request.ConnectionRevision is null or < 1 ||
                request.InstallationId is null or < 1 ||
                request.RepositoryId is null or < 1 ||
                !IsSha256Hash(request.PermissionDigest))) ||
            string.IsNullOrWhiteSpace(request.ConnectionId) ||
            (hasCode && (request.SelectionHash is not null ||
                request.ConnectionRevision is not null ||
                request.InstallationId is not null ||
                request.RepositoryId is not null ||
                request.PermissionDigest is not null)))
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.AuthorizationInvalid);

        var selectionHash = hasCode ? codeHash : request.SelectionHash!.ToLowerInvariant();
        if (!await grantAuthority.HasActiveRunBindingAsync(
                ownerId.ToString(), projectId, runId, cancellationToken).ConfigureAwait(false))
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RunBindingInvalid);

        var now = _timeProvider.GetUtcNow();
        var selection = await _db.RepoAppRepositorySelections
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.CodeHash == selectionHash && item.OwnerId == ownerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (selection is null ||
            !string.Equals(selection.ProjectId, projectId, StringComparison.Ordinal) &&
                selection.ProjectId is not null ||
            selection.ProjectId is null && (selection.ConsumedAt is not null || selection.ExpiresAt <= now) ||
            !string.Equals(selection.RepositoryFullName, request.ExpectedRepositoryFullName,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(selection.ConnectionId, request.ConnectionId, StringComparison.Ordinal) ||
            !hasCode && (
                selection.ConnectionRevision != request.ConnectionRevision ||
                selection.InstallationId != request.InstallationId ||
                selection.RepositoryId != request.RepositoryId ||
                !string.Equals(selection.PermissionDigest, request.PermissionDigest, StringComparison.Ordinal)))
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RepositoryUnavailable);

        if (selection.ProjectId is null)
        {
            var claimed = await _db.RepoAppRepositorySelections
                .Where(item =>
                    item.CodeHash == selectionHash &&
                    item.OwnerId == ownerId &&
                    item.ProjectId == null &&
                    item.ConsumedAt == null &&
                    item.ExpiresAt > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ProjectId, projectId)
                    .SetProperty(item => item.ConsumedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
            if (claimed != 1)
            {
                var latest = await _db.RepoAppRepositorySelections.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.CodeHash == selectionHash, cancellationToken)
                    .ConfigureAwait(false);
                if (latest?.ProjectId != projectId)
                    throw new GitHubRepoAppConnectionException(
                        GitHubRepoAppConnectionFailure.RepositoryUnavailable);
                selection = latest;
            }
            else
            {
                selection = await _db.RepoAppRepositorySelections.AsNoTracking()
                    .SingleAsync(item => item.CodeHash == selectionHash, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var connection = await _db.RepoAppConnections.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ConnectionId == selection.ConnectionId &&
                item.OwnerId == ownerId,
                cancellationToken).ConfigureAwait(false);
        if (connection is null ||
            connection.State != RepoAppConnectionState.Connected ||
            connection.ConnectionRevision != selection.ConnectionRevision)
            throw new GitHubRepoAppConnectionException(
                connection is null
                    ? GitHubRepoAppConnectionFailure.NotConnected
                    : FailureForState(connection.State).Failure);

        if (!hasCode &&
            (connection.ConnectionId != request.ConnectionId ||
             connection.ConnectionRevision != request.ConnectionRevision ||
             selection.InstallationId != request.InstallationId ||
             selection.RepositoryId != request.RepositoryId))
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RepositoryUnavailable);

        SecretCredential? userAccessToken = null;
        GitHubAppInstallationCredential? installationCredential = null;
        SecretCredential? appPrivateKey = null;
        try
        {
            userAccessToken = await GetUsableAccessTokenAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            var browse = await _provider.BrowseAsync(userAccessToken, cancellationToken).ConfigureAwait(false);
            var currentConnection = await EnsureConnectionCurrentAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            await SynchronizeInstallationsAsync(currentConnection, browse.Installations, cancellationToken)
                .ConfigureAwait(false);
            if (!await _db.RepoAppInstallations.AsNoTracking().AnyAsync(item =>
                    item.ConnectionId == currentConnection.ConnectionId &&
                    item.ConnectionRevision == currentConnection.ConnectionRevision &&
                    item.InstallationId == selection.InstallationId &&
                    item.RevokedAt == null,
                    cancellationToken).ConfigureAwait(false))
                throw new GitHubRepoAppConnectionException(
                    GitHubRepoAppConnectionFailure.RepositoryUnavailable);
            var repository = browse.Repositories.SingleOrDefault(item =>
                item.RepositoryId == selection.RepositoryId &&
                item.InstallationId == selection.InstallationId &&
                string.Equals(item.FullName, selection.RepositoryFullName, StringComparison.Ordinal));
            if (repository is null ||
                !browse.Installations.Any(item => item.InstallationId == selection.InstallationId))
                throw new GitHubRepoAppConnectionException(
                    GitHubRepoAppConnectionFailure.RepositoryUnavailable);

            var latestSelection = await _db.RepoAppRepositorySelections.AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.CodeHash == selectionHash &&
                    item.OwnerId == ownerId &&
                    item.ProjectId == projectId,
                    cancellationToken).ConfigureAwait(false);
            currentConnection = await EnsureConnectionCurrentAsync(currentConnection, cancellationToken)
                .ConfigureAwait(false);
            if (latestSelection is null ||
                latestSelection.ConnectionId != currentConnection.ConnectionId ||
                latestSelection.ConnectionRevision != currentConnection.ConnectionRevision ||
                latestSelection.InstallationId != selection.InstallationId ||
                latestSelection.RepositoryId != selection.RepositoryId ||
                (!hasCode && latestSelection.PermissionDigest != request.PermissionDigest) ||
                !await grantAuthority.HasActiveRunBindingAsync(
                    ownerId.ToString(), projectId, runId, cancellationToken).ConfigureAwait(false))
                throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RunBindingInvalid);

            appPrivateKey = await _secretRedemption.RedeemAsync(
                new SecretRedemptionRequest(
                    new SecretRef(_options.PrivateKeySecretId, _options.PrivateKeySecretVersion),
                    SourceControlSecretPurposes.GitHubAppPrivateKey,
                    runId),
                cancellationToken).ConfigureAwait(false);
            installationCredential = await tokenIssuer.MintAsync(
                selection.InstallationId,
                selection.RepositoryId,
                appPrivateKey,
                cancellationToken).ConfigureAwait(false);

            currentConnection = await EnsureConnectionCurrentAsync(currentConnection, cancellationToken)
                .ConfigureAwait(false);
            latestSelection = await _db.RepoAppRepositorySelections.AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.CodeHash == selectionHash &&
                    item.OwnerId == ownerId &&
                    item.ProjectId == projectId,
                    cancellationToken).ConfigureAwait(false);
            if (latestSelection is null ||
                latestSelection.ConnectionId != currentConnection.ConnectionId ||
                latestSelection.ConnectionRevision != currentConnection.ConnectionRevision ||
                latestSelection.InstallationId != selection.InstallationId ||
                latestSelection.RepositoryId != selection.RepositoryId ||
                !await grantAuthority.HasActiveRunBindingAsync(
                    ownerId.ToString(), projectId, runId, cancellationToken).ConfigureAwait(false))
                throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RunBindingInvalid);

            if (latestSelection.PermissionDigest is not null &&
                !string.Equals(
                    latestSelection.PermissionDigest,
                    installationCredential.PermissionDigest,
                    StringComparison.Ordinal))
                throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.PermissionsChanged);

            if (latestSelection.PermissionDigest is null)
            {
                await _db.RepoAppRepositorySelections
                    .Where(item =>
                        item.CodeHash == selectionHash &&
                        item.OwnerId == ownerId &&
                        item.ProjectId == projectId &&
                        item.PermissionDigest == null)
                    .ExecuteUpdateAsync(setters =>
                        setters.SetProperty(item => item.PermissionDigest, installationCredential.PermissionDigest),
                        cancellationToken)
                    .ConfigureAwait(false);
                latestSelection = await _db.RepoAppRepositorySelections.AsNoTracking()
                    .SingleAsync(item => item.CodeHash == selectionHash, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(
                        latestSelection.PermissionDigest,
                        installationCredential.PermissionDigest,
                        StringComparison.Ordinal))
                    throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.PermissionsChanged);
            }

            currentConnection = await EnsureConnectionCurrentAsync(currentConnection, cancellationToken)
                .ConfigureAwait(false);
            latestSelection = await _db.RepoAppRepositorySelections.AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.CodeHash == selectionHash &&
                    item.OwnerId == ownerId &&
                    item.ProjectId == projectId,
                    cancellationToken).ConfigureAwait(false);
            if (latestSelection is null ||
                latestSelection.ConnectionRevision != currentConnection.ConnectionRevision ||
                latestSelection.PermissionDigest != installationCredential.PermissionDigest ||
                !await grantAuthority.HasActiveRunBindingAsync(
                    ownerId.ToString(), projectId, runId, cancellationToken).ConfigureAwait(false))
                throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RunBindingInvalid);

            return new(
                installationCredential,
                currentConnection.ConnectionId,
                currentConnection.ConnectionRevision,
                selection.InstallationId,
                selection.RepositoryId,
                repository.FullName,
                repository.DefaultBranch,
                repository.IsPrivate,
                selectionHash);
        }
        catch (GitHubRepoAppProviderException error)
        {
            if (error.Failure == GitHubRepoAppProviderFailure.Revoked)
                await MarkConnectionStateAsync(connection, RepoAppConnectionState.Revoked)
                    .ConfigureAwait(false);
            throw MapProviderFailure(error);
        }
        catch
        {
            installationCredential?.Credential.Invalidate();
            throw;
        }
        finally
        {
            userAccessToken?.Invalidate();
            appPrivateKey?.Invalidate();
        }
    }

    private async Task CompleteUserAuthorizationAsync(
        RepoAppAuthorizationTransaction transaction,
        string code,
        CancellationToken cancellationToken)
    {
        string verifier;
        try
        {
            verifier = _verifierProtector.Unprotect(transaction.ProtectedCodeVerifier);
        }
        catch (CryptographicException)
        {
            throw new GitHubRepoAppConnectionException(
                GitHubRepoAppConnectionFailure.AuthorizationInvalid);
        }

        GitHubRepoAppTokenSet? tokens = null;
        try
        {
            tokens = await _provider.ExchangeCodeAsync(code, verifier, cancellationToken)
                .ConfigureAwait(false);
            var login = await _provider.ReadLoginAsync(tokens.AccessToken, cancellationToken)
                .ConfigureAwait(false);
            var existing = await _db.RepoAppConnections
                .SingleOrDefaultAsync(item => item.OwnerId == transaction.OwnerId, cancellationToken)
                .ConfigureAwait(false);
            var connectionId = existing?.ConnectionId ?? Guid.NewGuid().ToString("N");
            var accessId = TokenSecretId(connectionId, "access");
            var refreshId = TokenSecretId(connectionId, "refresh");
            var accessRef = await _secretWriter.WriteVersionAsync(
                accessId, tokens.AccessToken, cancellationToken).ConfigureAwait(false);
            var refreshRef = await _secretWriter.WriteVersionAsync(
                refreshId, tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();

            if (existing is null)
            {
                _db.RepoAppConnections.Add(new RepoAppConnectionRecord
                {
                    ConnectionId = connectionId,
                    OwnerId = transaction.OwnerId,
                    GitHubLogin = login,
                    AccessTokenSecretId = accessRef.Id,
                    AccessTokenSecretVersion = accessRef.Version,
                    AccessTokenExpiresAt = tokens.AccessToken.ExpiresAt,
                    RefreshTokenSecretId = refreshRef.Id,
                    RefreshTokenSecretVersion = refreshRef.Version,
                    RefreshTokenExpiresAt = tokens.RefreshToken.ExpiresAt,
                    ConnectionRevision = 1,
                    CredentialRevision = 1,
                    State = RepoAppConnectionState.Connected,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                existing.GitHubLogin = login;
                existing.AccessTokenSecretId = accessRef.Id;
                existing.AccessTokenSecretVersion = accessRef.Version;
                existing.AccessTokenExpiresAt = tokens.AccessToken.ExpiresAt;
                existing.RefreshTokenSecretId = refreshRef.Id;
                existing.RefreshTokenSecretVersion = refreshRef.Version;
                existing.RefreshTokenExpiresAt = tokens.RefreshToken.ExpiresAt;
                existing.ConnectionRevision = checked(existing.ConnectionRevision + 1);
                existing.CredentialRevision = checked(existing.CredentialRevision + 1);
                existing.State = RepoAppConnectionState.Connected;
                existing.RefreshLeaseId = null;
                existing.RefreshLeaseExpiresAt = null;
                existing.UpdatedAt = now;
                await _db.RepoAppInstallations
                    .Where(item => item.ConnectionId == existing.ConnectionId && item.RevokedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.RevokedAt, now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubRepoAppProviderException error)
        {
            throw MapProviderFailure(error);
        }
        finally
        {
            tokens?.AccessToken.Invalidate();
            tokens?.RefreshToken.Invalidate();
        }
    }

    private async Task CompleteInstallationSetupAsync(
        RepoAppAuthorizationTransaction transaction,
        long installationId,
        CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(transaction.OwnerId, cancellationToken).ConfigureAwait(false);
        if (connection.State != RepoAppConnectionState.Connected)
            throw FailureForState(connection.State);

        SecretCredential? accessToken = null;
        try
        {
            accessToken = await GetUsableAccessTokenAsync(connection, cancellationToken).ConfigureAwait(false);
            var browse = await _provider.BrowseAsync(accessToken, cancellationToken).ConfigureAwait(false);
            if (!browse.Installations.Any(item => item.InstallationId == installationId))
                throw new GitHubRepoAppConnectionException(
                    GitHubRepoAppConnectionFailure.RepositoryUnavailable);
            var current = await EnsureConnectionCurrentAsync(
                connection, cancellationToken).ConfigureAwait(false);
            await SynchronizeInstallationsAsync(current, browse.Installations, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitHubRepoAppProviderException error)
        {
            if (error.Failure == GitHubRepoAppProviderFailure.Revoked)
                await MarkConnectionStateAsync(connection, RepoAppConnectionState.Revoked)
                    .ConfigureAwait(false);
            throw MapProviderFailure(error);
        }
        finally
        {
            accessToken?.Invalidate();
        }
    }

    private async Task<CreatedGitHubRepoAppAuthorization> CreateAuthorizationTransactionAsync(
        Guid ownerId,
        RepoAppAuthorizationPurpose purpose,
        CancellationToken cancellationToken)
    {
        if (ownerId == Guid.Empty)
            throw new ArgumentException("A broker user is required.", nameof(ownerId));

        var state = NewOpaqueValue();
        var callbackCookie = NewOpaqueValue();
        var verifier = NewOpaqueValue();
        var now = _timeProvider.GetUtcNow();
        _db.RepoAppAuthorizationTransactions.Add(new RepoAppAuthorizationTransaction
        {
            StateHash = Hash(state),
            TransactionId = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            Purpose = purpose,
            CallbackCookieHash = Hash(callbackCookie),
            ProtectedCodeVerifier = _verifierProtector.Protect(verifier),
            ReturnRouteKey = "source-control",
            CreatedAt = now,
            ExpiresAt = now.Add(AuthorizationLifetime),
            State = RepoAppAuthorizationState.Pending
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(state, callbackCookie, verifier);
    }

    private async Task<SecretCredential> GetUsableAccessTokenAsync(
        RepoAppConnectionRecord connection,
        CancellationToken cancellationToken)
    {
        var current = await ReloadConnectionAsync(connection.ConnectionId, cancellationToken).ConfigureAwait(false);
        if (current.State != RepoAppConnectionState.Connected)
            throw FailureForState(current.State);
        if (current.AccessTokenExpiresAt > _timeProvider.GetUtcNow().Add(AccessTokenRefreshWindow))
            return await RedeemAsync(
                current.AccessTokenSecretId,
                current.AccessTokenSecretVersion,
                "github-repo-app-user-access",
                current.ConnectionId,
                cancellationToken).ConfigureAwait(false);

        var leaseId = Guid.NewGuid();
        var now = _timeProvider.GetUtcNow();
        var claimed = await _db.RepoAppConnections
            .Where(item =>
                item.ConnectionId == current.ConnectionId &&
                item.CredentialRevision == current.CredentialRevision &&
                item.State == RepoAppConnectionState.Connected &&
                (item.RefreshLeaseId == null || item.RefreshLeaseExpiresAt <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.RefreshLeaseId, leaseId)
                .SetProperty(item => item.RefreshLeaseExpiresAt, now.Add(RefreshLeaseLifetime)),
                cancellationToken)
            .ConfigureAwait(false);
        if (claimed != 1)
        {
            var latest = await ReloadConnectionAsync(current.ConnectionId, cancellationToken).ConfigureAwait(false);
            if (latest.State != RepoAppConnectionState.Connected)
                throw FailureForState(latest.State);
            if (latest.CredentialRevision != current.CredentialRevision &&
                latest.AccessTokenExpiresAt > _timeProvider.GetUtcNow())
                return await RedeemAsync(
                    latest.AccessTokenSecretId,
                    latest.AccessTokenSecretVersion,
                    "github-repo-app-user-access",
                    latest.ConnectionId,
                    cancellationToken).ConfigureAwait(false);
            throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RefreshInProgress);
        }

        SecretCredential? oldRefreshToken = null;
        GitHubRepoAppTokenSet? rotated = null;
        var providerRotationStarted = false;
        try
        {
            oldRefreshToken = await RedeemAsync(
                current.RefreshTokenSecretId,
                current.RefreshTokenSecretVersion,
                "github-repo-app-user-refresh",
                current.ConnectionId,
                cancellationToken).ConfigureAwait(false);
            providerRotationStarted = true;
            rotated = await _provider.RefreshAsync(oldRefreshToken, cancellationToken).ConfigureAwait(false);
            var accessRef = await _secretWriter.WriteVersionAsync(
                current.AccessTokenSecretId, rotated.AccessToken, cancellationToken).ConfigureAwait(false);
            var refreshRef = await _secretWriter.WriteVersionAsync(
                current.RefreshTokenSecretId, rotated.RefreshToken, cancellationToken).ConfigureAwait(false);
            var updatedAt = _timeProvider.GetUtcNow();
            var updated = await _db.RepoAppConnections
                .Where(item =>
                    item.ConnectionId == current.ConnectionId &&
                    item.CredentialRevision == current.CredentialRevision &&
                    item.State == RepoAppConnectionState.Connected &&
                    item.RefreshLeaseId == leaseId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AccessTokenSecretId, accessRef.Id)
                    .SetProperty(item => item.AccessTokenSecretVersion, accessRef.Version)
                    .SetProperty(item => item.AccessTokenExpiresAt, rotated.AccessToken.ExpiresAt)
                    .SetProperty(item => item.RefreshTokenSecretId, refreshRef.Id)
                    .SetProperty(item => item.RefreshTokenSecretVersion, refreshRef.Version)
                    .SetProperty(item => item.RefreshTokenExpiresAt, rotated.RefreshToken.ExpiresAt)
                    .SetProperty(item => item.CredentialRevision, item => item.CredentialRevision + 1)
                    .SetProperty(item => item.RefreshLeaseId, (Guid?)null)
                    .SetProperty(item => item.RefreshLeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(item => item.UpdatedAt, updatedAt),
                    cancellationToken)
                .ConfigureAwait(false);
            if (updated != 1)
            {
                await MarkConnectionStateAsync(
                    current, RepoAppConnectionState.RotationUncertain, leaseId).ConfigureAwait(false);
                throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RotationUncertain);
            }

            providerRotationStarted = false;
            var refreshed = await ReloadConnectionAsync(current.ConnectionId, cancellationToken).ConfigureAwait(false);
            return await RedeemAsync(
                refreshed.AccessTokenSecretId,
                refreshed.AccessTokenSecretVersion,
                "github-repo-app-user-access",
                refreshed.ConnectionId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubRepoAppProviderException error)
        {
            if (error.Failure is GitHubRepoAppProviderFailure.OutcomeUncertain)
            {
                await MarkConnectionStateAsync(
                    current, RepoAppConnectionState.RotationUncertain, leaseId).ConfigureAwait(false);
                throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RotationUncertain);
            }
            else if (error.Failure == GitHubRepoAppProviderFailure.Revoked)
            {
                await MarkConnectionStateAsync(current, RepoAppConnectionState.Revoked, leaseId)
                    .ConfigureAwait(false);
                throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.Revoked);
            }
            else
                await ReleaseRefreshLeaseAsync(current.ConnectionId, leaseId).ConfigureAwait(false);
            throw MapProviderFailure(error);
        }
        catch
        {
            if (providerRotationStarted)
                await MarkConnectionStateAsync(
                    current, RepoAppConnectionState.RotationUncertain, leaseId).ConfigureAwait(false);
            else
                await ReleaseRefreshLeaseAsync(current.ConnectionId, leaseId).ConfigureAwait(false);
            throw;
        }
        finally
        {
            oldRefreshToken?.Invalidate();
            rotated?.AccessToken.Invalidate();
            rotated?.RefreshToken.Invalidate();
        }
    }

    private async Task<SecretCredential> RedeemAsync(
        string secretId,
        string secretVersion,
        string purpose,
        string connectionId,
        CancellationToken cancellationToken) =>
        await _secretRedemption.RedeemAsync(
            new SecretRedemptionRequest(new SecretRef(secretId, secretVersion), purpose, connectionId),
            cancellationToken).ConfigureAwait(false);

    private async Task SynchronizeInstallationsAsync(
        RepoAppConnectionRecord connection,
        IReadOnlyList<GitHubRepoAppInstallationMetadata> installations,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var activeIds = installations.Select(installation => installation.InstallationId).ToHashSet();
        await _db.RepoAppInstallations
            .Where(item => item.ConnectionId == connection.ConnectionId &&
                item.ConnectionRevision == connection.ConnectionRevision &&
                item.RevokedAt == null &&
                !activeIds.Contains(item.InstallationId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.RevokedAt, now), cancellationToken)
            .ConfigureAwait(false);

        foreach (var installation in installations)
        {
            var existing = await _db.RepoAppInstallations.SingleOrDefaultAsync(item =>
                item.ConnectionId == connection.ConnectionId &&
                item.InstallationId == installation.InstallationId,
                cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                _db.RepoAppInstallations.Add(new RepoAppInstallationRecord
                {
                    ConnectionId = connection.ConnectionId,
                    InstallationId = installation.InstallationId,
                    AccountLogin = installation.AccountLogin,
                    AccountType = installation.AccountType,
                    RepositorySelection = installation.RepositorySelection,
                    ConnectionRevision = connection.ConnectionRevision,
                    AddedAt = now
                });
            }
            else
            {
                existing.AccountLogin = installation.AccountLogin;
                existing.AccountType = installation.AccountType;
                existing.RepositorySelection = installation.RepositorySelection;
                existing.ConnectionRevision = connection.ConnectionRevision;
                existing.RevokedAt = null;
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RepoAppConnectionRecord> GetConnectionAsync(
        Guid ownerId,
        CancellationToken cancellationToken) =>
        await _db.RepoAppConnections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OwnerId == ownerId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.NotConnected);

    private async Task<RepoAppConnectionRecord> ReloadConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken) =>
        await _db.RepoAppConnections.AsNoTracking()
            .SingleAsync(item => item.ConnectionId == connectionId, cancellationToken)
            .ConfigureAwait(false);

    private async Task<RepoAppConnectionRecord> EnsureConnectionCurrentAsync(
        RepoAppConnectionRecord expected,
        CancellationToken cancellationToken)
    {
        var current = await ReloadConnectionAsync(expected.ConnectionId, cancellationToken).ConfigureAwait(false);
        if (current.OwnerId != expected.OwnerId ||
            current.ConnectionRevision != expected.ConnectionRevision ||
            current.State != RepoAppConnectionState.Connected)
            throw current.State == RepoAppConnectionState.Connected
                ? new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RepositoryUnavailable)
                : FailureForState(current.State);
        return current;
    }

    private async Task MarkConnectionStateAsync(
        RepoAppConnectionRecord expected,
        RepoAppConnectionState state,
        Guid? leaseId = null)
    {
        await _db.RepoAppConnections
            .Where(item =>
                item.ConnectionId == expected.ConnectionId &&
                item.OwnerId == expected.OwnerId &&
                item.ConnectionRevision == expected.ConnectionRevision &&
                item.CredentialRevision == expected.CredentialRevision &&
                item.State == RepoAppConnectionState.Connected &&
                (leaseId == null || item.RefreshLeaseId == leaseId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, state)
                .SetProperty(item => item.RefreshLeaseId, (Guid?)null)
                .SetProperty(item => item.RefreshLeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(item => item.UpdatedAt, _timeProvider.GetUtcNow()),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task ReleaseRefreshLeaseAsync(string connectionId, Guid leaseId)
    {
        await _db.RepoAppConnections
            .Where(item => item.ConnectionId == connectionId && item.RefreshLeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.RefreshLeaseId, (Guid?)null)
                .SetProperty(item => item.RefreshLeaseExpiresAt, (DateTimeOffset?)null),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static GitHubRepoAppConnectionException FailureForState(RepoAppConnectionState state) =>
        state switch
        {
            RepoAppConnectionState.Revoked =>
                new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.Revoked),
            RepoAppConnectionState.RotationUncertain =>
                new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.RotationUncertain),
            _ => new GitHubRepoAppConnectionException(GitHubRepoAppConnectionFailure.NotConnected)
        };

    private static GitHubRepoAppConnectionException MapProviderFailure(
        GitHubRepoAppProviderException error) =>
        new(error.Failure switch
        {
            GitHubRepoAppProviderFailure.Revoked => GitHubRepoAppConnectionFailure.Revoked,
            GitHubRepoAppProviderFailure.ProviderUnavailable or
                GitHubRepoAppProviderFailure.RateLimited or
                GitHubRepoAppProviderFailure.OutcomeUncertain =>
                GitHubRepoAppConnectionFailure.ProviderUnavailable,
            _ => GitHubRepoAppConnectionFailure.RepositoryUnavailable
        });

    private static string TokenSecretId(string connectionId, string kind) =>
        $"agentweaver-gh-app-{connectionId}-{kind}";

    private static string NewOpaqueValue() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string NewSelectionCode() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool TryHashOpaque(string? value, out string hash)
    {
        hash = string.Empty;
        if (value is not { Length: 43 or 64 } ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            return false;
        hash = Hash(value);
        return true;
    }

    private static bool IsSha256Hash(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool FixedTimeHashEquals(string left, string right)
    {
        try
        {
            var leftBytes = Convert.FromHexString(left);
            var rightBytes = Convert.FromHexString(right);
            return leftBytes.Length == rightBytes.Length &&
                CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
