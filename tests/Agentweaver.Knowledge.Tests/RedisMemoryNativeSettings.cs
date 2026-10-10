using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Knowledge;
using Agentweaver.Providers;
using StackExchange.Redis;

namespace Agentweaver.Knowledge.Tests;

internal sealed class RedisMemoryNativeSettings
{
    internal const string OptInVariable = "AGENTWEAVER_REDIS_MEMORY_NATIVE";
    private const string TestLabel = "io.agentweaver.redis-memory.native-test";
    private const string OwnerLabel = "io.agentweaver.redis-memory.native-test-owner";
    private const int ContainerTlsPort = 6380;

    private readonly string containerId;
    private readonly string ownerId;
    private readonly string imageReference;
    private readonly string engine;
    private readonly string? userName;
    private readonly string? password;

    private RedisMemoryNativeSettings(
        Uri endpoint,
        string containerId,
        string ownerId,
        string imageReference,
        string engine,
        string? userName,
        string? password)
    {
        Endpoint = endpoint;
        this.containerId = containerId;
        this.ownerId = ownerId;
        this.imageReference = imageReference;
        this.engine = engine;
        this.userName = userName;
        this.password = password;
    }

    private Uri Endpoint { get; }

    public static RedisMemoryNativeSettings Read()
    {
        var endpointText = Required("AGENTWEAVER_REDIS_MEMORY_NATIVE_ENDPOINT");
        var containerId = Required("AGENTWEAVER_REDIS_MEMORY_NATIVE_CONTAINER_ID");
        var ownerId = Required("AGENTWEAVER_REDIS_MEMORY_NATIVE_OWNER_ID");
        var imageReference = Required("AGENTWEAVER_REDIS_MEMORY_NATIVE_IMAGE");
        var engine = Required("AGENTWEAVER_REDIS_MEMORY_NATIVE_ENGINE");
        var userName = Environment.GetEnvironmentVariable(
            "AGENTWEAVER_REDIS_MEMORY_NATIVE_USERNAME");
        var password = Environment.GetEnvironmentVariable(
            "AGENTWEAVER_REDIS_MEMORY_NATIVE_PASSWORD");

        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
            !string.Equals(endpoint.Scheme, "rediss", StringComparison.OrdinalIgnoreCase) ||
            !IPAddress.TryParse(endpoint.Host, out var address) ||
            !IPAddress.IsLoopback(address) ||
            endpoint.Port is < 1 or > 65_535 ||
            endpoint.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidOperationException(
                "The native Redis endpoint must be a loopback rediss URI with an explicit port.");
        if (containerId.Length != 64 || !containerId.All(Uri.IsHexDigit))
            throw new InvalidOperationException(
                "The native Redis container ID must be its full 64-character ID.");
        if (!Guid.TryParseExact(ownerId, "N", out _))
            throw new InvalidOperationException(
                "The native Redis owner ID must be a 32-character run GUID.");
        if (!HasPinnedImageDigest(imageReference))
            throw new InvalidOperationException(
                "The native Redis image must be referenced by an exact sha256 digest.");
        if (engine is not ("docker" or "podman"))
            throw new InvalidOperationException(
                "The native Redis container engine must be docker or podman.");
        if ((userName is null) != (password is null))
            throw new InvalidOperationException(
                "Native Redis ACL username and password must be supplied together.");

        return new RedisMemoryNativeSettings(
            endpoint, containerId, ownerId, imageReference, engine, userName, password);
    }

