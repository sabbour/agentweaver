using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task AgentHistoryRouteChecksCurrentAgentAndForwardsCallerToKnowledge()
    {
        await using var db = CreateDbContext();
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "agent-history-" + suffix;
        var tenantId = "agent-history-tenant-" + suffix;
        var scopes = new[] { "api.read", "projects.admin" };
        var caller = await SeedCallerAsync(
            db,
            store,
            subject,
            tenantId,
            scopes,
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await projects.CreateProjectAsync(caller, "Agent history", CancellationToken.None);
        var current = await projects.GetProjectConfigurationAsync(
            caller, project.ProjectId, revision: null, CancellationToken.None);
        // A real configuration revision proves that only a currently configured agent can trigger a Knowledge read.
        _ = await projects.UpdateProjectConfigurationAsync(
            caller,
            project.ProjectId,
            current.Revision,
            current.Configuration with
            {
                AgentCharters = [new ProjectAgentCharter("agent-a", "Ada", "Lead", "Lead the work.")],
                Casting = [new ProjectAgentCast("agent-a", "Lead", 0)]
            },
            CancellationToken.None);

        var history = new ProjectAgentHistoryResponse(project.ProjectId, "agent-a", []);
        var handler = new AgentHistoryResponseHandler(history);
        using var host = await StartProjectSummaryEndpointHostAsync(
            fixture.DataSource, knowledgeHistoryHandler: handler);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", string.Join(' ', scopes));
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/agents/agent-a/history");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            $"/internal/projects/{project.ProjectId}/agents/agent-a/history",
            handler.Path);
        Assert.Equal("Bearer caller-token", handler.Authorization);
        Assert.Equal(tenantId, handler.TenantSelector);

        using var unknownAgentRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/agents/unknown/history");
        unknownAgentRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
        using var unknownAgentResponse = await client.SendAsync(unknownAgentRequest);

        Assert.Equal(HttpStatusCode.NotFound, unknownAgentResponse.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PrivateKnowledgeSelectionDoesNotRequireRunSelectionPermission()
    {
        await using var db = CreateDbContext();
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(includeMemory: true), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "history-selection-tenant-" + suffix;
        var tenantSubject = "history-selection-admin-" + suffix;
        var adminScopes = new[] { "api.read", "projects.admin" };
        var platformAdmin = await SeedCallerAsync(
            db,
            store,
            "history-selection-platform-" + suffix,
            tenantId,
            adminScopes,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var tenantAdmin = await SeedCallerAsync(
            db,
            store,
            tenantSubject,
            tenantId,
            adminScopes,
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var platformHead = await projects.GetPlatformRuntimeDefaultsAsync(platformAdmin, CancellationToken.None);
        var platform = await projects.UpdatePlatformRuntimeDefaultsAsync(
            platformAdmin, platformHead.Revision, PlatformDefaults(), CancellationToken.None);
        var project = await projects.CreateProjectAsync(tenantAdmin, "History selection", CancellationToken.None);
        var configuration = await projects.UpdateProjectConfigurationAsync(
            tenantAdmin,
            project.ProjectId,
            project.ConfigurationRevision,
            ProjectSettings(),
            CancellationToken.None);

        var orchestratorSubject = "history-selection-orchestrator-" + suffix;
        var orchestratorMembership = await SeedMembershipAsync(store, orchestratorSubject, tenantId);
        await store.AssignRoleAsync(
            orchestratorMembership,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator,
            "fixture");
        var orchestrator = await ResolveCallerAsync(
            db, orchestratorSubject, tenantId, ["api.read", "projects.orchestrator"]);
        var runId = "history-selection-run-" + suffix;
        await projects.AcceptRunSelectionAsync(
            orchestrator,
            project.ProjectId,
            runId,
            RunRequest(configuration.Revision, platform.Revision, includeMemory: true),
            CancellationToken.None);

        // Tenant administration grants private Knowledge access but not permission to read run selection publicly.
        Assert.DoesNotContain(
            tenantAdmin.ToEffectiveResponse().EffectiveAuthority.SelectMany(item => item.Permissions),
            permission => permission.Permission == ProjectAuthorizationPermission.ReadRunSelection);
        using var host = await StartProjectSummaryEndpointHostAsync(fixture.DataSource);
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/internal/projects/{project.ProjectId}/runs/{runId}/knowledge-selection");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
        request.Headers.Add("X-Test-Subject", tenantSubject);
        request.Headers.Add("X-Test-Scope", string.Join(' ', adminScopes));
        request.Headers.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);

        using var response = await client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(project.ProjectId, body.GetProperty("projectId").GetString());
        Assert.Equal(runId, body.GetProperty("runId").GetString());
        Assert.False(body.TryGetProperty("projectConfiguration", out _));
        var providers = body.GetProperty("providers").EnumerateArray().ToArray();
        Assert.NotEmpty(providers);
        Assert.All(
            providers,
            provider => Assert.Equal("memory", provider.GetProperty("seam").GetString()));

        using var publicSelectionRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{runId}/selection");
        publicSelectionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
        publicSelectionRequest.Headers.Add("X-Test-Subject", tenantSubject);
        publicSelectionRequest.Headers.Add("X-Test-Scope", string.Join(' ', adminScopes));
        publicSelectionRequest.Headers.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        using var publicSelectionResponse = await client.SendAsync(publicSelectionRequest);

        Assert.Equal(HttpStatusCode.Forbidden, publicSelectionResponse.StatusCode);
    }

    private sealed class AgentHistoryResponseHandler(ProjectAgentHistoryResponse history) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Authorization { get; private set; }
        public string? TenantSelector { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Record the forwarded caller headers and return only the response for this isolated endpoint test.
            Path = request.RequestUri?.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            TenantSelector = request.Headers.TryGetValues(
                ProjectAuthorizationOwner.TenantSelectorHeader, out var values)
                ? values.Single()
                : null;
            RequestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(history),
                RequestMessage = request
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        }
    }
}
