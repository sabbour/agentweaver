using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Scaffolder.AgentRuntime.Providers;

public sealed class FoundryClientFactory
{
    private readonly string? _endpoint;
    private readonly string? _apiKey;
    private readonly string? _deployment;

    public FoundryClientFactory(IConfiguration configuration)
    {
        var section = configuration.GetSection("Providers:MicrosoftFoundry");
        _endpoint = section.GetValue<string>("Endpoint");
        _apiKey = section.GetValue<string>("ApiKey");
        _deployment = section.GetValue<string>("Deployment") ?? section.GetValue<string>("ModelId");
    }

    public IChatClient CreateChatClient()
    {
        if (_endpoint is null)
            throw new InvalidOperationException("Providers:MicrosoftFoundry:Endpoint is required.");
        if (_apiKey is null)
            throw new InvalidOperationException("Providers:MicrosoftFoundry:ApiKey is required.");
        if (_deployment is null)
            throw new InvalidOperationException("Providers:MicrosoftFoundry:Deployment is required.");

        // Strip project path — AzureOpenAIClient needs the resource root only.
        // e.g. https://foo.services.ai.azure.com/api/projects/bar → https://foo.services.ai.azure.com
        var uri = new Uri(_endpoint);
        var resourceEndpoint = new Uri($"{uri.Scheme}://{uri.Host}");

        return new AzureOpenAIClient(resourceEndpoint, new AzureKeyCredential(_apiKey))
            .GetChatClient(_deployment)
            .AsIChatClient();
    }
}
