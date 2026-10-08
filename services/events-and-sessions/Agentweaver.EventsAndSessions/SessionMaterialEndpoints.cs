using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Azure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Agentweaver.EventsAndSessions;

internal sealed class SessionMaterialRuntimeClient(HttpClient client, CoordinationOwnerClientOptions options)
{
    private Uri Address => Uri.TryCreate(options.OwnerBaseAddress, UriKind.Absolute, out var address)
        ? RuntimeOwnerHttpTransport.RequireOwnerAddress(address)
        : throw new RuntimeAuthorizationException("runtime_material_owner_configuration_invalid");

    internal Task<RuntimeRegistration> ReadCurrentAsync(
        Guid runtimeInstanceId, RuntimeActorAuthorization actor, CancellationToken cancellationToken) =>
        new RuntimeRegistrationHttpClient(client, Address).ReadCurrentAsync(runtimeInstanceId, actor, cancellationToken);

    internal Task<RuntimeSdkSourceReceipt> ReadSourceAsync(
        Guid runtimeInstanceId, RuntimeActorAuthorization actor, CancellationToken cancellationToken) =>
        RuntimeOwnerHttpTransport.SendAsync<RuntimeSdkSourceReceipt>(
            client, Address, $"/internal/runtime/sources/{runtimeInstanceId:D}", actor, null, cancellationToken);
}

