using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Manages the deployment-wide list of configured "bring your own key" inference providers.
/// GitHub Copilot itself is not stored here — it is the implicit default when no provider from
/// this list is marked active. All providers persist under a single secret so admins can
/// pre-configure several and keep their keys saved, but exactly one may be active at a time (the
/// one <see cref="GetAsync"/> returns to the assistant/agent runtime).
/// </summary>
public sealed class ByokProviderConfigurationService(ISecretStore secretStore)
    : IByokProviderConfigurationProvider
{
    private const string SecretName = "byok-provider-configurations";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record StoredState(
        [property: JsonPropertyName("active_provider_id")] string? ActiveProviderId,
        [property: JsonPropertyName("providers")] List<ByokProviderConfiguration> Providers);

    public async Task<ByokProviderConfiguration?> GetAsync(CancellationToken ct)
    {
        var state = await ReadStateAsync(ct).ConfigureAwait(false);
        if (state.ActiveProviderId is null)
            return null;
        return state.Providers.FirstOrDefault(p => p.Id == state.ActiveProviderId);
    }

    public async Task<string?> GetActiveProviderIdAsync(CancellationToken ct) =>
        (await ReadStateAsync(ct).ConfigureAwait(false)).ActiveProviderId;

    public async Task<IReadOnlyList<ByokProviderConfiguration>> ListAsync(CancellationToken ct) =>
        (await ReadStateAsync(ct).ConfigureAwait(false)).Providers;

    public async Task<ByokProviderConfiguration> AddAsync(ByokProviderConfiguration input, CancellationToken ct)
    {
        var configuration = input with { Id = Guid.NewGuid().ToString("n") };
        Validate(configuration);
        var state = await ReadStateAsync(ct).ConfigureAwait(false);
        state.Providers.Add(configuration);
        await WriteStateAsync(state, ct).ConfigureAwait(false);
        return configuration;
    }

    /// <summary>
    /// Replaces an existing provider's fields. When <paramref name="input"/>.ApiKey is blank the
    /// previously stored key is kept (the frontend never receives saved keys back, so leaving the
    /// field blank on an edit means "unchanged").
    /// </summary>
    public async Task<ByokProviderConfiguration> UpdateAsync(string id, ByokProviderConfiguration input, CancellationToken ct)
    {
        var state = await ReadStateAsync(ct).ConfigureAwait(false);
        var index = state.Providers.FindIndex(p => p.Id == id);
        if (index < 0)
            throw new KeyNotFoundException($"No configured provider with id '{id}'.");

        var existing = state.Providers[index];
        var configuration = input with
        {
            Id = id,
            ApiKey = string.IsNullOrWhiteSpace(input.ApiKey) ? existing.ApiKey : input.ApiKey,
        };
        Validate(configuration);
        state.Providers[index] = configuration;
        await WriteStateAsync(state, ct).ConfigureAwait(false);
        return configuration;
    }

    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        var state = await ReadStateAsync(ct).ConfigureAwait(false);
        state.Providers.RemoveAll(p => p.Id == id);
        var activeProviderId = state.ActiveProviderId == id ? null : state.ActiveProviderId;
        await WriteStateAsync(state with { ActiveProviderId = activeProviderId }, ct).ConfigureAwait(false);
    }

    /// <summary>Marks the given configured provider id as active, or <see langword="null"/> to
    /// switch the deployment back to GitHub Copilot mode.</summary>
    public async Task SetActiveAsync(string? id, CancellationToken ct)
    {
        var state = await ReadStateAsync(ct).ConfigureAwait(false);
        if (id is not null && state.Providers.All(p => p.Id != id))
            throw new KeyNotFoundException($"No configured provider with id '{id}'.");
        await WriteStateAsync(state with { ActiveProviderId = id }, ct).ConfigureAwait(false);
    }

    private async Task<StoredState> ReadStateAsync(CancellationToken ct)
    {
        var secret = await secretStore.GetSecretAsync(SecretName, ct).ConfigureAwait(false);
        if (!secret.Found || string.IsNullOrWhiteSpace(secret.Value))
            return new StoredState(null, []);
        try
        {
            var state = JsonSerializer.Deserialize<StoredState>(secret.Value, JsonOptions);
            if (state is null)
                return new StoredState(null, []);
            // Defensive: drop any persisted providers that no longer pass validation (e.g. an
            // older/corrupt record) rather than surfacing them to callers or the runtime.
            var validProviders = state.Providers.Where(IsValid).ToList();
            var activeProviderId = validProviders.Any(p => p.Id == state.ActiveProviderId)
                ? state.ActiveProviderId
                : null;
            return new StoredState(activeProviderId, validProviders);
        }
        catch (JsonException)
        {
            return new StoredState(null, []);
        }
    }

    private Task WriteStateAsync(StoredState state, CancellationToken ct) =>
        secretStore.SetSecretAsync(SecretName, JsonSerializer.Serialize(state, JsonOptions), ct: ct);

    private static void Validate(ByokProviderConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Type is not ("openai" or "azure" or "anthropic"))
            throw new ArgumentException("Provider type must be openai, azure, or anthropic.");
        if (string.IsNullOrWhiteSpace(configuration.Name))
            throw new ArgumentException("Provider display name is required.");
        if (!Uri.TryCreate(configuration.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Provider base URL must be an HTTPS URL.");
        if (configuration.Type == "azure" &&
            (!string.IsNullOrEmpty(baseUri.PathAndQuery.Trim('/')) || !string.IsNullOrEmpty(baseUri.Fragment)))
            throw new ArgumentException("Azure provider base URL must be its HTTPS host without an API path.");
        if (string.IsNullOrWhiteSpace(configuration.Model))
            throw new ArgumentException("Provider model is required.");
        // A custom (OpenAI-compatible) endpoint may be unauthenticated (e.g. a local vLLM
        // server); Azure and Anthropic always require a key.
        if (configuration.Type != "openai" && string.IsNullOrWhiteSpace(configuration.ApiKey))
            throw new ArgumentException("Provider API key is required.");
        if (configuration.WireApi is not (null or "completions" or "responses"))
            throw new ArgumentException("Provider wire API must be completions or responses.");
    }

    private static bool IsValid(ByokProviderConfiguration configuration)
    {
        try
        {
            Validate(configuration);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
