using System.Text.Json;
using Agentweaver.Domain;
using Azure;
using Azure.Security.KeyVault.Secrets;

namespace Agentweaver.AgentHost;

/// <summary>
/// Fetches the run owner's GitHub token from Azure Key Vault at configure-time using the pod's
/// workload identity (Option C, warm-pool path). The secret name is delivered via the
/// <c>POST /configure</c> call and stored on <see cref="AgentHostRuntimeState.KvUserSecretName"/>.
///
/// <para>
/// The secret value is the same <see cref="StoredCredential"/> JSON the API writes
/// (<c>KeyVaultGitHubTokenStore</c> / <c>FileSystemGitHubTokenStore</c>), so deserialization mirrors
/// <see cref="SharedHomeGitHubTokenStore"/>. The token is cached in memory for the pod lifetime — a
/// run's token does not change mid-run, and the pod hosts exactly one run.
/// </para>
///
/// <para>
/// Security: the pod fetches ONLY the single secret name it was configured with — it can never read
/// another user's token. The fetch fails closed (returns null) when no secret name is configured.
/// </para>
/// </summary>
internal sealed class KeyVaultUserTokenProvider
{
    private readonly SecretClient _client;
    private readonly AgentHostRuntimeState _runtimeState;
    private readonly ILogger<KeyVaultUserTokenProvider>? _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private StoredCredential? _cached;
    private bool _fetched;

    public KeyVaultUserTokenProvider(
        SecretClient client,
        AgentHostRuntimeState runtimeState,
        ILogger<KeyVaultUserTokenProvider>? logger = null)
    {
        _client = client;
        _runtimeState = runtimeState;
        _logger = logger;
    }

    /// <summary>
    /// Fetches (once, then cached) and returns the stored credential, or null when absent / not yet
    /// configured / malformed.
    /// </summary>
    public async Task<StoredCredential?> GetStoredCredentialAsync(CancellationToken ct = default)
    {
        if (_fetched)
            return _cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_fetched)
                return _cached;

            // Fast path: the API pre-resolved the token and passed it in /configure.
            // Skip the KV call entirely — the pod has no outbound access to Azure AD or KV.
            var preResolved = _runtimeState.GitHubAccessToken;
            if (!string.IsNullOrWhiteSpace(preResolved))
            {
                _logger?.LogInformation(
                    "KeyVaultUserTokenProvider: using pre-resolved GitHubAccessToken from /configure; skipping Key Vault fetch.");
                _cached = new StoredCredential { Status = "signed-in", AccessToken = preResolved };
                _fetched = true;
                return _cached;
            }

            var secretName = _runtimeState.KvUserSecretName;
            if (string.IsNullOrWhiteSpace(secretName))
            {
                _logger?.LogWarning(
                    "KeyVaultUserTokenProvider: no KvUserSecretName configured — cannot fetch user token.");
                _fetched = true;
                _cached = null;
                return null;
            }

            try
            {
                var response = await _client.GetSecretAsync(secretName, cancellationToken: ct).ConfigureAwait(false);
                _cached = JsonSerializer.Deserialize<StoredCredential>(response.Value.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger?.LogWarning(
                    "KeyVaultUserTokenProvider: secret {Secret} not found in Key Vault.", secretName);
                _cached = null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "KeyVaultUserTokenProvider: failed to fetch/parse secret {Secret}.", secretName);
                _cached = null;
            }

            _fetched = true;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Mirrors the on-disk / KV JSON shape written by the API token stores.
    internal sealed record StoredCredential
    {
        public string? Status { get; init; }
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public string? Login { get; init; }
        public string? AvatarUrl { get; init; }
        public string[]? Scopes { get; init; }
    }
}

/// <summary>
/// Read-only <see cref="IGitHubTokenStore"/> for the Option C warm-pool path: serves the single
/// run-owner token fetched from Key Vault by <see cref="KeyVaultUserTokenProvider"/>. The pod hosts
/// one run for one user, so all scopes resolve to that one token. Mutations are no-ops.
/// </summary>
internal sealed class KeyVaultGitHubTokenStore : IGitHubTokenStore
{
    private readonly KeyVaultUserTokenProvider _provider;

    public KeyVaultGitHubTokenStore(KeyVaultUserTokenProvider provider) => _provider = provider;

    public async Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var stored = await _provider.GetStoredCredentialAsync(ct).ConfigureAwait(false);
        if (stored is null)
            return new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null);
        if (stored.Status == "signed-out")
            return new GitHubTokenEntry(GitHubTokenStatus.SignedOut, null);
        if (!string.IsNullOrEmpty(stored.AccessToken))
            return new GitHubTokenEntry(GitHubTokenStatus.SignedIn, stored.AccessToken);
        return new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null);
    }

    public async Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var stored = await _provider.GetStoredCredentialAsync(ct).ConfigureAwait(false);
        if (stored?.Status == "signed-in" && !string.IsNullOrEmpty(stored.AccessToken))
            return new GitHubToken(
                stored.AccessToken,
                stored.RefreshToken,
                stored.ExpiresAt,
                stored.Login ?? "unknown",
                stored.AvatarUrl,
                stored.Scopes ?? []);
        return null;
    }

    public async Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var stored = await _provider.GetStoredCredentialAsync(ct).ConfigureAwait(false);
        return stored?.Login is not null ? new GitHubIdentity(stored.Login, stored.AvatarUrl) : null;
    }

    // Pod is a read-only consumer — never mutate the user's credentials.
    public Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>
/// Token-scope provider for the warm-pool path: resolves the per-user scope from the runtime state
/// populated by <c>POST /configure</c> (falls back to the runtime-supplied user id, then installation).
/// </summary>
internal sealed class RuntimeUserScopeProvider : IGitHubTokenScopeProvider
{
    private readonly AgentHostRuntimeState _runtimeState;

    public RuntimeUserScopeProvider(AgentHostRuntimeState runtimeState) => _runtimeState = runtimeState;

    public GitHubTokenScope Resolve(string? userId)
    {
        var effective = !string.IsNullOrWhiteSpace(_runtimeState.UserId)
            ? _runtimeState.UserId
            : (string.IsNullOrWhiteSpace(userId) ? null : userId);
        return effective is not null ? GitHubTokenScope.ForUser(effective) : GitHubTokenScope.Installation;
    }
}
