using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Integration tests verifying the /api/runs/{id}/stream endpoint:
/// - Authorization: non-owner is denied (returns 404 to prevent run-id enumeration)
/// - Both in-progress and completed runs are protected
/// </summary>
public sealed class StreamEndpointTests : IClassFixture<AgentweaverWebApplicationFactory>
{
    private readonly AgentweaverWebApplicationFactory _factory;
    private readonly HttpClient _ownerClient;

    public StreamEndpointTests(AgentweaverWebApplicationFactory factory)
    {
        _factory = factory;
        _ownerClient = factory.CreateClient();
        _ownerClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AgentweaverWebApplicationFactory.TestApiKey);
    }

    [Fact]
    public async Task NonOwner_IsDenied_InProgressRun()
    {
        // Arrange: create a stream entry owned by a different user
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var runId = Guid.NewGuid().ToString();
        var entry = streamStore.Create(runId, "other-user");
        entry.Record(new RunEvent(1, "agent.message.delta", new { delta = "secret data" }));

        // Act: the test user (who is not "other-user") requests the stream
        var response = await _ownerClient.GetAsync($"/api/runs/{runId}/stream");

        // Assert: 404 (not 403, to prevent enumeration)
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NonOwner_IsDenied_CompletedRun()
    {
        // Arrange: create a completed stream entry owned by a different user
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var runId = Guid.NewGuid().ToString();
        var entry = streamStore.Create(runId, "other-user");
        entry.Record(new RunEvent(1, "agent.message.delta", new { delta = "secret" }));
        entry.Record(new RunEvent(2, "run.completed", new { }));
        streamStore.Complete(runId);

        // Act
        var response = await _ownerClient.GetAsync($"/api/runs/{runId}/stream");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Owner_CanStream_InProgressRun()
    {
        // Arrange: create an entry owned by the test user
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var runId = Guid.NewGuid().ToString();
        var entry = streamStore.Create(runId, AgentweaverWebApplicationFactory.TestUser);
        entry.Record(new RunEvent(1, "agent.message.delta", new { delta = "hello" }));
        entry.Record(new RunEvent(2, "run.completed", new { }));
        entry.MarkCompleted();

        // Act
        var response = await _ownerClient.GetAsync($"/api/runs/{runId}/stream");

        // Assert: 200 with SSE content
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("agent.message.delta");
        body.Should().Contain("event: done");
    }

    [Fact]
    public async Task NonexistentRun_Returns404()
    {
        var response = await _ownerClient.GetAsync($"/api/runs/{Guid.NewGuid()}/stream");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
