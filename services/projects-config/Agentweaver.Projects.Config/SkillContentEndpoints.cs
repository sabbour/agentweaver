using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.Projects.Config;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillContentResourceRequest
{
    public required string RelativePath { get; init; }
    public required byte[] Content { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillContentCandidateRequest
{
    public required byte[] SkillMarkdown { get; init; }
    public required ImmutableArray<SkillContentResourceRequest?> Resources { get; init; }

    public SkillContentCandidate ToCandidate()
    {
        if (SkillMarkdown is null || Resources.IsDefault)
            throw new SkillContentValidationException("The skill markdown and resource inventory are required.");
        if (Resources.Length > SkillContentValidator.MaxResourceCount)
            throw new SkillContentValidationException(
                $"Skill has more than {SkillContentValidator.MaxResourceCount} bundled resources.");

        var resources = ImmutableArray.CreateBuilder<SkillContentResourceInput>(Resources.Length);
        foreach (var resource in Resources)
        {
            if (resource is null || string.IsNullOrWhiteSpace(resource.RelativePath) || resource.Content is null)
                throw new SkillContentValidationException("Each skill resource requires a path and content.");
            resources.Add(new SkillContentResourceInput(resource.RelativePath, resource.Content));
        }

        return new SkillContentCandidate(SkillMarkdown, resources.ToImmutable());
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewSkillContentRequest
{
    public required SkillContentCandidateRequest Candidate { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImportSkillContentRequest
{
    public required string IdempotencyKey { get; init; }
    public required string ExpectedContentDigest { get; init; }
    public string? SkillId { get; init; }
    public long? ExpectedRevision { get; init; }
    public SkillContentSourceSelection? Source { get; init; }
    public required SkillContentCandidateRequest Candidate { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateSkillAssignmentRequest
{
    public required long ExpectedProjectConfigurationRevision { get; init; }
    public required long Revision { get; init; }
    public required string ContentDigest { get; init; }
    public required bool Enabled { get; init; }
    public required int Order { get; init; }
    public required ImmutableArray<string> AgentIds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RevokeSkillRevisionRequest
{
    public required string Reason { get; init; }
}

public static class SkillContentEndpoints
{
    private const long MaxContentRequestBytes = 3 * 1024 * 1024;

    public static IEndpointRouteBuilder MapSkillContentEndpoints(this IEndpointRouteBuilder app)
    {
        var preview = app.MapGroup("/api/skills").RequireAuthorization();
        preview.MapPost("/preview", async (
            PreviewSkillContentRequest request,
            HttpContext context,
            ProjectAuthorizationOwner authorizationOwner,
            ISkillContentService service,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                RejectQuery(context);
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
                caller.RequireUnboundRequest();
                return Results.Ok(service.Preview(request.Candidate.ToCandidate()));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (SkillContentValidationException exception)
            {
                return ToProblem(exception);
            }
            catch (SkillContentServiceException exception)
            {
                return ToProblem(exception);
            }
        })
        .WithMetadata(new RequestSizeLimitAttribute(MaxContentRequestBytes))
        .WithName("PreviewSkillContent");

        var projects = app.MapGroup("/api/projects/{projectId}/skills").RequireAuthorization();
        projects.MapPost("/import", async (
            string projectId,
            ImportSkillContentRequest request,
            HttpContext context,
            ProjectAuthorizationOwner authorizationOwner,
            ISkillContentService service,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                RejectQuery(context);
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                var receipt = await service.ImportAsync(
                    caller,
                    projectId,
                    new SkillContentImportRequest(
                        request.IdempotencyKey,
                        request.ExpectedContentDigest,
                        request.SkillId,
                        request.ExpectedRevision,
                        request.Source,
                        request.Candidate.ToCandidate()),
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(receipt);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (SkillContentServiceException exception)
            {
                return ToProblem(exception);
            }
            catch (SkillContentValidationException exception)
            {
                return ToProblem(exception);
            }
            catch (SkillContentObjectStoreException exception)
            {
                return ToProblem(exception);
            }
        })
        .WithMetadata(new RequestSizeLimitAttribute(MaxContentRequestBytes))
        .WithName("ImportSkillContent");

        projects.MapPut("/{skillId}/assignment", async (
            string projectId,
            string skillId,
            UpdateSkillAssignmentRequest request,
            HttpContext context,
            ProjectAuthorizationOwner authorizationOwner,
            ISkillAssignmentService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RejectQuery(context);
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                var result = await service.UpdateAsync(
                    caller,
                    projectId,
                    new SkillAssignmentUpdateRequest(
                        request.ExpectedProjectConfigurationRevision,
                        skillId,
                        request.Revision,
                        request.ContentDigest,
                        request.Enabled,
                        request.Order,
                        request.AgentIds),
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (SkillContentServiceException exception)
            {
                return ToProblem(exception);
            }
        })
        .WithName("UpdateSkillAssignment");

        projects.MapPost("/{skillId}/revisions/{revision:long}/revoke", async (
            string projectId,
            string skillId,
            long revision,
            RevokeSkillRevisionRequest request,
            HttpContext context,
            ProjectAuthorizationOwner authorizationOwner,
            ISkillContentService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RejectQuery(context);
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                await service.RevokeAsync(
                    caller, projectId, skillId, revision, request.Reason, cancellationToken)
                    .ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (SkillContentServiceException exception)
            {
                return ToProblem(exception);
            }
        })
        .WithName("RevokeSkillRevision");

        app.MapGet(
            "/api/projects/{projectId}/runs/{runId}/agents/{agentId}/skills",
            async (
                string projectId,
                string runId,
                string agentId,
                HttpContext context,
                ProjectAuthorizationOwner authorizationOwner,
                ISkillContentService service,
                CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                try
                {
                    RejectQuery(context);
                    var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(await service.ReadAcceptedRunSkillsAsync(
                        caller, projectId, runId, agentId, cancellationToken).ConfigureAwait(false));
                }
                catch (ProjectConfigException exception)
                {
                    return ToProblem(exception);
                }
                catch (SkillContentServiceException exception)
                {
                    return ToProblem(exception);
                }
                catch (SkillContentObjectStoreException exception)
                {
                    return ToProblem(exception);
                }
            })
            .RequireAuthorization()
            .WithName("GetAcceptedRunSkills");

        return app;
    }

    private static void RejectQuery(HttpContext context)
    {
        if (context.Request.Query.Count != 0)
            throw ProjectConfigException.Forbidden();
    }

    private static Task<ProjectAuthorizationContext> ResolveCallerAsync(
        HttpContext context,
        ProjectAuthorizationOwner authorizationOwner,
        CancellationToken cancellationToken) =>
        authorizationOwner.ResolveAsync(
            context.User,
            context.Request.Headers[ProjectAuthorizationOwner.TenantSelectorHeader].ToArray(),
            cancellationToken);

    private static IResult ToProblem(ProjectConfigException exception) =>
        Results.Problem(
            title: exception.Code,
            detail: exception.Message,
            statusCode: exception.StatusCode,
            extensions: new Dictionary<string, object?> { ["code"] = exception.Code });

    private static IResult ToProblem(SkillContentServiceException exception) =>
        Results.Problem(
            title: exception.Code,
            detail: exception.Message,
            statusCode: exception.StatusCode,
            extensions: new Dictionary<string, object?> { ["code"] = exception.Code });

    private static IResult ToProblem(SkillContentValidationException exception) =>
        Results.Problem(
            title: SkillContentValidationException.ErrorCode,
            detail: exception.Message,
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = SkillContentValidationException.ErrorCode,
            });

    private static IResult ToProblem(SkillContentObjectStoreException exception)
    {
        var status = exception.Code == SkillContentObjectStoreException.UnavailableCode
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status500InternalServerError;
        return Results.Problem(
            title: exception.Code,
            detail: exception.Message,
            statusCode: status,
            extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
    }
}
