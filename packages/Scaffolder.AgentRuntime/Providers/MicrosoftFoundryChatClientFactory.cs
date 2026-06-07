using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime.Providers;

/// <summary>
/// Builds an <see cref="IChatClient"/> backed by a Microsoft Foundry (Azure
/// OpenAI compatible) deployment (Principle II). Configuration is read from the
/// <c>Providers:MicrosoftFoundry</c> section. Config is validated lazily — only
/// when a run that requests this provider is actually submitted, so the API starts
/// successfully without Foundry config when only GitHub Copilot is in use.
/// </summary>
public sealed class MicrosoftFoundryChatClientFactory : IChatClientFactory
{
    private readonly IConfiguration _configuration;

    public MicrosoftFoundryChatClientFactory(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }

    public IChatClient CreateForRun(Run run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.ModelSource != ModelSource.MicrosoftFoundry)
        {
            throw new InvalidOperationException(
                $"Factory is for MicrosoftFoundry; run uses {run.ModelSource}.");
        }

        var section = _configuration.GetSection("Providers:MicrosoftFoundry");
        var endpoint = section.GetValue<string>("Endpoint")
            ?? throw new InvalidOperationException(
                "Missing configuration 'Providers:MicrosoftFoundry:Endpoint' for the Microsoft Foundry model source.");
        var apiKey = section.GetValue<string>("ApiKey")
            ?? throw new InvalidOperationException(
                "Missing configuration 'Providers:MicrosoftFoundry:ApiKey' for the Microsoft Foundry model source.");
        var deployment = section.GetValue<string>("Deployment")
            ?? throw new InvalidOperationException(
                "Missing configuration 'Providers:MicrosoftFoundry:Deployment' for the Microsoft Foundry model source.");

        var client = new AzureOpenAIClient(
            new Uri(endpoint),
            new AzureKeyCredential(apiKey));

        return client.GetChatClient(deployment).AsIChatClient();
    }
}
