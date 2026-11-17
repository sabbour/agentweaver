using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Environment;

internal static class PlatformPolicySourceClientComposition
{
    internal static bool AddPlatformPolicySourceClient(
        this WebApplicationBuilder builder,
        Uri projectsBaseAddress)
    {
        if (!PlatformPolicySourceClientTls.IsEnabled(builder.Configuration))
            return false;

        var tlsIdentity = PlatformPolicySourceClientTls.CreateIdentity(
            builder.Configuration,
            projectsBaseAddress,
            TimeProvider.System);
        builder.Services.AddSingleton(tlsIdentity);
        builder.Services.AddSingleton(new PlatformPolicySourceHttpClient(projectsBaseAddress, tlsIdentity));
        return true;
    }
}
