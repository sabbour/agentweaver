using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class EventsAddressedMessageClientTests
{
    private static readonly SessionIdentity Identity = new("project-1", "run-1", "session-1");
    private static readonly OrchestratorOptions Options = new(
        "https://broker.test/",
        "https://orchestrator.test",
        "https://projects.test/",
        "https://projects.test",
        "https://events.test/",
        "https://events.test");

    [Fact]
    public async Task EnsureSessionForwardsTheCurrentCallerToTheFixedEventsRoute()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent(new SessionRecord(Identity, DateTimeOffset.UtcNow, 0))
        });
        using var httpClient = new HttpClient(handler);
        var client = new EventsAddressedMessageClient(httpClient, Options, new HttpContextAccessor());
        var context = CreateContext();

        await client.EnsureSessionAsync(context, Identity, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request?.Method);
        Assert.Equal("https://events.test/internal/sessions/session-1", handler.Request?.Uri?.AbsoluteUri);
        Assert.Equal("Bearer current-caller-token", handler.Request?.Authorization);
        Assert.Equal("tenant-1", handler.Request?.Tenant);
    }

    [Fact]
    public async Task EnsureSessionRejectsAResponseForAnotherIdentity()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent(new SessionRecord(
                new SessionIdentity("project-1", "run-1", "different-session"),
                DateTimeOffset.UtcNow,
                0))
        });
        using var httpClient = new HttpClient(handler);
        var client = new EventsAddressedMessageClient(httpClient, Options, new HttpContextAccessor());

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            client.EnsureSessionAsync(CreateContext(), Identity, CancellationToken.None));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("events_session_registration_contract_invalid", error.Code);
    }

    private static HttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer current-caller-token";
        context.Request.Headers["X-Agentweaver-Tenant"] = "tenant-1";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "33333333-3333-3333-3333-333333333333"),
            new Claim("iss", Options.Issuer),
            new Claim("aud", Options.EventsAudience),
            new Claim("scope", "api.read projects.orchestrator"),
            new Claim("project_id", Identity.ProjectId),
            new Claim("run_id", Identity.RunId)
        ], "test"));
        return context;
    }

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public CapturedRequest? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenant)
                    ? tenant.Single()
                    : null);
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string? Authorization, string? Tenant);
}
