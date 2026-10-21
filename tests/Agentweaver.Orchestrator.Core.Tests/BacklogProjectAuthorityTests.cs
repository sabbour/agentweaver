using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class BacklogProjectAuthorityTests
{
    private const string Issuer = "https://issuer.test/";
    private const string Actor = "11111111-1111-1111-1111-111111111111";

    [Theory]
    [InlineData(false, "readProjects")]
    [InlineData(true, "writeProjects")]
    public async Task CurrentProjectAuthorityDoesNotRequireRunSelection(bool write, string permission)
    {
        using var handler = new AuthorityHandler(Authority(permission));
        using var http = new HttpClient(handler);
        var authority = await Client(http).ReadBacklogProjectAuthorityAsync(
            Context(), "project", write, CancellationToken.None);
        Assert.Equal(Actor, authority.ActorId);
        Assert.Equal(["/api/authorization/context"], handler.Paths);
    }

    [Theory]
    [InlineData("run_id", "run")]
    [InlineData("project_id", "foreign")]
    [InlineData("purpose", "runtime")]
    public async Task RejectsBoundAndPurposeTokensBeforeOwnerRead(string type, string value)
    {
        using var handler = new AuthorityHandler(Authority("writeProjects"));
        using var http = new HttpClient(handler);
        var context = Context();
        ((ClaimsIdentity)context.User.Identity!).AddClaim(new(type, value));
        var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
            Client(http).ReadBacklogProjectAuthorityAsync(context, "project", true, CancellationToken.None));
        Assert.Equal(403, failure.StatusCode);
        Assert.Empty(handler.Paths);
    }

    [Theory]
    [InlineData("readProjects", 1)]
    [InlineData("writeProjects", 0)]
    public async Task DeniesMissingOrRevokedWritePermission(string permission, long revision)
    {
        using var handler = new AuthorityHandler(Authority(permission, revision));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
            Client(http).ReadBacklogProjectAuthorityAsync(Context(), "project", true, CancellationToken.None));
        Assert.Equal(403, failure.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TenantAdministratorMustResolveProjectInTheAuthorizedTenant(bool visible)
    {
        var authority = Authority("writeProjects") with
        {
            EffectiveAuthority = [new("tenant", "tenant", [new("writeProjects", 2)])]
        };
        using var handler = new AuthorityHandler(authority, visible);
        using var http = new HttpClient(handler);
        if (visible)
            Assert.Equal(Actor, (await Client(http).ReadBacklogProjectAuthorityAsync(
                Context(), "project", true, CancellationToken.None)).ActorId);
        else
            Assert.Equal(403, (await Assert.ThrowsAsync<CoordinationException>(() =>
                Client(http).ReadBacklogProjectAuthorityAsync(
                    Context(), "project", true, CancellationToken.None))).StatusCode);
        Assert.Equal(["/api/authorization/context", "/api/projects/project"], handler.Paths);
    }

    [Fact]
    public void RevalidationIgnoresUnrelatedGrantOrderButRejectsCurrentRevisionChanges()
    {
        var authority = Authority("writeProjects") with
        {
            EffectiveAuthority =
            [
                new("project", "project", [new("writeProjects", 1)]),
                new("project", "other", [new("writeProjects", 5)])
            ]
        };
        var unrelated = authority with
        {
            EffectiveAuthority =
            [
                new("project", "other", [new("writeProjects", 6)]),
                new("project", "project", [new("writeProjects", 1)])
            ]
        };
        Assert.True(CoordinationEndpoints.BacklogProjectAuthorityIsUnchanged(
            authority, unrelated, "project", true));
        Assert.False(CoordinationEndpoints.BacklogProjectAuthorityIsUnchanged(
            authority, Authority("writeProjects", 2), "project", true));
        Assert.False(CoordinationEndpoints.BacklogProjectAuthorityIsUnchanged(
            authority, authority with { MembershipRevision = 2 }, "project", true));
    }

    private static ProjectsAuthorizationContext Authority(string permission, long revision = 1) =>
        new(1, Issuer, Actor, "tenant", 1, null, null,
            [new("project", "project", [new(permission, revision)])]);

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-token").ToString();
        context.User = new(new ClaimsIdentity(
            [new("sub", Actor), new("aud", "orchestrator-api"), new("aud", "projects-api"),
                new("scope", "api.read projects.admin")], "test"));
        return context;
    }

    private static ProjectsRunSelectionClient Client(HttpClient http) =>
        new(http, new(Issuer, "orchestrator-api", "https://projects.test/", "projects-api", "", ""),
            new UnusedResolver());

    private sealed class UnusedResolver : IReviewedRemoteToolSnapshotResolver
    {
        public Task<ReviewedRemoteToolSnapshot?> ResolveAsync(
            ReviewedRemoteToolSnapshotReference reference, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Project task writes must not resolve run selections.");
    }

    private sealed class AuthorityHandler(
        ProjectsAuthorizationContext authority, bool visible = true) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath != "/api/authorization/context")
            {
                Assert.Equal("tenant", Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
                var project = new HttpResponseMessage(visible ? HttpStatusCode.OK : HttpStatusCode.NotFound)
                {
                    Content = JsonContent.Create(new { projectId = "project" })
                };
                project.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return Task.FromResult(project);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(authority) };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        }
    }
}
