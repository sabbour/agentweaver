using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.Environment;

/// <summary>Request body for creating a project-scoped Remote MCP connection.</summary>
public sealed record CreateRemoteMcpConnectionRequest(
    string IdempotencyKey,
    string DisplayName,
    string EndpointUri,
    string? ResourceUri,
    RemoteMcpAuthenticationMode AuthenticationMode,
    string? RegistryServerName = null,
    string? RegistryExactVersion = null);

/// <summary>Request body for replacing a connection configuration at an expected row revision.</summary>
public sealed record UpdateRemoteMcpConnectionRequest(
    long ExpectedRowRevision,
    string IdempotencyKey,
    string DisplayName,
    string EndpointUri,
    string? ResourceUri,
    RemoteMcpAuthenticationMode AuthenticationMode,
    string? RegistryServerName = null,
    string? RegistryExactVersion = null);

/// <summary>Request body for an idempotent connection state change.</summary>
public sealed record RemoteMcpConnectionStateRequest(
    long ExpectedRowRevision,
    string IdempotencyKey);

/// <summary>Request body for linking an identity binding to one exact configuration revision.</summary>
public sealed record LinkRemoteMcpIdentityBindingRequest(
    long ExpectedConfigurationRevision,
    string ExpectedConfigurationSha256,
    string IdentityBindingReference,
    string OperationId);

/// <summary>Request body for checking one reviewed tool against the current connection head.</summary>
public sealed record RemoteMcpInvocationValidityRequest(
    long ConfigurationRevision,
    string ConfigurationSha256,
    long DiscoveryRevision,
    string CatalogSha256,
    string ToolName,
    string ToolRevision);

/// <summary>Maps authenticated project APIs for Remote MCP connection management.</summary>
public static class RemoteMcpConnectionEndpoints
{
    // The Gateway publishes the directory, so enum values use stable camelCase strings.
    internal static readonly JsonSerializerOptions DirectoryJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>
    /// Registers authenticated connection routes; state mutations use a JSON body for revision and idempotency.
    /// </summary>
    public static IEndpointRouteBuilder MapRemoteMcpConnectionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/projects/{projectId}/remote-mcp/connections")
            .RequireAuthorization();

        group.MapGet("", async (
            string projectId,
            int? pageSize,
            Guid? after,
            HttpContext context,
            RemoteMcpConnectionStore store,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteAsync(context, async () =>
            {
                var page = await store.ListAsync(
                    caller!, projectId, pageSize ?? 50, after, cancellationToken).ConfigureAwait(false);
                return Results.Json(page, DirectoryJsonOptions);
            }).ConfigureAwait(false);
        });

        group.MapPost("", async (
            string projectId,
            CreateRemoteMcpConnectionRequest request,
            HttpContext context,
            RemoteMcpConnectionStore store,
            RemoteMcpDiscoveryService discovery,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteAsync(context, async () =>
            {
                // Resolve any Registry selection into a pinned draft before the store call. A bad
                // Registry version fails here, before any row or idempotency record is written.
                var result = await store.CreateAsync(
                    caller!,
                    projectId,
                    await discovery.ResolveDraftAsync(
                        caller!,
                        projectId,
                        request.DisplayName,
                        request.EndpointUri,
                        request.ResourceUri,
                        request.AuthenticationMode,
                        request.RegistryServerName,
                        request.RegistryExactVersion,
                        cancellationToken).ConfigureAwait(false),
                    request.IdempotencyKey,
                    cancellationToken).ConfigureAwait(false);
                return Results.Created(
                    $"/api/projects/{Uri.EscapeDataString(projectId)}/remote-mcp/connections/{result.Snapshot.Head.Connection.ConnectionId:D}",
                    result);
            }).ConfigureAwait(false);
        });

        group.MapGet("/{connectionId:guid}", async (
            string projectId,
            Guid connectionId,
            HttpContext context,
            RemoteMcpConnectionStore store,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteAsync(context, async () =>
            {
                var snapshot = await store.GetAsync(
                    caller!, projectId, connectionId, cancellationToken).ConfigureAwait(false);
                return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
            }).ConfigureAwait(false);
        });

        group.MapGet("/{connectionId:guid}/configurations/{configurationRevision:long}", async (
            string projectId,
            Guid connectionId,
            long configurationRevision,
            HttpContext context,
            RemoteMcpConnectionStore store,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteAsync(context, async () =>
            {
                var configuration = await store.GetConfigurationRevisionAsync(
                    caller!, projectId, connectionId, configurationRevision, cancellationToken)
                    .ConfigureAwait(false);
                return configuration is null ? Results.NotFound() : Results.Ok(configuration);
            }).ConfigureAwait(false);
        });

