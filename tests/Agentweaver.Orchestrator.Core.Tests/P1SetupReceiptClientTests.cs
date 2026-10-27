using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class P1SetupReceiptClientTests
{
    private const string Issuer = "https://issuer.test/";
    private const string ActorId = "11111111-1111-1111-1111-111111111111";
    private const string TenantId = "tenant-1";
    private const string ProjectId = "project-1";
    private const string RunId = "run-1";
    private const int MaxReceiptBytes = 16 * 1024;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void NonP1RequestKeepsItsOriginalSerializationForIdempotentRetries()
    {
        var request = new CreateProjectRunRequest("Hello", 1);
        Assert.Equal(
            """{"firstMessage":"Hello","expectedProjectConfigRevision":1,"title":null,"workflowId":null}""",
            JsonSerializer.Serialize(request, WebJson));
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("wrong-run")]
    [InlineData("wrong-audience")]
    [InlineData("missing-scope")]
    [InlineData("duplicate-run")]
    public void CurrentBindingReadRequiresExactBoundOwnerAuthority(string invalid)
    {
        var context = Context(bound: invalid != "unbound",
            runId: invalid == "wrong-run" ? "another-run" : null);
        var identity = Assert.IsType<ClaimsIdentity>(context.User.Identity);
        if (invalid == "wrong-audience")
            identity.RemoveClaim(identity.FindFirst(claim =>
                claim.Type == "aud" && claim.Value == "orchestrator-api")!);
        if (invalid == "missing-scope")
            identity.RemoveClaim(identity.FindFirst("scope")!);
        if (invalid == "duplicate-run")
            identity.AddClaim(new Claim("run_id", RunId));
        var options = new OrchestratorOptions(
            Issuer, "orchestrator-api", "https://projects.test/", "projects-api", string.Empty, string.Empty);
        var error = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.RequireOwnerActor(context, options, ProjectId, RunId, options.Audience));
        Assert.Equal(StatusCodes.Status403Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task UnboundReceiptReadUsesCallerBearerAndRequiresExactNoStoreReceipt()
    {
        var receipt = Receipt();
        var handler = new CapturingHandler(_ => JsonResponse(JsonSerializer.Serialize(receipt, WebJson), noStore: true));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.ReadP1SetupReceiptForCreationAsync(
            Context(bound: false), ProjectId, receipt.InvocationId, Authorization(bound: false), 2,
            CancellationToken.None);

        Assert.Equal(receipt, result);
        Assert.Equal($"/api/p1/invocations/{receipt.InvocationId:D}/setup", handler.Path);
        Assert.Equal("Bearer opaque-caller-token", handler.Authorization);
        Assert.Equal(TenantId, handler.Tenant);
    }

    [Fact]
    public async Task BoundReceiptReadRequiresExactRunBindingAndForwardsTheRunBoundBearer()
    {
        var receipt = Receipt();
        var handler = new CapturingHandler(_ => JsonResponse(JsonSerializer.Serialize(receipt, WebJson), noStore: true));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.ReadP1SetupReceiptForAcceptedRunAsync(
            Context(bound: true), Selection(), receipt.InvocationId, CancellationToken.None);

        Assert.Equal(receipt, result);
        Assert.Equal(
            $"/internal/projects/{ProjectId}/runs/{RunId}/p1-setup/{receipt.InvocationId:D}", handler.Path);
        Assert.Equal("Bearer opaque-caller-token", handler.Authorization);
        Assert.Equal(TenantId, handler.Tenant);

        var denied = await Assert.ThrowsAsync<CoordinationException>(() =>
            client.ReadP1SetupReceiptForAcceptedRunAsync(
                Context(bound: true, runId: "another-run"), Selection(), receipt.InvocationId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ReceiptReaderRejectsUnknownDuplicateOversizedAndCacheableBodies()
    {
        var receipt = Receipt();
        var validJson = JsonSerializer.Serialize(receipt, WebJson);
        var invalidBodies = new[]
        {
            validJson[..^1] + ",\"unexpected\":true}",
            validJson[..^1] + ",\"issuer\":\"duplicate\"}",
            new string('x', MaxReceiptBytes + 1)
        };
        foreach (var body in invalidBodies)
        {
            var handler = new CapturingHandler(_ => JsonResponse(body, noStore: true));
            using var httpClient = new HttpClient(handler);
            var client = CreateClient(httpClient);
            var error = await Assert.ThrowsAsync<CoordinationException>(() =>
                client.ReadP1SetupReceiptForCreationAsync(
                    Context(bound: false), ProjectId, receipt.InvocationId, Authorization(bound: false), 2,
                    CancellationToken.None));
            Assert.Equal("p1_setup_receipt_contract_invalid", error.Code);
        }

        var cacheableHandler = new CapturingHandler(_ => JsonResponse(validJson, noStore: false));
        using var cacheableHttpClient = new HttpClient(cacheableHandler);
        var cacheableClient = CreateClient(cacheableHttpClient);
        var cacheError = await Assert.ThrowsAsync<CoordinationException>(() =>
            cacheableClient.ReadP1SetupReceiptForCreationAsync(
                Context(bound: false), ProjectId, receipt.InvocationId, Authorization(bound: false), 2,
                CancellationToken.None));
        Assert.Equal("p1_setup_receipt_unavailable", cacheError.Code);
    }

    [Fact]
    public async Task ReceiptReadRejectsStaleScopeAndMissingUpstreamReceipt()
    {
        var receipt = Receipt();
        var staleHandler = new CapturingHandler(_ => JsonResponse(
            JsonSerializer.Serialize(receipt with { ProjectConfigurationRevision = 3 }, WebJson), noStore: true));
        using var staleHttpClient = new HttpClient(staleHandler);
        var staleClient = CreateClient(staleHttpClient);
        var stale = await Assert.ThrowsAsync<CoordinationException>(() =>
            staleClient.ReadP1SetupReceiptForCreationAsync(
                Context(bound: false), ProjectId, receipt.InvocationId, Authorization(bound: false), 2,
                CancellationToken.None));
        Assert.Equal("p1_setup_receipt_stale", stale.Code);

        var missingHandler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var missingHttpClient = new HttpClient(missingHandler);
        var missingClient = CreateClient(missingHttpClient);
        var missing = await Assert.ThrowsAsync<CoordinationException>(() =>
            missingClient.ReadP1SetupReceiptForCreationAsync(
                Context(bound: false), ProjectId, receipt.InvocationId, Authorization(bound: false), 2,
                CancellationToken.None));
        Assert.Equal("p1_setup_receipt_stale", missing.Code);
    }

    [Fact]
    public void ReceiptHashIsDeterministicAndCoversNullableOwnerSnapshotFields()
    {
        var receipt = Receipt() with
        {
            OwnerAssignmentId = null,
            OwnerAssignmentRevision = null,
            OwnerAuthorityAuditEventId = null
        };

        var firstHash = CoordinationOwnerStore.HashP1SetupReceipt(receipt);

        Assert.Equal(firstHash, CoordinationOwnerStore.HashP1SetupReceipt(receipt));
        Assert.NotEqual(firstHash, CoordinationOwnerStore.HashP1SetupReceipt(
            receipt with { OwnerAuthorityAuditEventId = Guid.NewGuid() }));
        Assert.NotEqual(firstHash, CoordinationOwnerStore.HashP1SetupReceipt(
            receipt with { OwnerAssignmentId = Guid.NewGuid(), OwnerAssignmentRevision = 1 }));
    }

    private static ProjectsRunSelectionClient CreateClient(HttpClient httpClient) =>
        new(httpClient, new OrchestratorOptions(
            Issuer, "orchestrator-api", "https://projects.test/", "projects-api", string.Empty, string.Empty),
            new EmptySnapshotResolver());

    private static HttpResponseMessage JsonResponse(string body, bool noStore)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (noStore)
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return response;
    }

    private static P1ProjectSetupReceiptSnapshot Receipt() =>
        new(Guid.NewGuid(), Issuer, ActorId, TenantId, Guid.NewGuid(), 3, ProjectId, ActorId, 1, 2,
            Guid.NewGuid(), 1, Guid.NewGuid(), DateTimeOffset.UtcNow);

    private static ProjectsAuthorizationContext Authorization(bool bound) =>
        new(1, Issuer, ActorId, TenantId, 3,
            bound ? ProjectId : null, bound ? RunId : null,
            ImmutableArray.Create(new ProjectsAuthority("project", ProjectId,
                ImmutableArray.Create(new ProjectsPermissionGrant("acceptRunSelection", 1)))));

    private static AuthorizedRunSelection Selection()
    {
        using var snapshot = JsonDocument.Parse("""{"source":"projects-config"}""");
        var selection = new EffectiveRunSelection(ProjectId, RunId, 1, 2, 1, "context-v1",
            snapshot.RootElement.Clone());
        return new AuthorizedRunSelection(selection, Authorization(bound: true));
    }

    private static DefaultHttpContext Context(bool bound, string? runId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer opaque-caller-token";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("iss", Issuer),
            new Claim("sub", ActorId),
            new Claim("aud", "orchestrator-api"),
            new Claim("aud", "projects-api"),
            new Claim("scope", bound ? "api.read projects.orchestrator" : "api.read projects.orchestrator projects.admin"),
            .. (bound
                ? new[] { new Claim("project_id", ProjectId), new Claim("run_id", runId ?? RunId) }
                : Array.Empty<Claim>())
        ], "test"));
        return context;
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? Path { get; private set; }
        public string? Authorization { get; private set; }
        public string? Tenant { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Path = request.RequestUri!.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            Tenant = request.Headers.TryGetValues("X-Agentweaver-Tenant", out var values)
                ? values.Single()
                : null;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class EmptySnapshotResolver : IReviewedRemoteToolSnapshotResolver
    {
        public Task<ReviewedRemoteToolSnapshot?> ResolveAsync(
            ReviewedRemoteToolSnapshotReference reference,
            CancellationToken cancellationToken) => Task.FromResult<ReviewedRemoteToolSnapshot?>(null);
    }
}
