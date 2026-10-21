using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class UserNotificationContractTests
{
    internal const string Issuer = "https://identity.example/";
    internal const string Actor = "11111111-1111-1111-1111-111111111111";

    [Theory]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?pageSize=-1")]
    [InlineData("?pageSize=1&pageSize=2")]
    [InlineData("?limit=10")]
    [InlineData("?cursor=")]
    [InlineData("?includeDismissed=yes")]
    [InlineData("?includeDismissed=true&includeDismissed=false")]
    public void RejectsUnknownRepeatedAndUnboundedQueries(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        var error = Assert.Throws<CoordinationException>(() =>
            UserNotificationEndpoints.ParseQuery(context.Request.Query));
        Assert.Equal("query_invalid", error.Code);
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void QuerySupportsSharedFlowWithoutChangingTheBellDefault()
    {
        var context = new DefaultHttpContext();
        Assert.Equal((25, null, null, false), UserNotificationEndpoints.ParseQuery(context.Request.Query));
        context.Request.QueryString = new QueryString("?projectId=project-a&includeDismissed=true&pageSize=100");
        Assert.Equal((100, null, "project-a", true), UserNotificationEndpoints.ParseQuery(context.Request.Query));
    }

    [Fact]
    public void StableIdentityIncludesProjectRunAndGateOccurrence()
    {
        var first = UserNotificationStore.NotificationId("project-a", "run-a", "request-a");
        Assert.Equal(first, UserNotificationStore.NotificationId("project-a", "run-a", "request-a"));
        Assert.NotEqual(first, UserNotificationStore.NotificationId("project-b", "run-a", "request-a"));
        Assert.NotEqual(first, UserNotificationStore.NotificationId("project-a", "run-b", "request-a"));
        Assert.NotEqual(first, UserNotificationStore.NotificationId("project-a", "run-a", "request-b"));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("""{"requestId":"other","fence":1,"allowedChoices":[]}""")]
    [InlineData("""{"requestId":"request","fence":2,"allowedChoices":[]}""")]
    public void InvalidPersistedGateFailsExplicitlyInsteadOfReturningAnEmptyList(string json)
    {
        var error = Assert.Throws<CoordinationException>(() =>
            UserNotificationStore.ReadGate(json, "request", 1));
        Assert.Equal("coordinator_gate_state_unavailable", error.Code);
        Assert.Equal(503, error.StatusCode);
    }

    [Fact]
    public void ReadAuthorityAcceptsViewersAndOrchestratorsButNotUnrelatedPermissions()
    {
        var authority = Authority() with
        {
            EffectiveAuthority =
            [
                new("project", "viewer", [new("readProjects", 1)]),
                new("project", "orchestrator", [new("readRunSelection", 1)]),
                new("project", "writer-only", [new("acceptRunSelection", 1)]),
                new("project", "stale", [new("readProjects", 0)]),
                new("tenant", "other-tenant", [new("readProjects", 1)])
            ]
        };
        var (all, ids) = UserNotificationStore.ReadProjects(authority);
        Assert.False(all);
        Assert.Equal(["orchestrator", "viewer"], ids);
    }

    [Fact]
    public async Task UnboundBrowserCallerUsesFreshProjectsAuthorityAndForwardsTenant()
    {
        using var http = new HttpClient(new AuthorityHandler(Authority()));
        var client = Client(http);
        var result = await client.ReadCallerAuthorityAsync(Context(), CancellationToken.None);
        Assert.Equal(Authority().ActorId, result.ActorId);
        Assert.Equal("project-a", Assert.Single(result.EffectiveAuthority).ResourceId);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("issuer")]
    [InlineData("tenant")]
    [InlineData("membership")]
    [InlineData("binding")]
    public async Task RejectsMismatchedOwnerAuthority(string change)
    {
        var authority = change switch
        {
            "actor" => Authority() with { ActorId = Guid.NewGuid().ToString() },
            "issuer" => Authority() with { Issuer = "https://other.example/" },
            "tenant" => Authority() with { TenantId = "other" },
            "membership" => Authority() with { MembershipRevision = 0 },
            "binding" => Authority() with { BoundProjectId = "project-a" },
            _ => throw new ArgumentException(change)
        };
        using var http = new HttpClient(new AuthorityHandler(authority));
        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            Client(http).ReadCallerAuthorityAsync(Context(), CancellationToken.None));
        Assert.Equal("projects_authorization_contract_invalid", error.Code);
    }

    [Theory]
    [InlineData("aud")]
    [InlineData("scope")]
    [InlineData("duplicate-project")]
    [InlineData("run-without-project")]
    public async Task RejectsInvalidBrowserClaimsBeforeCallingOwner(string change)
    {
        var context = Context();
        var identity = (ClaimsIdentity)context.User.Identity!;
        if (change is "aud" or "scope")
            identity.RemoveClaim(identity.FindFirst(change)!);
        if (change == "duplicate-project")
            identity.AddClaims([new("project_id", "a"), new("project_id", "b")]);
        if (change == "run-without-project") identity.AddClaim(new("run_id", "run-a"));
        using var http = new HttpClient(new AuthorityHandler(Authority()));
        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            Client(http).ReadCallerAuthorityAsync(context, CancellationToken.None));
        Assert.Equal(403, error.StatusCode);
    }

    internal static ProjectsAuthorizationContext Authority() => new(
        1, Issuer, Actor, "tenant-a", 1, null, null,
        [new("project", "project-a", [new("readProjects", 1)])]);

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new("sub", Actor), new("aud", "orchestrator"), new("aud", "projects"),
            new("scope", "api.read")
        ], "test"));
        context.Request.Headers.Authorization = "Bearer notification-test";
        context.Request.Headers["X-Agentweaver-Tenant"] = "tenant-a";
        return context;
    }

    private static ProjectsRunSelectionClient Client(HttpClient http) => new(
        http, new(Issuer, "orchestrator", "https://projects.example/", "projects", "", ""), new NoSnapshots());

    private sealed class NoSnapshots : IReviewedRemoteToolSnapshotResolver
    {
        public Task<ReviewedRemoteToolSnapshot?> ResolveAsync(
            ReviewedRemoteToolSnapshotReference reference, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Notifications do not read accepted selection snapshots.");
    }

    private sealed class AuthorityHandler(ProjectsAuthorizationContext authority) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/api/authorization/context", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer notification-test", request.Headers.Authorization!.ToString());
            Assert.Equal("tenant-a", request.Headers.GetValues("X-Agentweaver-Tenant").Single());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(authority) };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }
    }
}
