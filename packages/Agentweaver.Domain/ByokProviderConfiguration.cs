using System.Security.Cryptography;
using System.Text;

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

public static class ByokProviderConfigurationExtensions
{
    public static string ExecutionFingerprint(this ByokProviderConfiguration configuration)
    {
        var headers = configuration.Headers is null
            ? string.Empty
            : string.Join(
                "\n",
                configuration.Headers
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}"));
        var material = string.Join(
            "\n",
            configuration.Id,
            configuration.Type,
            configuration.BaseUrl,
            configuration.Model,
            configuration.ApiKey,
            configuration.WireApi ?? string.Empty,
            configuration.AzureApiVersion ?? string.Empty,
            headers);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}

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
