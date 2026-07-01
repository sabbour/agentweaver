namespace Agentweaver.Api.Auth;

/// <summary>
/// In-memory <see cref="ISecretStore"/> implementation.
/// Thread-safe.  Suitable for unit tests and environments where no persistent secret
/// backend is required.  ETag simulation: monotonically increasing integer per write.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore, IAtomicSecretLeaseStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _store = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LeaseEntry> _leases = new(StringComparer.Ordinal);
    private long _seq;

    private sealed record Entry(string Value, string ETag);
    private sealed record LeaseEntry(string Owner, DateTimeOffset ExpiresAt);

    public Task<SecretGetResult> GetSecretAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return _store.TryGetValue(key, out var e)
                ? Task.FromResult(SecretGetResult.Of(e.Value, e.ETag))
                : Task.FromResult(SecretGetResult.NotFound);
        }
    }

    public Task<string> SetSecretAsync(string key, string value, string? etag = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (etag is not null)
            {
                if (!_store.TryGetValue(key, out var current) || current.ETag != etag)
                    throw new SecretPreconditionFailedException();
            }

            var newETag = System.Threading.Interlocked.Increment(ref _seq).ToString();
            _store[key] = new Entry(value, newETag);
            return Task.FromResult(newETag);
        }
    }

    public Task DeleteSecretAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _store.Remove(key);
            _leases.Remove(key);
        }
        return Task.CompletedTask;
    }

    public Task<ISecretStoreLease?> TryAcquireLeaseAsync(
        string key,
        string owner,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_store.ContainsKey(key))
                return Task.FromResult<ISecretStoreLease?>(null);

            var now = DateTimeOffset.UtcNow;
            if (_leases.TryGetValue(key, out var lease) && lease.ExpiresAt > now)
                return Task.FromResult<ISecretStoreLease?>(null);

            _leases[key] = new LeaseEntry(owner, now + ttl);
            return Task.FromResult<ISecretStoreLease?>(new InMemorySecretStoreLease(this, key, owner));
        }
    }

    private void ReleaseLease(string key, string owner)
    {
        lock (_lock)
        {
            if (_leases.TryGetValue(key, out var lease) && lease.Owner == owner)
                _leases.Remove(key);
        }
    }

    private sealed class InMemorySecretStoreLease(
        InMemorySecretStore store,
        string key,
        string owner) : ISecretStoreLease
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                store.ReleaseLease(key, owner);
            return ValueTask.CompletedTask;
        }
    }
}
