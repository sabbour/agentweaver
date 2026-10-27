using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task P1SetupEndpointReturnsOnlyCurrentReceiptAndRejectsInvalidOrCleanedReplays()
    {
        await using var db = CreateDbContext();
        var options = CreateDbContextOptions();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var tenantId = $"p1-endpoint-tenant-{Guid.NewGuid():N}";
        var approvedSubject = $"p1-endpoint-admin-{Guid.NewGuid():N}";
        var caller = await SeedCallerAsync(
            db,
            authorityStore,
            approvedSubject,
            tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var otherAdmin = await SeedCallerAsync(
            db,
            authorityStore,
            $"p1-endpoint-other-admin-{Guid.NewGuid():N}",
            tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var approval = new PrivilegedProjectSetupApproval(
            new Uri(TestIssuer), "projects-config-tests", approvedSubject, tenantId);
        var capability = new P1ProjectSetupAuthorityCapability(options, approval);
        await using var app = await StartP1SetupServerAsync(capability);
        using var client = app.GetTestClient();
        var invocationId = Guid.NewGuid();

        using (var body = SetupRequest(invocationId, approvedSubject, tenantId))
        {
            body.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, await SendStatusAsync(client, body));
        }
        using (var query = SetupRequest(invocationId, approvedSubject, tenantId, query: "?unexpected=1"))
            Assert.Equal(HttpStatusCode.BadRequest, await SendStatusAsync(client, query));
        using (var missingTenant = SetupRequest(invocationId, approvedSubject, null))
            Assert.Equal(HttpStatusCode.BadRequest, await SendStatusAsync(client, missingTenant));
        using (var wrongTenant = SetupRequest(invocationId, approvedSubject, "another-tenant"))
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, wrongTenant));
        using (var wrongActor = SetupRequest(invocationId, otherAdmin.ActorId, tenantId))
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, wrongActor));
        using (var missingScope = SetupRequest(
                   invocationId, approvedSubject, tenantId, scope: "api.read projects.orchestrator"))
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, missingScope));
        using (var boundRequest = SetupRequest(
                   invocationId, approvedSubject, tenantId, boundProjectId: "bound-project"))
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, boundRequest));
        using (var mismatchedAssertion = SetupRequest(
                   invocationId, approvedSubject, tenantId, tenantAssertion: "foreign-tenant"))
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, mismatchedAssertion));

        using var initial = SetupRequest(invocationId, approvedSubject, tenantId);
        using var response = await client.SendAsync(initial);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receiptJson = await response.Content.ReadAsStringAsync();
        var receipt = JsonSerializer.Deserialize<ProjectSetupReceipt>(
            receiptJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(receipt);
        Assert.Equal(invocationId, receipt.InvocationId);
        Assert.Equal(approvedSubject, receipt.Subject);
        Assert.Equal(tenantId, receipt.TenantId);
        Assert.NotNull(receipt.OwnerAssignmentId);
        using (var document = JsonDocument.Parse(receiptJson))
        {
            Assert.True(document.RootElement.TryGetProperty("projectId", out _));
            Assert.False(document.RootElement.TryGetProperty("setup", out _));
            Assert.False(document.RootElement.TryGetProperty("replayed", out _));
        }

        using var replayRequest = SetupRequest(invocationId, approvedSubject, tenantId);
        using var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(receipt, await replayResponse.Content.ReadFromJsonAsync<ProjectSetupReceipt>());

        await authorityStore.ExecuteProjectSetupOperationAsync(
            caller,
            new ProjectSetupOperationRequest(
                1,
                invocationId,
                ProjectSetupOperationKind.Cleanup,
                tenantId,
                receipt.ProjectRevision,
                receipt.AssignmentRevision));
        using var cleanedReplayRequest = SetupRequest(invocationId, approvedSubject, tenantId);
        using var cleanedReplayResponse = await client.SendAsync(cleanedReplayRequest);
        Assert.Equal(HttpStatusCode.Conflict, cleanedReplayResponse.StatusCode);
        using var cleanedProblem = JsonDocument.Parse(await cleanedReplayResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            "project_setup_receipt_stale",
            cleanedProblem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task P1SetupEndpointRejectsStaleAndRevokedMembership()
    {
        await using var db = CreateDbContext();
        var options = CreateDbContextOptions();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var tenantId = $"p1-stale-tenant-{Guid.NewGuid():N}";
        var subject = $"p1-stale-admin-{Guid.NewGuid():N}";
        var caller = await SeedCallerAsync(
            db,
            authorityStore,
            subject,
            tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var capability = new P1ProjectSetupAuthorityCapability(
            options,
            new PrivilegedProjectSetupApproval(new Uri(TestIssuer), "projects-config-tests", subject, tenantId));
        await using var app = await StartP1SetupServerAsync(capability);
        using var client = app.GetTestClient();
        var invocationId = Guid.NewGuid();

        using (var initial = SetupRequest(invocationId, subject, tenantId))
        using (var response = await client.SendAsync(initial))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await db.TenantMemberships
            .Where(item => item.MembershipId == caller.MembershipId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                item => item.Revision,
                item => item.Revision + 1));
        using (var staleReplay = SetupRequest(invocationId, subject, tenantId))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(staleReplay)).StatusCode);
        }

        await db.TenantMemberships
            .Where(item => item.MembershipId == caller.MembershipId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                item => item.State,
                ProjectAuthorityRecordState.Revoked));
        using (var revokedMembership = SetupRequest(Guid.NewGuid(), subject, tenantId))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(revokedMembership)).StatusCode);
        }
    }

    private static async Task<WebApplication> StartP1SetupServerAsync(
        P1ProjectSetupAuthorityCapability capability)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddAuthentication("p1-test")
            .AddScheme<AuthenticationSchemeOptions, P1SetupTestAuthenticationHandler>("p1-test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(capability);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapP1ProjectSetupEndpoint();
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage SetupRequest(
        Guid invocationId,
        string subject,
        string? tenantId,
        string? scope = null,
        string? boundProjectId = null,
        string? tenantAssertion = null,
        string? query = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/p1/invocations/{invocationId:D}/setup{query}");
        request.Headers.Add("X-Test-Subject", subject);
        request.Headers.Add(
            "X-Test-Scope",
            scope ?? "api.read projects.admin projects.orchestrator");
        if (tenantId is not null)
            request.Headers.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        if (boundProjectId is not null)
            request.Headers.Add("X-Test-Bound-Project", boundProjectId);
        if (tenantAssertion is not null)
            request.Headers.Add("X-Test-Tenant-Assertion", tenantAssertion);
        return request;
    }

    private static async Task<HttpStatusCode> SendStatusAsync(
        HttpClient client,
        HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    internal sealed class P1SetupTestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim>
            {
                new("sub", Request.Headers["X-Test-Subject"].ToString()),
                new("scope", Request.Headers["X-Test-Scope"].ToString()),
            };
            if (Request.Headers.TryGetValue("X-Test-Bound-Project", out var projectId))
                claims.Add(new Claim("project_id", projectId.ToString()));
            if (Request.Headers.TryGetValue("X-Test-Tenant-Assertion", out var tenantId))
                claims.Add(new Claim("tenant_id", tenantId.ToString()));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
