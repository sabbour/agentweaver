using GitHub.Copilot;

namespace Agentweaver.Api.Auth;

public interface IGitHubCopilotEntitlementProbe
{
    Task<bool?> ProbeAsync(string accessToken, CancellationToken ct = default);
}

public interface ICopilotEntitlementSdkClient : IAsyncDisposable
{
    Task<IList<ModelInfo>> ListModelsAsync(CancellationToken ct);
}

public interface ICopilotEntitlementSdkClientFactory
{
    ICopilotEntitlementSdkClient Create(string accessToken);
}

public sealed class CopilotEntitlementSdkClientFactory : ICopilotEntitlementSdkClientFactory
{
    public ICopilotEntitlementSdkClient Create(string accessToken) => new CopilotEntitlementSdkClient(accessToken);

    private sealed class CopilotEntitlementSdkClient(string accessToken) : ICopilotEntitlementSdkClient
    {
        private readonly CopilotClient _client = new(new CopilotClientOptions
        {
            GitHubToken = accessToken,
        });

        public async Task<IList<ModelInfo>> ListModelsAsync(CancellationToken ct)
        {
            await _client.StartAsync(ct).ConfigureAwait(false);
            return await _client.ListModelsAsync(ct).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => _client.DisposeAsync();
    }
}

public sealed class GitHubCopilotEntitlementProbe(
    ICopilotEntitlementSdkClientFactory clientFactory,
    ILogger<GitHubCopilotEntitlementProbe> logger) : IGitHubCopilotEntitlementProbe
{
    /// <summary>
    /// Uses the GitHub Copilot SDK/native CLI path instead of the raw HTTPS endpoint. The bundled
    /// runtime carries the registered Copilot editor credentials that Agentweaver's OAuth app lacks,
    /// so a successful <c>ListModelsAsync</c> here is the same proof path used by the Copilot CLI.
    /// </summary>
    public async Task<bool?> ProbeAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        try
        {
            await using var client = clientFactory.Create(accessToken);
            _ = await client.ListModelsAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "GitHub Copilot entitlement probe failed via SDK model listing; treating as inconclusive.");
            return null;
        }
    }
}
