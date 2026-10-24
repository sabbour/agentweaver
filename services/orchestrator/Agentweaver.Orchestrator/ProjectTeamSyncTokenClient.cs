using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal interface IProjectTeamSyncTokenClient
{
    Task<ProjectTeamSyncTokenResponse> MintAsync(
        ProjectTeamSyncTokenRequest request,
        CancellationToken cancellationToken);
}

internal sealed class ProjectTeamSyncTokenClient(IServiceProvider services) : IProjectTeamSyncTokenClient
{
    public async Task<ProjectTeamSyncTokenResponse> MintAsync(
        ProjectTeamSyncTokenRequest request,
        CancellationToken cancellationToken)
    {
        // Identity accepts this token request only over the existing certificate-pinned worker channel.
        try
        {
            return await IssueSyncServiceTransport.SendAsync<ProjectTeamSyncTokenResponse>(
                services,
                IssueSyncAuthorityClient.Channel,
                "/internal/source-control/github-app/project-team-sync/token",
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (RuntimeAuthorizationException error)
        {
            var status = error.Code.Contains("unavailable", StringComparison.Ordinal) ||
                error.Code.Contains("invalid", StringComparison.Ordinal)
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status403Forbidden;
            throw new CoordinationException("team_sync_identity_" + error.Code, status, error);
        }
        catch (InvalidOperationException error)
        {
            throw new CoordinationException(
                "team_sync_identity_unavailable", StatusCodes.Status503ServiceUnavailable, error);
        }
        catch (HttpRequestException error)
        {
            throw new CoordinationException(
                "team_sync_identity_unavailable", StatusCodes.Status503ServiceUnavailable, error);
        }
        catch (System.Text.Json.JsonException error)
        {
            throw new CoordinationException(
                "team_sync_identity_contract_invalid", StatusCodes.Status502BadGateway, error);
        }
    }
}
