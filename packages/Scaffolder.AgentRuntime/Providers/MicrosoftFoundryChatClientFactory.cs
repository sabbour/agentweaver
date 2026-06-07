using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime.Providers;

/// <summary>
/// Builds an <see cref="IChatClient"/> backed by a Microsoft Foundry (Azure
/// OpenAI compatible) deployment (Principle II). Configuration is read from the
/// <c>Providers:MicrosoftFoundry</c> section.
/// </summary>
public sealed class MicrosoftFoundryChatClientFactory : IChatClientFactory
{
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _deployment;

    public MicrosoftFoundryChatClientFactory(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Providers:MicrosoftFoundry");
        _endpoint = section.GetValue<string>("Endpoint")
            ?? throw new InvalidOperationException(
                "Missing configuration 'Providers:MicrosoftFoundry:Endpoint' for the Microsoft Foundry model source.");
        _apiKey = section.GetValue<string>("ApiKey")
            ?? throw new InvalidOperationException(
                "Missing configuration 'Providers:MicrosoftFoundry:ApiKey' for the Microsoft Foundry model source.");
        _deployment = section.GetValue<string>("Deployment")
            ?? throw new InvalidOperationException(
                "Missing configuration 'Providers:MicrosoftFoundry:Deployment' for the Microsoft Foundry model source.");
    }

    public IChatClient CreateForRun(Run run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.ModelSource != ModelSource.MicrosoftFoundry)
        {
            throw new InvalidOperationException(
                $"Factory is for MicrosoftFoundry; run uses {run.ModelSource}.");
        }

        var client = new AzureOpenAIClient(
            new Uri(_endpoint),
            new AzureKeyCredential(_apiKey));

        return client.GetChatClient(_deployment).AsIChatClient();
    }
}
