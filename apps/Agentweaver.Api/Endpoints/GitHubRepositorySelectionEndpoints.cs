using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Security;

namespace Agentweaver.Api.Endpoints;

/// <summary>Pre-project endpoints that turn a caller's Repo App browse choice into opaque authority.</summary>
public static class GitHubRepositorySelectionEndpoints
{
    public static void MapGitHubRepositorySelectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/github/repository-selections", async (
            HttpContext httpContext,
            GitHubRepositorySelectionBroker broker,
            CancellationToken ct) =>
        {
            var caller = httpContext.GetCaller();
            if (RejectUnauthorizedSelectionCaller(httpContext, caller) is { } forbidden)
                return forbidden;

            var result = await broker.ListAsync(caller, ct).ConfigureAwait(false);
            return result.Outcome switch
            {
                GitHubRepositorySelectionOutcome.Issued => Results.Ok(new GitHubRepositorySelectionListResponse
                {
                    Repositories = result.Candidates.Select(candidate => new GitHubRepositorySelectionCandidateDto
                    {
                        FullName = candidate.FullName,
                        OwnerLogin = candidate.OwnerLogin,
                        IsPrivate = candidate.IsPrivate,
                        DefaultBranch = candidate.DefaultBranch,
                        PushedAt = candidate.PushedAt,
                    }).ToList(),
                }),
                GitHubRepositorySelectionOutcome.GitHubBindingUnavailable =>
                    Results.Conflict(new { error = "github_binding_unavailable" }),
                GitHubRepositorySelectionOutcome.GitHubCapabilityTransientError =>
                    Results.Json(new { error = "github_capability_transient" }, statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Conflict(new { error = "github_capability_unavailable" }),
            };
        })
        .WithName("ListGitHubRepositorySelections")
        .WithTags("GitHub", "Projects")
        .AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Description = "Lists bounded metadata-only repositories from the signed-in human's active Repo App authorization. The response is not repository authority.";
            return Task.CompletedTask;
        });

        app.MapPost("/api/github/repository-selections", async (
            HttpContext httpContext,
            IssueGitHubRepositorySelectionRequest? request,
            GitHubRepositorySelectionBroker broker,
            CancellationToken ct) =>
        {
            var caller = httpContext.GetCaller();
            if (RejectUnauthorizedSelectionCaller(httpContext, caller) is { } forbidden)
                return forbidden;
            if (string.IsNullOrWhiteSpace(request?.FullName))
                return Results.BadRequest(new { error = "full_name is required." });

            var result = await broker.IssueAsync(caller, request.FullName, ct)
                .ConfigureAwait(false);
            return result.Outcome switch
            {
                GitHubRepositorySelectionOutcome.Issued => Results.Ok(new GitHubRepositorySelectionCodeResponse
                {
                    SelectionCode = result.Code!,
                    ExpiresAt = result.ExpiresAt!.Value,
                }),
                GitHubRepositorySelectionOutcome.GitHubBindingUnavailable =>
                    Results.Conflict(new { error = "github_binding_unavailable" }),
                GitHubRepositorySelectionOutcome.GitHubCapabilityTransientError =>
                    Results.Json(new { error = "github_capability_transient" }, statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Conflict(new { error = "github_capability_unavailable" }),
            };
        })
        .WithName("IssueGitHubRepositorySelection")
        .WithTags("GitHub", "Projects")
        .AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Description = "Verifies one browse-result repository through the signed-in caller's active GitHub authorization and mints one short-lived, single-use opaque selection code. Repository and authorization identifiers are never returned. POST /api/projects accepts only this code as repository authority.";
            return Task.CompletedTask;
        });
    }

    private static IResult? RejectUnauthorizedSelectionCaller(HttpContext httpContext, CallerContext caller)
    {
        return HumanEntraSubjectAuthorization.Evaluate(caller, httpContext.User) == HumanEntraSubjectState.Allowed
            ? null
            : Results.Conflict(new { error = "human_entra_subject_required" });
    }
}
