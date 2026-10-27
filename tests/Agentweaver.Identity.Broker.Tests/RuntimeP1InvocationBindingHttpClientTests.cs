using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeP1InvocationBindingHttpClientTests
{
    [Fact]
    public async Task ReadsExactCoreRouteWithOriginalActorAndRechecksRegistration()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var binding = Binding(registration);
        var paths = new List<string>();
        using var http = Client(registration, (request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("actor-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("tenant", Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
            Assert.True(request.Headers.CacheControl?.NoStore);
            Assert.True(request.Headers.CacheControl?.NoCache);
            Assert.Null(request.Content);
            return Task.FromResult(Response(request, binding));
        }, paths);

        Assert.Equal(binding, await Reader(http).ReadRootAsync(registration, default));
        Assert.Equal(
            [$"/internal/runtime/registrations/{registration.RuntimeInstanceId:D}",
                "/internal/projects/project/runs/run/p1-invocation-binding",
                $"/internal/runtime/registrations/{registration.RuntimeInstanceId:D}"],
            paths);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("core-id")]
    [InlineData("setup-id")]
    [InlineData("receipt-hash")]
    [InlineData("tenant")]
    [InlineData("project")]
    [InlineData("run")]
    [InlineData("root")]
    [InlineData("issuer")]
    [InlineData("actor")]
    [InlineData("membership")]
    [InlineData("membership-revision")]
    [InlineData("selection")]
    [InlineData("fence")]
    [InlineData("state-version")]
    [InlineData("accepted-at")]
    [InlineData("future-accepted-at")]
    public async Task RejectsForeignStaleOrInvalidBinding(string change)
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var binding = Binding(registration);
        binding = change switch
        {
            "version" => binding with { ContractVersion = 2 },
            "core-id" => binding with { CoreInvocationId = Guid.Empty },
            "setup-id" => binding with { SetupInvocationId = Guid.Empty },
            "receipt-hash" => binding with { SetupReceiptHash = "invalid" },
            "tenant" => binding with { TenantId = "foreign" },
            "project" => binding with { ProjectId = "foreign" },
            "run" => binding with { RunId = "foreign" },
            "root" => binding with { RootSessionId = "parent-not-child" },
            "issuer" => binding with { ActorIssuer = "https://foreign.test/" },
            "actor" => binding with { ActorSubject = Guid.NewGuid().ToString("D") },
            "membership" => binding with { MembershipId = Guid.Empty },
            "membership-revision" => binding with { MembershipRevision = 0 },
            "selection" => binding with { AcceptedSelectionHash = new string('b', 64) },
            "fence" => binding with { ExecutionFence = binding.ExecutionFence + 1 },
            "state-version" => binding with { RootStateVersion = 0 },
            "accepted-at" => binding with { AcceptedAt = default },
            "future-accepted-at" => binding with { AcceptedAt = DateTimeOffset.UtcNow.AddDays(1) },
            _ => throw new InvalidOperationException()
        };
        using var http = Client(registration, (request, _) => Task.FromResult(Response(request, binding)));
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(
            () => Reader(http).ReadRootAsync(registration, default));
        Assert.Equal(change == "root" ? "runtime_p1_root_session_mismatch" :
            change == "receipt-hash" ? "runtime_hash_invalid" :
            change is "tenant" or "project" or "run" or "issuer" or "actor" or "selection" or "fence"
                ? "runtime_p1_binding_mismatch" : "runtime_p1_binding_invalid", failure.Code);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("case")]
    [InlineData("null")]
    [InlineData("truncated")]
    public async Task RejectsMalformedJson(string change)
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var json = JsonSerializer.Serialize(Binding(registration), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json = change switch
        {
            "missing" => json.Replace("\"contractVersion\":1,", ""),
            "unknown" => json.Insert(1, "\"extra\":true,"),
            "duplicate" => json.Insert(1, "\"contractVersion\":2,"),
            "case" => json.Replace("contractVersion", "ContractVersion"),
            "null" => "null",
            "truncated" => "{",
            _ => throw new InvalidOperationException()
        };
        using var http = Client(registration, (request, _) =>
        {
            var response = Response(request, Binding(registration));
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        });
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(
            () => Reader(http).ReadRootAsync(registration, default));
    }

    [Theory]
    [InlineData(404, "runtime_owner_unavailable")]
    [InlineData(401, "runtime_owner_denied")]
    [InlineData(403, "runtime_owner_denied")]
    [InlineData(409, "runtime_owner_binding_changed")]
    [InlineData(503, "runtime_owner_unavailable")]
    [InlineData(302, "runtime_owner_redirect_rejected")]
    public async Task RejectsOwnerFailuresWithoutReadingOrForwardingErrorContent(int status, string code)
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var calls = 0;
        using var http = Client(registration, (request, _) =>
        {
            calls++;
            var response = Response(request, new { error = "owner-error" });
            response.StatusCode = (HttpStatusCode)status;
            response.Headers.Location = new Uri("https://foreign.test/");
            return Task.FromResult(response);
        });
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(
            () => Reader(http).ReadRootAsync(registration, default));
        Assert.Equal(code, failure.Code);
        Assert.DoesNotContain("owner-error", failure.Message);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("cache", "runtime_owner_cache_contract_invalid")]
    [InlineData("foreign-response", "runtime_owner_redirect_rejected")]
    [InlineData("content-type", "runtime_owner_response_size_invalid")]
    [InlineData("length", "runtime_owner_response_size_invalid")]
    [InlineData("stream", "runtime_owner_response_size_invalid")]
    public async Task RejectsInvalidTransportAndOversizedStream(string change, string code)
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        using var http = Client(registration, (request, _) =>
        {
            var response = Response(request, Binding(registration));
            switch (change)
            {
                case "cache": response.Headers.CacheControl = null; break;
                case "foreign-response":
                    response.RequestMessage = new(HttpMethod.Get, "https://foreign.test/"); break;
                case "content-type": response.Content.Headers.ContentType = new("text/plain"); break;
                case "length":
                    response.Content.Headers.ContentLength = RuntimeP1InvocationBindingHttpClient.MaximumResponseBytes + 1;
                    break;
                case "stream":
                    response.Content = new StreamContent(new NonSeekableStream(
                        new byte[RuntimeP1InvocationBindingHttpClient.MaximumResponseBytes + 1]));
                    response.Content.Headers.ContentType = new("application/json");
                    Assert.Null(response.Content.Headers.ContentLength);
                    break;
            }
            return Task.FromResult(response);
        });
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(
            () => Reader(http).ReadRootAsync(registration, default));
        Assert.Equal(code, failure.Code);
    }

    [Fact]
    public async Task AcceptsExactlyTheResponseByteLimit()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var binding = Binding(registration);
        var json = JsonSerializer.Serialize(binding, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var http = Client(registration, (request, _) =>
        {
            var response = Response(request, binding);
            response.Content = new StringContent(
                json.PadRight(RuntimeP1InvocationBindingHttpClient.MaximumResponseBytes), Encoding.UTF8, "application/json");
            Assert.Equal(RuntimeP1InvocationBindingHttpClient.MaximumResponseBytes, response.Content.Headers.ContentLength);
            return Task.FromResult(response);
        });
        Assert.Equal(binding, await Reader(http).ReadRootAsync(registration, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsUnavailableInputBeforeSending(bool expired)
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        registration = expired ? registration with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }
            : registration with { State = RuntimeRegistrationState.Revoked };
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Must not send")));
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(
            () => Reader(http).ReadRootAsync(registration, default));
        Assert.Equal("runtime_registration_unavailable", failure.Code);
    }

    [Theory]
    [InlineData(false, "revision")]
    [InlineData(true, "revision")]
    [InlineData(true, "actor")]
    [InlineData(true, "fence")]
    [InlineData(true, "expired")]
    [InlineData(true, "revoked")]
    public async Task RejectsChangedRegistrationBeforeOrAfterCoreWait(bool afterRead, string change)
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var reads = 0;
        var coreReads = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/registrations/", StringComparison.Ordinal))
            {
                reads++;
                var current = reads == (afterRead ? 2 : 1) ? change switch
                {
                    "revision" => registration with { Revision = 2 },
                    "actor" => registration with { Binding = registration.Binding with { ActorId = Guid.NewGuid().ToString("D") } },
                    "fence" => registration with { Binding = registration.Binding with { ExecutionFence = 2 } },
                    "expired" => registration with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
                    "revoked" => registration with { State = RuntimeRegistrationState.Revoked },
                    _ => throw new InvalidOperationException()
                } : registration;
                return Task.FromResult(Response(request, current));
            }
            coreReads++;
            return Task.FromResult(Response(request, Binding(registration)));
        }));
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(
            () => Reader(http).ReadRootAsync(registration, default));
        Assert.Equal(change is "expired" or "revoked" ? "runtime_registration_unavailable" :
            "runtime_registration_stale", failure.Code);
        Assert.Equal(afterRead ? 1 : 0, coreReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundsDelayedHeadersAndBodyByClientTimeout(bool body)
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        using var http = Client(registration, async (request, token) =>
        {
            if (!body)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            var response = Response(request, Binding(registration));
            response.Content = new StreamContent(new DelayedStream());
            response.Content.Headers.ContentType = new("application/json");
            return response;
        });
        http.Timeout = TimeSpan.FromMilliseconds(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Reader(http).ReadRootAsync(registration, default));
    }

    [Fact]
    public async Task PropagatesCallerCancellationDuringCoreRead()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        using var cancellation = new CancellationTokenSource();
        using var http = Client(registration, async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Reader(http).ReadRootAsync(registration, cancellation.Token));
    }

    [Fact]
    public async Task RejectsForeignTenantSelector()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        using var http = Client(registration, (request, _) => Task.FromResult(Response(request, Binding(registration))));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(
            () => Reader(http, "foreign").ReadRootAsync(registration, default));
    }

    private static RuntimeP1InvocationBindingHttpClient Reader(HttpClient http, string tenant = "tenant") =>
        new(http, new("https://orchestrator.test/"),
            new(new SecretCredential("actor-token", DateTimeOffset.UtcNow.AddMinutes(5)), tenant),
            TimeProvider.System);

    private static P1InvocationBinding Binding(RuntimeRegistration registration)
    {
        var b = registration.Binding;
        return new(1, Guid.NewGuid(), Guid.NewGuid(), new string('c', 64), b.TenantId,
            b.ProjectId, b.RunId, b.SessionId, b.ActorIssuer, b.ActorId, Guid.NewGuid(), 1,
            b.AcceptedSelectionHash, b.ExecutionFence, 1, DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    private static HttpClient Client(
        RuntimeRegistration registration,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> readBinding,
        List<string>? paths = null) =>
        new(new Handler((request, token) =>
        {
            Assert.Equal("orchestrator.test", request.RequestUri!.Host);
            if (request.RequestUri.AbsolutePath.Contains("/registrations/", StringComparison.Ordinal))
            {
                paths?.Add(request.RequestUri.AbsolutePath);
                return Task.FromResult(Response(request, registration));
            }
            Assert.Equal("/internal/projects/project/runs/run/p1-invocation-binding", request.RequestUri.AbsolutePath);
            return readBinding(request, token);
        }));

    private static HttpResponseMessage Response(HttpRequestMessage request, object value) =>
        new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = JsonContent.Create(value),
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
        };

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            send(request, token);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class DelayedStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
