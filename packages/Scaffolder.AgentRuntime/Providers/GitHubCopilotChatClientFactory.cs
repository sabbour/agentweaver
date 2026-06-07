using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime.Providers;

/// <summary>
/// Builds an <see cref="IChatClient"/> backed by the GitHub Copilot model
/// endpoint, which exposes an OpenAI-compatible chat completions surface
/// (Principle II). Configuration is read from the <c>Providers:GitHubCopilot</c>
/// section.
/// </summary>
public sealed class GitHubCopilotChatClientFactory : IChatClientFactory
{
    private const string DefaultEndpoint = "https://api.githubcopilot.com";
    private const string DefaultModel = "gpt-4o";

    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;

    public GitHubCopilotChatClientFactory(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Providers:GitHubCopilot");
        _endpoint = section.GetValue("Endpoint", DefaultEndpoint) ?? DefaultEndpoint;
        _model = section.GetValue("Model", DefaultModel) ?? DefaultModel;
        _apiKey = section.GetValue<string>("ApiKey")
            ?? throw new InvalidOperationException(
                "Missing configuration 'Providers:GitHubCopilot:ApiKey' for the GitHub Copilot model source.");
    }

    public IChatClient CreateForRun(Run run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.ModelSource != ModelSource.GitHubCopilot)
        {
            throw new InvalidOperationException(
                $"Factory is for GitHubCopilot; run uses {run.ModelSource}.");
        }

        var client = new OpenAIClient(
            new ApiKeyCredential(_apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(_endpoint) });

        return client.GetChatClient(_model).AsIChatClient();
    }
}
