using Agentweaver.Abstractions;
using Agentweaver.Providers.Storage.AzureFiles;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Environment;

/// <summary>Registers the Azure Files-backed workspace-volume provider and its Environment service dependencies.</summary>
public static class WorkspaceVolumeServiceCollectionExtensions
{
    /// <summary>Configures a TLS-validated Kubernetes API client and scoped workspace-volume services.</summary>
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
        // Both Kubernetes providers derive their cluster pin from this TLS trust bundle.
        services.AddSingleton<byte[]>(_ => File.ReadAllBytes(certificateAuthorityFile));
        services.AddHttpClient<IAzureFilesCsiClient, KubernetesAzureFilesCsiClient>(client =>
        {
            client.BaseAddress = trustedKubernetesBaseAddress;
            client.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            KubernetesServiceAccountHandler.Create(
                serviceAccountTokenFile,
                serviceProvider.GetRequiredService<byte[]>()));
        services.AddScoped<IWorkspaceVolumeProvider, AzureFilesCsiWorkspaceVolumeProvider>();
        services.AddScoped<WorkspaceVolumeService>();
        services.AddScoped<EnvironmentWorkspaceVolumeManager>();
        return services;
    }
}
