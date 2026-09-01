namespace Agentweaver.Domain;

/// <summary>
/// A single configured "bring your own key" inference provider. Multiple providers can be
/// configured (and their keys kept) at once, but only one is ever the deployment-wide active
/// provider — see <see cref="IByokProviderConfigurationProvider.GetAsync"/>.
/// </summary>
public sealed record ByokProviderConfiguration(
    string Id,
    string Name,
    string Type,
    string BaseUrl,
    string Model,
    string ApiKey,
    string? WireApi = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? AzureApiVersion = null);

public interface IByokProviderConfigurationProvider
{
    /// <summary>
    /// Returns the currently ACTIVE deployment-wide BYOK provider configuration, or
    /// <see langword="null"/> when GitHub Copilot is the active AI source (no BYOK provider is
    /// active). Other configured-but-inactive providers are not returned here — see
    /// <c>ByokProviderConfigurationService.ListAsync</c> for the full configured list.
    /// </summary>
    Task<ByokProviderConfiguration?> GetAsync(CancellationToken ct);
}
