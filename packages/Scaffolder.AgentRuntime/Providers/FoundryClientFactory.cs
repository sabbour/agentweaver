using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using System.ClientModel;

namespace Scaffolder.AgentRuntime.Providers;

public sealed class FoundryClientFactory
{
    private readonly string? _endpoint;
    private readonly string? _apiKey;
    private readonly string? _modelId;

    public FoundryClientFactory(IConfiguration configuration)
    {
        var section = configuration.GetSection("Providers:MicrosoftFoundry");
        _endpoint = section.GetValue<string>("Endpoint");
        _apiKey = section.GetValue<string>("ApiKey");
        _modelId = section.GetValue<string>("ModelId") ?? "gpt-4o";
    }

    public IChatClient CreateChatClient()
    {
        if (_endpoint is null)
            throw new InvalidOperationException("Providers:MicrosoftFoundry:Endpoint is required.");
        if (_apiKey is null)
            throw new InvalidOperationException("Providers:MicrosoftFoundry:ApiKey is required.");

        var oaiClient = new OpenAIClient(
            new ApiKeyCredential(_apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(_endpoint) });
        return oaiClient.GetChatClient(_modelId).AsIChatClient();
    }
}
