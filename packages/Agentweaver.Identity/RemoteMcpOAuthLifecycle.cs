using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

public sealed class RemoteMcpOAuthConnectionBinding
{
    public const string SupportedTransportProfile = "mcp-2025-06-18";

    public RemoteMcpOAuthConnectionBinding(
        string humanId,
        string tenantId,
        string projectId,
        Guid connectionId,
        long configurationRevision,
        string environmentConfigurationHash,
        string identityBindingReference,
        Uri endpoint,
        Uri resource,
        Uri issuer,
        Uri redirectUri,
        string transportProfile,
        IEnumerable<string> scopes)
    {
        HumanId = GrantIdentifier.Validate(humanId, nameof(humanId));
        TenantId = GrantIdentifier.Validate(tenantId, nameof(tenantId));
        ProjectId = GrantIdentifier.Validate(projectId, nameof(projectId));
        if (connectionId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(connectionId));
        if (configurationRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(configurationRevision));
        RuntimeContractValidation.ValidateHash(environmentConfigurationHash);
        if (!Guid.TryParseExact(identityBindingReference, "N", out var parsedBindingReference) ||
            parsedBindingReference == Guid.Empty)
            throw new ArgumentException("Expected an Identity-issued opaque UUID-N reference.", nameof(identityBindingReference));

        ConnectionId = connectionId;
        ConfigurationRevision = configurationRevision;
        EnvironmentConfigurationHash = environmentConfigurationHash;
        IdentityBindingReference = identityBindingReference;
        Endpoint = ValidateHttpsUri(endpoint, nameof(endpoint));
        Resource = ValidateHttpsUri(resource, nameof(resource));
        Issuer = ValidateHttpsUri(issuer, nameof(issuer));
        RedirectUri = ValidateHttpsUri(redirectUri, nameof(redirectUri));
        if (transportProfile != SupportedTransportProfile)
            throw new ArgumentException("Only the installed MCP 2025-06-18 transport profile is supported.", nameof(transportProfile));
        TransportProfile = transportProfile;

        ArgumentNullException.ThrowIfNull(scopes);
        var values = scopes.ToArray();
        if (values.Length == 0 || values.Any(scope => !IsScopeToken(scope)) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Scopes must be a nonempty set of valid OAuth scope tokens.", nameof(scopes));

        Scopes = Array.AsReadOnly(values.Order(StringComparer.Ordinal).ToArray());
        BindingHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            HumanId,
            TenantId,
            ProjectId,
            ConnectionId,
            ConfigurationRevision,
            EnvironmentConfigurationHash,
            IdentityBindingReference,
            Endpoint = Endpoint.AbsoluteUri,
            Resource = Resource.AbsoluteUri,
            Issuer = Issuer.AbsoluteUri,
            RedirectUri = RedirectUri.AbsoluteUri,
            TransportProfile,
            Scopes
        }))).ToLowerInvariant();
    }

    public string HumanId { get; }
    public string TenantId { get; }
    public string ProjectId { get; }
    public Guid ConnectionId { get; }
    public long ConfigurationRevision { get; }
    public string EnvironmentConfigurationHash { get; }
    public string IdentityBindingReference { get; }
    public Uri Endpoint { get; }
    public Uri Resource { get; }
    public Uri Issuer { get; }
    public Uri RedirectUri { get; }
    public string TransportProfile { get; }
    public IReadOnlyList<string> Scopes { get; }
    public string BindingHash { get; }

    public bool Matches(RemoteMcpOAuthConnectionBinding? other) =>
        other is not null && string.Equals(BindingHash, other.BindingHash, StringComparison.Ordinal);

    public override string ToString() => nameof(RemoteMcpOAuthConnectionBinding) + " [REDACTED]";

    private static Uri ValidateHttpsUri(Uri uri, string name)
    {
        ArgumentNullException.ThrowIfNull(uri, name);
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(uri.OriginalString, uri.AbsoluteUri, StringComparison.Ordinal))
            throw new ArgumentException("Expected a canonical absolute HTTPS URI without user info or fragment.", name);
        return uri;
    }

    private static bool IsScopeToken(string? scope) =>
        !string.IsNullOrEmpty(scope) &&
        scope.All(character =>
            character is >= '\x21' and <= '\x7e' &&
            character is not ('"' or '\\'));
}

