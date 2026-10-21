using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Azure.Core;
using Npgsql;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Registers the Guardrails evidence journal and, when configured, the Azure AI Content Safety Prompt Shields check.
/// </summary>
internal static class GuardrailsServiceRegistration
{
    internal const string PromptShieldsSection = "Guardrails:PromptShields";
    internal const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// Adds the evidence journal. When <c>Guardrails:PromptShields</c> exists, also adds the Prompt Shields
    /// classifier and the ordered stage plan resolver. The classifier uses the Orchestrator workload identity
    /// and never reads an API key.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the Prompt Shields section is incomplete or invalid.</exception>
    internal static void AddGuardrails(this IServiceCollection services, IConfiguration configuration, string schema)
    {
        services.AddSingleton<IGuardrailsEvidenceJournal>(provider =>
            new GuardrailsEvidenceStore(provider.GetRequiredService<NpgsqlDataSource>(), schema));
        var section = configuration.GetSection(PromptShieldsSection);
        if (!section.Exists())
            return;

        var options = ReadOptions(section);
        var resourceId = section["ResourceId"];
        if (!long.TryParse(section["ResourceGeneration"], out var generation) || generation < 1 ||
            string.IsNullOrWhiteSpace(resourceId))
            throw new InvalidOperationException(
                $"{PromptShieldsSection} requires ResourceId and a positive ResourceGeneration.");

        services.AddHttpClient(PromptShieldsSection, client => client.Timeout = options.RequestTimeout + TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IGuardrailsCheckRegistration>(provider =>
            new GuardrailsCheckRegistration<PromptShieldsClassifierOptions>(
                options,
                new PromptShieldsClassifier(
                    provider.GetRequiredService<IHttpClientFactory>().CreateClient(PromptShieldsSection),
                    new TokenCredentialClassifierCredential(provider.GetRequiredService<TokenCredential>()),
                    options.ProviderId),
                resourceId,
                generation));
        services.AddSingleton(provider => new GuardrailsStagePlanResolver(
            provider.GetRequiredService<ProviderCatalog>(),
            provider.GetServices<IGuardrailsCheckRegistration>()));
    }

    /// <summary>Reads and validates the Prompt Shields options.</summary>
    internal static PromptShieldsClassifierOptions ReadOptions(IConfigurationSection section)
    {
        var timeoutSeconds = section["RequestTimeoutSeconds"] is { } value && int.TryParse(value, out var parsed)
            ? parsed
            : 10;
        if (!Uri.TryCreate(section["Endpoint"], UriKind.Absolute, out var endpoint) ||
            string.IsNullOrWhiteSpace(section["OptionsRevision"]) ||
            timeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException(
                $"{PromptShieldsSection} requires an HTTPS Endpoint, an OptionsRevision, and RequestTimeoutSeconds from 1 to 60.");
        var options = new PromptShieldsClassifierOptions(
            section["ProviderId"] ?? PromptShieldsClassifier.DefaultProviderId,
            section["OptionsRevision"]!,
            endpoint,
            section["ApiVersion"] ?? "2024-09-01",
            TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            options.Validate();
        }
        catch (ArgumentException failure)
        {
            throw new InvalidOperationException($"{PromptShieldsSection} is invalid.", failure);
        }
        return options;
    }

    private sealed class TokenCredentialClassifierCredential(TokenCredential credential) : IGuardrailsClassifierCredential
    {
        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            (await credential.GetTokenAsync(
                new TokenRequestContext([CognitiveServicesScope]), cancellationToken).ConfigureAwait(false)).Token;
    }
}
