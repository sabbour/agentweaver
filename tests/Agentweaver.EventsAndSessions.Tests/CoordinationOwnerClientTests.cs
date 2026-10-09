using System.Net;
using System.Security.Claims;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class CoordinationOwnerClientTests
{
    [Fact]
    public async Task ProducedCaptureReadPreservesOwnerNotFoundOnlyWhenRequested()
    {
        using var httpClient = new HttpClient(new NotFoundHandler());
        var client = new CoordinationOwnerClient(
            httpClient,
            new CoordinationOwnerClientOptions(
                "https://orchestrator.test/",
                "events",
                "https://issuer.test/"));
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer token";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("aud", "events"),
            new Claim("iss", "https://issuer.test/")
        ], "test"));
        var identity = new SessionIdentity("project-1", "run-1", "session-1");
        const string captureId = "sc-output-00000000000000000000000000000000";

        var missing = await Assert.ThrowsAsync<CoordinationOwnerClientException>(() =>
            client.ReadProducedRunCaptureProofAsync(
                context,
                identity,
                captureId,
                CancellationToken.None,
                notFoundIsMissing: true));
        Assert.Equal(StatusCodes.Status404NotFound, missing.StatusCode);
        Assert.Equal("source_control_output_capture_not_found", missing.Code);

        var unavailable = await Assert.ThrowsAsync<CoordinationOwnerClientException>(() =>
            client.ReadProducedRunCaptureProofAsync(
                context, identity, captureId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status502BadGateway, unavailable.StatusCode);
        Assert.Equal("coordination_owner_unavailable", unavailable.Code);
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
