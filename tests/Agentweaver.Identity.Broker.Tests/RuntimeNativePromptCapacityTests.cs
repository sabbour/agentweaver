extern alias AgentHostService;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using AgentHostService::Agentweaver.AgentHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Sdk;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeNativePromptCapacityTests
{
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, false)]
    public void NativeInferenceCorrelationRequiresInitialProofAndRejectsMismatches(
        bool containsSessionMarker,
        bool markerMatchesBoundSession,
        bool initialProofAccepted,
        bool expected)
    {
        Assert.Equal(expected, LoopbackInferenceEndpoint.IsSessionCorrelationValid(
            containsSessionMarker, markerMatchesBoundSession, initialProofAccepted));
    }

    [NativePromptCapacityFact]
    public async Task ByokNativeRuntimeCompactsRemovableHistoryWithinTheAcceptedCapacity()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var harness = await NativePromptHarness.StartAsync(timeout.Token);
        await using var session = await harness.CreateSessionAsync(16384, 8192, timeout.Token);
        Assert.Equal("byok", session.Facts.SourceMode);
        Assert.Equal(8192, session.Facts.MaxPromptTokens);
        Assert.Equal("accepted-model-reference", session.Facts.ModelSelectionReference);
        Assert.Equal("openai", session.Facts.ByokProvider?.Type);

        await AssertAuthenticatedNativeByokRouteAsync(harness, session, timeout.Token);
        var before = await RequireContextInfoAsync(session, timeout.Token);
        AssertContextIdentity(before, session);

        using var eventTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var compactionGeneration = session.LatestPromptCapacityCompaction?.Generation ?? 0;
        var compactionTask = session.WaitForPromptCapacityCompactionAsync(
            compactionGeneration, eventTimeout.Token);
        var previousConversationTokens = before.ConversationTokens;
        var observedCompaction = false;
        for (var turn = 0; turn < 64 && !compactionTask.IsCompleted; turn++)
        {
            await session.SendTurnAsync(string.Concat(Enumerable.Repeat("history continues ", 100)),
                timeout.Token);
            var current = await RequireContextInfoAsync(session, timeout.Token);
            Assert.True(current.TotalTokens <= current.PromptTokenLimit);
            observedCompaction |= current.ConversationTokens < previousConversationTokens;
            previousConversationTokens = current.ConversationTokens;
            if (observedCompaction)
                break;
        }

        var compaction = await compactionTask.WaitAsync(timeout.Token);
        Assert.NotNull(compaction.Start);
        Assert.Equal(session.Facts.ModelId, compaction.Start.Model);
        Assert.True(compaction.Start.TokenLimit is > 0);
        Assert.True(compaction.Complete.Success);
        Assert.Null(compaction.Complete.Error);
        Assert.True(compaction.Complete.PreCompactionTokens is long preCompactionTokens &&
            compaction.Complete.PostCompactionTokens is long postCompactionTokens &&
            preCompactionTokens > postCompactionTokens);

        var after = await RequireContextInfoAsync(session, timeout.Token);
        AssertContextIdentity(after, session);
        Assert.True(after.TotalTokens <= after.PromptTokenLimit);
        Assert.True(after.PromptTokenLimit <= session.Facts.MaxPromptTokens);
        Assert.Equal(after.SystemTokens + after.ConversationTokens + after.ToolDefinitionsTokens,
            after.TotalTokens);

        var followUp = await session.SendTurnAsync(
            "Continue after native history compaction.", timeout.Token);
        Assert.Contains(harness.InferenceEndpoint.Receipts, item =>
            followUp == $"native-route-response:{session.Facts.SdkSessionId}:{item.ResponseId}");
        Assert.Equal(0, harness.InferenceEndpoint.FailedRequests);
    }

    [NativePromptCapacityFact]
    public async Task ByokNativeRuntimeFailsAnIrreducibleLatestPromptInsteadOfTheAcceptingFakeEndpoint()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var harness = await NativePromptHarness.StartAsync(timeout.Token);
        await using var session = await harness.CreateSessionAsync(1024, 8192, timeout.Token);
        await AssertAuthenticatedNativeByokRouteAsync(harness, session, timeout.Token);

        var before = await RequireContextInfoAsync(session, timeout.Token);
        AssertContextIdentity(before, session);
        Assert.True(before.PromptTokenLimit > 0);
        Assert.Equal(1024, session.Facts.MaxPromptTokens);
        Assert.Equal(1024L, before.PromptTokenLimit);
        var inferenceRequestsBefore = harness.InferenceEndpoint.RequestCount;
        var prompt = "LATEST_OVERFLOW_INPUT " + string.Join(' ',
            Enumerable.Repeat("irreducible", 1200));

        var previousErrorGeneration = session.LatestNativeSessionError?.Generation ?? 0;
        var failure = await Record.ExceptionAsync(() => session.SendTurnAsync(prompt, timeout.Token));

        Assert.NotNull(failure);
        Assert.False(failure is OperationCanceledException or TimeoutException);
        var nativeError = Assert.IsType<RuntimeNativeSessionErrorObservation>(
            session.LatestNativeSessionError);
        Assert.True(nativeError.Generation > previousErrorGeneration);
        Assert.Equal("context_limit", nativeError.ErrorType);
        Assert.Equal(inferenceRequestsBefore, harness.InferenceEndpoint.RequestCount);
        Assert.Equal(0, harness.InferenceEndpoint.FailedRequests);
    }

    private static async Task AssertAuthenticatedNativeByokRouteAsync(
        NativePromptHarness harness,
        RuntimeCopilotSession session,
        CancellationToken cancellationToken)
    {
        var sessionId = session.Facts.SdkSessionId;
        var correlationMarker = $"agentweaver-native-session:{sessionId}";
        harness.InferenceEndpoint.BindSession(sessionId);

        var response = await session.SendTurnAsync(
            $"Initialize this local test session with {correlationMarker}.", cancellationToken);
        var receipt = Assert.Single(harness.InferenceEndpoint.Receipts,
            item => response == $"native-route-response:{sessionId}:{item.ResponseId}");

        Assert.Equal("byok", session.Facts.SourceMode);
        Assert.Equal("accepted-model-reference", session.Facts.ModelSelectionReference);
        Assert.Equal(harness.AcceptedSelectionHash, session.Facts.AcceptedSelectionHash);
        Assert.Equal("openai", session.Facts.ByokProvider?.Type);
        Assert.Equal(session.Facts.ModelId, session.Facts.ByokProvider?.DeploymentId);
        Assert.Equal(harness.ModelBindingConfigurationHash,
            session.Facts.ByokProvider?.ConfigurationHash);
        Assert.True(receipt.CredentialMatched);
        Assert.Equal(sessionId, receipt.CorrelatedSessionId);
        Assert.Equal(session.Facts.ModelId, receipt.ModelId);
        Assert.Equal("POST", receipt.Method);
        Assert.Equal(harness.InferenceEndpoint.BaseUrl.Scheme, receipt.Scheme);
        Assert.Equal(harness.InferenceEndpoint.BaseUrl.Host, receipt.Host);
        Assert.Equal(harness.InferenceEndpoint.BaseUrl.Port, receipt.Port);
        Assert.StartsWith(
            harness.InferenceEndpoint.BaseUrl.AbsolutePath.TrimEnd('/') + "/", receipt.Path);
        Assert.EndsWith("/chat/completions", receipt.Path);
    }

    private static async Task<GitHub.Copilot.Rpc.MetadataContextInfoResultContextInfo> RequireContextInfoAsync(
        RuntimeCopilotSession session, CancellationToken cancellationToken)
    {
        var context = await session.ReadPromptContextInfoAsync(cancellationToken);
        return context ?? throw new XunitException(
            "Native context information is unavailable; an uninitialized context is not zero.");
    }

    private static void AssertContextIdentity(
        GitHub.Copilot.Rpc.MetadataContextInfoResultContextInfo context,
        RuntimeCopilotSession session)
    {
        Assert.Equal("openai", session.Facts.ByokProvider?.Type);
        Assert.Equal(session.Facts.ModelId, context.ModelName);
        if (!string.IsNullOrEmpty(context.Provider?.Id))
            Assert.Equal(session.Facts.ByokProvider?.Type, context.Provider.Id);
    }

    private sealed class NativePromptHarness : IAsyncDisposable
    {
        private const string ExpectedArchiveSha512 =
            "c33a2d65fbc7908b6eb5c88b14c5d94f693d4228a2e136bcb6c54308c43b08ff213f157dd45c89d727f7f8515249f3ef5ab7d833a04e022a88b97f8b8e046e8b";
        private readonly string _runtimeRoot;
        private readonly string _testRoot;
        private readonly string _certificatePath;
        private readonly string _providerCredential;
        private readonly X509Certificate2 _certificate;

        private NativePromptHarness(
            string runtimeRoot,
            string testRoot,
            string certificatePath,
            string providerCredential,
            X509Certificate2 certificate,
            LoopbackInferenceEndpoint inferenceEndpoint,
            string executablePath,
            string runtimeVersion)
        {
            _runtimeRoot = runtimeRoot;
            _testRoot = testRoot;
            _certificatePath = certificatePath;
            _providerCredential = providerCredential;
            _certificate = certificate;
            InferenceEndpoint = inferenceEndpoint;
            ExecutablePath = executablePath;
            RuntimeVersion = runtimeVersion;
        }

        public LoopbackInferenceEndpoint InferenceEndpoint { get; }
        public string? AcceptedSelectionHash { get; private set; }
        public string? ModelBindingConfigurationHash { get; private set; }
        private string ExecutablePath { get; }
        private string RuntimeVersion { get; }

        public static async Task<NativePromptHarness> StartAsync(CancellationToken cancellationToken)
        {
            var runtimeRoot = Environment.GetEnvironmentVariable("AGENTWEAVER_NATIVE_RUNTIME_ROOT")
                ?? throw new InvalidOperationException("The pinned native runtime root is not configured.");
            var (executablePath, runtimeVersion) = NativeRuntimeManifest.ReadAndVerify(runtimeRoot);
            using var manifest = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(runtimeRoot, "runtime-manifest.json"), cancellationToken));
            Assert.Equal("1.0.18", manifest.RootElement.GetProperty("sdkVersion").GetString());
            Assert.Equal("1.0.79", manifest.RootElement.GetProperty("runtimeVersion").GetString());
            Assert.Equal(ExpectedArchiveSha512,
                manifest.RootElement.GetProperty("archiveSha512").GetString());

            var testRoot = Path.Combine(Path.GetTempPath(), "agentweaver-prompt-capacity-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
            var certificate = CreateLoopbackCertificate();
            LoopbackInferenceEndpoint? inferenceEndpoint = null;
            var certificatePath = Path.Combine(testRoot, "loopback-ca.pem");
            var providerCredential = $"loopback-only-test-key-{Guid.NewGuid():N}";
            try
            {
                await File.WriteAllTextAsync(certificatePath, certificate.ExportCertificatePem(), cancellationToken);
                inferenceEndpoint = await LoopbackInferenceEndpoint.StartAsync(
                    certificate, providerCredential, cancellationToken);
                return new NativePromptHarness(runtimeRoot, testRoot, certificatePath, providerCredential, certificate,
                    inferenceEndpoint, executablePath, runtimeVersion);
            }
            catch
            {
                if (inferenceEndpoint is not null)
                    await inferenceEndpoint.DisposeAsync();
                certificate.Dispose();
                Directory.Delete(testRoot, recursive: true);
                throw;
            }
        }

        public async Task<RuntimeCopilotSession> CreateSessionAsync(
            int acceptedCapacity, int serverCapacity, CancellationToken cancellationToken)
        {
            var modelReference = "accepted-model-reference";
            var resolver = new RuntimeModelBindingsResolver("prompt-capacity-test-v1",
                new Dictionary<string, RuntimeModelBinding>
                {
                    [modelReference] = new("prompt-test-model", ModelSourceMode.Byok,
                        new RuntimeByokProvider("openai", InferenceEndpoint.BaseUrl, "completions"))
                    {
                        PromptCapacityTokens = serverCapacity
                    }
                });
            ModelBindingConfigurationHash = resolver.ConfigurationHash;
            var registration = RuntimeCopilotSessionTests.Registration();
            registration = registration with
            {
                Binding = registration.Binding with
                {
                    ModelSelectionReference = modelReference,
                    ModelSourceMode = ModelSourceMode.Byok,
                    MaxPromptTokens = acceptedCapacity,
                    ModelBindingPin = resolver.Pin(modelReference, ModelSourceMode.Byok)
                }
            };
            AcceptedSelectionHash = registration.Binding.AcceptedSelectionHash;
            var privateState = Path.Combine(_testRoot, "private-state");
            var workingDirectory = Path.Combine(_testRoot, "workspace");
            var baseDirectory = Path.Combine(_testRoot, "base");
            Directory.CreateDirectory(privateState);
            Directory.CreateDirectory(workingDirectory);
            Directory.CreateDirectory(baseDirectory);
            var connection = NativeRuntimeManifest.Connection(ExecutablePath, privateState);
            connection.Environment = connection.Environment!
                .Append(new KeyValuePair<string, string>("SSL_CERT_FILE", _certificatePath))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var factory = new RuntimeCopilotSessionFactory(connection, baseDirectory, resolver,
                workingDirectory: workingDirectory, expectedRuntimeVersion: RuntimeVersion);
            return await factory.CreateAsync(registration, modelReference,
                new SecretCredential(_providerCredential, DateTimeOffset.UtcNow.AddMinutes(5)),
                _ => Task.CompletedTask, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await InferenceEndpoint.DisposeAsync();
            _certificate.Dispose();
            Directory.Delete(_testRoot, recursive: true);
        }

        private static X509Certificate2 CreateLoopbackCertificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=127.0.0.1", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var subjectAlternativeName = new SubjectAlternativeNameBuilder();
            subjectAlternativeName.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(subjectAlternativeName.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            return request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        }
    }

    private sealed class LoopbackInferenceEndpoint : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly string _providerCredential;
        private readonly ConcurrentQueue<InferenceRequestReceipt> _receipts = [];
        private string? _expectedSessionId;
        private int _initialSessionCorrelationProven;
        private int _requestCount;
        private int _failedRequests;

        private LoopbackInferenceEndpoint(WebApplication application, int port, string providerCredential)
        {
            _application = application;
            _providerCredential = providerCredential;
            BaseUrl = new Uri($"https://127.0.0.1:{port}/v1");
        }

        public Uri BaseUrl { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public int FailedRequests => Volatile.Read(ref _failedRequests);
        public IReadOnlyCollection<InferenceRequestReceipt> Receipts => _receipts.ToArray();

        public void BindSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) ||
                Interlocked.CompareExchange(ref _expectedSessionId, sessionId, null) is not null)
                throw new InvalidOperationException("The inference endpoint accepts one native test session.");
        }

        public static async Task<LoopbackInferenceEndpoint> StartAsync(
            X509Certificate2 certificate, string providerCredential, CancellationToken cancellationToken)
        {
            var port = ReserveLoopbackPort();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
            var application = builder.Build();
            var endpoint = new LoopbackInferenceEndpoint(application, port, providerCredential);
            application.MapPost("/{**path}", endpoint.HandleInferenceAsync);
            try
            {
                await application.StartAsync(cancellationToken);
                return endpoint;
            }
            catch
            {
                await application.DisposeAsync();
                throw;
            }
        }

        private async Task HandleInferenceAsync(HttpContext context)
        {
            Interlocked.Increment(ref _requestCount);
            try
            {
                using var request = await JsonDocument.ParseAsync(context.Request.Body,
                    cancellationToken: context.RequestAborted);
                var root = request.RootElement;
                var modelId = root.TryGetProperty("model", out var modelValue) &&
                    modelValue.ValueKind == JsonValueKind.String ? modelValue.GetString() : null;
                var sessionId = Volatile.Read(ref _expectedSessionId);
                var correlationMarker = sessionId is null ? null : $"agentweaver-native-session:{sessionId}";
                var requestContent = root.GetRawText();
                var containsSessionMarker = requestContent.Contains(
                    "agentweaver-native-session:", StringComparison.Ordinal);
                var markerMatchesBoundSession = correlationMarker is not null &&
                    requestContent.Contains(correlationMarker, StringComparison.Ordinal);
                var correlatedSessionId = markerMatchesBoundSession ? sessionId : null;
                var initialProofAccepted = Volatile.Read(ref _initialSessionCorrelationProven) != 0;
                var sessionCorrelationIsValid = IsSessionCorrelationValid(
                    containsSessionMarker, markerMatchesBoundSession, initialProofAccepted);
                var credentialMatched = string.Equals(
                    context.Request.Headers.Authorization.ToString(),
                    $"Bearer {_providerCredential}", StringComparison.Ordinal);
                var requestPath = context.Request.Path.Value ?? string.Empty;
                var responseId = $"chatcmpl-agentweaver-{Guid.NewGuid():N}";
                _receipts.Enqueue(new(
                    context.Request.Method,
                    context.Request.Scheme,
                    context.Request.Host.Host,
                    context.Request.Host.Port ?? 443,
                    requestPath,
                    modelId,
                    credentialMatched,
                    correlatedSessionId,
                    responseId));

                var requestIsValid = credentialMatched &&
                    sessionCorrelationIsValid &&
                    modelId == "prompt-test-model" &&
                    requestPath.EndsWith("/chat/completions", StringComparison.Ordinal);
                if (!requestIsValid)
                {
                    Interlocked.Increment(ref _failedRequests);
                    context.Response.StatusCode = credentialMatched
                        ? StatusCodes.Status400BadRequest
                        : StatusCodes.Status401Unauthorized;
                    return;
                }

                if (correlatedSessionId is not null)
                    Interlocked.Exchange(ref _initialSessionCorrelationProven, 1);

                var responseContent = $"native-route-response:{sessionId}:{responseId}";
                var stream = request.RootElement.TryGetProperty("stream", out var streamValue) &&
                    streamValue.ValueKind == JsonValueKind.True;
                if (stream)
                {
                    context.Response.ContentType = "text/event-stream";
                    await WriteStreamChunkAsync(context, responseContent, null, responseId);
                    await WriteStreamChunkAsync(context, "", "stop", responseId);
                    await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
                }
                else
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        id = responseId,
                        @object = "chat.completion",
                        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        model = "prompt-test-model",
                        choices = new[]
                        {
                            new
                            {
                                index = 0,
                                message = new { role = "assistant", content = responseContent },
                                finish_reason = "stop"
                            }
                        },
                        usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }
                    }, cancellationToken: context.RequestAborted);
                }
            }
            catch
            {
                Interlocked.Increment(ref _failedRequests);
                throw;
            }
        }

        internal static bool IsSessionCorrelationValid(
            bool containsSessionMarker,
            bool markerMatchesBoundSession,
            bool initialProofAccepted) =>
            containsSessionMarker
                ? markerMatchesBoundSession
                : initialProofAccepted;

        private static async Task WriteStreamChunkAsync(
            HttpContext context, string content, string? finishReason, string responseId)
        {
            var data = finishReason is null
                ? JsonSerializer.Serialize(new
                {
                    id = responseId,
                    @object = "chat.completion.chunk",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "prompt-test-model",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new { role = "assistant", content },
                            finish_reason = (string?)null
                        }
                    }
                })
                : JsonSerializer.Serialize(new
                {
                    id = responseId,
                    @object = "chat.completion.chunk",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "prompt-test-model",
                    choices = new[]
                    {
                        new { index = 0, delta = new { }, finish_reason = finishReason }
                    }
                });
            await context.Response.WriteAsync($"data: {data}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _application.StopAsync(timeout.Token);
            await _application.DisposeAsync();
        }

        private static int ReserveLoopbackPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public sealed record InferenceRequestReceipt(
            string Method,
            string Scheme,
            string Host,
            int Port,
            string Path,
            string? ModelId,
            bool CredentialMatched,
            string? CorrelatedSessionId,
            string ResponseId);
    }
}

public sealed class NativePromptCapacityFactAttribute : FactAttribute
{
    public NativePromptCapacityFactAttribute()
    {
        if (!OperatingSystem.IsLinux() ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTWEAVER_NATIVE_RUNTIME_ROOT")) ||
            Environment.GetEnvironmentVariable("AGENTWEAVER_NATIVE_PROMPT_EGRESS_BLOCKED") != "true")
            Skip = "Requires the pinned native bundle in an isolated Linux runner with external egress blocked.";
    }
}
