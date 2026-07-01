namespace Agentweaver.Api.Auth;

/// <summary>
/// Result of a secret-get operation.
/// </summary>
public sealed record SecretGetResult(string? Value, string? ETag, bool Found)
{
    public static SecretGetResult NotFound { get; } = new(null, null, false);
    public static SecretGetResult Of(string value, string etag) => new(value, etag, true);
}

/// <summary>
/// Raised by <see cref="ISecretStore.SetSecretAsync"/> when an ETag precondition fails
/// (optimistic-concurrency conflict: another writer updated the secret first).
/// </summary>
public sealed class SecretPreconditionFailedException : Exception
{
    public SecretPreconditionFailedException()
        : base("Secret ETag precondition failed — a concurrent writer updated the secret first.") { }
}

/// <summary>
/// Handle returned by an atomic lease acquisition. Disposing releases the lease best-effort.
/// </summary>
public interface ISecretStoreLease : IAsyncDisposable;

/// <summary>
/// Provides an atomic compare-and-set lease attached to an existing secret key.
/// Implementations must guarantee that only one unexpired owner can acquire a key at a time.
/// </summary>
public interface IAtomicSecretLeaseStore
{
    Task<ISecretStoreLease?> TryAcquireLeaseAsync(
        string key,
        string owner,
        TimeSpan ttl,
        CancellationToken ct = default);
}

/// <summary>
/// Backend-agnostic key/value secret store used by token stores.
/// Implementations: <see cref="InMemorySecretStore"/> (tests/dev),
/// <c>KeyVaultSecretStore</c> (Azure Key Vault, production).
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Returns the secret value and its opaque ETag, or <see cref="SecretGetResult.NotFound"/>
    /// when the key does not exist.
    /// </summary>
    Task<SecretGetResult> GetSecretAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Creates or replaces a secret.  When <paramref name="etag"/> is supplied the write is
    /// conditional: if the stored ETag no longer matches, <see cref="SecretPreconditionFailedException"/>
    /// is thrown so the caller can re-read and decide whether to retry.
    /// Returns the ETag of the newly stored value.
    /// </summary>
    Task<string> SetSecretAsync(string key, string value, string? etag = null, CancellationToken ct = default);

    /// <summary>
    /// Removes a secret.  No error is raised when the key is absent.
    /// </summary>
    Task DeleteSecretAsync(string key, CancellationToken ct = default);
}