public enum RemoteMcpOAuthConnectionState
{
    NotConnected,
    PendingConsent,
    Authorized,
    RefreshInProgress,
    RefreshIndeterminate,
    Disconnected,
    Revoked
}

public sealed class RemoteMcpOAuthConnection
{
    public RemoteMcpOAuthConnection(
        RemoteMcpOAuthConnectionBinding binding,
        long revision,
        long credentialRevision,
        RemoteMcpOAuthConnectionState state,
        SecretRef? accessTokenReference = null,
        SecretRef? refreshTokenReference = null,
        DateTimeOffset? accessTokenExpiresAt = null,
        Guid? refreshAttemptId = null,
        DateTimeOffset? refreshStartedAt = null,
        DateTimeOffset? refreshTokenExpiresAt = null)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));
        if (credentialRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(credentialRevision));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));

        var hasCredential = accessTokenReference is not null ||
            refreshTokenReference is not null || accessTokenExpiresAt is not null ||
            refreshTokenExpiresAt is not null;
        var hasRefreshAttempt = refreshAttemptId is not null || refreshStartedAt is not null;
        switch (state)
        {
            case RemoteMcpOAuthConnectionState.NotConnected:
            case RemoteMcpOAuthConnectionState.PendingConsent:
            case RemoteMcpOAuthConnectionState.Disconnected:
            case RemoteMcpOAuthConnectionState.Revoked:
                if (hasCredential || hasRefreshAttempt)
                    throw new ArgumentException("This connection state cannot carry current credentials or a refresh attempt.");
                break;
            case RemoteMcpOAuthConnectionState.Authorized:
                if (accessTokenReference is null || refreshAttemptId is not null || refreshStartedAt is not null ||
                    accessTokenExpiresAt is null ||
                    (refreshTokenReference is null && refreshTokenExpiresAt is not null))
                    throw new ArgumentException("An authorized connection requires an access-token reference and expiry only.");
                break;
            case RemoteMcpOAuthConnectionState.RefreshInProgress:
            case RemoteMcpOAuthConnectionState.RefreshIndeterminate:
                if (refreshTokenReference is null || refreshAttemptId is null || refreshStartedAt is null ||
                    accessTokenReference is null || accessTokenExpiresAt is null)
                    throw new ArgumentException("A refresh attempt requires current token references, expiry, and attempt metadata.");
                if (refreshTokenReference is null && refreshTokenExpiresAt is not null)
                    throw new ArgumentException("A refresh-token expiry requires a refresh-token reference.");
                break;
        }

        Revision = revision;
        CredentialRevision = credentialRevision;
        State = state;
        AccessTokenReference = accessTokenReference;
        RefreshTokenReference = refreshTokenReference;
        AccessTokenExpiresAt = accessTokenExpiresAt;
        RefreshAttemptId = refreshAttemptId;
        RefreshStartedAt = refreshStartedAt;
        RefreshTokenExpiresAt = refreshTokenExpiresAt;
    }

    public RemoteMcpOAuthConnectionBinding Binding { get; }
    public long Revision { get; }
    public long CredentialRevision { get; }
    public RemoteMcpOAuthConnectionState State { get; }
    public SecretRef? AccessTokenReference { get; }
    public SecretRef? RefreshTokenReference { get; }
    public DateTimeOffset? AccessTokenExpiresAt { get; }
    public Guid? RefreshAttemptId { get; }
    public DateTimeOffset? RefreshStartedAt { get; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; }

    public override string ToString() => nameof(RemoteMcpOAuthConnection) + " [REDACTED]";
}

