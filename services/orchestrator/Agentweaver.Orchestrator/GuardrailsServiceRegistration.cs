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
        // The evidence journal is always registered, even without a classifier, so the gate has one place to
        // record decisions.
        services.AddSingleton<IGuardrailsEvidenceJournal>(provider =>
            new GuardrailsEvidenceStore(provider.GetRequiredService<NpgsqlDataSource>(), schema));
        // No Prompt Shields section means no classifier. The runtime gate then fails closed if a run or the
        // platform catalog still asks for Guardrails checks.
        var section = configuration.GetSection(PromptShieldsSection);
        if (!section.Exists())
            return;

        // Validate all settings at startup, so a bad configuration stops the service instead of failing turns later.
        var options = ReadOptions(section);
        var resourceId = section["ResourceId"];
        if (!long.TryParse(section["ResourceGeneration"], out var generation) || generation < 1 ||
            string.IsNullOrWhiteSpace(resourceId))
            throw new InvalidOperationException(
                $"{PromptShieldsSection} requires ResourceId and a positive ResourceGeneration.");

        // The HTTP client timeout is a little longer than the classifier's own timeout, so the classifier
        // reports a clean "timed out" result instead of an HttpClient cancellation. Redirects are disabled so
        // the bearer token is never sent to a different host.
        services.AddHttpClient(PromptShieldsSection, client => client.Timeout = options.RequestTimeout + TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        // The check registration pins the classifier to a resource ID and generation. These values become part of
        // the evidence, so a later change to the Content Safety resource is visible in the record.
        services.AddSingleton<IGuardrailsCheckRegistration>(provider =>
            new GuardrailsCheckRegistration<PromptShieldsClassifierOptions>(
                options,
                new PromptShieldsClassifier(
                    provider.GetRequiredService<IHttpClientFactory>().CreateClient(PromptShieldsSection),
                    new TokenCredentialClassifierCredential(provider.GetRequiredService<TokenCredential>()),
                    options.ProviderId),
                resourceId,
                generation));
        // The resolver matches the run's accepted checks against the registered classifiers and the catalog.
        services.AddSingleton(provider => new GuardrailsStagePlanResolver(
            provider.GetRequiredService<ProviderCatalog>(),
            provider.GetServices<IGuardrailsCheckRegistration>()));
    }

    /// <summary>Reads and validates the Prompt Shields options.</summary>
    internal static PromptShieldsClassifierOptions ReadOptions(IConfigurationSection section)
    {
        // Default to a 10-second timeout. A value that isn't a number also falls back to 10.
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
            // Validate applies the classifier's own rules (for example, HTTPS only). Wrap its error so startup
            // reports which configuration section is wrong.
            options.Validate();
        }
        catch (ArgumentException failure)
        {
            throw new InvalidOperationException($"{PromptShieldsSection} is invalid.", failure);
        }
        return options;
    }

    // Gets a Microsoft Entra token for Cognitive Services from the Orchestrator workload identity.
    // No API key is stored or read.
    private sealed class TokenCredentialClassifierCredential(TokenCredential credential) : IGuardrailsClassifierCredential
    {
        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            (await credential.GetTokenAsync(
                new TokenRequestContext([CognitiveServicesScope]), cancellationToken).ConfigureAwait(false)).Token;
    }
}
