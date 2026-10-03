namespace Agentweaver.Abstractions;

// References are identifiers, not credentials. They can be persisted; their values cannot.
public sealed class SecretRef
{
    public SecretRef(string id, string version)
    {
        Id = SecretContractIdentifier.Validate(id, nameof(id));
        Version = SecretContractIdentifier.Validate(version, nameof(version));
    }

    public string Id { get; }
    public string Version { get; }
}

public sealed class SecretRedemptionRequest
{
    public SecretRedemptionRequest(SecretRef secret, string purpose, string runId)
    {
        Secret = secret ?? throw new ArgumentNullException(nameof(secret));
        Purpose = SecretContractIdentifier.Validate(purpose, nameof(purpose));
        RunId = SecretContractIdentifier.Validate(runId, nameof(runId));
    }

    public SecretRef Secret { get; }
    public string Purpose { get; }
    public string RunId { get; }
}

// Only the trusted Identity/control-plane implementation may implement this boundary.
// Callers must authorize the run and purpose before redemption and revalidate on refresh.
public interface ISecretRedemption
{
    Task<SecretCredential> RedeemAsync(
        SecretRedemptionRequest request, CancellationToken cancellationToken);
}

public sealed class SecretCredential
{
    private string? _value;
    private readonly TimeProvider _timeProvider;

    public SecretCredential(string value, DateTimeOffset expiresAt, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("A credential value is required.", nameof(value));
        if (expiresAt <= _timeProvider.GetUtcNow())
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Credential must have a future expiry.");

        _value = value;
        ExpiresAt = expiresAt;
    }

    public DateTimeOffset ExpiresAt { get; }

    // Explicit access only: no public value property for default JSON or diagnostic walkers.
    public string GetValue()
    {
        if (_timeProvider.GetUtcNow() >= ExpiresAt)
            throw new InvalidOperationException("Credential has expired.");
        return Volatile.Read(ref _value) ?? throw new InvalidOperationException("Credential has been invalidated.");
    }

    public void Invalidate() => Interlocked.Exchange(ref _value, null);

    public override string ToString() => nameof(SecretCredential) + " [REDACTED]";
}

internal static class SecretContractIdentifier
{
    internal static string Validate(string value, string paramName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("Expected a nonempty opaque identifier (at most 256 ASCII characters).", paramName);
        return value;
    }
}