public enum RemoteMcpOAuthConsentState
{
    Pending,
    CallbackClaimed,
    Consumed,
    Denied,
    Superseded
}

public enum RemoteMcpOAuthRequestFailure
{
    DefinitelyNotSent,
    PossiblySent,
    ProviderRejected
}

public sealed class RemoteMcpOAuthPendingConsent
{
    public RemoteMcpOAuthPendingConsent(
        Guid correlationId,
        string bindingHash,
        long connectionRevision,
        string stateHash,
        string pkceChallenge,
        SecretRef protectedVerifierReference,
        long revision,
        RemoteMcpOAuthConsentState state,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        Guid? claimAttemptId = null)
    {
        if (correlationId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(correlationId));
        RuntimeContractValidation.ValidateHash(bindingHash);
        RuntimeContractValidation.ValidateHash(stateHash);
        ValidateChallenge(pkceChallenge);
        if (connectionRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(connectionRevision));
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        ProtectedVerifierReference = protectedVerifierReference ??
            throw new ArgumentNullException(nameof(protectedVerifierReference));
        if (expiresAt <= createdAt)
            throw new ArgumentOutOfRangeException(nameof(expiresAt));
        if ((state == RemoteMcpOAuthConsentState.CallbackClaimed) != (claimAttemptId is not null))
            throw new ArgumentException("Only a claimed callback can have a claim attempt ID.", nameof(claimAttemptId));

        CorrelationId = correlationId;
        BindingHash = bindingHash;
        ConnectionRevision = connectionRevision;
        StateHash = stateHash;
        PkceChallenge = pkceChallenge;
        Revision = revision;
        State = state;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        ClaimAttemptId = claimAttemptId;
    }

    public Guid CorrelationId { get; }
    public string BindingHash { get; }
    public long ConnectionRevision { get; }
    public string StateHash { get; }
    public string PkceChallenge { get; }
    public SecretRef ProtectedVerifierReference { get; }
    public long Revision { get; }
    public RemoteMcpOAuthConsentState State { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public Guid? ClaimAttemptId { get; }

    public override string ToString() => nameof(RemoteMcpOAuthPendingConsent) + " [REDACTED]";

    private static void ValidateChallenge(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 43 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("Expected a canonical S256 PKCE challenge.", nameof(value));
        var decoded = FromBase64Url(value);
        if (decoded.Length != 32 || !string.Equals(ToBase64Url(decoded), value, StringComparison.Ordinal))
            throw new ArgumentException("Expected a canonical S256 PKCE challenge.", nameof(value));
    }

    internal static string ToBase64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        return Convert.FromBase64String(base64);
    }
}

public sealed class RemoteMcpOAuthConsentMaterial
{
    private readonly RemoteMcpOAuthConnectionBinding _binding;

