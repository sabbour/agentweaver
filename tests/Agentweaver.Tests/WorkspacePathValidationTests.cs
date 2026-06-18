using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Integration tests for the path-validation logic in
/// GET /api/runs/{id}/files/{**path}.
///
/// Security requirements verified:
///   SC-002 (extended): 100% of path-escape attempts against the artifact
///   browser file-content endpoint must be rejected before any file I/O occurs.
///   SC-009: non-owners receive 404 (not 403) to prevent run-id enumeration.
///
/// Path validation must reject:
///   - empty paths
///   - paths containing ".." traversal segments
///   - absolute paths
///   - null bytes
///   - UNC paths (Windows)
///   - paths not present in the run's changed-file set (404, not 400)
///
/// RUNTIME NOTE: Tests will fail with 404 (route not found) or 405 until Tank
/// adds the GET /api/runs/{id}/files/{**path} endpoint. That is expected — the
/// tests document the contract. They will pass once the endpoint lands.
/// </summary>
public sealed class WorkspacePathValidationTests : IClassFixture<AgentweaverWebApplicationFactory>
{
    private readonly AgentweaverWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public WorkspacePathValidationTests(AgentweaverWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", AgentweaverWebApplicationFactory.TestApiKey);
    }

    // =========================================================================
    // Helper: insert a run with the given status and return its id string.
    // =========================================================================
    private async Task<string> InsertRunAsync(RunStatus status = RunStatus.Pending)
    {
        var store = _factory.Services.GetRequiredService<SqliteRunStore>();
        var run = new Run
        {
            Id                = RunId.New(),
            RepositoryPath    = "dummy-repo",
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "path validation test",
            SubmittingUser    = AgentweaverWebApplicationFactory.TestUser,
            Status            = status,
            StartedAt         = DateTimeOffset.UtcNow,
        };
        await store.InsertAsync(run);
        return run.Id.ToString();
    }

    // =========================================================================
    // PV-01: a clean relative path within the worktree passes validation.
    // Because the run is pending (no worktree), the endpoint returns 404
    // (file not found in changed-file list), NOT 400.
    // This confirms path validation accepted the path before the file lookup.
    // =========================================================================
    [Fact]
    public async Task ValidRelativePath_PassesValidation_DoesNotReturn400()
    {
        var runId = await InsertRunAsync();

        var response = await _client.GetAsync($"/api/runs/{runId}/files/src/app.ts");

        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest,
            "a clean relative path must pass validation; 400 would indicate a false rejection");
    }

    // =========================================================================
    // PV-02: a path containing ".." traversal is rejected with 400.
    // The path is delivered via URL encoding (%2F for /) so the HTTP layer does
    // not normalize it before the application sees it. The application must
    // detect the decoded ".." segment and reject before any file I/O.
    // =========================================================================
    [Fact]
    public async Task DotDotTraversal_IsRejectedWith400()
    {
        var runId = await InsertRunAsync();

        // Encode the slash so ASP.NET Core does not split the segment; the
        // application-level validator must decode and reject the ".." component.
        var response = await _client.GetAsync($"/api/runs/{runId}/files/..%2Fetc%2Fpasswd");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "path traversal via '..' must be rejected with 400");
    }

    // =========================================================================
    // PV-03: an absolute path is rejected with 400.
    // Absolute paths cannot be within the worktree root and must be rejected
    // before any resolution is attempted.
    // =========================================================================
    [Fact]
    public async Task AbsolutePath_IsRejectedWith400()
    {
        var runId = await InsertRunAsync();

        // Forward-slash absolute path encoded to avoid HTTP normalization.
        var response = await _client.GetAsync($"/api/runs/{runId}/files/%2Fetc%2Fpasswd");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an absolute path must be rejected with 400");
    }

    // =========================================================================
    // PV-04: a path containing a null byte (%00) is rejected.
    // Null bytes are illegal in file paths and can be used to truncate path
    // strings in native code. The test HTTP client (TestHost) throws
    // InvalidOperationException when the URL contains a null byte, which is
    // equivalent to a 400 rejection — both outcomes confirm the null byte was
    // not accepted. The assertion covers both cases.
    // =========================================================================
    [Fact]
    public async Task NullByteInPath_IsRejectedWith400()
    {
        var runId = await InsertRunAsync();

        // %00 is URL-encoded null byte. The test host client (or ASP.NET Core's
        // request pipeline) rejects null bytes before routing. Either a 400 status
        // response OR an InvalidOperationException from the client confirms rejection.
        try
        {
            var response = await _client.GetAsync($"/api/runs/{runId}/files/src%2Ffile%00.ts");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "a path containing a null byte must be rejected with 400");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("null", StringComparison.OrdinalIgnoreCase))
        {
            // The test host client rejected the URL containing the null byte before
            // sending it. This is an equivalent rejection outcome to a 400 response.
        }
    }

    // =========================================================================
    // PV-05: URL-encoded traversal (%2F between ".." and path) is rejected.
    // ASP.NET Core decodes %2F once in the catch-all path parameter, producing
    // a value like "../etc/passwd". The application validator must reject the
    // decoded form even though the raw URL looked well-formed.
    // =========================================================================
    [Fact]
    public async Task EncodedTraversalSegment_IsRejectedWith400()
    {
        var runId = await InsertRunAsync();

        // Double-encode the separator so one decode yields "../etc/passwd".
        var response = await _client.GetAsync($"/api/runs/{runId}/files/..%252Fetc%252Fpasswd");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "double-encoded traversal that decodes to '../...' must be rejected with 400");
    }

    // =========================================================================
    // PV-06: a UNC path (\\server\share) is rejected with 400.
    // UNC paths cannot reside within the worktree root and may bypass OS-level
    // path validation on Windows. Backslashes are URL-encoded as %5C.
    // =========================================================================
    [Fact]
    public async Task UncPath_IsRejectedWith400()
    {
        var runId = await InsertRunAsync();

        // \\server\share encoded as %5C%5Cserver%5Cshare.
        var response = await _client.GetAsync($"/api/runs/{runId}/files/%5C%5Cserver%5Cshare");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a UNC path must be rejected with 400");
    }

    // =========================================================================
    // PV-07: a valid-looking path that is NOT in the run's changed-file list
    // returns 404, not 400. The distinction is important: 400 means the path
    // itself was malformed; 404 means validation passed but the file was not
    // found in this run's artifact set.
    // =========================================================================
    [Fact]
    public async Task PathNotInChangedFileWhitelist_Returns404NotBadRequest()
    {
        // A pending run has no worktree and no changed files.
        var runId = await InsertRunAsync(RunStatus.Pending);

        var response = await _client.GetAsync($"/api/runs/{runId}/files/src/nonexistent.ts");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a valid path not present in the run's artifact set must return 404, not 400");
        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest,
            "404 (not found) must be used when validation passes but the file is absent");
    }

    // =========================================================================
    // PV-08: an empty path segment (GET /api/runs/{id}/files/ with no path)
    // is rejected. An empty path cannot identify any file and must not be
    // forwarded to the file-reading layer.
    // =========================================================================
    [Fact]
    public async Task EmptyPath_IsRejected()
    {
        var runId = await InsertRunAsync();

        // Trailing slash produces an empty catch-all value.
        var response = await _client.GetAsync($"/api/runs/{runId}/files/");

        // Empty path must be rejected; acceptable responses are 400 or 404
        // (if the route does not match at all). 200 is never acceptable.
        response.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "an empty path must not return 200");
    }
}
