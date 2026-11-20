using System.Collections.Immutable;
using System.Data;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Projects.Config;

internal static class AuthorizationTargetContext
{
    internal static bool IsPath(PathString path) => string.Equals(
        path.Value?.TrimEnd('/'), "/api/authorization/target-context", StringComparison.OrdinalIgnoreCase);

    internal static Func<int, CancellationToken, Task>? BeforeAuthorityLock { get; set; }
    internal static Func<int, CancellationToken, Task>? AfterAuthorityLock { get; set; }

    internal static IApplicationBuilder UseTargetContextNoStore(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (IsPath(context.Request.Path))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                try
                {
                    if (!HttpMethods.IsGet(context.Request.Method) ||
                        context.Request.Query.Count != 0 ||
                        context.Request.ContentLength is > 0 ||
                        context.Request.Headers.ContainsKey("Transfer-Encoding") ||
                        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
                    {
                        await Error(400, "request_shape_invalid").ExecuteAsync(context).ConfigureAwait(false);
                        return;
                    }
                    await next(context).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    if (context.Response.HasStarted)
                        context.Abort();
                    else
                        await Error(502, "owner_unavailable").ExecuteAsync(context).ConfigureAwait(false);
                }
                return;
            }
            await next(context).ConfigureAwait(false);
        });

    internal static void MapTargetContext(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/authorization/target-context", HandleAsync)
            .AllowAnonymous()
            .WithName("GetAuthorizationTargetContext");

    private static async Task<IResult> HandleAsync(
        HttpContext http, ProjectAuthorizationOwner owner, ProjectsConfigDbContext db,
        CancellationToken cancellationToken)
    {
        using var protectedRead = PlatformPolicyReadLoggerFactory.BeginTargetContextRead();
        if (http.User.Identity?.IsAuthenticated != true)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Error(401, "authentication_required");
        }
        if (http.Request.Query.Count != 0 ||
            http.Request.ContentLength is > 0 ||
            http.Request.Headers.ContainsKey("Transfer-Encoding") ||
            http.Request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            return Error(400, "request_shape_invalid");
        ProjectAuthorizationContext caller;
        try
        {
            caller = await owner.ResolveAsync(http.User,
                http.Request.Headers[ProjectAuthorizationOwner.TenantSelectorHeader].ToArray(), cancellationToken)
                .ConfigureAwait(false);
            caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
            caller.RequireUnboundRequest();
        }
        catch (ProjectConfigException)
        {
            return Error(403, "target_context_forbidden");
        }

        var candidates = caller.Grants.Where(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Platform &&
            grant.ResourceId == ProjectAuthorizationOwner.PlatformResourceId &&
            grant.Role == ProjectAuthorityRole.PlatformAdmin).ToArray();
        if (candidates.Length == 0)
            return Error(403, "target_context_forbidden");

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();
        if (BeforeAuthorityLock is { } beforeLock)
            await beforeLock(connection.ProcessID, cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand("""
            SELECT assignment_id, revision, authorization_result
            FROM projects_config.lock_native_acs_policy_authority(
                @membership_id, @issuer, @actor, @tenant, @membership_revision,
                'Platform', 'default', @ids, @types, @resources, @roles, @revisions)
            """, connection, npgsqlTransaction))
        {
            command.Parameters.AddWithValue("membership_id", caller.MembershipId);
            command.Parameters.AddWithValue("issuer", caller.Issuer);
            command.Parameters.AddWithValue("actor", caller.ActorId);
            command.Parameters.AddWithValue("tenant", caller.TenantId);
            command.Parameters.AddWithValue("membership_revision", caller.MembershipRevision);
            command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid,
                candidates.Select(item => item.AssignmentId).ToArray());
            command.Parameters.AddWithValue("types", NpgsqlDbType.Array | NpgsqlDbType.Text,
                candidates.Select(item => item.ResourceType.ToString()).ToArray());
            command.Parameters.AddWithValue("resources", NpgsqlDbType.Array | NpgsqlDbType.Text,
                candidates.Select(item => item.ResourceId).ToArray());
            command.Parameters.AddWithValue("roles", NpgsqlDbType.Array | NpgsqlDbType.Text,
                candidates.Select(item => item.Role.ToString()).ToArray());
            command.Parameters.AddWithValue("revisions", NpgsqlDbType.Array | NpgsqlDbType.Bigint,
                candidates.Select(item => item.Revision).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(2) != "authorized" ||
                !candidates.Any(item => item.AssignmentId == reader.GetGuid(0) && item.Revision == reader.GetInt64(1)))
                return Error(403, "target_context_forbidden");
        }
        if (AfterAuthorityLock is { } afterLock)
            await afterLock(connection.ProcessID, cancellationToken).ConfigureAwait(false);

        var grants = await db.RoleAssignments.AsNoTracking()
            .Where(item => item.MembershipId == caller.MembershipId &&
                item.State == ProjectAuthorityRecordState.Active)
            .OrderBy(item => item.ResourceType).ThenBy(item => item.ResourceId).ThenBy(item => item.Role)
            .Select(item => new ProjectAuthorizationGrant(item.AssignmentId, item.ResourceType,
                item.ResourceId, item.Role, item.Revision))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var fresh = new ProjectAuthorizationContext(caller.Issuer, caller.ActorId, caller.TenantId,
            caller.MembershipId, caller.MembershipRevision, grants.ToImmutableArray(), caller.Scopes,
            caller.BoundProjectId, caller.BoundRunId, caller.Purpose).ToEffectiveResponse();

        Guid installationId;
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT installation_id FROM projects_config.installation_identity WHERE singleton = true",
                connection, npgsqlTransaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
                return Error(503, "target_context_unavailable");
            installationId = reader.GetGuid(0);
            if (installationId == Guid.Empty || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return Error(503, "target_context_unavailable");
        }
        catch (NpgsqlException)
        {
            return Error(503, "target_context_unavailable");
        }
        catch (InvalidCastException)
        {
            return Error(503, "target_context_unavailable");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(new
        {
            contractVersion = 1,
            fresh.Issuer,
            fresh.ActorId,
            fresh.TenantId,
            fresh.MembershipRevision,
            fresh.BoundProjectId,
            fresh.BoundRunId,
            fresh.EffectiveAuthority,
            installationId = installationId.ToString("D"),
        });
    }

    private static IResult Error(int status, string code) =>
        Results.Json(new { type = "about:blank", title = code, status, code },
            statusCode: status, contentType: "application/problem+json");
}
