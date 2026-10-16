using System.Net;
using System.Net.Http.Headers;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Authentication;

namespace Agentweaver.Environment;

public sealed record CreateRemoteMcpConnectionRequest(
    string IdempotencyKey,
    string DisplayName,
    string EndpointUri,
    string? ResourceUri,
    RemoteMcpAuthenticationMode AuthenticationMode);

public sealed record UpdateRemoteMcpConnectionRequest(
    long ExpectedRowRevision,
    string IdempotencyKey,
    string DisplayName,
    string EndpointUri,
    string? ResourceUri,
    RemoteMcpAuthenticationMode AuthenticationMode);

public sealed record RemoteMcpConnectionStateRequest(
    long ExpectedRowRevision,
    string IdempotencyKey);

public sealed record LinkRemoteMcpIdentityBindingRequest(
    long ExpectedConfigurationRevision,
    string ExpectedConfigurationSha256,
    string IdentityBindingReference,
    string OperationId);

public static class RemoteMcpConnectionEndpoints
{
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
                return Results.Ok(page);
            }).ConfigureAwait(false);
        });

        group.MapPost("", async (
            string projectId,
            CreateRemoteMcpConnectionRequest request,
            HttpContext context,
            RemoteMcpConnectionStore store,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteAsync(context, async () =>
            {
                var result = await store.CreateAsync(
                    caller!,
                    projectId,
                    new(
                        request.DisplayName,
                        request.EndpointUri,
                        request.ResourceUri,
                        request.AuthenticationMode),
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
                    new(
                        request.DisplayName,
                        request.EndpointUri,
                        request.ResourceUri,
                        request.AuthenticationMode),
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
                    RemoteMcpConnectionState.Removed,
                    request.IdempotencyKey,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(result);
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