    public async Task<RedisMemoryNativeContext> OpenContextAsync(
        string keyPrefix,
        TimeProvider? timeProvider = null)
    {
        var container = await InspectOwnedContainerAsync();
        var options = CreateOptions(keyPrefix);

        var connection = await ConnectionMultiplexer.ConnectAsync(options.CreateConnectionOptions());
        var client = new RedisMemoryCommandClient(() => connection, options);
        try
        {
            await ValidateAofVolumeAsync(container, connection);
            var store = new RedisMemoryDocumentStore(client, options);
            var provider = new RedisMemoryProvider(store, options, timeProvider);
            var catalog = ProviderCatalog.Create(
                [
                    new ProviderRegistration(
                        provider.Descriptor,
                        true,
                        options.OptionsRevision,
                        options.OptionsSchemaVersion)
                ],
                [new ProviderSelection(ProviderSeam.Memory, RedisMemoryProvider.ProviderId)],
                [new ProviderOverridePermission(ProviderSeam.Memory, RedisMemoryProvider.ProviderId)]);
            var providerCatalog = catalog.Value
                ?? throw new InvalidOperationException("Could not create the Redis test provider catalog.");
            var resolution = new ProviderResolver(providerCatalog).Resolve(new ProviderResolutionRequest(
                ProviderSeam.Memory,
                null,
                RedisMemoryProvider.AdapterVersion,
                options.OptionsSchemaVersion,
                MemoryProviderCapabilities.All));
            var candidate = resolution.Value?.Candidate
                ?? throw new InvalidOperationException("Could not resolve the Redis test provider candidate.");
            return new RedisMemoryNativeContext(options, client, store, provider, candidate);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task RestartOwnedInstanceAsync()
    {
        var before = await InspectOwnedContainerAsync();
        {
            using var connection = await ConnectionMultiplexer.ConnectAsync(
                CreateOptions($"native-redis-{ownerId}").CreateConnectionOptions());
            await ValidateAofVolumeAsync(before, connection);
        }

        var restart = await RunAsync(engine, ["restart", "--time", "10", containerId]);
        if (restart.ExitCode != 0)
            throw new InvalidOperationException(
                $"The dedicated Redis container restart failed: {restart.StandardError}");

        var after = await InspectOwnedContainerAsync();
        if (!string.Equals(before.Id, after.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(before.ImageId, after.ImageId, StringComparison.OrdinalIgnoreCase) ||
            after.StartedAt <= before.StartedAt)
            throw new InvalidOperationException(
                "The dedicated Redis container did not restart as the same pinned instance.");
    }

    private RedisMemoryOptions CreateOptions(string keyPrefix)
    {
        var options = new RedisMemoryOptions(
            Endpoint,
            $"native-redis-{ownerId}",
            1,
            "native-test-v1",
            RedisMemoryOptions.CurrentOptionsSchemaVersion,
            0,
            keyPrefix,
            userName,
            password);
        options.Validate();
        return options;
    }

    private async Task<NativeContainer> InspectOwnedContainerAsync()
    {
        var result = await RunAsync(engine, ["inspect", containerId]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Could not inspect the dedicated Redis container: {result.StandardError}");

        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement[0]
            : json.RootElement;
        var id = root.GetProperty("Id").GetString()
            ?? throw new InvalidOperationException("The Redis container has no ID.");
        if (!string.Equals(id, containerId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The inspected Redis container ID does not match.");

        var configuration = root.GetProperty("Config");
        var configuredImage = configuration.GetProperty("Image").GetString();
        if (!string.Equals(configuredImage, imageReference, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The dedicated Redis container does not use the requested pinned image.");
        var labels = configuration.GetProperty("Labels");
        if (!LabelEquals(labels, TestLabel, "true") ||
            !LabelEquals(labels, OwnerLabel, ownerId))
            throw new InvalidOperationException(
                "The Redis container is not labeled as owned by this native test run.");

        var state = root.GetProperty("State");
        if (!state.GetProperty("Running").GetBoolean())
            throw new InvalidOperationException("The dedicated Redis container is not running.");
        var startedAt = DateTimeOffset.Parse(
            state.GetProperty("StartedAt").GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        var imageId = root.GetProperty("Image").GetString()
            ?? throw new InvalidOperationException("The Redis container has no image ID.");
        var portBindings = root.GetProperty("NetworkSettings")
            .GetProperty("Ports")
            .GetProperty($"{ContainerTlsPort}/tcp");
        var bindings = portBindings.EnumerateArray().ToArray();
        var loopbackBinding = bindings.Length == 1 &&
            IPAddress.TryParse(bindings[0].GetProperty("HostIp").GetString(), out var hostAddress) &&
            IPAddress.IsLoopback(hostAddress) &&
            int.TryParse(
                bindings[0].GetProperty("HostPort").GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var hostPort) &&
            hostPort == Endpoint.Port;
        if (!loopbackBinding)
            throw new InvalidOperationException(
                "Redis TLS must be published exactly once on the configured loopback port.");

        await VerifyPinnedImageAsync(imageId);
        var mounts = root.GetProperty("Mounts").EnumerateArray()
            .Select(mount => new NativeMount(
                mount.GetProperty("Type").GetString() ?? string.Empty,
                mount.TryGetProperty("Name", out var name) ? name.GetString() : null,
                mount.GetProperty("Destination").GetString() ?? string.Empty))
            .ToArray();
        return new NativeContainer(
            id,
            imageId,
            startedAt,
            mounts);
    }

    private async Task VerifyPinnedImageAsync(string containerImageId)
    {
        var result = await RunAsync(engine, ["image", "inspect", imageReference]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"The pinned Redis image is not present locally: {result.StandardError}");
        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement[0]
            : json.RootElement;
        var imageId = root.GetProperty("Id").GetString();
        if (!string.Equals(imageId, containerImageId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The Redis container image ID does not match the locally cached pinned image.");
    }

    private async Task ValidateAofVolumeAsync(
        NativeContainer container,
        IConnectionMultiplexer connection)
    {
        var server = connection.GetServer(Endpoint.Host, Endpoint.Port);
        var dataDirectory = await ReadConfigValueAsync(server, "dir");
        var appendDirectory = await ReadConfigValueAsync(server, "appenddirname");
        if (string.IsNullOrWhiteSpace(dataDirectory) ||
            !dataDirectory.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidOperationException("Redis did not report its AOF data directory.");
        var aofDirectory = string.IsNullOrWhiteSpace(appendDirectory)
            ? NormalizeContainerPath(dataDirectory)
            : appendDirectory.StartsWith("/", StringComparison.Ordinal)
                ? NormalizeContainerPath(appendDirectory)
                : NormalizeContainerPath($"{dataDirectory}/{appendDirectory}");
        var volume = container.Mounts.SingleOrDefault(mount =>
            string.Equals(mount.Type, "volume", StringComparison.Ordinal) &&
            mount.Name is not null &&
            Covers(mount.Destination, aofDirectory));
        if (volume?.Name is null)
            throw new InvalidOperationException(
                "The active Redis AOF directory is not on a dedicated named volume.");

        var result = await RunAsync(engine, ["volume", "inspect", volume.Name]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Could not inspect the dedicated Redis AOF volume: {result.StandardError}");
        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement[0]
            : json.RootElement;
        var labels = root.GetProperty("Labels");
        if (!LabelEquals(labels, TestLabel, "true") ||
            !LabelEquals(labels, OwnerLabel, ownerId))
            throw new InvalidOperationException(
                "The AOF volume is not labeled as owned by this native test run.");
    }

    private static async Task<string?> ReadConfigValueAsync(IServer server, string name)
    {
        var values = await server.ConfigGetAsync(name);
        return values.FirstOrDefault(pair =>
            string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static bool LabelEquals(JsonElement labels, string name, string value) =>
        labels.ValueKind == JsonValueKind.Object &&
        labels.TryGetProperty(name, out var label) &&
        string.Equals(label.GetString(), value, StringComparison.Ordinal);

    private static bool Covers(string mount, string path)
    {
        var normalizedMount = NormalizeContainerPath(mount);
        var normalizedPath = NormalizeContainerPath(path);
        return normalizedMount != "/" &&
            (string.Equals(normalizedMount, normalizedPath, StringComparison.Ordinal) ||
             normalizedPath.StartsWith($"{normalizedMount.TrimEnd('/')}/", StringComparison.Ordinal));
    }

    private static string NormalizeContainerPath(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    throw new InvalidOperationException("The Redis AOF path escapes the container root.");
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return $"/{string.Join('/', segments)}";
    }

    private static bool HasPinnedImageDigest(string imageReference)
    {
        var marker = imageReference.LastIndexOf("@sha256:", StringComparison.Ordinal);
        return marker > 0 &&
            imageReference.Length - marker - 8 == 64 &&
            imageReference[(marker + 8)..].All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Missing required environment variable '{name}'.");

    private sealed record NativeMount(string Type, string? Name, string Destination);

    private sealed record NativeContainer(
        string Id,
        string ImageId,
        DateTimeOffset StartedAt,
        IReadOnlyList<NativeMount> Mounts);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

internal sealed class RedisMemoryNativeContext(
    RedisMemoryOptions options,
    RedisMemoryCommandClient client,
    RedisMemoryDocumentStore store,
    RedisMemoryProvider provider,
    ProviderCandidate candidate) : IDisposable
{
    public RedisMemoryOptions Options { get; } = options;
    public RedisMemoryCommandClient Client { get; } = client;
    public RedisMemoryDocumentStore Store { get; } = store;
    public RedisMemoryProvider Provider { get; } = provider;
    public ProviderCandidate Candidate { get; } = candidate;

    public void Dispose() => Client.Dispose();
}
