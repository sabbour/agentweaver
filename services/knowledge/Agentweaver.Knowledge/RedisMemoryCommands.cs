using System.Collections.Immutable;
using StackExchange.Redis;

namespace Agentweaver.Knowledge;

/// <summary>A Redis hash field name and value returned by bounded namespace scans.</summary>
public sealed record RedisMemoryHashField(string Name, string Value);

/// <summary>Server configuration and INFO data used to check Redis durability and topology.</summary>
public sealed record RedisMemoryServerSnapshot(
    ImmutableDictionary<string, string> Configuration,
    ImmutableDictionary<string, string> Server,
    ImmutableDictionary<string, string> Persistence,
    ImmutableDictionary<string, string> Replication,
    ImmutableDictionary<string, string> Cluster);

/// <summary>Redis operations required by the Memory document store, isolated for deterministic testing.</summary>
public interface IRedisMemoryCommandClient
{
    /// <summary>Reads the Redis configuration and INFO snapshots needed to check resource compatibility.</summary>
    Task<RedisMemoryServerSnapshot> ReadServerSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Returns the remaining TTL for a key, or null when the key has no expiry.</summary>
    Task<TimeSpan?> GetKeyTimeToLiveAsync(string key, CancellationToken cancellationToken);

    /// <summary>Scans keys matching the supplied pattern.</summary>
    Task<IReadOnlyList<string>> ScanKeysAsync(string pattern, CancellationToken cancellationToken);

    /// <summary>Scans all fields from one hash.</summary>
    Task<IReadOnlyList<RedisMemoryHashField>> ScanHashAsync(
        string key,
        CancellationToken cancellationToken);

    /// <summary>Reads named hash fields, optionally rejecting any field-level expiry.</summary>
    Task<IReadOnlyList<string?>> ReadHashFieldsAsync(
        string key,
        IReadOnlyList<string> fields,
        bool checkFieldExpiration,
        CancellationToken cancellationToken);

    /// <summary>Reads the TTL for each named hash field in the same order.</summary>
    Task<IReadOnlyList<long>> ReadHashFieldTtlsAsync(
        string key,
        IReadOnlyList<string> fields,
        CancellationToken cancellationToken);

