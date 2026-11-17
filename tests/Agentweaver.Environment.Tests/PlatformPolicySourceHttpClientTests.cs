using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Policy.Acs;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class PlatformPolicySourceHttpClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string PolicyId = "platform-policy-1";

    [Fact]
    public async Task ReadsOnlyTheFixedPendingSourceAndCompilesAnUnadmittedCandidate()
    {
        var artifact = CreateArtifact("Allowlist", ["approved.example.com"]);
        var handler = new ResponseHandler(_ => JsonResponse(ObservationJson(artifact)));
        var client = Client(handler);

        var read = await client.ReadAsync();

        Assert.True(read.IsSuccess, read.FailureCode);
        var observation = read.Observation!;
        Assert.Equal($"projects-config:platform:default:{PolicyId}", observation.SourceId);
        Assert.Equal("42", observation.SourceRevision);
        Assert.Equal(42, observation.Revision);
        Assert.Equal(artifact.ContentSha256, observation.ContentSha256);
        Assert.Equal("pending", observation.AdmissionState);
        Assert.Null(observation.AdmissionCheckpoint);
        Assert.NotNull(handler.Request);
        Assert.Equal(HttpMethod.Get, handler.Request.Method);
        Assert.Equal(
            "/internal/policy-source/platform",
            handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal(string.Empty, handler.Request.RequestUri.Query);
        Assert.Null(handler.Request.Authorization);
        Assert.False(handler.Request.HasBody);

        var compiled = PlatformEgressAcsCompiler.CompileCandidate(
            observation.Artifact, observation.SourceId, observation.SourceRevision);
        Assert.True(compiled.IsSuccess, compiled.Failure?.Code);
        Assert.Equal(PlatformEgressMode.Allowlist, compiled.Candidate!.Mode);
        Assert.Equal(artifact.ContentSha256, compiled.Candidate.SourceHash);
        Assert.Equal(PlatformEgressAcsCompiler.CompilerVersion, compiled.Candidate.CompilerVersion);
        Assert.Contains(compiled.Candidate.Destinations,
            destination => destination.Host == "approved.example.com");
        Assert.Contains(compiled.Candidate.Destinations,
            destination => destination.Host == "api.github.com");

        var admitted = PlatformEgressAcsCompiler.Compile(
            observation.Artifact,
            observation.SourceId,
            observation.SourceRevision,
            observation.AdmissionCheckpoint?.ToString()!);
        Assert.False(admitted.IsSuccess);
        Assert.Null(admitted.Snapshot);
        Assert.Equal("source_metadata_invalid", admitted.Failure?.Code);
    }

    [Fact]
    public async Task ReportsStableUnavailableCodeForConnectionFailures()
    {
        const string exceptionText = "private proxy connection detail";
        var handler = new ResponseHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException(exceptionText)));

        var result = await Client(handler).ReadAsync();

        Assert.False(result.IsSuccess);
        Assert.Null(result.Observation);
        Assert.Equal("policy_source_unavailable", result.FailureCode);
        Assert.DoesNotContain(exceptionText, result.FailureCode ?? string.Empty);
    }

    [Fact]
    public async Task RejectsMissingNoStoreAndNonJsonResponses()
    {
        var artifact = CreateArtifact("PublicHttps", []);
        var missingNoStore = Client(new ResponseHandler(_ =>
            JsonResponse(ObservationJson(artifact), noStore: false)));
        var wrongType = Client(new ResponseHandler(_ =>
            JsonResponse(ObservationJson(artifact), contentType: "text/plain")));

        Assert.Equal("policy_source_protocol_invalid",
            (await missingNoStore.ReadAsync()).FailureCode);
        Assert.Equal("policy_source_protocol_invalid",
            (await wrongType.ReadAsync()).FailureCode);
    }

    [Theory]
    [InlineData("accepted", 1L)]
    [InlineData("pending", 1L)]
    public async Task RejectsAdmissionThatIsNotExactlyPendingWithNullCheckpoint(
        string state,
        long? checkpoint)
    {
        var artifact = CreateArtifact("PublicHttps", []);
        var response = ObservationJson(artifact, state: state, checkpoint: checkpoint);
        var client = Client(new ResponseHandler(_ => JsonResponse(response)));

        var result = await client.ReadAsync();

        Assert.False(result.IsSuccess);
        Assert.Null(result.Observation);
        Assert.Equal("policy_source_protocol_invalid", result.FailureCode);
    }

    [Theory]
    [InlineData("bad-hash")]
    [InlineData("duplicate-member")]
    [InlineData("unknown-member")]
    [InlineData("unsafe-path")]
    [InlineData("bad-byte")]
    [InlineData("wrong-scope")]
    public async Task RejectsMalformedOrUnsupportedEnvelope(string caseName)
    {
        var artifact = CreateArtifact("PublicHttps", []);
        var json = ObservationJson(artifact);
        json = caseName switch
        {
            "bad-hash" => json.Replace(
                artifact.ContentSha256,
                new string('0', 64),
                StringComparison.Ordinal),
            "duplicate-member" => json.Replace(
                "\"revision\":42",
                "\"revision\":42,\"revision\":43",
                StringComparison.Ordinal),
            "unknown-member" => json.Insert(json.Length - 1, ",\"extra\":true"),
            "unsafe-path" => ObservationJson(ArtifactWithPath(artifact, "../policy/data.json")),
            "bad-byte" => json.Replace(
                "\"manifestBytes\":[",
                "\"manifestBytes\":[256,",
                StringComparison.Ordinal),
            "wrong-scope" => json.Replace(
                "\"scopeId\":\"default\"",
                "\"scopeId\":\"project-a\"",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };
        var client = Client(new ResponseHandler(_ => JsonResponse(json)));

        var result = await client.ReadAsync();

        Assert.False(result.IsSuccess);
        Assert.Null(result.Observation);
        Assert.Equal("policy_source_protocol_invalid", result.FailureCode);
    }

    [Fact]
    public async Task RejectsOversizedChunkedResponsesWithoutContentLength()
    {
        var handler = new ResponseHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableReadStream(new byte[5_242_881])),
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            Assert.Null(response.Content.Headers.ContentLength);
            return response;
        });

        var result = await Client(handler).ReadAsync();

        Assert.False(result.IsSuccess);
        Assert.Null(result.Observation);
        Assert.Equal("policy_source_protocol_invalid", result.FailureCode);
    }

    [Fact]
    public async Task AcceptsOnlyAllowlistedBoundedSourceErrorsAndRejectsRedirects()
    {
        var missing = Client(new ResponseHandler(_ =>
            JsonResponse("{\"code\":\"native_acs_policy_not_found\"}", HttpStatusCode.NotFound)));
        var unknown = Client(new ResponseHandler(_ =>
            JsonResponse("{\"code\":\"internal_exception\"}", HttpStatusCode.InternalServerError)));
        var redirect = Client(new ResponseHandler(_ =>
            JsonResponse("{}", HttpStatusCode.TemporaryRedirect)));

        Assert.Equal("native_acs_policy_not_found", (await missing.ReadAsync()).FailureCode);
        Assert.Equal("policy_source_protocol_invalid", (await unknown.ReadAsync()).FailureCode);
        Assert.Equal("policy_source_protocol_invalid", (await redirect.ReadAsync()).FailureCode);
    }

    [Fact]
    public async Task RejectsASecondReadWhileTheFirstReadIsStillActive()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ResponseHandler(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return JsonResponse(ObservationJson(CreateArtifact("PublicHttps", [])));
        });
        var client = Client(handler);
        using var cancellation = new CancellationTokenSource();
        var first = client.ReadAsync(cancellation.Token);
        await entered.Task;

        var second = await client.ReadAsync();
        Assert.Equal("policy_source_read_in_progress", second.FailureCode);

        cancellation.Cancel();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    private static PlatformPolicySourceHttpClient Client(HttpMessageHandler handler) =>
        new(new Uri("https://projects-config.example/"), () => handler);

    private static NativeAcsArtifact CreateArtifact(string mode, IEnumerable<string> approved)
    {
        var data = JsonSerializer.Serialize(new
        {
            agentweaver = new
            {
                platform_egress = new
                {
                    profile = NativeAcsFiniteNetworkProfile.ProfileId,
                    mode,
                    approved = approved.Select(host => new { host, port = 443 }).ToArray(),
                    system = new
                    {
                        catalog = "hosted-copilot-v1",
                        destinations = new[]
                        {
                            new { host = "api.business.githubcopilot.com", port = 443 },
                            new { host = "api.enterprise.githubcopilot.com", port = 443 },
                            new { host = "api.github.com", port = 443 },
                            new { host = "api.githubcopilot.com", port = 443 },
                            new { host = "api.individual.githubcopilot.com", port = 443 },
                        },
                    },
                },
            },
        });
        return NativeAcsFiniteNetworkProfile.CreateArtifact(data);
    }

    private static NativeAcsArtifact ArtifactWithPath(NativeAcsArtifact artifact, string path)
    {
        var changed = artifact with
        {
            Files = artifact.Files.SetItem(0, artifact.Files[0] with { RelativePath = path }),
        };
        return changed with { ContentSha256 = NativeAcsArtifactHash.Compute(changed) };
    }

    private static string ObservationJson(
        NativeAcsArtifact artifact,
        string state = "pending",
        long? checkpoint = null,
        string? scopeId = null) =>
        JsonSerializer.Serialize(new
        {
            scopeType = "platform",
            scopeId = scopeId ?? "default",
            policyId = PolicyId,
            revision = 42L,
            contentSha256 = artifact.ContentSha256,
            updatedAt = DateTimeOffset.UtcNow,
            artifact,
            admission = new { state, checkpoint },
        }, JsonOptions);

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        bool noStore = true,
        string contentType = "application/json")
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, contentType),
        };
        if (noStore)
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return response;
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responseFactory;

        internal ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            : this((request, _) => Task.FromResult(responseFactory(request)))
        {
        }

        internal ResponseHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) =>
            _responseFactory = responseFactory;

        internal RequestObservation? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = new RequestObservation(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization,
                request.Content is not null);
            return _responseFactory(request, cancellationToken);
        }
    }

    private sealed record RequestObservation(
        HttpMethod Method,
        Uri? RequestUri,
        AuthenticationHeaderValue? Authorization,
        bool HasBody);

    private sealed class NonSeekableReadStream(byte[] content) : Stream
    {
        private readonly MemoryStream _stream = new(content, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _stream.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _stream.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            _stream.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _stream.Dispose();
            base.Dispose(disposing);
        }
    }
}
