using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class ProjectsRunSelectionReviewedSnapshotTests
{
    private const string Issuer = "https://issuer.test/";
    private const string ActorId = "11111111-1111-1111-1111-111111111111";
    private const string ProjectId = "project-1";
    private const string RunId = "run-1";

    public static IEnumerable<object[]> LegacySelections()
    {
        yield return [ """{"projectId" : "project-1", "runId" : "run-1", "projectRevision" : 1, "projectConfigurationRevision" : 2, "platformRuntimeRevision" : 3, "contextRevision" : "context-1" }""" ];
        yield return [ """{"projectId" : "project-1", "runId" : "run-1", "projectRevision" : 1, "projectConfigurationRevision" : 2, "platformRuntimeRevision" : 3, "contextRevision" : "context-1", "projectConfiguration" : null }""" ];
        yield return [ """{"projectId" : "project-1", "runId" : "run-1", "projectRevision" : 1, "projectConfigurationRevision" : 2, "platformRuntimeRevision" : 3, "contextRevision" : "context-1", "projectConfiguration" : {} }""" ];
        yield return [ """{"projectId" : "project-1", "runId" : "run-1", "projectRevision" : 1, "projectConfigurationRevision" : 2, "platformRuntimeRevision" : 3, "contextRevision" : "context-1", "projectConfiguration" : {"reviewedRemoteToolSnapshots" : null} }""" ];
    }

    [Theory]
    [MemberData(nameof(LegacySelections))]
    public async Task LegacySelectionsRemainByteStableAndDoNotResolveSnapshots(string rawSelection)
    {
        var resolver = new RecordingResolver((_, _) =>
            Task.FromResult<ReviewedRemoteToolSnapshot?>(null));
        var events = new List<string>();
        var handler = SelectionHandler(rawSelection, allowed: true, allowedAfterSelection: true, events);
        using var httpClient = new HttpClient(handler);
        var client = new ProjectsRunSelectionClient(httpClient, Options(), resolver);

        var result = await client.ReadAcceptedSelectionWithAuthorityAsync(
            RequestContext(), ProjectId, RunId, CancellationToken.None);

        var rawReturned = result.Selection.Snapshot.GetRawText();
        Assert.Equal(rawSelection, rawReturned);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawSelection))),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawReturned))));
        Assert.Empty(resolver.Received);
        Assert.Equal(
            ["/api/authorization/context", $"/api/projects/{ProjectId}/runs/{RunId}/selection",
                "/api/authorization/context"],
            handler.RequestPaths);
    }

    [Fact]
    public async Task ValidReferenceResolvesTheExactImmutableIdentityAndPreservesRawSelection()
    {
        var snapshot = CreateSnapshot();
        var rawSelection = SelectionWithReferences(ReferenceJson(snapshot.Reference));
        var events = new List<string>();
        var resolver = new RecordingResolver(
            (_, _) => Task.FromResult<ReviewedRemoteToolSnapshot?>(snapshot),
            events);
        var handler = SelectionHandler(rawSelection, allowed: true, allowedAfterSelection: true, events);
        using var httpClient = new HttpClient(handler);
        var client = new ProjectsRunSelectionClient(httpClient, Options(), resolver);

        var result = await client.ReadAcceptedSelectionWithAuthorityAsync(
            RequestContext(), ProjectId, RunId, CancellationToken.None);

        var resolvedReference = Assert.Single(resolver.Received);
        Assert.Equal(snapshot.Reference.ProjectId, resolvedReference.ProjectId);
        Assert.Equal(snapshot.Reference.SnapshotId, resolvedReference.SnapshotId);
        Assert.Equal(snapshot.Reference.SnapshotDigest, resolvedReference.SnapshotDigest);
        Assert.Equal(snapshot.Reference.AgentId, resolvedReference.AgentId);
        Assert.Equal(snapshot.Reference.NodeId, resolvedReference.NodeId);
        Assert.Equal(rawSelection, result.Selection.Snapshot.GetRawText());
        Assert.Equal("resolve", events[2]);
        Assert.Equal("/api/authorization/context", events[3]);
    }

    [Fact]
    public async Task MalformedReferenceFailsAsInvalidUpstreamBeforeResolving()
    {
        const string malformed = """{"projectId":"project-1","snapshotId":"11111111-1111-1111-1111-111111111111","snapshotDigest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","nodeId":"node-1"}""";
        var rawSelection = SelectionWithReferences(malformed);
        var resolver = new RecordingResolver((_, _) =>
            Task.FromResult<ReviewedRemoteToolSnapshot?>(CreateSnapshot()));
        var handler = SelectionHandler(rawSelection, allowed: true, allowedAfterSelection: true, []);
        using var httpClient = new HttpClient(handler);
        var client = new ProjectsRunSelectionClient(httpClient, Options(), resolver);

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            client.ReadAcceptedSelectionWithAuthorityAsync(
                RequestContext(), ProjectId, RunId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status502BadGateway, error.StatusCode);
        Assert.Equal("projects_run_selection_contract_invalid", error.Code);
        Assert.Empty(resolver.Received);
        Assert.Equal(2, handler.RequestPaths.Count);
    }

    [Fact]
    public async Task ForeignAndDuplicateReferencesFailAsInvalidUpstream()
    {
        var snapshot = CreateSnapshot();
        var foreignReference = ReferenceJson(new ReviewedRemoteToolSnapshotReference(
            "project-2",
            snapshot.SnapshotId,
            snapshot.SnapshotDigest,
            snapshot.AgentId,
            snapshot.NodeId));
        var foreignResolver = new RecordingResolver((_, _) =>
            Task.FromResult<ReviewedRemoteToolSnapshot?>(snapshot));
        var foreignHandler = SelectionHandler(
            SelectionWithReferences(foreignReference), allowed: true, allowedAfterSelection: true, []);
        using var foreignHttpClient = new HttpClient(foreignHandler);
        var foreignClient = new ProjectsRunSelectionClient(foreignHttpClient, Options(), foreignResolver);

        var foreignError = await Assert.ThrowsAsync<CoordinationException>(() =>
            foreignClient.ReadAcceptedSelectionWithAuthorityAsync(
                RequestContext(), ProjectId, RunId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status502BadGateway, foreignError.StatusCode);
        Assert.Empty(foreignResolver.Received);

        var reference = ReferenceJson(snapshot.Reference);
        var duplicateResolver = new RecordingResolver((_, _) =>
            Task.FromResult<ReviewedRemoteToolSnapshot?>(snapshot));
        var duplicateHandler = SelectionHandler(
            SelectionWithReferences($"{reference},{reference}"),
            allowed: true,
            allowedAfterSelection: true,
            []);
        using var duplicateHttpClient = new HttpClient(duplicateHandler);
        var duplicateClient = new ProjectsRunSelectionClient(duplicateHttpClient, Options(), duplicateResolver);

        var duplicateError = await Assert.ThrowsAsync<CoordinationException>(() =>
            duplicateClient.ReadAcceptedSelectionWithAuthorityAsync(
                RequestContext(), ProjectId, RunId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status502BadGateway, duplicateError.StatusCode);
        Assert.Single(duplicateResolver.Received);
        Assert.Equal(2, duplicateHandler.RequestPaths.Count);
    }

    [Fact]
    public async Task MissingReferenceReturnsConflict()
    {
        var snapshot = CreateSnapshot();
        var resolver = new RecordingResolver((_, _) =>
            Task.FromResult<ReviewedRemoteToolSnapshot?>(null));
        var handler = SelectionHandler(
            SelectionWithReferences(ReferenceJson(snapshot.Reference)),
            allowed: true,
            allowedAfterSelection: true,
            []);
        using var httpClient = new HttpClient(handler);
        var client = new ProjectsRunSelectionClient(httpClient, Options(), resolver);

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            client.ReadAcceptedSelectionWithAuthorityAsync(
                RequestContext(), ProjectId, RunId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, error.StatusCode);
        Assert.Equal("reviewed_remote_tool_snapshot_unavailable", error.Code);
        Assert.Single(resolver.Received);
        Assert.Equal(2, handler.RequestPaths.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolverIntegrityFailureReturnsBadGateway(bool jsonFailure)
    {
        var snapshot = CreateSnapshot();
        var resolver = new RecordingResolver((_, _) => jsonFailure
            ? Task.FromException<ReviewedRemoteToolSnapshot?>(new JsonException("Invalid stored snapshot."))
            : Task.FromException<ReviewedRemoteToolSnapshot?>(new InvalidOperationException("Invalid stored snapshot.")));
        var handler = SelectionHandler(
            SelectionWithReferences(ReferenceJson(snapshot.Reference)),
            allowed: true,
            allowedAfterSelection: true,
            []);
        using var httpClient = new HttpClient(handler);
        var client = new ProjectsRunSelectionClient(httpClient, Options(), resolver);

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            client.ReadAcceptedSelectionWithAuthorityAsync(
                RequestContext(), ProjectId, RunId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status502BadGateway, error.StatusCode);
        Assert.Equal("reviewed_remote_tool_snapshot_invalid", error.Code);
        Assert.Single(resolver.Received);
        Assert.Equal(2, handler.RequestPaths.Count);
    }

    [Fact]
    public async Task AuthorityRevocationAfterResolutionStopsAcceptedSelection()
    {
        var snapshot = CreateSnapshot();
        var rawSelection = SelectionWithReferences(ReferenceJson(snapshot.Reference));
        var events = new List<string>();
        var resolver = new RecordingResolver(
            (_, _) => Task.FromResult<ReviewedRemoteToolSnapshot?>(snapshot),
            events);
        var handler = SelectionHandler(rawSelection, allowed: true, allowedAfterSelection: false, events);
        using var httpClient = new HttpClient(handler);
        var client = new ProjectsRunSelectionClient(httpClient, Options(), resolver);

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            client.ReadAcceptedSelectionWithAuthorityAsync(
                RequestContext(), ProjectId, RunId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, error.StatusCode);
        Assert.Equal("run_selection_permission_denied", error.Code);
        Assert.Single(resolver.Received);
        Assert.Equal(
            ["/api/authorization/context", $"/api/projects/{ProjectId}/runs/{RunId}/selection",
                "resolve", "/api/authorization/context"],
            events);
    }

    private static OrchestratorOptions Options() =>
        new(Issuer, "orchestrator-api", "https://projects.test/", "projects-api", string.Empty, string.Empty);

    private static DefaultHttpContext RequestContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer test-token";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", ActorId),
            new Claim("aud", "projects-api"),
            new Claim("project_id", ProjectId),
            new Claim("run_id", RunId),
            new Claim("scope", "api.read projects.orchestrator")
        ], "test"));
        return context;
    }

    private static ResponseSequenceHandler SelectionHandler(
        string selection,
        bool allowed,
        bool allowedAfterSelection,
        List<string> events) =>
        new(events,
        [
            AuthorizationResponse(allowed),
            JsonResponse(selection),
            AuthorizationResponse(allowedAfterSelection)
        ]);

    private static HttpResponseMessage AuthorizationResponse(bool allowed)
    {
        var permissions = allowed
            ? ImmutableArray.Create(new ProjectsPermissionGrant("acceptRunSelection", 1))
            : ImmutableArray<ProjectsPermissionGrant>.Empty;
        var authorization = new ProjectsAuthorizationContext(
            1,
            Issuer,
            ActorId,
            "tenant-1",
            3,
            ProjectId,
            RunId,
            [new ProjectsAuthority("project", ProjectId, permissions)]);
        var response = JsonResponse(JsonSerializer.Serialize(authorization));
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return response;
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string SelectionWithReferences(string references) =>
        """{"projectId":"project-1","runId":"run-1","projectRevision":1,"projectConfigurationRevision":2,"platformRuntimeRevision":3,"contextRevision":"context-1","projectConfiguration":{"reviewedRemoteToolSnapshots":["""
        + references + "]}}";

    private static string ReferenceJson(ReviewedRemoteToolSnapshotReference reference) =>
        JsonSerializer.Serialize(new
        {
            reference.ProjectId,
            reference.SnapshotId,
            reference.SnapshotDigest,
            reference.AgentId,
            reference.NodeId
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static ReviewedRemoteToolSnapshot CreateSnapshot() =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ProjectId,
            "agent-1",
            "node-1",
            "connection-1",
            1,
            2,
            3,
            "Enabled",
            new string('a', 64),
            "https://mcp.test/api",
            null,
            "DelegatedOAuth",
            null,
            "StreamableHttp20250618",
            null,
            "catalog-r1",
            new string('b', 64),
            "remote.lookup",
            "tool-r1",
            new string('c', 64),
            "schema-r1",
            new string('d', 64),
            """{"name":"remote.lookup","description":"Lookup"}""",
            """{"type":"object","properties":{}}""",
            new ReviewedRemoteToolPermissionMetadata(
                "tool.read", "runtime.execution", """{"source":"explicit-review"}"""));

    private sealed class RecordingResolver : IReviewedRemoteToolSnapshotResolver
    {
        private readonly Func<ReviewedRemoteToolSnapshotReference, CancellationToken,
            Task<ReviewedRemoteToolSnapshot?>> _resolve;
        private readonly List<string>? _events;

        public RecordingResolver(
            Func<ReviewedRemoteToolSnapshotReference, CancellationToken, Task<ReviewedRemoteToolSnapshot?>> resolve,
            List<string>? events = null)
        {
            _resolve = resolve;
            _events = events;
        }

        public List<ReviewedRemoteToolSnapshotReference> Received { get; } = [];

        public Task<ReviewedRemoteToolSnapshot?> ResolveAsync(
            ReviewedRemoteToolSnapshotReference reference,
            CancellationToken cancellationToken)
        {
            Received.Add(reference);
            _events?.Add("resolve");
            return _resolve(reference, cancellationToken);
        }
    }

    private sealed class ResponseSequenceHandler(
        List<string> events,
        IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            RequestPaths.Add(path);
            events.Add(path);
            if (!_responses.TryDequeue(out var response))
                throw new InvalidOperationException("No response was queued for this HTTP request.");
            return Task.FromResult(response);
        }
    }
}