    private RemoteMcpOAuthConsentMaterial(
        RemoteMcpOAuthConnectionBinding binding,
        Guid correlationId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        string state,
        string verifier,
        TimeProvider timeProvider)
    {
        _binding = binding;
        CorrelationId = correlationId;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        State = new SecretCredential(state, expiresAt, timeProvider);
        PkceVerifier = new SecretCredential(verifier, expiresAt, timeProvider);
        PkceChallenge = RemoteMcpOAuthPendingConsent.ToBase64Url(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    public Guid CorrelationId { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    [JsonIgnore]
    public SecretCredential State { get; }
    [JsonIgnore]
    public SecretCredential PkceVerifier { get; }
    public string PkceChallenge { get; }

    public static RemoteMcpOAuthConsentMaterial Create(
        RemoteMcpOAuthConnectionBinding binding,
        Guid correlationId,
        DateTimeOffset expiresAt,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (correlationId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(correlationId));

        var clock = timeProvider ?? TimeProvider.System;
        var now = clock.GetUtcNow();
        if (expiresAt <= now)
            throw new ArgumentOutOfRangeException(nameof(expiresAt));

        return new RemoteMcpOAuthConsentMaterial(
            binding, correlationId, now, expiresAt,
            NewVerifier(), NewVerifier(), clock);
    }

    public RemoteMcpOAuthPendingConsent CreatePendingConsent(
        long connectionRevision,
        SecretRef protectedVerifierReference,
        long revision = 1)
    {
        var stateHash = Convert.ToHexString(
            SHA256.HashData(Encoding.ASCII.GetBytes(State.GetValue()))).ToLowerInvariant();
        return new RemoteMcpOAuthPendingConsent(
            CorrelationId, _binding.BindingHash, connectionRevision, stateHash, PkceChallenge,
            protectedVerifierReference, revision, RemoteMcpOAuthConsentState.Pending,
            CreatedAt, ExpiresAt);
    }

    public override string ToString() => nameof(RemoteMcpOAuthConsentMaterial) + " [REDACTED]";

    internal bool IsFor(RemoteMcpOAuthConnectionBinding binding) =>
        _binding.Matches(binding) && State.IsUsable() && PkceVerifier.IsUsable();

    private static string NewVerifier() =>
        RemoteMcpOAuthPendingConsent.ToBase64Url(RandomNumberGenerator.GetBytes(32));
}

public sealed record RemoteMcpOAuthConsentStart(
    long ExpectedConnectionRevision,
    RemoteMcpOAuthConnection Connection,
    RemoteMcpOAuthPendingConsent Consent,
    RemoteMcpOAuthConsentMaterial Material);

public sealed record RemoteMcpOAuthRefreshClaim(
    Guid ConnectionId,
    string BindingHash,
    long ConnectionRevision,
    long CredentialRevision,
    Guid AttemptId,
    SecretRef RefreshTokenReference);

public sealed record RemoteMcpOAuthCallbackCompletion(
    RemoteMcpOAuthConnection Connection,
    RemoteMcpOAuthPendingConsent Consent);

// These transitions are pure. Their owner must persist each returned snapshot
// with an atomic revision compare-and-swap; a SecretRef write is not that CAS.
public static class RemoteMcpOAuthLifecycle
{
    public static RemoteMcpOAuthConsentStart? TryBeginConsent(
        RemoteMcpOAuthConnection current,
        RemoteMcpOAuthConnectionBinding currentBinding,
        long expectedConnectionRevision,
        RemoteMcpOAuthConsentMaterial material,
        SecretRef protectedVerifierReference)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(currentBinding);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(protectedVerifierReference);
        if (current.Revision != expectedConnectionRevision ||
            !current.Binding.Matches(currentBinding) ||
            !material.IsFor(currentBinding) ||
            current.State == RemoteMcpOAuthConnectionState.RefreshInProgress ||
            current.State == RemoteMcpOAuthConnectionState.PendingConsent)
            return null;

        var nextRevision = checked(current.Revision + 1);
        var nextCredentialRevision = checked(current.CredentialRevision + 1);
        var connection = new RemoteMcpOAuthConnection(
            currentBinding, nextRevision, nextCredentialRevision,
            RemoteMcpOAuthConnectionState.PendingConsent);
        var consent = material.CreatePendingConsent(nextRevision, protectedVerifierReference);
        if (!string.Equals(consent.BindingHash, currentBinding.BindingHash, StringComparison.Ordinal))
            return null;
        return new RemoteMcpOAuthConsentStart(current.Revision, connection, consent, material);
    }

    public static RemoteMcpOAuthPendingConsent? TryClaimCallback(
        RemoteMcpOAuthPendingConsent consent,
        RemoteMcpOAuthConnection connection,
        RemoteMcpOAuthConnectionBinding currentBinding,
        string presentedState,
        Guid claimAttemptId,
        long expectedConsentRevision,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(consent);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(currentBinding);
        if (claimAttemptId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(claimAttemptId));
        if (consent.Revision != expectedConsentRevision ||
            consent.State != RemoteMcpOAuthConsentState.Pending ||
            consent.ExpiresAt <= now ||
            connection.Revision != consent.ConnectionRevision ||
            connection.State != RemoteMcpOAuthConnectionState.PendingConsent ||
            !BindingMatches(consent.BindingHash, connection, currentBinding) ||
            !MatchesState(presentedState, consent.StateHash))
            return null;

        return NewConsentRevision(consent, RemoteMcpOAuthConsentState.CallbackClaimed, claimAttemptId);
    }

    public static RemoteMcpOAuthCallbackCompletion? TryCompleteCallback(
        RemoteMcpOAuthPendingConsent consent,
        RemoteMcpOAuthConnection connection,
        RemoteMcpOAuthConnectionBinding currentBinding,
        Guid claimAttemptId,
        long expectedConsentRevision,
        SecretRef accessTokenReference,
        SecretRef? refreshTokenReference,
        DateTimeOffset accessTokenExpiresAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(consent);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(currentBinding);
        ArgumentNullException.ThrowIfNull(accessTokenReference);
        if (claimAttemptId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(claimAttemptId));
        if (!IsCurrentClaim(consent, connection, currentBinding, claimAttemptId,
                expectedConsentRevision) ||
            consent.ExpiresAt <= now ||
            accessTokenExpiresAt <= now)
            return null;

        var authorized = new RemoteMcpOAuthConnection(
            currentBinding, checked(connection.Revision + 1), checked(connection.CredentialRevision + 1),
            RemoteMcpOAuthConnectionState.Authorized, accessTokenReference, refreshTokenReference,
            accessTokenExpiresAt);
        return new RemoteMcpOAuthCallbackCompletion(
            authorized,
            NewConsentRevision(consent, RemoteMcpOAuthConsentState.Consumed, null));
    }

    public static RemoteMcpOAuthPendingConsent? TryResolveCallbackFailure(
        RemoteMcpOAuthPendingConsent consent,
        RemoteMcpOAuthConnection connection,
        RemoteMcpOAuthConnectionBinding currentBinding,
        Guid claimAttemptId,
        long expectedConsentRevision,
        RemoteMcpOAuthRequestFailure failure,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(consent);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(currentBinding);
        if (!Enum.IsDefined(failure))
            throw new ArgumentOutOfRangeException(nameof(failure));
        if (!IsCurrentClaim(consent, connection, currentBinding, claimAttemptId, expectedConsentRevision))
            return null;

        if (failure == RemoteMcpOAuthRequestFailure.DefinitelyNotSent && consent.ExpiresAt > now)
            return NewConsentRevision(consent, RemoteMcpOAuthConsentState.Pending, null);
        if (failure == RemoteMcpOAuthRequestFailure.ProviderRejected)
            return NewConsentRevision(consent, RemoteMcpOAuthConsentState.Denied, null);
        return NewConsentRevision(consent, RemoteMcpOAuthConsentState.Consumed, null);
    }

    public static RemoteMcpOAuthRefreshClaim? TryBeginRefresh(
        RemoteMcpOAuthConnection current,
        RemoteMcpOAuthConnectionBinding currentBinding,
        long expectedConnectionRevision,
        Guid attemptId,
        DateTimeOffset now,
        out RemoteMcpOAuthConnection? next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(currentBinding);
        if (attemptId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(attemptId));
        next = null;
        if (current.Revision != expectedConnectionRevision ||
            current.State != RemoteMcpOAuthConnectionState.Authorized ||
            current.RefreshTokenReference is null || current.AccessTokenReference is null ||
            current.AccessTokenExpiresAt is null ||
            (current.RefreshTokenExpiresAt is { } refreshExpiresAt && refreshExpiresAt <= now) ||
            !current.Binding.Matches(currentBinding))
            return null;

        var revision = checked(current.Revision + 1);
        next = new RemoteMcpOAuthConnection(
            currentBinding, revision, current.CredentialRevision,
            RemoteMcpOAuthConnectionState.RefreshInProgress,
            current.AccessTokenReference, current.RefreshTokenReference,
            current.AccessTokenExpiresAt, attemptId, now, current.RefreshTokenExpiresAt);
        return new RemoteMcpOAuthRefreshClaim(
            current.Binding.ConnectionId, currentBinding.BindingHash, revision,
            current.CredentialRevision, attemptId, current.RefreshTokenReference);
    }

    public static RemoteMcpOAuthConnection? TryCompleteRefresh(
        RemoteMcpOAuthConnection current,
        RemoteMcpOAuthConnectionBinding currentBinding,
        RemoteMcpOAuthRefreshClaim claim,
        long expectedConnectionRevision,
        SecretRef accessTokenReference,
        SecretRef? refreshTokenReference,
        DateTimeOffset accessTokenExpiresAt,
        DateTimeOffset now,
        DateTimeOffset? refreshTokenExpiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(currentBinding);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(accessTokenReference);
        if (!IsCurrentRefresh(current, currentBinding, claim, expectedConnectionRevision) ||
            accessTokenExpiresAt <= now)
            return null;

        return new RemoteMcpOAuthConnection(
            currentBinding, checked(current.Revision + 1), checked(current.CredentialRevision + 1),
            RemoteMcpOAuthConnectionState.Authorized, accessTokenReference,
            refreshTokenReference ?? current.RefreshTokenReference, accessTokenExpiresAt,
            refreshTokenExpiresAt: refreshTokenReference is null
                ? current.RefreshTokenExpiresAt
                : refreshTokenExpiresAt);
    }

    public static RemoteMcpOAuthConnection? TryResolveRefreshFailure(
        RemoteMcpOAuthConnection current,
        RemoteMcpOAuthConnectionBinding currentBinding,
        RemoteMcpOAuthRefreshClaim claim,
        long expectedConnectionRevision,
        RemoteMcpOAuthRequestFailure failure)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(currentBinding);
        ArgumentNullException.ThrowIfNull(claim);
        if (!Enum.IsDefined(failure))
            throw new ArgumentOutOfRangeException(nameof(failure));
        if (!IsCurrentRefresh(current, currentBinding, claim, expectedConnectionRevision))
            return null;

        if (failure == RemoteMcpOAuthRequestFailure.ProviderRejected)
            return new RemoteMcpOAuthConnection(
                currentBinding, checked(current.Revision + 1), checked(current.CredentialRevision + 1),
                RemoteMcpOAuthConnectionState.Revoked);

        var state = failure == RemoteMcpOAuthRequestFailure.DefinitelyNotSent
            ? RemoteMcpOAuthConnectionState.Authorized
            : RemoteMcpOAuthConnectionState.RefreshIndeterminate;
        return new RemoteMcpOAuthConnection(
            currentBinding, checked(current.Revision + 1), current.CredentialRevision, state,
            current.AccessTokenReference, current.RefreshTokenReference,
            current.AccessTokenExpiresAt,
            state == RemoteMcpOAuthConnectionState.RefreshIndeterminate ? claim.AttemptId : null,
            state == RemoteMcpOAuthConnectionState.RefreshIndeterminate ? current.RefreshStartedAt : null,
            current.RefreshTokenExpiresAt);
    }

    public static RemoteMcpOAuthConnection? TryDisconnect(
        RemoteMcpOAuthConnection current,
        RemoteMcpOAuthConnectionBinding currentBinding,
        long expectedConnectionRevision,
        bool revoked = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(currentBinding);
        if (current.Revision != expectedConnectionRevision ||
            !current.Binding.Matches(currentBinding))
            return null;

        return new RemoteMcpOAuthConnection(
            currentBinding, checked(current.Revision + 1), checked(current.CredentialRevision + 1),
            revoked ? RemoteMcpOAuthConnectionState.Revoked : RemoteMcpOAuthConnectionState.Disconnected);
    }

    public static bool IsCurrentAccessTokenUsable(
        RemoteMcpOAuthConnection current,
        RemoteMcpOAuthConnectionBinding currentBinding,
        DateTimeOffset now) =>
        current is not null && currentBinding is not null &&
        current.Binding.Matches(currentBinding) &&
        current.State == RemoteMcpOAuthConnectionState.Authorized &&
        current.AccessTokenReference is not null &&
        current.AccessTokenExpiresAt is { } expiresAt && expiresAt > now;

    private static bool IsCurrentClaim(
        RemoteMcpOAuthPendingConsent consent,
        RemoteMcpOAuthConnection connection,
        RemoteMcpOAuthConnectionBinding binding,
        Guid attemptId,
        long expectedConsentRevision) =>
        attemptId != Guid.Empty &&
        consent.Revision == expectedConsentRevision &&
        consent.State == RemoteMcpOAuthConsentState.CallbackClaimed &&
        consent.ClaimAttemptId == attemptId &&
        connection.Revision == consent.ConnectionRevision &&
        connection.State == RemoteMcpOAuthConnectionState.PendingConsent &&
        BindingMatches(consent.BindingHash, connection, binding);

    private static bool IsCurrentRefresh(
        RemoteMcpOAuthConnection connection,
        RemoteMcpOAuthConnectionBinding binding,
        RemoteMcpOAuthRefreshClaim claim,
        long expectedConnectionRevision) =>
        connection.Revision == expectedConnectionRevision &&
        connection.Revision == claim.ConnectionRevision &&
        connection.CredentialRevision == claim.CredentialRevision &&
        connection.State == RemoteMcpOAuthConnectionState.RefreshInProgress &&
        connection.RefreshAttemptId == claim.AttemptId &&
        connection.Binding.ConnectionId == claim.ConnectionId &&
        string.Equals(connection.Binding.BindingHash, claim.BindingHash, StringComparison.Ordinal) &&
        connection.Binding.Matches(binding) &&
        Equals(connection.RefreshTokenReference, claim.RefreshTokenReference);

    private static bool BindingMatches(
        string expectedHash,
        RemoteMcpOAuthConnection connection,
        RemoteMcpOAuthConnectionBinding currentBinding) =>
        string.Equals(expectedHash, currentBinding.BindingHash, StringComparison.Ordinal) &&
        connection.Binding.Matches(currentBinding);

    private static bool MatchesState(string presentedState, string expectedHash)
    {
        if (string.IsNullOrEmpty(presentedState) ||
            presentedState.Length != 43 ||
            presentedState.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            return false;

        byte[] bytes;
        try
        {
            bytes = RemoteMcpOAuthPendingConsent.FromBase64Url(presentedState);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length != 32 ||
            !string.Equals(RemoteMcpOAuthPendingConsent.ToBase64Url(bytes), presentedState, StringComparison.Ordinal))
            return false;
        var actualHash = SHA256.HashData(Encoding.ASCII.GetBytes(presentedState));
        var expected = Convert.FromHexString(expectedHash);
        return CryptographicOperations.FixedTimeEquals(actualHash, expected);
    }

    private static RemoteMcpOAuthPendingConsent NewConsentRevision(
        RemoteMcpOAuthPendingConsent current,
        RemoteMcpOAuthConsentState state,
        Guid? attemptId) =>
        new(current.CorrelationId, current.BindingHash, current.ConnectionRevision,
            current.StateHash, current.PkceChallenge, current.ProtectedVerifierReference,
            checked(current.Revision + 1), state, current.CreatedAt, current.ExpiresAt, attemptId);
}
