namespace Agentweaver.Api.Auth;

/// <summary>
/// In-memory <see cref="ISecretStore"/> implementation.
/// Thread-safe.  Suitable for unit tests and environments where no persistent secret
/// backend is required.  ETag simulation: monotonically increasing integer per write.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _store = new(StringComparer.Ordinal);
    private long _seq;

    private sealed record Entry(string Value, string ETag);

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
        }
        return Task.CompletedTask;
    }
}
