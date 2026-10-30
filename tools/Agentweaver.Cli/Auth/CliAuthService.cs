using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Cli.Vault;

namespace Agentweaver.Cli.Auth;

public sealed class CliAuthService : ICliAuthentication, IDisposable
{
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient;
    private readonly ICliCredentialVault _vault;
    private readonly ICliSystemBrowser _browser;
    private readonly IOAuthCallbackListenerFactory _callbackFactory;
    private readonly CliCredentialLock _credentialLock;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _callbackTimeout;
    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);
    private int _disposed;

    public static ICliAuthentication CreateDefault(HttpClient brokerHttpClient)
    {
        ArgumentNullException.ThrowIfNull(brokerHttpClient);
        brokerHttpClient.Timeout = RequestTimeout;
        // Default sign-in requires the platform vault; never fall back to writing refresh tokens to disk.
        return new CliAuthService(
            brokerHttpClient,
            NativeCredentialVault.CreateDefault(),
            new SystemBrowser(),
            new OAuthCallbackListenerFactory(),
            new CliCredentialLock());
    }

    internal CliAuthService(
        HttpClient httpClient,
        ICliCredentialVault vault,
        ICliSystemBrowser browser,
        IOAuthCallbackListenerFactory callbackFactory,
        CliCredentialLock credentialLock,
        TimeProvider? timeProvider = null,
        TimeSpan? callbackTimeout = null)
    {
        if (callbackTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(callbackTimeout));
        _httpClient = httpClient;
        _vault = vault;
        _browser = browser;
        _callbackFactory = callbackFactory;
        _credentialLock = credentialLock;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _callbackTimeout = callbackTimeout ?? CallbackTimeout;
    }

    public async Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct)
    {
        ThrowIfDisposed();
        var target = CliAuthIdentity.Create(identity);
        return await _credentialLock.RunAsync(target.Account, async (intent, lockCt) =>
        {
            RecoverInterruptedRefresh(target.Account, intent, proceedToLogin: true);
            var metadata = await OidcProtocol.DiscoverAsync(_httpClient, target.Issuer, lockCt)
                .ConfigureAwait(false);
            // These independent random values bind the callback, ID token, and code exchange to this browser attempt.
            var state = RandomBase64Url(32);
            var nonce = RandomBase64Url(32);
            var verifier = RandomBase64Url(48);
            var verifierBytes = Encoding.ASCII.GetBytes(verifier);
            var challengeBytes = SHA256.HashData(verifierBytes);
            var challenge = Base64Url(challengeBytes);
            CryptographicOperations.ZeroMemory(verifierBytes);
            CryptographicOperations.ZeroMemory(challengeBytes);
            await using var callback = _callbackFactory.Start();
            using var callbackCts = CancellationTokenSource.CreateLinkedTokenSource(lockCt);
            callbackCts.CancelAfter(_callbackTimeout);
            var callbackTask = callback.WaitAsync(state, metadata.Issuer, callbackCts.Token);
            try
            {
                var authorizationUri = OidcProtocol.CreateAuthorizationUri(
                    metadata, target, callback.RedirectUri, state, nonce, challenge);
                await _browser.OpenAsync(authorizationUri, callbackCts.Token).ConfigureAwait(false);

                OAuthCallback result;
                try
                {
                    result = await callbackTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!lockCt.IsCancellationRequested)
                {
                    throw new TimeoutException("Timed out waiting for the localhost sign-in callback.");
                }
                if (result.Error is not null)
                    throw new InvalidOperationException(result.Error == "access_denied"
                        ? "Sign-in was canceled or access was denied."
                        : "The Identity Broker could not complete sign-in.");
                var code = result.Code
                    ?? throw new InvalidOperationException("The sign-in callback did not contain an authorization code.");

                var token = await OidcProtocol.ExchangeCodeAsync(
                    _httpClient, metadata, target, callback.RedirectUri, code, verifier, lockCt)
                    .ConfigureAwait(false);
                if (token.RefreshToken is null)
                    throw new InvalidOperationException(
                        "The Identity Broker did not issue a refresh token; check the CLI offline_access registration.");
                if (token.IdToken is null)
                    throw new InvalidOperationException("The Identity Broker did not issue a validated identity token.");

                // The validated ID token identifies the actor; it is not used as a Gateway authorization decision.
                var actorId = await OidcProtocol.ValidateIdTokenAsync(
                    _httpClient, metadata, token.IdToken, target.Identity, nonce, _timeProvider.GetUtcNow(), lockCt)
                    .ConfigureAwait(false);
                lockCt.ThrowIfCancellationRequested();
                var session = CreateSession(token, actorId);

                try
                {
                    _vault.Set(target.Account, token.RefreshToken);
                }
                catch (Exception saveError)
                {
                    _tokens.TryRemove(target.Account, out _);
                    try
                    {
                        _vault.Delete(target.Account);
                    }
                    catch (Exception cleanupError)
                    {
                        throw new CliAuthCleanupException(
                            "The OS vault could not save or clear the sign-in. Resolve the vault error before retrying login.",
                            cleanupError);
                    }
                    throw new InvalidOperationException("The OS vault could not save the sign-in. Run login again.", saveError);
                }

                _tokens[target.Account] = session;
                return Status(target, isSignedIn: true, actorId);
            }
            finally
            {
                callbackCts.Cancel();
                await IgnoreCallbackFailureAsync(callbackTask).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct)
    {
        ThrowIfDisposed();
        var target = CliAuthIdentity.Create(identity);
        return await _credentialLock.RunAsync(target.Account, (intent, _) =>
        {
            if (RecoverInterruptedRefresh(target.Account, intent, proceedToLogin: false))
                return Task.FromResult(Status(target, isSignedIn: false, actorId: null));

            var refreshToken = ReadRefreshToken(target.Account);
            var signedIn = refreshToken is not null;
            var actorId = signedIn && _tokens.TryGetValue(target.Account, out var cached)
                ? cached.ActorId
                : null;
            return Task.FromResult(Status(target, signedIn, actorId));
        }, ct).ConfigureAwait(false);
    }

    public async Task LogoutAsync(CliIdentity identity, CancellationToken ct)
    {
        ThrowIfDisposed();
        var target = CliAuthIdentity.Create(identity);
        await _credentialLock.RunAsync(target.Account, (intent, lockCt) =>
        {
            lockCt.ThrowIfCancellationRequested();
            _tokens.TryRemove(target.Account, out _);
            try
            {
                _vault.Delete(target.Account);
            }
            catch (Exception exception)
            {
                throw new CliAuthCleanupException(
                    "The OS vault could not remove the selected CLI sign-in. Resolve the vault error before retrying logout.",
                    exception);
            }

            try
            {
                intent.Clear();
            }
            catch (Exception exception)
            {
                throw new CliAuthCleanupException(
                    "The local sign-in was removed, but refresh-recovery state could not be cleared.",
                    exception);
            }
            return Task.FromResult(true);
        }, ct).ConfigureAwait(false);
    }

    public async Task<string> GetBearerAsync(
        CliIdentity identity,
        InteractionPolicy interaction,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        if (interaction is not (InteractionPolicy.Never or InteractionPolicy.Allow))
            throw new ArgumentOutOfRangeException(nameof(interaction));
        var target = CliAuthIdentity.Create(identity);
        if (GetCachedToken(target.Account) is { } cached)
            return cached;

        try
        {
            return await _credentialLock.RunAsync(target.Account, async (intent, lockCt) =>
            {
                if (RecoverInterruptedRefresh(target.Account, intent, proceedToLogin: false))
                    throw new CliAuthNeedsLoginException(
                        "A previous refresh was interrupted. The local sign-in was cleared; run login again.");
                if (GetCachedToken(target.Account) is { } current)
                    return current;

                var refreshToken = ReadRefreshToken(target.Account)
                    ?? throw new CliAuthNeedsLoginException(
                        "Not signed in for this Broker, Gateway, tenant, scope, and selection. Run login first.");
                if (!_tokens.TryGetValue(target.Account, out var session))
                    session = new CachedToken(string.Empty, DateTimeOffset.MinValue, null);
                return await RefreshAsync(target, intent, refreshToken, session, lockCt).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (CliAuthNeedsLoginException) when (interaction == InteractionPolicy.Allow && !ct.IsCancellationRequested)
        {
            await LoginAsync(target.Identity, ct).ConfigureAwait(false);
            return await GetBearerAsync(target.Identity, InteractionPolicy.Never, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _tokens.Clear();
    }

    private async Task<string> RefreshAsync(
        CliAuthIdentity target,
        CliCredentialLock.RefreshIntent intent,
        string refreshToken,
        CachedToken priorSession,
        CancellationToken ct)
    {
        try
        {
            // Persist recovery intent before sending the rotating token: a crash afterward must never replay it.
            intent.Mark();
        }
        catch (Exception exception)
        {
            throw new CliAuthCleanupException(
                "The CLI could not record refresh recovery state. The saved sign-in was not used.",
                exception);
        }

        OAuthTokenResponse token;
        try
        {
            var metadata = await OidcProtocol.DiscoverAsync(_httpClient, target.Issuer, ct).ConfigureAwait(false);
            token = await OidcProtocol.RefreshAsync(
                _httpClient, metadata, target, refreshToken, ct).ConfigureAwait(false);
            if (token.RefreshToken is not { Length: > 0 } replacement ||
                string.Equals(replacement, refreshToken, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The Identity Broker did not return a replacement refresh token.");
        }
        catch (Exception exception)
        {
            // A transport failure can happen after token rotation, so clear the old credential instead of replaying it.
            _tokens.TryRemove(target.Account, out _);
            ClearCredentialAndIntent(target.Account, intent,
                "The refresh outcome is uncertain and the OS vault could not clear the saved sign-in. Do not retry; resolve the vault error before signing in again.");
            if (exception is OperationCanceledException && ct.IsCancellationRequested)
                throw;
            throw new CliAuthNeedsLoginException(
                "The refresh outcome is uncertain; the local sign-in was cleared. Run login again.", exception);
        }

        try
        {
            _vault.Set(target.Account, token.RefreshToken!);
            intent.Clear();
        }
        catch (Exception exception)
        {
            _tokens.TryRemove(target.Account, out _);
            ClearCredentialAndIntent(target.Account, intent,
                "The rotated refresh token could not be safely stored or cleared. Do not retry; resolve the vault error before signing in again.");
            throw new CliAuthNeedsLoginException(
                "The refresh could not be committed; the local sign-in was cleared. Run login again.", exception);
        }

        if (ct.IsCancellationRequested)
        {
            _tokens.TryRemove(target.Account, out _);
            throw new OperationCanceledException(ct);
        }

        var session = CreateSession(token, priorSession.ActorId);
        _tokens[target.Account] = session;
        return session.AccessToken;
    }

    private void ClearCredentialAndIntent(
        string account,
        CliCredentialLock.RefreshIntent intent,
        string cleanupFailure)
    {
        try
        {
            _vault.Delete(account);
        }
        catch (Exception exception)
        {
            throw new CliAuthCleanupException(cleanupFailure, exception);
        }

        try
        {
            intent.Clear();
        }
        catch (Exception exception)
        {
            throw new CliAuthCleanupException(cleanupFailure, exception);
        }
    }

    private bool RecoverInterruptedRefresh(
        string account,
        CliCredentialLock.RefreshIntent intent,
        bool proceedToLogin)
    {
        if (!intent.Exists()) return false;
        // The Broker may have consumed the old token before a crash; discard it instead of guessing or retrying.
        _tokens.TryRemove(account, out _);
        try
        {
            _vault.Delete(account);
        }
        catch (Exception exception)
        {
            throw new CliAuthCleanupException(
                "An interrupted refresh may have consumed the saved token, and the OS vault could not clear it. Do not retry; resolve the vault error before signing in again.",
                exception);
        }
        try
        {
            intent.Clear();
        }
        catch (Exception exception)
        {
            throw new CliAuthCleanupException(
                "The saved sign-in was removed, but refresh-recovery state could not be cleared.",
                exception);
        }
        return !proceedToLogin;
    }

    private string? ReadRefreshToken(string account)
    {
        var refreshToken = _vault.Get(account);
        if (refreshToken is null) return null;
        if (IsOpaqueToken(refreshToken)) return refreshToken;

        _tokens.TryRemove(account, out _);
        try
        {
            _vault.Delete(account);
        }
        catch (Exception exception)
        {
            throw new CliAuthCleanupException(
                "The saved CLI sign-in is invalid and the OS vault could not clear it.",
                exception);
        }
        return null;
    }

    private string? GetCachedToken(string account)
    {
        if (!_tokens.TryGetValue(account, out var token)) return null;
        if (token.ExpiresAt > _timeProvider.GetUtcNow()) return token.AccessToken;
        ((ICollection<KeyValuePair<string, CachedToken>>)_tokens)
            .Remove(new KeyValuePair<string, CachedToken>(account, token));
        return null;
    }

    private CachedToken CreateSession(OAuthTokenResponse token, string? actorId)
    {
        var lifetime = TimeSpan.FromSeconds(token.ExpiresIn);
        var margin = TimeSpan.FromSeconds(Math.Min(60, Math.Floor(token.ExpiresIn / 10)));
        return new CachedToken(token.AccessToken, _timeProvider.GetUtcNow() + lifetime - margin, actorId);
    }

    private static CliAuthStatus Status(CliAuthIdentity identity, bool isSignedIn, string? actorId) =>
        new(isSignedIn, actorId, identity.Identity);

    private static string RandomBase64Url(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        var value = Base64Url(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return value;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsOpaqueToken(string value) =>
        value.Length is > 0 and <= 16 * 1024 &&
        value.All(character => character is > (char)0x20 and < (char)0x7f);

    private static async Task IgnoreCallbackFailureAsync(Task callbackTask)
    {
        try
        {
            await callbackTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt, string? ActorId);
}

internal sealed class CliAuthNeedsLoginException : InvalidOperationException
{
    public CliAuthNeedsLoginException(string message) : base(message) { }
    public CliAuthNeedsLoginException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed class CliAuthCleanupException : InvalidOperationException
{
    public CliAuthCleanupException(string message, Exception innerException) : base(message, innerException) { }
}
