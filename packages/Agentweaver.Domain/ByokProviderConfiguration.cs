namespace Agentweaver.Domain;

public sealed record ByokProviderConfiguration(
    string Type,
    string BaseUrl,
    string Model,
    string ApiKey);

public interface IByokProviderConfigurationProvider
{
    Task<ByokProviderConfiguration?> GetAsync(CancellationToken ct);
}
