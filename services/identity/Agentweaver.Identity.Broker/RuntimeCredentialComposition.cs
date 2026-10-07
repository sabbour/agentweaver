using Agentweaver.Identity;

namespace Agentweaver.Identity.Broker;

public sealed record RuntimeBootstrapOptions(
    Uri OrchestratorOwnerAddress,
    Uri EnvironmentOwnerAddress,
    TimeSpan BootstrapLifetime,
    TimeSpan SourceLifetime);

public static class RuntimeCredentialComposition
{
    public static void AddIdentityRuntimeCredentials(
        this IServiceCollection services, RuntimeBootstrapOptions options, string issuer)
    {
        var orchestrator = RuntimeOwnerHttpTransport.RequireOwnerAddress(options.OrchestratorOwnerAddress);
        var environment = RuntimeOwnerHttpTransport.RequireOwnerAddress(options.EnvironmentOwnerAddress);
        services.AddSingleton(new RuntimeCredentialPolicy(
            issuer, options.BootstrapLifetime, options.SourceLifetime));
        services.AddHttpClient(nameof(RuntimeRegistrationHttpClient))
            .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
        services.AddHttpClient(nameof(BrokerRuntimeBootstrapDeliveryClient))
            .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
        services.AddSingleton(options);
        services.AddTransient<IRuntimeRegistrationOwner>(provider =>
            new RuntimeRegistrationHttpClient(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(RuntimeRegistrationHttpClient)),
                orchestrator));
        services.AddTransient<IRuntimeBootstrapDelivery>(provider =>
            new BrokerRuntimeBootstrapDeliveryClient(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(BrokerRuntimeBootstrapDeliveryClient)),
                environment));
    }
}
