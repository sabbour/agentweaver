using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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

    [Fact]
    public async Task ExplicitForkForwardsTheCallerAndExactCommittedPrefix()
    {
        var fork = new SessionForkRequest(
            "fork-target", Guid.NewGuid(), "committed-cursor-v1", "fork-once");
        var target = new SessionIdentity(Identity.ProjectId, Identity.RunId, fork.TargetSessionId);
        var result = new SessionForkResult(
            new SessionRecord(target, DateTimeOffset.UtcNow, 3),
            new SessionForkLineage(
                Identity,
                fork.SourceEventId,
                3,
                1,
                1,
                1,
                new string('a', 64),
                fork.SourceCursor),
            IsDuplicate: false);
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent(result)
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        });
        using var httpClient = new HttpClient(handler);
        var client = new EventsAddressedMessageClient(httpClient, Options, new HttpContextAccessor());
        var context = CreateContext();
        var expectedAuthorization = CoordinationIdentity.RequireBearer(context).ToString();

        var returned = await client.ForkFromExplicitEventAsync(
            context, Identity, fork, CancellationToken.None);

        Assert.Equal(result, returned);
        Assert.Equal(HttpMethod.Post, handler.Request?.Method);
        Assert.Equal("https://events.test/internal/sessions/session-1/fork",
            handler.Request?.Uri?.AbsoluteUri);
        Assert.Equal(expectedAuthorization, handler.Request?.Authorization);
        Assert.Equal("tenant-1", handler.Request?.Tenant);
        var forwarded = JsonSerializer.Deserialize<SessionForkRequest>(
            handler.Request!.Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(fork, forwarded);
    }

    [Fact]
    public async Task ProducedRunCaptureWriteForwardsPackageAndValidatesJournalAcknowledgment()
    {
        var package = Encoding.ASCII.GetBytes("AWSCAP01data");
        var proof = CreateCaptureProof(package);
        var acknowledgment = new ProducedRunCaptureAcknowledgment(
            new ProducedRunCaptureJournalEntry(proof, 7),
            IsDuplicate: false);
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent(acknowledgment)
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        });
        using var httpClient = new HttpClient(handler);
        var client = new EventsAddressedMessageClient(httpClient, Options, new HttpContextAccessor());
        var context = CreateContext();
        var expectedAuthorization = CoordinationIdentity.RequireBearer(context).ToString();

        var returned = await client.WriteProducedRunCaptureAsync(
            context, proof, package, CancellationToken.None);

        Assert.Equal(acknowledgment, returned);
        Assert.Equal(HttpMethod.Post, handler.Request?.Method);
        Assert.Equal(
            $"https://events.test/internal/sessions/session-1/produced-run-captures/{proof.CaptureId}",
            handler.Request?.Uri?.AbsoluteUri);
        Assert.Equal(expectedAuthorization, handler.Request?.Authorization);
        Assert.Equal("tenant-1", handler.Request?.Tenant);
        Assert.Equal("application/octet-stream", handler.Request?.ContentType);
        Assert.Equal(Encoding.ASCII.GetString(package), handler.Request?.Body);
    }

    [Fact]
    public async Task ProducedRunCaptureReadValidatesRawPackageAndReturnsJournalPosition()
    {
        var package = Encoding.ASCII.GetBytes("AWSCAP01data");
        var proof = CreateCaptureProof(package);
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(package)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            response.Headers.TryAddWithoutValidation("X-Session-Event-Position", "7");
            return response;
        });
        using var httpClient = new HttpClient(handler);
        var client = new EventsAddressedMessageClient(httpClient, Options, new HttpContextAccessor());
        var context = CreateContext();
        var expectedAuthorization = CoordinationIdentity.RequireBearer(context).ToString();

        var returned = await client.ReadProducedRunCaptureAsync(
            context, proof, CancellationToken.None);

        Assert.Equal(new ProducedRunCaptureJournalEntry(proof, 7), returned.Entry);
        Assert.Equal(package, returned.PackageBytes);
        Assert.Equal(HttpMethod.Get, handler.Request?.Method);
        Assert.Equal(
            $"https://events.test/internal/sessions/session-1/produced-run-captures/events/{proof.EventId:D}",
            handler.Request?.Uri?.AbsoluteUri);
        Assert.Equal(expectedAuthorization, handler.Request?.Authorization);
        Assert.Equal("tenant-1", handler.Request?.Tenant);
    }

    [Fact]
    public async Task ProducedRunCaptureWriteRejectsAnAcknowledgmentForAnotherProof()
    {
        var package = Encoding.ASCII.GetBytes("AWSCAP01data");
        var proof = CreateCaptureProof(package);
        var otherProof = CreateCaptureProof(package, "other-session");
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent(new ProducedRunCaptureAcknowledgment(
                    new ProducedRunCaptureJournalEntry(otherProof, 7),
                    IsDuplicate: false))
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        });
        using var httpClient = new HttpClient(handler);
        var client = new EventsAddressedMessageClient(httpClient, Options, new HttpContextAccessor());

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            client.WriteProducedRunCaptureAsync(CreateContext(), proof, package, CancellationToken.None));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("events_output_capture_contract_invalid", error.Code);
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

    private static ProducedRunCaptureProof CreateCaptureProof(byte[] package, string sessionId = "session-1")
    {
        var identity = new SessionIdentity("project-1", "run-1", sessionId);
        var selectionHash = new string('a', 64);
        var manifestHash = new string('b', 64);
        var captureIdentity = ProducedRunCaptureContractValidation.CreateIdentity(
            identity,
            "pin-1",
            selectionHash,
            "workspace-1",
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "repo-1",
            1,
            new string('c', 40),
            new string('d', 40),
            manifestHash);
        return new ProducedRunCaptureProof(
            ProducedRunCaptureLimits.ContractVersion,
            identity,
            captureIdentity.CaptureId,
            captureIdentity.EventId,
            Options.Issuer,
            "actor-1",
            "tenant-1",
            "pin-1",
            selectionHash,
            "workspace-1",
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "repo-1",
            1,
            new string('c', 40),
            new string('d', 40),
            manifestHash,
            1,
            new string('e', 64),
            0,
            Convert.ToHexStringLower(SHA256.HashData(package)),
            package.LongLength,
            DateTimeOffset.UnixEpoch);
    }

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public CapturedRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Request = new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenant)
                    ? tenant.Single()
                    : null,
                body,
                request.Content?.Headers.ContentType?.MediaType);
            return responseFactory(request);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? Uri,
        string? Authorization,
        string? Tenant,
        string? Body,
        string? ContentType);
}
