using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.Projects.Config;

/// <summary>One bundled skill resource sent to the preview or import endpoint.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillContentResourceRequest
{
    public required string RelativePath { get; init; }
    public required byte[] Content { get; init; }
}

/// <summary>Strict API payload for a skill manifest and its bounded resource inventory.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillContentCandidateRequest
{
    public required byte[] SkillMarkdown { get; init; }
    public required ImmutableArray<SkillContentResourceRequest?> Resources { get; init; }

    /// <summary>Rejects missing, excessive, or malformed resource entries before creating the internal candidate.</summary>
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

/// <summary>Request to validate a skill candidate without persisting it.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewSkillContentRequest
{
    public required SkillContentCandidateRequest Candidate { get; init; }
}

/// <summary>Request to import a validated candidate at an expected content digest and idempotency key.</summary>
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

/// <summary>Request to pin a skill revision and agent assignment in project configuration.</summary>
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

/// <summary>Request to revoke one immutable skill revision with a human-readable reason.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RevokeSkillRevisionRequest
{
    public required string Reason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefreshAcceptedRunSkillPolicyPinRequest
{
    public required string PolicyId { get; init; }
    public required long Revision { get; init; }
    public required string ContentSha256 { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefreshAcceptedRunSkillRequest
{
    public required string SkillId { get; init; }
    public required long Revision { get; init; }
    public required string ContentDigest { get; init; }
    public required RefreshAcceptedRunSkillPolicyPinRequest ExpectedPlatformPolicy { get; init; }
    public RefreshAcceptedRunSkillPolicyPinRequest? ExpectedProjectPolicy { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Reason { get; init; }
    public required string CorrelationId { get; init; }
}

/// <summary>Maps the authenticated skill-content owner routes and applies request-size and no-store controls.</summary>
public static class SkillContentEndpoints
{
    private const long MaxContentRequestBytes = 3 * 1024 * 1024;

    /// <summary>Registers skill preview, packaging, inventory, import, assignment, revocation, and accepted-run read routes.</summary>
    /// <param name="app">The application route builder.</param>
    /// <returns>The same route builder for endpoint composition.</returns>
    public static IEndpointRouteBuilder MapSkillContentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/platform/skills/admission/cutover", async (
                SkillReleaseInventoryManifest request,
                HttpContext context,
                ProjectAuthorizationOwner authorizationOwner,
                CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                try
                {
                    RejectQuery(context);
                    var caller = await ResolveCallerAsync(
                            context, authorizationOwner, cancellationToken)
                        .ConfigureAwait(false);
                    await context.RequestServices
                        .GetRequiredService<SkillAdmissionService>()
                        .ActivateAsync(caller, request, cancellationToken)
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
            .RequireAuthorization()
            .WithName("ActivateSkillAdmissionCutover");

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

        preview.MapPost("/package", async (
            PackageSkillContentRequest request,
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
                return Results.Ok(SkillContentPackager.Package(request, service.Preview));
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
        .WithName("PackageSkillContent");

        var projects = app.MapGroup("/api/projects/{projectId}/skills").RequireAuthorization();
        projects.MapGet("/", async (
            string projectId,
            string? cursor,
            int? pageSize,
            HttpContext context,
            ProjectAuthorizationOwner authorizationOwner,
            ISkillContentService service,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Query.Keys.Any(key => key is not ("cursor" or "pageSize")) ||
                    context.Request.Query.Any(item => item.Value.Count > 1))
                    throw SkillContentServiceException.Invalid(
                        "The skill inventory query contains an unsupported or repeated parameter.");

                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(await service.ListInventoryAsync(
                    caller,
                    projectId,
                    cursor,
                    pageSize ?? SkillContentService.DefaultInventoryPageSize,
                    cancellationToken).ConfigureAwait(false));
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
        .WithName("ListProjectSkillInventory");

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

        projects.MapGet("/{skillId}/revisions/{revision:long}/trust", async (
            string projectId,
            string skillId,
            long revision,
            HttpContext context,
            ProjectAuthorizationOwner authorizationOwner,
            ISkillContentService service,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                RejectQuery(context);
                var caller = await ResolveCallerAsync(
                        context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(await service.ReadTrustProjectionAsync(
                    caller, projectId, skillId, revision, cancellationToken).ConfigureAwait(false));
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
        .WithName("ReadProjectSkillTrust");

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

        app.MapGet(
            "/api/projects/{projectId}/runs/{runId}/agents/{agentId}/skills/refresh-inputs",
            async (
                string projectId,
                string runId,
                string agentId,
                HttpContext context,
                ProjectAuthorizationOwner authorizationOwner,
                SkillContentService service,
                CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                try
                {
                    EnsureNoQueryBadRequest(context);
                    var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(await service.ReadRefreshInputsAsync(
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
            })
            .RequireAuthorization()
            .WithName("ReadAcceptedRunSkillRefreshInputs");

        app.MapPost(
            "/api/projects/{projectId}/runs/{runId}/agents/{agentId}/skills/refresh",
            async (
                string projectId,
                string runId,
                string agentId,
                HttpContext context,
                ProjectAuthorizationOwner authorizationOwner,
                ProjectSkillTrustEvidenceService trustEvidence,
                CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                try
                {
                    EnsureNoQueryBadRequest(context);
                    var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                        .ConfigureAwait(false);
                    caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
                    caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
                    caller.RequireHumanActor();
                    if (!context.Request.HasJsonContentType())
                        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
                    if (context.Request.ContentLength > MaxRefreshRequestBytes)
                        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    var body = await context.Request
                        .ReadFromJsonAsync<RefreshAcceptedRunSkillRequest>(cancellationToken)
                        .ConfigureAwait(false);
                    if (body is null ||
                        body.ExpectedPlatformPolicy is null)
                        throw SkillContentServiceException.Invalid(
                            "A complete Skill refresh request is required.");
                    var request = new ProjectSkillRefreshRequest(
                        projectId,
                        runId,
                        agentId,
                        body.SkillId,
                        body.Revision,
                        body.ContentDigest,
                        new ProjectSkillRefreshPolicyPin(
                            body.ExpectedPlatformPolicy.PolicyId,
                            body.ExpectedPlatformPolicy.Revision,
                            body.ExpectedPlatformPolicy.ContentSha256),
                        body.ExpectedProjectPolicy is null
                            ? null
                            : new ProjectSkillRefreshPolicyPin(
                                body.ExpectedProjectPolicy.PolicyId,
                                body.ExpectedProjectPolicy.Revision,
                                body.ExpectedProjectPolicy.ContentSha256),
                        body.IdempotencyKey,
                        body.Reason,
                        body.CorrelationId);
                    var receipt = await trustEvidence.RefreshAsync(caller, request, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(new AcceptedRunSkillRefreshReceipt(
                        receipt.EvidenceId,
                        receipt.AdmissionId,
                        new AcceptedRunSkillRefreshPolicyPin(
                            receipt.PlatformPolicy.PolicyId,
                            receipt.PlatformPolicy.Revision,
                            receipt.PlatformPolicy.ContentSha256),
                        receipt.ProjectPolicy is null
                            ? null
                            : new AcceptedRunSkillRefreshPolicyPin(
                                receipt.ProjectPolicy.PolicyId,
                                receipt.ProjectPolicy.Revision,
                                receipt.ProjectPolicy.ContentSha256),
                        receipt.ObservedAt,
                        receipt.Replayed));
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
                catch (BadHttpRequestException exception)
                {
                    return Results.StatusCode(exception.StatusCode);
                }
                catch (System.Text.Json.JsonException)
                {
                    return ToProblem(SkillContentServiceException.Invalid(
                        "The Skill refresh request body is malformed."));
                }
            })
            .WithMetadata(new RequestSizeLimitAttribute(MaxRefreshRequestBytes))
            .RequireAuthorization()
            .WithName("RefreshAcceptedRunSkill");

        return app;
    }

    private const int MaxRefreshRequestBytes = 64 * 1024;

    /// <summary>Rejects any query parameter or bare query marker with 400 Bad Request.</summary>
    private static void EnsureNoQueryBadRequest(HttpContext context)
    {
        if (context.Request.Query.Count != 0 || context.Request.QueryString.HasValue)
            throw SkillContentServiceException.Invalid("Skill refresh routes do not accept query parameters.");
    }

    /// <summary>Rejects query parameters on routes whose request contract is body-only.</summary>
    private static void RejectQuery(HttpContext context)
    {
        if (context.Request.Query.Count != 0)
            throw ProjectConfigException.Forbidden();
    }

    /// <summary>Resolves current authority from the authenticated principal and tenant selector header.</summary>
    private static Task<ProjectAuthorizationContext> ResolveCallerAsync(
        HttpContext context,
        ProjectAuthorizationOwner authorizationOwner,
        CancellationToken cancellationToken) =>
        authorizationOwner.ResolveAsync(
            context.User,
            context.Request.Headers[ProjectAuthorizationOwner.TenantSelectorHeader].ToArray(),
            cancellationToken);

    /// <summary>Maps an owner authorization or project error to a problem response.</summary>
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
