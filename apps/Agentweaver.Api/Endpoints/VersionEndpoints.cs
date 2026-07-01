using Agentweaver.Api.Infrastructure;

namespace Agentweaver.Api.Endpoints;

public static class VersionEndpoints
{
    public static void MapVersionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/version", (IAppVersionProvider versionProvider) =>
            Results.Ok(new { version = versionProvider.Version }))
            .AllowAnonymous()
            .WithName("GetVersion");
    }
}
