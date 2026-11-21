using System.Collections.Immutable;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Policy.Acs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Agentweaver.Projects.Config;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeAcsPolicyValidationRequest
{
    public required NativeAcsArtifact Artifact { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeAcsPolicyWriteBody
{
    public required long? ExpectedRevision { get; init; }
    public required NativeAcsArtifact Artifact { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Reason { get; init; }
    public required string CorrelationId { get; init; }
}

public sealed record NativeAcsPolicyValidationResponse(
    bool IsValid,
    ImmutableArray<NativeAcsDiagnostic> Diagnostics);

public static class NativeAcsPolicyEndpoints
{
    private const long MaximumRequestBodyBytes = 5L * 1024 * 1024;
    private static readonly JsonSerializerOptions ProjectValidationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
    };
    // Used only when the scope has no head yet. An existing head keeps its own stable ID.
    private const string NewPolicyId = "canonical";
    // Head-advancing writes stay off until every included domain has a proven enforcer and profile check.
    internal const string HeadWritesEnabledSetting = "NativeAcsPolicy:HeadWritesEnabled";

    public static IEndpointRouteBuilder MapNativeAcsPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/policies/platform").RequireAuthorization();
        platform.MapGet("/", async (
            HttpContext context,
            [FromServices] ProjectsConfigService projectsService,
            [FromServices] NativeAcsPolicyStore policyStore,
            [FromServices] ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            using var protectedRead = PlatformPolicyReadLoggerFactory.BeginProtectedRead();
            try
            {
                EnsureNoQuery(context, rejectEmptyQuery: true);
                EnsureNoReadBody(context, () => NativeAcsPolicyStore.ReadRequestInvalid());
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                await projectsService.EnsurePlatformPolicyReaderAsync(caller, cancellationToken)
                    .ConfigureAwait(false);
                IResult outcome;
                try
                {
                    var result = await policyStore.ReadPlatformHeadAsync(cancellationToken).ConfigureAwait(false);
                    outcome = result is null
                        ? Results.NotFound(new { code = "native_acs_policy_not_found" })
                        : Results.Ok(result);
                }
                catch (ProjectConfigException exception) when (exception.Code == "native_acs_policy_corrupt")
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    outcome = ToProblem(new ProjectConfigException(
                        "native_acs_policy_corrupt", "The current Platform Policy source is corrupt.",
                        StatusCodes.Status500InternalServerError));
                }
                catch (Exception exception) when (IsPlatformHeadSourceFailure(exception))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    outcome = SourceUnavailable();
                }
                await projectsService.EnsurePlatformPolicyReaderAsync(caller, cancellationToken)
                    .ConfigureAwait(false);
                return outcome;
            }
            catch (ProjectConfigException exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ToProblem(exception);
            }
            catch (Exception exception) when (IsPlatformHeadSourceFailure(exception))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return SourceUnavailable();
            }
        }).WithName("ReadPlatformNativeAcsPolicy");

        platform.MapGet("/revisions", async (
            HttpContext context,
            [FromServices] ProjectsConfigService projectsService,
            [FromServices] NativeAcsPolicyStore policyStore,
            [FromServices] ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Query.Keys.Any(key => key is not ("pageSize" or "cursor")) ||
                    context.Request.Query["pageSize"].Count > 1 ||
                    context.Request.Query["cursor"].Count > 1)
                    throw NativeAcsPolicyStore.HistoryRequestInvalid();
                EnsureNoReadBody(context, () => NativeAcsPolicyStore.HistoryRequestInvalid());
                var pageSize = NativeAcsPolicyStore.ParseHistoryPageSize(
                    context.Request.Query.TryGetValue("pageSize", out var sizes) ? sizes[0] : null);
                var cursor = context.Request.Query.TryGetValue("cursor", out var cursors)
                    ? cursors[0] ?? string.Empty
                    : null;
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                await projectsService.EnsurePlatformPolicyReaderAsync(caller, cancellationToken)
                    .ConfigureAwait(false);
                var page = await policyStore.ReadHistoryAsync(pageSize, cursor, cancellationToken)
                    .ConfigureAwait(false);
                await projectsService.EnsurePlatformPolicyReaderAsync(caller, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(page);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (NpgsqlException exception) when (IsSourceUnavailable(exception))
            {
                return SourceUnavailable();
            }
            catch (InvalidOperationException exception) when (IsSourceUnavailable(exception))
            {
                return SourceUnavailable();
            }
        }).WithName("ListPlatformNativeAcsPolicyRevisions");

        platform.MapGet("/revisions/{revision}", async (
            string revision,
            HttpContext context,
            [FromServices] ProjectsConfigService projectsService,
            [FromServices] NativeAcsPolicyStore policyStore,
            [FromServices] ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Query.Count != 0)
                    throw NativeAcsPolicyStore.RevisionRequestInvalid();
                EnsureNoReadBody(context, () => NativeAcsPolicyStore.RevisionRequestInvalid());
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                await projectsService.EnsurePlatformPolicyReaderAsync(caller, cancellationToken)
                    .ConfigureAwait(false);
                var result = await policyStore.ReadRevisionAsync(
                    NativeAcsPolicyStore.ParseRevision(revision),
                    cancellationToken).ConfigureAwait(false);
                await projectsService.EnsurePlatformPolicyReaderAsync(caller, cancellationToken)
                    .ConfigureAwait(false);
                return result is null
                    ? Results.NotFound(new { code = "native_acs_policy_not_found" })
                    : Results.Ok(result);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (NpgsqlException exception) when (IsSourceUnavailable(exception))
            {
                return SourceUnavailable();
            }
            catch (InvalidOperationException exception) when (IsSourceUnavailable(exception))
            {
                return SourceUnavailable();
            }
        }).WithName("ReadPlatformNativeAcsPolicyRevision");

        platform.MapPost("/validate", async (
            NativeAcsPolicyValidationRequest request,
            HttpContext context,
            [FromServices] ProjectsConfigService projectsService,
            [FromServices] ProjectAuthorizationOwner authorizationOwner,
            [FromServices] NativeAcsArtifactValidator? validator,
            [FromServices] NativeAcsPolicyRequestGate requestGate,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                EnsureNoQuery(context);
                if (request?.Artifact is null)
                    throw ProjectConfigException.Invalid(
                        "native_acs_policy_invalid",
                        "A complete native ACS policy artifact is required.");
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                await projectsService.EnsurePlatformPolicyReaderAsync(caller, cancellationToken)
                    .ConfigureAwait(false);
                if (!requestGate.TryAcquireCallerRequest(caller.Issuer, caller.ActorId))
                    return RateLimited(context);

                var error = ValidateArtifact(validator, requestGate, request.Artifact, context, out var result);
                return error ?? Results.Ok(new NativeAcsPolicyValidationResponse(true, result.Diagnostics));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("ValidatePlatformNativeAcsPolicy");

        platform.MapPut("/", async (
            NativeAcsPolicyWriteBody request,
            HttpContext context,
            [FromServices] NativeAcsPolicyStore policyStore,
            [FromServices] ProjectAuthorizationOwner authorizationOwner,
            [FromServices] NativeAcsArtifactValidator? validator,
            [FromServices] NativeAcsPolicyRequestGate requestGate,
            [FromServices] IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                EnsureNoQuery(context);
                if (request?.Artifact is null)
                    throw ProjectConfigException.Invalid(
                        "native_acs_policy_invalid",
                        "A complete native ACS policy artifact is required.");
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                var policyId = await policyStore.FindPolicyIdAsync(
                    NativeAcsPolicyScopeType.Platform,
                    ProjectAuthorizationOwner.PlatformResourceId,
                    cancellationToken).ConfigureAwait(false) ?? NewPolicyId;
                var writeRequest = new NativeAcsPolicyWriteRequest(
                    NativeAcsPolicyScopeType.Platform,
                    ProjectAuthorizationOwner.PlatformResourceId,
                    policyId,
                    request.ExpectedRevision,
                    request.Artifact,
                    request.IdempotencyKey,
                    request.Reason,
                    request.CorrelationId);
                NativeAcsPolicyStore.ValidateWriteMetadata(writeRequest);
                NativeAcsPolicyStore.EnsureWriteAuthorized(caller, writeRequest);
                // A retry of a committed request gets its receipt even when validation or writes are unavailable.
                var replay = await policyStore.FindReplayAsync(caller, writeRequest, cancellationToken)
                    .ConfigureAwait(false);
                if (replay is not null)
                    return Results.Ok(replay);
                var preparationReplay = await policyStore.FindPreparationReplayAsync(
                    caller, writeRequest, cancellationToken).ConfigureAwait(false);
                if (preparationReplay is not null)
                    return Results.Ok(preparationReplay);
                if (!configuration.GetValue<bool>(HeadWritesEnabledSetting))
                    return Results.Problem(
                        title: "native_acs_policy_writes_unavailable",
                        detail: "Policy head changes are not available on this deployment.",
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        extensions: new Dictionary<string, object?>
                        {
                            ["code"] = "native_acs_policy_writes_unavailable",
                        });
                if (!requestGate.TryAcquireCallerRequest(caller.Issuer, caller.ActorId))
                    return RateLimited(context);

                var error = ValidateArtifact(validator, requestGate, request.Artifact, context, out _);
                if (error is not null)
                    return error;
                var receipt = await policyStore.PrepareAsync(caller, writeRequest, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(receipt);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("WritePlatformNativeAcsPolicy");

        var projects = app.MapGroup("/api/policies/projects").RequireAuthorization();
        projects.MapGet("/", async (
            HttpContext context,
            [FromServices] NativeAcsPolicyStore policyStore,
            [FromServices] ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Query.Keys.Any(key => key is not ("pageSize" or "cursor")) ||
                    context.Request.Query["pageSize"].Count > 1 ||
                    context.Request.Query["cursor"].Count > 1)
                    throw NativeAcsPolicyStore.ScopeListRequestInvalid();
                EnsureNoReadBody(context, () => NativeAcsPolicyStore.ScopeListRequestInvalid());
                var pageSize = NativeAcsPolicyStore.ParseScopeListPageSize(
                    context.Request.Query.TryGetValue("pageSize", out var sizes) ? sizes[0] : null);
                var cursor = context.Request.Query.TryGetValue("cursor", out var cursors)
                    ? cursors[0] ?? string.Empty
                    : null;
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                var page = await policyStore.ReadProjectPolicyScopesAsync(
                    caller,
                    pageSize,
                    cursor,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(page);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (NpgsqlException exception) when (IsSourceUnavailable(exception))
            {
                return SourceUnavailable("Project");
            }
            catch (InvalidOperationException exception) when (IsSourceUnavailable(exception))
            {
                return SourceUnavailable("Project");
            }
        }).WithName("ListProjectNativeAcsPolicies");

        projects.MapPost("/{projectId}/validate", async (
            string projectId,
            JsonElement requestJson,
            HttpContext context,
            [FromServices] ProjectsConfigService projectsService,
            [FromServices] ProjectAuthorizationOwner authorizationOwner,
            [FromServices] NativeAcsArtifactValidator? validator,
            [FromServices] NativeAcsPolicyRequestGate requestGate,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                EnsureNoQuery(context, rejectEmptyQuery: true);
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                await projectsService.EnsureProjectPolicyAuthorAsync(caller, projectId, cancellationToken)
                    .ConfigureAwait(false);
                if (!requestGate.TryAcquireCallerRequest(caller.Issuer, caller.ActorId))
                    return RateLimited(context);
                if (HasAmbiguousProperties(requestJson))
                    return Results.BadRequest();
                var request = requestJson.Deserialize<NativeAcsPolicyValidationRequest>(ProjectValidationJsonOptions);
                if (request?.Artifact is null)
                    throw ProjectConfigException.Invalid(
                        "native_acs_policy_invalid",
                        "A complete native ACS policy artifact is required.");

                cancellationToken.ThrowIfCancellationRequested();
                var error = ValidateArtifact(validator, requestGate, request.Artifact, context, out var result, projectScope: true);
                cancellationToken.ThrowIfCancellationRequested();
                await projectsService.EnsureProjectPolicyAuthorAsync(caller, projectId, cancellationToken)
                    .ConfigureAwait(false);
                return error ?? Results.Ok(new NativeAcsPolicyValidationResponse(true, result.Diagnostics));
            }
            catch (ProjectConfigException exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ToProblem(exception);
            }
            catch (JsonException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Results.BadRequest();
            }
            catch (NpgsqlException exception) when (IsSourceUnavailable(exception))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return SourceUnavailable("Project");
            }
            catch (InvalidOperationException exception) when (IsSourceUnavailable(exception))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return SourceUnavailable("Project");
            }
        })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("ValidateProjectNativeAcsPolicy");

        projects.MapGet("/{projectId}", async (
            string projectId,
            HttpContext context,
            [FromServices] ProjectsConfigService projectsService,
            [FromServices] NativeAcsPolicyStore policyStore,
            [FromServices] ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                EnsureNoQuery(context);
                EnsureNoReadBody(context, () => NativeAcsPolicyStore.ReadRequestInvalid("Project"));
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                await projectsService.EnsureProjectPolicyReaderAsync(caller, projectId, cancellationToken)
                    .ConfigureAwait(false);
                var result = await policyStore.ReadAsync(
                    NativeAcsPolicyScopeType.Project,
                    projectId,
                    cancellationToken).ConfigureAwait(false);
                await projectsService.EnsureProjectPolicyReaderAsync(caller, projectId, cancellationToken)
                    .ConfigureAwait(false);
                return result is null
                    ? Results.NotFound(new { code = "native_acs_policy_not_found" })
                    : Results.Ok(result);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (NpgsqlException exception) when (IsSourceUnavailable(exception))
            {
                return SourceUnavailable("Project");
            }
            catch (InvalidOperationException exception) when (IsSourceUnavailable(exception))
            {
                return SourceUnavailable("Project");
            }
        }).WithName("ReadProjectNativeAcsPolicy");

        projects.MapGet("/{projectId}/revisions", async (
            string projectId,
            HttpContext context,
            [FromServices] ProjectsConfigService projectsService,
            [FromServices] NativeAcsPolicyStore policyStore,
            [FromServices] ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Query.Keys.Any(key => key is not ("pageSize" or "cursor")) ||
                    context.Request.Query["pageSize"].Count > 1 ||
                    context.Request.Query["cursor"].Count > 1)
                    throw NativeAcsPolicyStore.HistoryRequestInvalid("Project");
                EnsureNoReadBody(context, () => NativeAcsPolicyStore.HistoryRequestInvalid("Project"));
                var pageSize = NativeAcsPolicyStore.ParseHistoryPageSize(
                    context.Request.Query.TryGetValue("pageSize", out var sizes) ? sizes[0] : null,
                    "Project");
                var cursor = context.Request.Query.TryGetValue("cursor", out var cursors)
                    ? cursors[0] ?? string.Empty
                    : null;
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                await projectsService.EnsureProjectPolicyReaderAsync(caller, projectId, cancellationToken)
                    .ConfigureAwait(false);
                var page = await policyStore.ReadHistoryAsync(
                    NativeAcsPolicyScopeType.Project,
                    projectId,
                    pageSize,
                    cursor,
                    cancellationToken).ConfigureAwait(false);
                await projectsService.EnsureProjectPolicyReaderAsync(caller, projectId, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(page);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (NpgsqlException exception) when (IsSourceUnavailable(exception))
            {
                return SourceUnavailable("Project");
            }
            catch (InvalidOperationException exception) when (IsSourceUnavailable(exception))
            {
                return SourceUnavailable("Project");
            }
        }).WithName("ListProjectNativeAcsPolicyRevisions");

        projects.MapGet("/{projectId}/revisions/{revision}", async (
            string projectId,
            string revision,
            HttpContext context,
            [FromServices] ProjectsConfigService projectsService,
            [FromServices] NativeAcsPolicyStore policyStore,
            [FromServices] ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Query.Count != 0)
                    throw NativeAcsPolicyStore.RevisionRequestInvalid("Project");
                EnsureNoReadBody(context, () => NativeAcsPolicyStore.RevisionRequestInvalid("Project"));
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                await projectsService.EnsureProjectPolicyReaderAsync(caller, projectId, cancellationToken)
                    .ConfigureAwait(false);
                var result = await policyStore.ReadRevisionAsync(
                    NativeAcsPolicyScopeType.Project,
                    projectId,
                    NativeAcsPolicyStore.ParseRevision(revision, "Project"),
                    cancellationToken).ConfigureAwait(false);
                await projectsService.EnsureProjectPolicyReaderAsync(caller, projectId, cancellationToken)
                    .ConfigureAwait(false);
                return result is null
                    ? Results.NotFound(new { code = "native_acs_policy_not_found" })
                    : Results.Ok(result);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
            catch (NpgsqlException exception) when (IsSourceUnavailable(exception))
            {
                return SourceUnavailable("Project");
            }
            catch (InvalidOperationException exception) when (IsSourceUnavailable(exception))
            {
                return SourceUnavailable("Project");
            }
        }).WithName("ReadProjectNativeAcsPolicyRevision");

        return app;
    }

    private static bool HasAmbiguousProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasAmbiguousProperties(property.Value))
                    return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (HasAmbiguousProperties(item))
                    return true;
        }
        return false;
    }

    private static IResult? ValidateArtifact(
        NativeAcsArtifactValidator? validator,
        NativeAcsPolicyRequestGate requestGate,
        NativeAcsArtifact artifact,
        HttpContext context,
        out NativeAcsValidationResult result,
        bool projectScope = false)
    {
        // Run only one validator at a time; busy callers receive a retry response instead of queuing more work.
        using var slot = requestGate.TryEnterValidationSlot();
        if (slot is null)
        {
            context.Response.Headers["Retry-After"] = "1";
            result = null!;
            return Results.Problem(
                title: "native_acs_validation_busy",
                detail: "Policy validation is busy. Retry the request shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?> { ["code"] = "native_acs_validation_busy" });
        }

        // A missing or invalid ACS_OPA_PATH leaves the validator unregistered.
        result = (projectScope ? validator?.ValidateComposedProject(artifact) : validator?.Validate(artifact))
            ?? new NativeAcsValidationResult(
            false, [], NativeAcsValidationDependencyFailure.OpaUnavailable);
        if (result.DependencyFailure is { } dependencyFailure)
        {
            var dependency = dependencyFailure switch
            {
                NativeAcsValidationDependencyFailure.OpaUnavailable => "opa",
                NativeAcsValidationDependencyFailure.NativeValidatorUnavailable => "native_validator",
                _ => "unknown",
            };
            return Results.Problem(
                title: "native_acs_dependency_unavailable",
                detail: "The native ACS validation dependency is unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "native_acs_dependency_unavailable",
                    ["dependency"] = dependency,
                });
        }

        if (!result.IsValid)
            return Results.Problem(
                title: "native_acs_policy_invalid",
                detail: "The native ACS policy artifact is invalid.",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "native_acs_policy_invalid",
                    ["diagnostics"] = result.Diagnostics,
                });
        return null;
    }

    private static IResult RateLimited(HttpContext context)
    {
        context.Response.Headers["Retry-After"] = "60";
        return Results.Problem(
            title: "native_acs_validation_rate_limited",
            detail: "The policy validation limit was reached. Retry after the limit window.",
            statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?> { ["code"] = "native_acs_validation_rate_limited" });
    }

    private static void EnsureNoQuery(HttpContext context, bool rejectEmptyQuery = false)
    {
        if (context.Request.Query.Count != 0 || rejectEmptyQuery && context.Request.QueryString.HasValue)
            throw ProjectConfigException.Invalid(
                "native_acs_policy_query_not_supported",
                "Policy routes do not accept query parameters.");
    }

    private static void EnsureNoReadBody(HttpContext context, Func<ProjectConfigException> invalidRequest)
    {
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true ||
            context.Request.ContentLength is > 0 ||
            context.Request.ContentType is not null ||
            context.Request.Headers.ContainsKey("Transfer-Encoding"))
            throw invalidRequest();
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

    private static IResult SourceUnavailable(string scope = "Platform") =>
        Results.Problem(
            title: "native_acs_policy_source_unavailable",
            detail: $"The {scope} Policy source is unavailable.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "native_acs_policy_source_unavailable",
            });

    internal static bool IsSourceUnavailable(InvalidOperationException exception) =>
        exception.InnerException is NpgsqlException providerException && IsSourceUnavailable(providerException);

    private static bool IsPlatformHeadSourceFailure(Exception exception) =>
        exception is NpgsqlException or TimeoutException ||
        exception is InvalidOperationException { InnerException: NpgsqlException or TimeoutException };

    private static bool IsSourceUnavailable(NpgsqlException exception) =>
        exception.InnerException is IOException or SocketException or TimeoutException ||
        exception is PostgresException { SqlState: "57P01" or "57P02" or "57P03" };
}

