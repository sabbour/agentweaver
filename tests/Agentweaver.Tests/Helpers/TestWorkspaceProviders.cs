using Microsoft.Extensions.Configuration;
using Agentweaver.Api.Infrastructure;

namespace Agentweaver.Tests.Helpers;

internal static class TestWorkspaceProviders
{
    public static LocalFilesystemWorkspaceProvider CreateLocal(string? rootPath = null)
    {
        var builder = new ConfigurationBuilder();
        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Workspace:Local:RootPath"] = rootPath
            });
        }

        return new LocalFilesystemWorkspaceProvider(builder.Build());
    }
}
