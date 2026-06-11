using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Api;

/// <summary>
/// Integration tests for GET /api/runs/{id}/files.
///
/// Tests the file-listing endpoint introduced by the artifact-browser feature
/// (FR-034, FR-035). Exercises run ownership, filter parameters, and the
/// behavior for runs in various states.
///
/// Constitution coverage:
///   SC-009 / Principle X: non-owner receives 404 (not 403) to prevent
///   run-id enumeration.
///   NFR-002 (Principle VII): no emoji in file paths or status strings
///   returned by the API.
///
/// Uses ReviewWebApplicationFactory which registers two API keys (owner and
/// other) so authorization tests can exercise the non-owner 404 path.
/// </summary>
public sealed class ArtifactFilesEndpointTests : IClassFixture<ReviewWebApplicationFactory>
{
    private readonly ReviewWebApplicationFactory _factory;
    private readonly HttpClient _ownerClient;
    private readonly HttpClient _otherClient;

    public ArtifactFilesEndpointTests(ReviewWebApplicationFactory factory)
    {
        _factory = factory;

        _ownerClient = factory.CreateClient();
        _ownerClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", ReviewWebApplicationFactory.OwnerApiKey);

        _otherClient = factory.CreateClient();
        _otherClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", ReviewWebApplicationFactory.OtherApiKey);
    }

    // =========================================================================
    // Helper: insert a run owned by the owner user and return its id string.
    // =========================================================================
    private async Task<string> InsertOwnerRunAsync(RunStatus status = RunStatus.Pending)
    {
        var store = _factory.Services.GetRequiredService<SqliteRunStore>();
        var run = new Run
        {
            Id                = RunId.New(),
            RepositoryPath    = "dummy-repo",
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "artifact files test",
            SubmittingUser    = ReviewWebApplicationFactory.OwnerUser,
            Status            = status,
            StartedAt         = DateTimeOffset.UtcNow,
        };
        await store.InsertAsync(run);
        return run.Id.ToString();
    }

    // =========================================================================
    // AF-01: unknown run id returns 404.
    // =========================================================================
    [Fact]
    public async Task UnknownRunId_Returns404()
    {
        var unknownId = Guid.NewGuid().ToString();

        var response = await _ownerClient.GetAsync($"/api/runs/{unknownId}/files");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =========================================================================
    // AF-02 (SC-009 / Principle X): a run owned by a different API key returns
    // 404 (not 403) to prevent run-id enumeration.
    // =========================================================================
    [Fact]
    public async Task NonOwner_IsDenied_Returns404NotForbidden()
    {
        var runId = await InsertOwnerRunAsync();

        // The other client is authenticated but does not own the run.
        var response = await _otherClient.GetAsync($"/api/runs/{runId}/files");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "non-owners must receive 404, not 403, to prevent run-id enumeration (SC-009)");
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "403 would reveal that the run exists and is owned by someone else");
    }

    // =========================================================================
    // AF-03: a pending run (no worktree yet) returns 200 with an empty array.
    // The browser must be accessible from the moment the run is created (FR-038).
    // =========================================================================
    [Fact]
    public async Task PendingRun_Returns200WithEmptyArray()
    {
        var runId = await InsertOwnerRunAsync(RunStatus.Pending);

        var response = await _ownerClient.GetAsync($"/api/runs/{runId}/files");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the artifact browser must be accessible for pending runs (FR-038)");

        var body   = await response.Content.ReadAsStringAsync();
        var parsed = JsonDocument.Parse(body);
        parsed.RootElement.ValueKind.Should().Be(JsonValueKind.Array,
            "the response body must be a JSON array");
        parsed.RootElement.GetArrayLength().Should().Be(0,
            "a pending run with no worktree has no changed files");
    }

    // =========================================================================
    // AF-04: filter param ?filter=all is accepted; returns 200.
    // =========================================================================
    [Fact]
    public async Task FilterAll_Returns200()
    {
        var runId = await InsertOwnerRunAsync();

        var response = await _ownerClient.GetAsync($"/api/runs/{runId}/files?filter=all");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "'all' is a valid filter value per FR-035");
    }

    // =========================================================================
    // AF-05: an invalid filter param returns 400 with an error message.
    // =========================================================================
    [Fact]
    public async Task InvalidFilter_Returns400WithErrorMessage()
    {
        var runId = await InsertOwnerRunAsync();

        var response = await _ownerClient.GetAsync($"/api/runs/{runId}/files?filter=invalid");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an unrecognised filter value must be rejected with 400");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrEmpty("a 400 response must include an error message");

        // The error message itself must not contain emoji (NFR-002).
        ContainsEmoji(body).Should().BeFalse(
            "error messages must not contain emoji (NFR-002)");
    }

    // =========================================================================
    // AF-06: omitting the filter param returns 200 using the default "all"
    // behaviour. This validates that the filter is optional.
    // =========================================================================
    [Fact]
    public async Task NoFilterParam_Returns200WithDefaultBehavior()
    {
        var runId = await InsertOwnerRunAsync();

        var response = await _ownerClient.GetAsync($"/api/runs/{runId}/files");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the filter param must be optional; omitting it must default to 'all'");

        var body   = await response.Content.ReadAsStringAsync();
        var parsed = JsonDocument.Parse(body);
        parsed.RootElement.ValueKind.Should().Be(JsonValueKind.Array,
            "the default response must be a JSON array");
    }

    // =========================================================================
    // AF-07 (NFR-002 / Principle VII): file paths and status strings in the
    // API response must not contain emoji characters.
    // This test is a placeholder that asserts the property on an empty response
    // today; it will exercise real entries once Tank wires up diff parsing.
    // =========================================================================
    [Fact]
    public async Task FileListResponse_ContainsNoEmojiInPathsOrStatusStrings()
    {
        var runId = await InsertOwnerRunAsync();

        var response = await _ownerClient.GetAsync($"/api/runs/{runId}/files");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body   = await response.Content.ReadAsStringAsync();
        var parsed = JsonDocument.Parse(body);

        foreach (var entry in parsed.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("path", out var pathProp))
            {
                var path = pathProp.GetString() ?? string.Empty;
                ContainsEmoji(path).Should().BeFalse(
                    $"file path '{path}' in API response must not contain emoji (NFR-002)");
            }

            if (entry.TryGetProperty("status", out var statusProp))
            {
                var status = statusProp.GetString() ?? string.Empty;
                ContainsEmoji(status).Should().BeFalse(
                    $"status '{status}' in API response must not contain emoji (NFR-002)");
            }
        }
    }

    // Returns true when the string contains any emoji or symbol-range Unicode code point.
    private static bool ContainsEmoji(string value) =>
        value.EnumerateRunes().Any(r =>
            (r.Value >= 0x1F300 && r.Value <= 0x1FAFF) ||
            (r.Value >= 0x2600  && r.Value <= 0x27BF));
}
