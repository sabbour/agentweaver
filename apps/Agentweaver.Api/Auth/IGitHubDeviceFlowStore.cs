using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

public sealed record GitHubDeviceFlowState(string DeviceCode, int Interval, DateTimeOffset ExpiresAt);

public interface IGitHubDeviceFlowStore
{
    Task SetAsync(GitHubTokenScope scope, GitHubDeviceFlowState state, CancellationToken ct = default);
    Task<GitHubDeviceFlowState?> GetAsync(GitHubTokenScope scope, CancellationToken ct = default);
    Task DeleteAsync(GitHubTokenScope scope, CancellationToken ct = default);
}

public sealed class InMemoryGitHubDeviceFlowStore : IGitHubDeviceFlowStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GitHubDeviceFlowState> _flows = new(StringComparer.Ordinal);

    public Task SetAsync(GitHubTokenScope scope, GitHubDeviceFlowState state, CancellationToken ct = default)
    {
        _flows[scope.Key] = state;
        return Task.CompletedTask;
    }

    public Task<GitHubDeviceFlowState?> GetAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        _flows.TryGetValue(scope.Key, out var state);
        return Task.FromResult(state);
    }

    public Task DeleteAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        _flows.TryRemove(scope.Key, out _);
        return Task.CompletedTask;
    }
}

public sealed class SecretStoreGitHubDeviceFlowStore(ISecretStore secretStore) : IGitHubDeviceFlowStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task SetAsync(GitHubTokenScope scope, GitHubDeviceFlowState state, CancellationToken ct = default)
    {
        var stored = new StoredDeviceFlow
        {
            DeviceCode = state.DeviceCode,
            Interval = state.Interval,
            ExpiresAt = state.ExpiresAt,
        };
        await secretStore.SetSecretAsync(Key(scope), JsonSerializer.Serialize(stored, Json), etag: null, ct)
            .ConfigureAwait(false);
    }

    public async Task<GitHubDeviceFlowState?> GetAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var result = await secretStore.GetSecretAsync(Key(scope), ct).ConfigureAwait(false);
        if (!result.Found || string.IsNullOrWhiteSpace(result.Value))
            return null;

        try
        {
            var stored = JsonSerializer.Deserialize<StoredDeviceFlow>(result.Value, Json);
            if (stored is null || string.IsNullOrWhiteSpace(stored.DeviceCode))
                return null;
            return new GitHubDeviceFlowState(stored.DeviceCode!, stored.Interval, stored.ExpiresAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task DeleteAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        secretStore.DeleteSecretAsync(Key(scope), ct);

    private static string Key(GitHubTokenScope scope) => $"github-device-flow:{scope.Key}";

    private sealed class StoredDeviceFlow
    {
        [JsonPropertyName("DeviceCode")] public string? DeviceCode { get; init; }
        [JsonPropertyName("Interval")] public int Interval { get; init; }
        [JsonPropertyName("ExpiresAt")] public DateTimeOffset ExpiresAt { get; init; }
    }
}
