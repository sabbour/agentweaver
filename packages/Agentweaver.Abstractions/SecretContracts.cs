namespace Agentweaver.Abstractions;

// References are identifiers, not credentials. They can be persisted; their values cannot.
/// <summary>Opaque reference to an exact secret version; it never contains the secret value.</summary>
public sealed record SecretRef
{
    /// <summary>Creates a secret reference after validating both opaque identifiers.</summary>
    /// <exception cref="ArgumentException">The secret ID or version is not a supported identifier.</exception>
    public SecretRef(string id, string version)
    {
        Id = SecretContractIdentifier.Validate(id, nameof(id));
        Version = SecretContractIdentifier.Validate(version, nameof(version));
    }

    /// <summary>Stable identity of the secret within its owner.</summary>
    public string Id { get; }
    /// <summary>Exact version requested so consumers do not silently receive a rotated value.</summary>
    public string Version { get; }
}

/// <summary>Trusted secret redemption request scoped to a specific purpose and run.</summary>
public sealed class SecretRedemptionRequest
{
    /// <summary>Creates a run-bound request for the exact secret reference.</summary>
    /// <exception cref="ArgumentException">Purpose or run ID is not a supported opaque identifier.</exception>
    public SecretRedemptionRequest(SecretRef secret, string purpose, string runId)
    {
        Secret = secret ?? throw new ArgumentNullException(nameof(secret));
        Purpose = SecretContractIdentifier.Validate(purpose, nameof(purpose));
        RunId = SecretContractIdentifier.Validate(runId, nameof(runId));
    }

    /// <summary>Exact secret version requested from the trusted Identity owner.</summary>
    public SecretRef Secret { get; }
    /// <summary>Owner-defined use for which this run is authorized to redeem the secret.</summary>
    public string Purpose { get; }
    /// <summary>Run whose authorization is checked before secret redemption.</summary>
    public string RunId { get; }
}

// Only the trusted Identity/control-plane implementation may implement this boundary.
// Callers must authorize the run and purpose before redemption and revalidate on refresh.
/// <summary>Trusted boundary that redeems an exact secret reference after owner authorization.</summary>
public interface ISecretRedemption
{
    /// <summary>Returns a short-lived credential or fails when authorization or resolution is rejected.</summary>
    /// <exception cref="OperationCanceledException">The caller cancels the redemption.</exception>
    Task<SecretCredential> RedeemAsync(
        SecretRedemptionRequest request, CancellationToken cancellationToken);
}

// Only a trusted credential lifecycle owner may publish a new protected version.
// The store does not compare-and-swap owner state or authorize a connection.
/// <summary>Trusted boundary for publishing a credential as a new protected secret version.</summary>
public interface ISecretVersionWriter
{
    /// <summary>Stores a credential value and returns its new opaque version reference.</summary>
    Task<SecretRef> WriteVersionAsync(
        string secretId, SecretCredential credential, CancellationToken cancellationToken);
}

/// <summary>
/// Short-lived in-memory credential handle. Its value is accessible only through an
/// explicit method, may have its expiry narrowed, and can be invalidated by its owner.
/// </summary>
public sealed class SecretCredential
{
    private string? _value;
    private DateTimeOffset _expiresAt;
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a credential handle that expires in the future according to the supplied clock.</summary>
    /// <exception cref="ArgumentException">The credential value is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The expiry is not in the future.</exception>
    public SecretCredential(string value, DateTimeOffset expiresAt, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("A credential value is required.", nameof(value));
        if (expiresAt <= _timeProvider.GetUtcNow())
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Credential must have a future expiry.");

        _value = value;
        _expiresAt = expiresAt;
    }

    /// <summary>Current expiry time; this metadata can only stay the same or move earlier.</summary>
    public DateTimeOffset ExpiresAt
    {
        get { lock (_sync) return _expiresAt; }
    }

    /// <summary>Returns true while the value remains present and unexpired.</summary>
    public bool IsUsable()
    {
        lock (_sync) return _value is not null && _timeProvider.GetUtcNow() < _expiresAt;
    }

    // Narrow metadata on the same credential so backend invalidation still applies.
    /// <summary>Narrows expiry on the same handle without extending its current lifetime.</summary>
    /// <exception cref="InvalidOperationException">The credential was invalidated or the new limit is expired.</exception>
    public void LimitLifetime(DateTimeOffset expiresAt)
    {
        lock (_sync)
        {
            if (_value is null)
                throw new InvalidOperationException("Credential has been invalidated.");
            var limit = expiresAt < _expiresAt ? expiresAt : _expiresAt;
            if (limit <= _timeProvider.GetUtcNow())
            {
                _value = null;
                throw new InvalidOperationException("Credential has expired.");
            }
            _expiresAt = limit;
        }
    }

    // Explicit access only: no public value property for default JSON or diagnostic walkers.
    /// <summary>Returns the value only while the credential remains valid and unexpired.</summary>
    /// <exception cref="InvalidOperationException">The credential expired or was invalidated.</exception>
    public string GetValue()
    {
        lock (_sync)
        {
            if (_timeProvider.GetUtcNow() >= _expiresAt)
                throw new InvalidOperationException("Credential has expired.");
            return _value ?? throw new InvalidOperationException("Credential has been invalidated.");
        }
    }

    /// <summary>Erases the in-memory credential value so later access fails.</summary>
    public void Invalidate()
    {
        lock (_sync) _value = null;
    }

    /// <summary>Returns a redacted label instead of credential contents.</summary>
    public override string ToString() => nameof(SecretCredential) + " [REDACTED]";
}

/// <summary>Validates the restricted ASCII identifier format used by secret references and requests.</summary>
internal static class SecretContractIdentifier
{
    /// <summary>Returns a valid nonempty identifier or rejects values outside the contract.</summary>
    /// <exception cref="ArgumentException">The value is empty, over 256 characters, or contains unsupported characters.</exception>
    internal static string Validate(string value, string paramName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("Expected a nonempty opaque identifier (at most 256 ASCII characters).", paramName);
        return value;
    }
}
