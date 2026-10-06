using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Environment;

public static class WorkspaceVolumeServiceCollectionExtensions
{
    public static IServiceCollection AddAgentweaverWorkspaceVolumeService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<WorkspaceVolumeService>();
        return services;
    }
}
