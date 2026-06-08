using Azure;
using Azure.AI.Inference;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

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

        return new ChatCompletionsClient(
            new Uri(_endpoint),
            new AzureKeyCredential(_apiKey)
        ).AsIChatClient(_modelId);
    }
}