internal sealed class NativeAcsPolicyRequestGate(
    IMemoryCache cache,
    TimeProvider timeProvider) : IDisposable
{
    private static readonly TimeSpan RequestWindow = TimeSpan.FromMinutes(1);
    private const int MaximumRequestsPerActor = 6;
    private readonly object requestLock = new();
    private readonly SemaphoreSlim validationSlot = new(1, 1);

    internal bool TryAcquireCallerRequest(string issuer, string actorId)
    {
        var key = new ActorKey(issuer, actorId);
        var now = timeProvider.GetUtcNow();
        lock (requestLock)
        {
            var timestamps = cache.Get<List<DateTimeOffset>>(key) ?? [];
            timestamps.RemoveAll(timestamp => now - timestamp >= RequestWindow);
            if (timestamps.Count >= MaximumRequestsPerActor)
                return false;

            timestamps.Add(now);
            cache.Set(
                key,
                timestamps,
                new MemoryCacheEntryOptions { AbsoluteExpiration = now + RequestWindow });
            return true;
        }
    }

    internal IDisposable? TryEnterValidationSlot() =>
        validationSlot.Wait(0) ? new ValidationSlot(validationSlot) : null;

    public void Dispose() => validationSlot.Dispose();

    private readonly record struct ActorKey(string Issuer, string ActorId);

    private sealed class ValidationSlot(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? owner = semaphore;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}
