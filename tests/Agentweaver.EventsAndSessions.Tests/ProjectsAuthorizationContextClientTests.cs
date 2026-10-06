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
