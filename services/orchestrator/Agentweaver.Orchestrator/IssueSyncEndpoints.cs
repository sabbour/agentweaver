using System.Net.Http.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal static class IssueSyncEndpoints
{
    internal static void MapIssueSync(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/issue-sync").RequireAuthorization();
        group.MapGet("/", (string projectId, HttpContext context, IssueSyncStore store,
            HttpClient client, OrchestratorOptions options) => Execute(context, async () =>
        {
            var proof = await ReadOwnerAsync(projectId, context, client, options);
            var project = await store.ReadAsync(projectId, context.RequestAborted)
                ?? throw new CoordinationException("issue_sync_not_configured", 404);
            if (project.TenantId != proof.TenantId)
                throw new CoordinationException("issue_sync_project_denied", 403);
            await ReadOwnerAsync(projectId, context, client, options);
            return Results.Ok(project);
        }));
        group.MapPut("/", (string projectId, ConfigureIssueSyncRequest request, HttpContext context,
            IssueSyncStore store, HttpClient client, OrchestratorOptions options) => Execute(context, async () =>
        {
            if (request.Settings is null)
                throw new ArgumentException("Issue sync settings are required.");
            request.Settings.Validate();
            var proof = await ReadOwnerAsync(projectId, context, client, options);
            var existing = await store.ReadAsync(projectId, context.RequestAborted);
            IssueSyncGrantReceipt grant;
            // Disabling sync can reuse the saved grant proof without contacting the grant owner.
            if (!request.Settings.Enabled && existing is not null &&
                request.Settings.GrantId == existing.Settings.GrantId && existing.TenantId == proof.TenantId)
                grant = existing.GrantProof;
            else
            {
                var tls = context.RequestServices.GetKeyedService<RuntimeServiceTlsIdentity>(IssueSyncAuthorityClient.Channel)
                    ?? throw new CoordinationException("issue_sync_worker_unavailable", 503);
                using var message = new HttpRequestMessage(HttpMethod.Get,
                    new Uri(tls.PeerOwnerAddress, $"/api/projects/{Uri.EscapeDataString(projectId)}/issue-sync/grants/{request.Settings.GrantId:D}"));
                message.Headers.Authorization = CoordinationIdentity.RequireBearer(context);
                message.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", proof.TenantId);
                using var response = await context.RequestServices.GetRequiredService<IHttpClientFactory>()
                    .CreateClient(IssueSyncAuthorityClient.Channel).SendAsync(message, context.RequestAborted);
                await response.Content.LoadIntoBufferAsync(64 * 1024, context.RequestAborted);
                if (!response.IsSuccessStatusCode || response.Headers.CacheControl?.NoStore != true)
                    throw new CoordinationException("issue_sync_grant_denied", 403);
                grant = await response.Content.ReadFromJsonAsync<IssueSyncGrantReceipt>(context.RequestAborted)
                    ?? throw new CoordinationException("issue_sync_grant_response_invalid", 502);
                if (grant.Project is null || !grant.Active || grant.GrantId != request.Settings.GrantId ||
                    grant.Project.ProjectId != projectId || grant.Project.TenantId != proof.TenantId ||
                    string.IsNullOrWhiteSpace(grant.Project.ConnectionId))
                    throw new CoordinationException("issue_sync_grant_scope_denied", 403);
            }
            await store.ConfigureAsync(projectId, proof.TenantId, request.Settings, grant, request.ExpectedRevision,
                async ct =>
                {
                    var refreshed = await ReadOwnerAsync(projectId, context, client, options);
                    if (refreshed != proof)
                        throw new CoordinationException("issue_sync_project_authority_changed", 403);
                }, context.RequestAborted);
            return Results.Ok(await store.ReadAsync(projectId, context.RequestAborted));
        }));
    }

    private static async Task<IssueSyncOwnerProof> ReadOwnerAsync(string projectId, HttpContext context,
        HttpClient client, OrchestratorOptions options)
    {
        _ = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        // Sync configuration requires project-owner authority, not a run-bound or purpose-bound token.
        if (context.User.HasClaim(claim => claim.Type is "run_id" or "purpose"))
            throw new CoordinationException("issue_sync_owner_token_required", 403);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(RuntimeOwnerHttpTransport.RequireOwnerAddress(new Uri(options.ProjectsOwnerBaseAddress)),
                $"/api/projects/{Uri.EscapeDataString(projectId)}/issue-sync-owner"));
        request.Headers.Authorization = CoordinationIdentity.RequireBearer(context);
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        if (tenant is not null) request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenant);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        await response.Content.LoadIntoBufferAsync(64 * 1024, context.RequestAborted);
        if (!response.IsSuccessStatusCode || response.Headers.CacheControl?.NoStore != true)
            throw new CoordinationException("issue_sync_project_owner_denied", 403);
        var proof = await response.Content.ReadFromJsonAsync<IssueSyncOwnerProof>(context.RequestAborted)
            ?? throw new CoordinationException("issue_sync_project_response_invalid", 502);
        if (proof.ProjectId != projectId || tenant is not null && tenant != proof.TenantId)
            throw new CoordinationException("issue_sync_project_scope_denied", 403);
        return proof;
    }

    private static async Task<IResult> Execute(HttpContext context, Func<Task<IResult>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (CoordinationException error) { return Results.Json(new { error = error.Code }, statusCode: error.StatusCode); }
        catch (ArgumentException) { return Results.Json(new { error = "issue_sync_settings_invalid" }, statusCode: 400); }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException)
        { return Results.Json(new { error = "issue_sync_authority_unavailable" }, statusCode: 503); }
    }
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(
    System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfigureIssueSyncRequest(long ExpectedRevision, IssueSyncSettings Settings);
