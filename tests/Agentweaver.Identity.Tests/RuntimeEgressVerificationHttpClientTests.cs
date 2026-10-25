using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class RuntimeEgressVerificationHttpClientTests
{
    private static readonly Uri Owner = new("https://environment.example/");
    private static readonly RuntimeEgressVerificationRequest Request = new(
        1,
        new EnvironmentGenerationFence(
            new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a"), 7),
        3,
        "selection-revision-4");

    [Fact]
    public async Task VerificationForwardsCallerAuthorizationAndAcceptsExactFenceReceipt()
    {
        var receipt = Receipt();
        using var client = CreateClient(HttpStatusCode.OK, RuntimeEgressVerificationResponse.Success(receipt),
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(new Uri(Owner, "/api/network-egress/verify"), request.RequestUri);
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal("caller-token", request.Headers.Authorization.Parameter);
                Assert.Equal("tenant-a", request.Headers.GetValues("X-Agentweaver-Tenant").Single());
                Assert.True(request.Headers.CacheControl!.NoStore);
                var payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var document = JsonDocument.Parse(payload);
                Assert.Equal(Request.ContextRevision, document.RootElement.GetProperty("contextRevision").GetString());
                Assert.Equal(Request.PolicyGeneration, document.RootElement.GetProperty("policyGeneration").GetInt64());
                Assert.Equal(Request.Fence.LifecycleGeneration,
                    document.RootElement.GetProperty("fence").GetProperty("lifecycleGeneration").GetInt64());
            });

        var result = await Client(client).VerifyAsync(Request, Actor(), CancellationToken.None);

        Assert.Equal(receipt, result);
    }

    [Theory]
    [InlineData(404, RuntimeEgressVerificationFailureCodes.Missing)]
    [InlineData(409, RuntimeEgressVerificationFailureCodes.StaleFence)]
    [InlineData(409, RuntimeEgressVerificationFailureCodes.GenerationUnverified)]
    [InlineData(403, RuntimeEgressVerificationFailureCodes.AuthorizationChanged)]
    [InlineData(409, RuntimeEgressVerificationFailureCodes.SelectionChanged)]
    [InlineData(503, RuntimeEgressVerificationFailureCodes.Unavailable)]
    public async Task VerificationFailsClosedWithDistinctEnvironmentOutcomes(int status, string code)
    {
        using var client = CreateClient((HttpStatusCode)status,
            RuntimeEgressVerificationResponse.Failure(code, "not verified"));

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Client(client).VerifyAsync(Request, Actor(), CancellationToken.None));

        Assert.Equal(code, failure.Code);
    }

    [Fact]
    public async Task ClientRejectsReceiptForChangedSelectionRevision()
    {
        using var client = CreateClient(HttpStatusCode.OK, RuntimeEgressVerificationResponse.Success(
            Receipt() with { ContextRevision = "new-selection-revision" }));

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Client(client).VerifyAsync(Request, Actor(), CancellationToken.None));

        Assert.Equal("egress_verification_receipt_invalid", failure.Code);
    }

    [Fact]
    public async Task ClientRejectsReceiptForDifferentFullOwnerFence()
    {
        using var client = CreateClient(HttpStatusCode.OK, RuntimeEgressVerificationResponse.Success(
            Receipt() with
            {
                Fence = new EnvironmentGenerationFence(
                    new EnvironmentOwnerIdentity(
                        "another-tenant",
                        Request.Fence.Owner.ProjectId,
                        Request.Fence.Owner.RunId,
                        Request.Fence.Owner.EnvironmentId),
                    Request.Fence.LifecycleGeneration)
            }));

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Client(client).VerifyAsync(Request, Actor(), CancellationToken.None));

        Assert.Equal("egress_verification_receipt_invalid", failure.Code);
    }

    [Fact]
    public async Task TransportFailureBecomesEnvironmentUnavailableOutcome()
    {
        using var client = new HttpClient(new FailingHandler());

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Client(client).VerifyAsync(Request, Actor(), CancellationToken.None));

        Assert.Equal(RuntimeEgressVerificationFailureCodes.Unavailable, failure.Code);
    }

    [Fact]
    public async Task TruncatedEnvironmentResponseBecomesUnavailableOutcome()
    {
        using var client = new HttpClient(new TruncatedResponseHandler());

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Client(client).VerifyAsync(Request, Actor(), CancellationToken.None));

        Assert.Equal(RuntimeEgressVerificationFailureCodes.Unavailable, failure.Code);
    }

    [Fact]
    public async Task InvalidatedCallerAuthorizationIsReportedAsChanged()
    {
        using var client = new HttpClient(new FailingHandler());
        var bearer = new SecretCredential("caller-token", DateTimeOffset.UtcNow.AddMinutes(5));
        bearer.Invalidate();
        var actor = new RuntimeActorAuthorization(bearer, "tenant-a");

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Client(client).VerifyAsync(Request, actor, CancellationToken.None));

        Assert.Equal(RuntimeEgressVerificationFailureCodes.AuthorizationChanged, failure.Code);
    }

    private static RuntimeEgressVerificationReceipt Receipt() =>
        new(1, Request.Fence, Request.PolicyGeneration, Request.ContextRevision, new string('a', 64));

    private static RuntimeEgressVerificationHttpClient Client(HttpClient client) =>
        new(client, Owner);

    private static RuntimeActorAuthorization Actor() =>
        new(new SecretCredential("caller-token", DateTimeOffset.UtcNow.AddMinutes(5)), "tenant-a");

    private static HttpClient CreateClient(
        HttpStatusCode status,
        RuntimeEgressVerificationResponse response,
        Action<HttpRequestMessage>? verifyRequest = null)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            System.Text.Encoding.UTF8,
            "application/json");
        return new HttpClient(new ResponseHandler(status, content, verifyRequest));
    }

    private sealed class ResponseHandler(
        HttpStatusCode status,
        HttpContent content,
        Action<HttpRequestMessage>? verifyRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            verifyRequest?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = content,
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
            });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Environment unavailable.");
    }

    private sealed class TruncatedResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new ThrowingReadStream());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content,
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
            });
        }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("The Environment response ended before its body was read.");
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(
                new IOException("The Environment response ended before its body was read."));
        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(
                new IOException("The Environment response ended before its body was read."));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
