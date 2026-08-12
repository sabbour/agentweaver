using Agentweaver.AgentRuntime;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;

namespace Agentweaver.Api.Security;

public sealed record VerifiedAuthor(
    string AgentName,
    string SourceKind,
    string SourceIdentity,
    string? SourceRunId,
    bool IsCoordinator);

public static class RunAuthorship
{
    public static async Task<(VerifiedAuthor? Author, IResult? Failure)> ResolveApproverAsync(
        HttpContext httpContext,
        Project project,
        IConfiguration configuration,
        IRunSubmittingUserResolver runResolver,
        IRunAuthorshipCapabilityStore capabilityStore,
        CancellationToken ct)
    {
        var (author, failure) = await ResolveAsync(
            httpContext, project.Id.ToString(), requestedAgentName: null, runResolver, capabilityStore, ct);
        if (failure is not null)
            return (null, failure);

        if (author!.SourceKind == MemorySourceKinds.Run)
            return author.IsCoordinator
                ? (author, null)
                : (null, Forbidden("coordinator_approval_required"));

        var forbid = await ProjectAuthorization.RequireAccessAsync(
            httpContext, project, configuration, ProjectRole.Owner, ct).ConfigureAwait(false);
        return forbid is null ? (author, null) : (null, forbid);
    }

    public static async Task<(VerifiedAuthor? Author, IResult? Failure)> ResolveAsync(
        HttpContext httpContext,
        string projectId,
        string? requestedAgentName,
        IRunSubmittingUserResolver runResolver,
        IRunAuthorshipCapabilityStore capabilityStore,
        CancellationToken ct)
    {
        var caller = GitHubTokenAuthMiddleware.GetCaller(httpContext);
        if (!httpContext.User.HasClaim("agentweaver_internal", "true"))
        {
            var displayName = string.IsNullOrWhiteSpace(requestedAgentName)
                ? caller.User
                : requestedAgentName.Trim();
            return (new VerifiedAuthor(
                displayName,
                MemorySourceKinds.Human,
                caller.User,
                SourceRunId: null,
                IsCoordinator: false), null);
        }

        var runId = httpContext.Request.Headers[RunAuthorshipHeaders.RunId].ToString();
        var suppliedToken = httpContext.Request.Headers[RunAuthorshipHeaders.RunToken].ToString();
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(suppliedToken))
            return (null, Forbidden("verified_run_identity_required"));

        if (!await capabilityStore.ValidateAsync(runId, suppliedToken, ct).ConfigureAwait(false))
            return (null, Forbidden("invalid_run_identity"));

        var (runProjectId, runAgentName) = await runResolver.GetRunIdentityAsync(runId, ct).ConfigureAwait(false);
        if (!string.Equals(runProjectId, projectId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(runAgentName))
            return (null, Forbidden("run_identity_scope_mismatch"));

        if (!string.IsNullOrWhiteSpace(requestedAgentName)
            && !string.Equals(runAgentName, requestedAgentName, StringComparison.OrdinalIgnoreCase))
            return (null, Forbidden("agent_identity_mismatch"));

        return (new VerifiedAuthor(
            runAgentName,
            MemorySourceKinds.Run,
            $"run:{runId}",
            runId,
            string.Equals(runAgentName, "coordinator", StringComparison.OrdinalIgnoreCase)), null);
    }

    private static IResult Forbidden(string error) =>
        Results.Json(new { error }, statusCode: StatusCodes.Status403Forbidden);
}
