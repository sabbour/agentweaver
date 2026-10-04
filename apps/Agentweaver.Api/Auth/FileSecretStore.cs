using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Development-only file-backed secret store for local multi-process gates.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    private readonly string root;

    private sealed record StoredSecret(string Value, string ETag);

    public FileSecretStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Secret store path is required.", nameof(root));
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
    }

    public async Task<SecretGetResult> GetSecretAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stored = await ReadAsync(path, ct).ConfigureAwait(false);
            return stored is null ? SecretGetResult.NotFound : SecretGetResult.Of(stored.Value, stored.ETag);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> SetSecretAsync(string key, string value, string? etag = null, CancellationToken ct = default)
    {
        var path = PathFor(key);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await ReadAsync(path, ct).ConfigureAwait(false);
            if (etag is not null && existing?.ETag != etag)
                throw new SecretPreconditionFailedException();
            var next = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var payload = JsonSerializer.Serialize(new StoredSecret(value, next));
            await File.WriteAllTextAsync(path, payload, ct).ConfigureAwait(false);
            return next;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteSecretAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            gate.Release();
        }
    }

    private string PathFor(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(root, $"{Convert.ToHexString(bytes).ToLowerInvariant()}.json");
    }

    private static async Task<StoredSecret?> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<StoredSecret>(stream, cancellationToken: ct).ConfigureAwait(false);
    }
}
