using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Agentweaver.EventsAndSessions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class ProjectsAuthorizationContextClientTests
{
    private const string Issuer = "https://identity.example";
    private const string Audience = "agentweaver.projects";
    private const string ActorId = "33333333-3333-3333-3333-333333333333";

    /// <summary>Rechecks project permission on every call rather than caching a previously granted read.</summary>
    [Fact]
    public async Task ProjectReadObservesRevokedAuthorityOnNextCall()
    {
        var owner = ValidContext() with { EffectiveAuthority = [new("project", "project-1", [new("readProjects", 1)])] };
        var calls = 0;
        var handler = new CaptureHandler(_ =>
        {
            calls++;
            return Success(owner);
        });
        var context = CreateRequestContext();
        ((ClaimsIdentity)context.User.Identity!).AddClaims([new("aud", "events"), new("scope", "api.read")]);
        var client = CreateClient(handler);
        await client.GetProjectReadAsync(context, "project-1", "events", default);
        owner = owner with { EffectiveAuthority = [] };
        await Assert.ThrowsAsync<ProjectsAuthorizationContextException>(
            () => client.GetProjectReadAsync(context, "project-1", "events", default));
        Assert.Equal(2, calls);
    }

    /// <summary>Accepts current project or tenant read grants and preserves optional run narrowing.</summary>
    [Theory]
    [InlineData("project", "readProjects", null, null, true)]
    [InlineData("project", "readRunSelection", "project-1", null, true)]
    [InlineData("tenant", "readProjects", "project-1", "run-1", true)]
    [InlineData("project", "writeProjects", null, null, false)]
    [InlineData("tenant", "readRunSelection", null, null, false)]
    public async Task ProjectReadRequiresCurrentReadPermission(
        string resource, string permission, string? project, string? run, bool allowed)
    {
        var owner = ValidContext() with
        {
            BoundProjectId = project, BoundRunId = run,
            EffectiveAuthority = [new(resource, resource == "tenant" ? "tenant-1" : "project-1",
                [new(permission, 1)])]
        };
        var handler = new CaptureHandler(_ => Success(owner));
        var context = CreateRequestContext();
        context.User = new(new ClaimsIdentity(
            new[] { new Claim("sub", ActorId), new("aud", Audience), new("aud", "events"), new("scope", "api.read") }
                .Concat(project is null ? [] : new[] { new Claim("project_id", project) })
                .Concat(run is null ? [] : new[] { new Claim("run_id", run) }), "validated"));
        if (allowed)
        {
            var result = await CreateClient(handler).GetProjectReadAsync(context, "project-1", "events", default);
            Assert.Equal(run, result.BoundRunId);
            Assert.Equal("tenant-1", result.TenantId);
        }
        else
            Assert.Equal(HttpStatusCode.Forbidden, (await Assert.ThrowsAsync<ProjectsAuthorizationContextException>(
                () => CreateClient(handler).GetProjectReadAsync(context, "project-1", "events", default))).StatusCode);
    }

    /// <summary>Rejects missing scopes, either missing audience, or conflicting bindings before sending a request.</summary>
    [Theory]
    [InlineData("projects.orchestrator", Audience, "events", "project-1", "run-1")]
    [InlineData("api.read", "other", "events", "project-1", "run-1")]
    [InlineData("api.read", Audience, "other", "project-1", "run-1")]
    [InlineData("api.read", Audience, "events", "other-project", "run-1")]
    [InlineData("api.read", Audience, "events", null, "run-1")]
    public async Task ProjectReadRejectsInvalidCallerBeforeOwnerCall(
        string scope, string projectsAudience, string eventsAudience, string? project, string? run)
    {
        var handler = new CaptureHandler(_ => throw new InvalidOperationException("Must not call Projects."));
        var context = CreateRequestContext();
        context.User = new(new ClaimsIdentity(
            new[] { new Claim("sub", ActorId), new("aud", projectsAudience), new("aud", eventsAudience), new("scope", scope) }
                .Concat(project is null ? [] : new[] { new Claim("project_id", project) })
                .Concat(run is null ? [] : new[] { new Claim("run_id", run) }), "validated"));
        await Assert.ThrowsAsync<ProjectsAuthorizationContextException>(
            () => CreateClient(handler).GetProjectReadAsync(context, "project-1", "events", default));
        Assert.Null(handler.RequestUri);
    }

    /// <summary>Does not accept changed tenant, caller, binding, or a cacheable response on the project-read path.</summary>
    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("binding")]
    [InlineData("cache")]
    public async Task ProjectReadRejectsStaleOrMismatchedContext(string change)
    {
        var owner = ValidContext() with { EffectiveAuthority = [new("project", "project-1", [new("readProjects", 1)])] };
        owner = change switch
        {
            "tenant" => owner with { TenantId = "tenant-2" },
            "actor" => owner with { ActorId = Guid.NewGuid().ToString("D") },
            "binding" => owner with { BoundRunId = "run-2" },
            _ => owner
        };
        var handler = new CaptureHandler(_ => Success(owner, noStore: change != "cache"));
        var context = CreateRequestContext();
        ((ClaimsIdentity)context.User.Identity!).AddClaims([new("aud", "events"), new("scope", "api.read")]);
        await Assert.ThrowsAsync<ProjectsAuthorizationContextException>(
            () => CreateClient(handler).GetProjectReadAsync(context, "project-1", "events", default));
    }

    [Fact]
    public async Task ForwardsCurrentCallerAndCheckedTenantToFixedNoStoreOwnerEndpoint()
    {
        var handler = new CaptureHandler(_ => Success(ValidContext()));
        var client = CreateClient(handler);

        var result = await client.GetCurrentAsync(CreateRequestContext());

        Assert.Equal(new Uri("https://projects.internal/api/authorization/context"), handler.RequestUri);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("current-caller-token", handler.BearerToken);
        Assert.Equal("tenant-1", handler.TenantSelector);
        Assert.Equal(ActorId, result.ActorId);
        Assert.Equal("project-1", result.BoundProjectId);
        Assert.Equal("run-1", result.BoundRunId);
        Assert.DoesNotContain("current-caller-token", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsRedirectsMissingNoStoreAndMismatchedOwnerContext()
    {
        var redirectHandler = new CaptureHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://untrusted.example/") }
            });
        var redirect = await Assert.ThrowsAsync<ProjectsAuthorizationContextException>(() =>
            CreateClient(redirectHandler).GetCurrentAsync(CreateRequestContext()));
        Assert.Equal("projects_authorization_redirect_rejected", redirect.Code);

        var noStoreHandler = new CaptureHandler(_ => Success(ValidContext(), noStore: false));
        var noStore = await Assert.ThrowsAsync<ProjectsAuthorizationContextException>(() =>
            CreateClient(noStoreHandler).GetCurrentAsync(CreateRequestContext()));
        Assert.Equal("projects_authorization_cache_policy_invalid", noStore.Code);

        var mismatchHandler = new CaptureHandler(_ =>
            Success(ValidContext() with { ActorId = "another-caller" }));
        var mismatch = await Assert.ThrowsAsync<ProjectsAuthorizationContextException>(() =>
            CreateClient(mismatchHandler).GetCurrentAsync(CreateRequestContext()));
        Assert.Equal("projects_authorization_context_mismatch", mismatch.Code);
    }

    [Fact]
    public async Task RequiresTrustedHttpsOwnerAndCallerTokenBoundForOwnerAudience()
    {
        var handler = new CaptureHandler(_ => Success(ValidContext()));
        var insecure = new ProjectsAuthorizationContextClient(
            new HttpClient(handler),
            new ProjectsAuthorizationContextOptions("http://projects.internal/", Audience, Issuer));
        var invalidConfiguration = await Assert.ThrowsAsync<ProjectsAuthorizationContextException>(() =>
            insecure.GetCurrentAsync(CreateRequestContext()));
        Assert.Equal("projects_authorization_configuration_invalid", invalidConfiguration.Code);
        Assert.Null(handler.RequestUri);

        var wrongAudienceRequest = CreateRequestContext();
        wrongAudienceRequest.User = CallerPrincipal(Audience: "agentweaver.events");
        var wrongAudience = await Assert.ThrowsAsync<ProjectsAuthorizationContextException>(() =>
            CreateClient(handler).GetCurrentAsync(wrongAudienceRequest));
        Assert.Equal("projects_authorization_caller_invalid", wrongAudience.Code);
        Assert.Null(handler.RequestUri);
    }

    private static ProjectsAuthorizationContextClient CreateClient(CaptureHandler handler) =>
        new(
            new HttpClient(handler),
            new ProjectsAuthorizationContextOptions(
                "https://projects.internal/", Audience, Issuer));

    private static DefaultHttpContext CreateRequestContext()
    {
        var context = new DefaultHttpContext
        {
            User = CallerPrincipal(Audience)
        };
        context.Request.Headers.Authorization = "Bearer current-caller-token";
        context.Request.Headers["X-Agentweaver-Tenant"] = "tenant-1";
        return context;
    }

    private static ClaimsPrincipal CallerPrincipal(string Audience)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", ActorId),
                new Claim("project_id", "project-1"),
                new Claim("run_id", "run-1"),
                new Claim("aud", Audience)
            ],
            "validated-bearer");
        return new ClaimsPrincipal(identity);
    }

    private static ProjectsAuthorizationContextResponse ValidContext() =>
        new(
            ContractVersion: 1,
            Issuer,
            ActorId,
            TenantId: "tenant-1",
            MembershipRevision: 3,
            BoundProjectId: "project-1",
            BoundRunId: "run-1",
            EffectiveAuthority: []);

    private static HttpResponseMessage Success(
        ProjectsAuthorizationContextResponse context,
        bool noStore = true)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(context, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json")
        };
        if (noStore)
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return response;
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? BearerToken { get; private set; }
        public string? TenantSelector { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            BearerToken = request.Headers.Authorization?.Parameter;
            TenantSelector = request.Headers.TryGetValues("X-Agentweaver-Tenant", out var values)
                ? values.Single()
                : null;
            return Task.FromResult(respond(request));
        }
    }
}
