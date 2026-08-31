using System.Text.Json;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

public sealed class ByokProviderConfigurationService(ISecretStore secretStore)
    : IByokProviderConfigurationProvider
{
    private const string SecretName = "byok-provider-configuration";
    private static readonly JsonSerializerOptions ReadJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<ByokProviderConfiguration?> GetAsync(CancellationToken ct)
    {
        var secret = await secretStore.GetSecretAsync(SecretName, ct).ConfigureAwait(false);
        if (!secret.Found || string.IsNullOrWhiteSpace(secret.Value))
            return null;
        try
        {
            var configuration = JsonSerializer.Deserialize<ByokProviderConfiguration>(secret.Value, ReadJsonOptions);
            return IsValid(configuration) ? configuration : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SetAsync(ByokProviderConfiguration configuration, CancellationToken ct)
    {
        Validate(configuration);
        await secretStore.SetSecretAsync(
            SecretName,
            JsonSerializer.Serialize(configuration),
            ct: ct).ConfigureAwait(false);
    }

    public Task ClearAsync(CancellationToken ct) =>
        secretStore.DeleteSecretAsync(SecretName, ct);

    private static void Validate(ByokProviderConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Type is not ("openai" or "azure" or "anthropic"))
            throw new ArgumentException("Provider type must be openai, azure, or anthropic.");
        if (!Uri.TryCreate(configuration.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Provider base URL must be an HTTPS URL.");
        if (configuration.Type == "azure" &&
            (!string.IsNullOrEmpty(baseUri.PathAndQuery.Trim('/')) || !string.IsNullOrEmpty(baseUri.Fragment)))
            throw new ArgumentException("Azure provider base URL must be its HTTPS host without an API path.");
        if (string.IsNullOrWhiteSpace(configuration.Model))
            throw new ArgumentException("Provider model is required.");
        if (string.IsNullOrWhiteSpace(configuration.ApiKey))
            throw new ArgumentException("Provider API key is required.");
    }

    private static bool IsValid(ByokProviderConfiguration? configuration)
    {
        try
        {
            Validate(configuration!);
            return true;
        }
        catch (ArgumentNullException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
