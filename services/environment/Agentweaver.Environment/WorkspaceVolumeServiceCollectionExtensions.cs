using Agentweaver.Abstractions;
using Agentweaver.Providers.Storage.AzureFiles;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Environment;

public static class WorkspaceVolumeServiceCollectionExtensions
{
    public static IServiceCollection AddAgentweaverWorkspaceVolumeService(
        this IServiceCollection services,
        AzureFilesCsiOptions options,
        Uri kubernetesBaseAddress,
        string serviceAccountTokenFile,
        string certificateAuthorityFile)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(kubernetesBaseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceAccountTokenFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateAuthorityFile);
        var trustedKubernetesBaseAddress = EnvironmentHttpTransport.RequireTrustedHttpsBaseUri(
            kubernetesBaseAddress.ToString(),
            nameof(kubernetesBaseAddress));

        services.AddSingleton(options.Validate());
        services.AddHttpClient<IAzureFilesCsiClient, KubernetesAzureFilesCsiClient>(client =>
        {
            client.BaseAddress = trustedKubernetesBaseAddress;
            client.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(() =>
            KubernetesServiceAccountHandler.Create(serviceAccountTokenFile, certificateAuthorityFile));
        services.AddScoped<IWorkspaceVolumeProvider, AzureFilesCsiWorkspaceVolumeProvider>();
        services.AddScoped<WorkspaceVolumeService>();
        services.AddScoped<EnvironmentWorkspaceVolumeManager>();
        return services;
    }
}