        group.MapPut("/{connectionId:guid}/identity-binding", async (
            string projectId,
            Guid connectionId,
            LinkRemoteMcpIdentityBindingRequest request,
            HttpContext context,
            RemoteMcpConnectionStore store,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteAsync(context, async () =>
            {
                var result = await store.BindIdentityAsync(
                    caller!,
                    projectId,
                    connectionId,
                    request.ExpectedConfigurationRevision,
                    request.ExpectedConfigurationSha256,
                    request.IdentityBindingReference,
                    request.OperationId,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(result);
            }).ConfigureAwait(false);
        });

        group.MapPut("/{connectionId:guid}", async (
            string projectId,
            Guid connectionId,
            UpdateRemoteMcpConnectionRequest request,
            HttpContext context,
            RemoteMcpConnectionStore store,
            RemoteMcpDiscoveryService discovery,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteAsync(context, async () =>
            {
                var result = await store.UpdateAsync(
                    caller!,
                    projectId,
                    connectionId,
                    request.ExpectedRowRevision,
                    // Same Registry resolution as create. A changed Registry pin creates a new configuration revision.
                    await discovery.ResolveDraftAsync(
                        caller!,
                        projectId,
                        request.DisplayName,
                        request.EndpointUri,
                        request.ResourceUri,
                        request.AuthenticationMode,
                        request.RegistryServerName,
                        request.RegistryExactVersion,
                        cancellationToken).ConfigureAwait(false),
                    request.IdempotencyKey,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(result);
            }).ConfigureAwait(false);
        });

        group.MapPost("/{connectionId:guid}/enable", async (
            string projectId,
            Guid connectionId,
            RemoteMcpConnectionStateRequest request,
            HttpContext context,
            RemoteMcpConnectionStore store,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteAsync(context, async () =>
            {
                var result = await store.SetStateAsync(
                    caller!,
                    projectId,
                    connectionId,
                    request.ExpectedRowRevision,
                    RemoteMcpConnectionState.Enabled,
                    request.IdempotencyKey,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(result);
            }).ConfigureAwait(false);
        });
        group.MapPost("/{connectionId:guid}/disable", async (
            string projectId,
            Guid connectionId,
            RemoteMcpConnectionStateRequest request,
            HttpContext context,
            RemoteMcpConnectionStore store,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteAsync(context, async () =>
            {
                var result = await store.SetStateAsync(
                    caller!,
                    projectId,
                    connectionId,
                    request.ExpectedRowRevision,
                    RemoteMcpConnectionState.Disabled,
                    request.IdempotencyKey,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(result);
            }).ConfigureAwait(false);
        });

        group.MapDelete("/{connectionId:guid}", async (
            string projectId,
            Guid connectionId,
            [FromBody] RemoteMcpConnectionStateRequest request,
            HttpContext context,
            RemoteMcpConnectionStore store,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteAsync(context, async () =>
            {
                var result = await store.SetStateAsync(
                    caller!,
                    projectId,
                    connectionId,
                    request.ExpectedRowRevision,
                    RemoteMcpConnectionState.Removed,
                    request.IdempotencyKey,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(result);
            }).ConfigureAwait(false);
        });

        group.MapPost("/{connectionId:guid}/discover", async (
            string projectId,
            Guid connectionId,
            RemoteMcpConnectionStateRequest request,
            HttpContext context,
            RemoteMcpDiscoveryService discovery,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteAsync(context, async () =>
            {
                var result = await discovery.DiscoverAsync(
                    caller!,
                    projectId,
                    connectionId,
                    request.ExpectedRowRevision,
                    request.IdempotencyKey,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(result);
            }).ConfigureAwait(false);
        });

        group.MapPost("/{connectionId:guid}/invocation-validity", async (
            string projectId,
            Guid connectionId,
            RemoteMcpInvocationValidityRequest request,
            HttpContext context,
            RemoteMcpConnectionStore store,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteAsync(context, async () =>
            {
                // Rebuild the review reference from the route and body. The project and connection come
                // from the route, so a body can't point the check at another connection.
                var reviewed = new RemoteMcpConnectionReviewReference(
                    new RemoteMcpConnectionReference(projectId, connectionId),
                    request.ConfigurationRevision,
                    request.ConfigurationSha256,
                    request.DiscoveryRevision,
                    request.CatalogSha256);
                var validity = await store.EvaluateInvocationAsync(
                    caller!, reviewed, request.ToolName, request.ToolRevision, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(validity);
            }).ConfigureAwait(false);
        });

        // Project-level routes that don't address one connection. The Gateway forwards the public
        // directory route here.
        var project = endpoints.MapGroup("/api/projects/{projectId}/remote-mcp")
            .RequireAuthorization();

        project.MapGet("/directory", async (
            string projectId,
            int? pageSize,
            Guid? after,
            HttpContext context,
            RemoteMcpConnectionStore store,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteAsync(context, async () =>
            {
                var page = await store.ListDirectoryAsync(
                    caller!, projectId, pageSize ?? 50, after, cancellationToken).ConfigureAwait(false);
                return Results.Json(page, DirectoryJsonOptions);
            }).ConfigureAwait(false);
        });

        project.MapGet("/registry/servers", async (
            string projectId,
            string? cursor,
            HttpContext context,
            RemoteMcpDiscoveryService discovery,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteAsync(context, async () =>
            {
                var page = await discovery.ReadRegistryPageAsync(caller!, projectId, cursor, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(page);
            }).ConfigureAwait(false);
        });

        return endpoints;
    }

    private static bool TryReadCaller(HttpContext context, out CurrentCallerRequest? caller)
    {
        caller = null;
        context.Response.Headers.CacheControl = "no-store";
        if (context.User.Identity?.IsAuthenticated != true ||
            !AuthenticationHeaderValue.TryParse(
                context.Request.Headers.Authorization.ToString(),
                out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
            return false;

        var tenantSelector = context.Request.Headers.TryGetValue(
            ProjectAuthorizationContextContract.TenantSelectorHeader,
            out var values)
            ? values.ToString()
            : null;
        try
        {
            caller = new CurrentCallerRequest(authorization.Parameter, tenantSelector);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<IResult> ExecuteAsync(HttpContext context, Func<Task<IResult>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (RemoteMcpConnectionException exception)
        {
            return Results.Json(
                new { code = exception.Code, message = exception.Message },
                statusCode: exception.StatusCode);
        }
        catch (ProjectsConfigApiException exception)
        {
            return EnvironmentEndpoints.ToProjectAuthorizationResult(exception);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { code = "invalid_remote_mcp_connection", message = exception.Message });
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);
        }
    }
}