internal sealed class SessionMaterialApplicationService(
    SessionMaterialRuntimeClient runtime, IProjectsAuthorizationContextClient projects,
    SessionMaterialStore material, PostgresSessionsJournal journal, ISessionsProviderBinder bindings,
    TimeProvider timeProvider)
{
    internal async Task<SessionMaterialAcknowledgment> WriteAsync(
        HttpContext context, string sessionId, SessionMaterialWriteRequest request, CancellationToken cancellationToken)
    {
        SessionMaterialValidation.Validate(request);
        var first = await projects.GetCurrentAsync(context, cancellationToken).ConfigureAwait(false);
        NativeUsageApplicationService.RequireReadAuthority(first);
        var bearer = await NativeUsageApplicationService.BearerAsync(context, timeProvider).ConfigureAwait(false);
        try
        {
            var actor = new RuntimeActorAuthorization(bearer, first.TenantId);
            var registration = await runtime.ReadCurrentAsync(
                request.RuntimeInstanceId, actor, cancellationToken).ConfigureAwait(false);
            RequireScope(first, registration.Binding.TenantId, registration.Binding.ProjectId, registration.Binding.RunId);
            var pinned = await journal.GetProviderBindingAsync(context.User, sessionId, cancellationToken)
                .ConfigureAwait(false);
            await bindings.VerifyPinnedAsync(context.User, pinned, cancellationToken).ConfigureAwait(false);
            var source = await runtime.ReadSourceAsync(request.RuntimeInstanceId, actor, cancellationToken)
                .ConfigureAwait(false);
            if (source.RuntimeInstanceId != registration.RuntimeInstanceId ||
                source.RegistrationRevision != registration.Revision ||
                source.CanonicalPayloadHash != RuntimeUsageSourceReceiptContract.HashSource(registration, source.Source))
                throw new RuntimeAuthorizationException("runtime_material_source_invalid");
            return await material.WriteAsync(context.User, sessionId, request, registration, source.Source,
                async token =>
                {
                    var current = await projects.GetCurrentAsync(context, token).ConfigureAwait(false);
                    NativeUsageApplicationService.RequireReadAuthority(current);
                    RequireScope(current, registration.Binding.TenantId,
                        registration.Binding.ProjectId, registration.Binding.RunId);
                    await bindings.VerifyPinnedAsync(context.User, pinned, token).ConfigureAwait(false);
                    var currentRegistration = await runtime.ReadCurrentAsync(
                        request.RuntimeInstanceId, actor, token).ConfigureAwait(false);
                    if (currentRegistration != registration || !bearer.IsUsable())
                        throw new RuntimeAuthorizationException("runtime_material_authority_changed");
                }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            bearer.Invalidate();
        }
    }

    internal async Task<SessionMaterialReadResult> ReadAsync(
        HttpContext context, string sessionId, Guid eventId, SessionMaterialKind kind,
        CancellationToken cancellationToken)
    {
        var first = await projects.GetCurrentAsync(context, cancellationToken).ConfigureAwait(false);
        RequireMaterialReadAuthority(first, kind);
        var pinned = await journal.GetProviderBindingAsync(context.User, sessionId, cancellationToken)
            .ConfigureAwait(false);
        await bindings.VerifyPinnedAsync(context.User, pinned, cancellationToken).ConfigureAwait(false);
        return await material.ReadAsync(context.User, sessionId, eventId, kind, async (recorded, token) =>
        {
            var current = await projects.GetCurrentAsync(context, token).ConfigureAwait(false);
            RequireMaterialReadAuthority(current, kind);
            RequireScope(current, recorded.TenantId, pinned.ProjectId, pinned.RunId);
            await bindings.VerifyPinnedAsync(context.User, pinned, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static void RequireMaterialReadAuthority(ProjectsAuthorizationContextResponse authority, SessionMaterialKind kind)
    {
        if (kind == SessionMaterialKind.SdkCache)
        {
            NativeUsageApplicationService.RequireReadAuthority(authority);
            return;
        }
        if (kind != SessionMaterialKind.TurnContent || authority.MembershipRevision < 1 ||
            authority.BoundProjectId is null || authority.BoundRunId is null ||
            !HasTurnContentReadEntitlement(authority))
            throw new RuntimeAuthorizationException("runtime_material_read_authority_denied");
    }

    private static bool HasTurnContentReadEntitlement(ProjectsAuthorizationContextResponse authority) =>
        !authority.EffectiveAuthority.IsDefault && authority.EffectiveAuthority.Any(resource =>
                resource.ResourceType == "project" && resource.ResourceId == authority.BoundProjectId &&
                !resource.Permissions.IsDefault && resource.Permissions.Any(permission =>
                    permission.Permission is "readProjects" or "readRunSelection" && permission.RoleRevision > 0));

    private static void RequireScope(
        ProjectsAuthorizationContextResponse current, string tenantId, string projectId, string runId)
    {
        if (current.TenantId != tenantId || current.BoundProjectId != projectId || current.BoundRunId != runId)
            throw new RuntimeAuthorizationException("runtime_material_scope_invalid");
    }
}

public static class SessionMaterialEndpoints
{
    public static void AddSessionMaterialOwner(this IServiceCollection services)
    {
        services.AddScoped<SessionMaterialStore>();
        services.AddScoped<SessionMaterialApplicationService>();
        services.AddHttpClient<SessionMaterialRuntimeClient>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
    }

    public static void MapSessionMaterialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/sessions/{sessionId}/material",
            (HttpContext context, string sessionId,
                [FromServices] SessionMaterialApplicationService service, CancellationToken token) =>
                ExecuteAsync(context, async () =>
                {
                    if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                        limit.MaxRequestBodySize = 2 * SessionMaterialValidation.MaximumBytes;
                    var request = await context.Request.ReadFromJsonAsync<SessionMaterialWriteRequest>(
                        cancellationToken: token).ConfigureAwait(false)
                        ?? throw new ArgumentException("A typed session material request is required.");
                    return await service.WriteAsync(context, sessionId, request, token).ConfigureAwait(false);
                }))
            .RequireAuthorization();
        endpoints.MapGet("/internal/sessions/{sessionId}/material/{eventId:guid}/{kind}",
            (HttpContext context, string sessionId, Guid eventId, string kind,
                [FromServices] SessionMaterialApplicationService service, CancellationToken token) =>
                ExecuteAsync(context, () => service.ReadAsync(context, sessionId, eventId, ParseKind(kind), token),
                    missingCacheIsNotFound: kind is "sdkCache" or "SdkCache"))
            .RequireAuthorization();
    }

    private static SessionMaterialKind ParseKind(string kind) => kind switch
    {
        "turnContent" or "TurnContent" => SessionMaterialKind.TurnContent,
        "sdkCache" or "SdkCache" => SessionMaterialKind.SdkCache,
        _ => throw new ArgumentException("A supported session material kind is required.", nameof(kind))
    };

    private static async Task<IResult> ExecuteAsync<T>(
        HttpContext context, Func<Task<T>> action, bool missingCacheIsNotFound = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return Results.Ok(await action().ConfigureAwait(false));
        }
        catch (RuntimeAuthorizationException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ProjectsAuthorizationContextException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: (int)exception.StatusCode);
        }
        catch (SessionEventConflictException)
        {
            return Results.Conflict(new { error = "session_material_event_conflict" });
        }
        catch (SessionNotFoundException)
        {
            return Results.NotFound(new { error = "session_material_not_found" });
        }
        catch (SessionAuthenticationException)
        {
            return Results.Unauthorized();
        }
        catch (SessionAccessDeniedException)
        {
            return Results.Json(new { error = "session_material_access_denied" },
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (SessionPinnedProviderUnavailableException)
        {
            return Results.Json(new { error = "session_material_provider_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (SessionMaterialIntegrityException exception)
        {
            return Results.Json(new { error = exception.Code },
                statusCode: missingCacheIsNotFound && exception.Code == "session_material_object_missing"
                    ? StatusCodes.Status404NotFound : StatusCodes.Status503ServiceUnavailable);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "session_material_request_invalid" });
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "session_material_request_invalid" });
        }
        catch (RequestFailedException)
        {
            return Results.Json(new { error = "session_material_storage_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { error = "session_material_owner_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(new { error = "session_material_owner_timeout" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
