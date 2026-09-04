using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Auth;

namespace Agentweaver.Api.Endpoints;

public static class VersionEndpoints
{
    public static void MapVersionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/version", (IAppVersionProvider versionProvider) =>
            Results.Ok(new
            {
                version = versionProvider.Version,
                gitSha = versionProvider.GitSha,
                isRelease = versionProvider.IsRelease,
            }))
            .OperationalAnonymous()
            .WithName("GetVersion");
    }
}
