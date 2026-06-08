using Azure;
using Azure.AI.Inference;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime.Providers;

/// <summary>
/// Builds an <see cref="IChatClient"/> backed by a Microsoft Foundry deployment
/// through the <see cref="ChatCompletionsClient"/> from
/// <c>Azure.AI.Inference</c> (Principle II). Configuration is read from the
/// <c>Providers:MicrosoftFoundry</c> section at startup.
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

        // Security (Seraph F8): the model endpoint receives every prompt, tool
        // schema, and run-context value. Reject any non-HTTPS endpoint at startup
        // so credentials and prompts are never sent over an unencrypted channel.
        if (!Uri.TryCreate(_endpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new InvalidOperationException(
                $"Configuration 'Providers:MicrosoftFoundry:Endpoint' is not a valid absolute URI: '{_endpoint}'.");
        }

        if (!string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Configuration 'Providers:MicrosoftFoundry:Endpoint' must use the 'https' scheme; " +
                $"got '{endpointUri.Scheme}'. Plain-text model endpoints are not permitted.");
        }
    }

    public IChatClient CreateForRun(Run run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.ModelSource != ModelSource.MicrosoftFoundry)
        {
            throw new InvalidOperationException(
                $"Factory is for MicrosoftFoundry; run uses {run.ModelSource}.");
        }

        var client = new ChatCompletionsClient(
            new Uri(_endpoint),
            new AzureKeyCredential(_apiKey));

        return client.AsIChatClient(_deployment);
    }
}