    /// <summary>Executes the atomic batch script for one key and returns its provider status value.</summary>
    Task<long> ExecuteBatchScriptAsync(
        string script,
        string key,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

/// <summary>Executes bounded Redis commands and scripts for the Memory document store.</summary>
public sealed class RedisMemoryCommandClient : IRedisMemoryCommandClient, IDisposable
{
    private static readonly string[] RequiredConfiguration =
    [
        "appendonly",
        "appendfsync",
        "no-appendfsync-on-rewrite",
        "maxmemory-policy"
    ];

    private const string ReadHashFieldsScript = """
        local keyType = redis.call('TYPE', KEYS[1]).ok
        if keyType == 'none' then return {'missing'} end
        if keyType ~= 'hash' or redis.call('PTTL', KEYS[1]) ~= -1 then return {'invalid'} end
        if ARGV[1] == '1' then
            local expirations = redis.call('HPTTL', KEYS[1], 'FIELDS', #ARGV - 1, unpack(ARGV, 2))
            for _, expiration in ipairs(expirations) do
                if expiration >= 0 then return {'invalid'} end
            end
        end
        local values = redis.call('HMGET', KEYS[1], unpack(ARGV, 2))
        local result = {'ok'}
        for _, value in ipairs(values) do table.insert(result, value or false) end
        return result
        """;

    private readonly Lazy<IConnectionMultiplexer> _connection;
    private readonly int _databaseNumber;
    private readonly Uri _endpoint;

    /// <summary>Creates a lazy Redis connection using the validated endpoint and database.</summary>
    public RedisMemoryCommandClient(
        Func<IConnectionMultiplexer> connectionFactory,
        RedisMemoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _connection = new Lazy<IConnectionMultiplexer>(
            connectionFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        _databaseNumber = options.Database;
        _endpoint = options.Endpoint;
    }

    private IDatabase Database => _connection.Value.GetDatabase(_databaseNumber);
    private IServer Server => _connection.Value.GetServer(_endpoint.Host, _endpoint.Port);

    /// <summary>Reads the configuration and server sections required to verify Redis durability.</summary>
    public async Task<RedisMemoryServerSnapshot> ReadServerSnapshotAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in RequiredConfiguration)
        {
            var values = await Server.ConfigGetAsync(name).ConfigureAwait(false);
            var value = values.FirstOrDefault(pair =>
                string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(value.Key))
                throw new KnowledgeProviderUnavailableException(
                    $"Redis did not return required configuration '{name}'.");
            configuration.Add(name, value.Value);
        }

        var server = await ReadInfoAsync("server").ConfigureAwait(false);
        var persistence = await ReadInfoAsync("persistence").ConfigureAwait(false);
        var replication = await ReadInfoAsync("replication").ConfigureAwait(false);
        var cluster = await ReadInfoAsync("cluster").ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new RedisMemoryServerSnapshot(
            configuration.ToImmutable(),
            server,
            persistence,
            replication,
            cluster);
    }

    /// <summary>Reads a key's TTL so the store can reject expiring document hashes.</summary>
    public async Task<TimeSpan?> GetKeyTimeToLiveAsync(
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Database.KeyTimeToLiveAsync(key).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Scans matching keys, failing closed if the global scan exceeds its configured bound.</summary>
    public async Task<IReadOnlyList<string>> ScanKeysAsync(
        string pattern,
        CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        await foreach (var key in Server.KeysAsync(
                database: _databaseNumber,
                pattern: pattern,
                pageSize: 256).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (keys.Count >= RedisMemoryDocumentStore.MaximumGlobalReceiptScanKeys)
                throw new KnowledgeStorageUnavailableException();
            keys.Add(key.ToString());
        }
        return keys;
    }

    /// <summary>Scans one project's hash, failing closed when its field count exceeds the supported bound.</summary>
    public async Task<IReadOnlyList<RedisMemoryHashField>> ScanHashAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var fields = new List<RedisMemoryHashField>();
        await foreach (var entry in Database.HashScanAsync(
                key,
                pageSize: 256).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (fields.Count >= RedisMemoryDocumentStore.MaximumProjectHashFields)
                throw new KnowledgeStorageUnavailableException();
            fields.Add(new RedisMemoryHashField(entry.Name.ToString(), entry.Value.ToString()));
        }
        return fields;
    }

    /// <summary>Reads requested hash fields atomically while optionally rejecting field-level expiration.</summary>
    public async Task<IReadOnlyList<string?>> ReadHashFieldsAsync(
        string key,
        IReadOnlyList<string> fields,
        bool checkFieldExpiration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var arguments = new RedisValue[fields.Count + 1];
        arguments[0] = checkFieldExpiration ? "1" : "0";
        for (var index = 0; index < fields.Count; index++)
            arguments[index + 1] = fields[index];
        var result = await Database.ScriptEvaluateAsync(
            ReadHashFieldsScript,
            [key],
            arguments).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var values = (RedisResult[]?)result ?? throw new KnowledgeStorageUnavailableException();
        return values.Select(item =>
            item.IsNull ? null : item.ToString()).ToArray();
    }

    /// <summary>Reads hash-field TTLs in one command for the requested names.</summary>
    public async Task<IReadOnlyList<long>> ReadHashFieldTtlsAsync(
        string key,
        IReadOnlyList<string> fields,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var arguments = new object[fields.Count + 3];
        arguments[0] = key;
        arguments[1] = "FIELDS";
        arguments[2] = fields.Count;
        for (var index = 0; index < fields.Count; index++)
            arguments[index + 3] = fields[index];
        var result = await Database.ExecuteAsync("HPTTL", arguments).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var values = (RedisResult[]?)result ?? throw new KnowledgeStorageUnavailableException();
        return values.Select(item => (long)item).ToArray();
    }

    /// <summary>Runs the supplied atomic batch script against one project hash.</summary>
    public async Task<long> ExecuteBatchScriptAsync(
        string script,
        string key,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Database.ScriptEvaluateAsync(
            script,
            [key],
            arguments.Select(argument => (RedisValue)argument).ToArray()).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return (long)result;
    }

    /// <summary>Flattens a Redis INFO section into a case-insensitive key/value map.</summary>
    private async Task<ImmutableDictionary<string, string>> ReadInfoAsync(string section)
    {
        var groups = await Server.InfoAsync(section).ConfigureAwait(false);
        return groups.SelectMany(group => group)
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Disposes the multiplexer only if a command caused the lazy connection to be created.</summary>
    public void Dispose()
    {
        if (_connection.IsValueCreated)
            _connection.Value.Dispose();
    }
}
