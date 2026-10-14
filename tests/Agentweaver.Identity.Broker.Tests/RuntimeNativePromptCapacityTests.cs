extern alias AgentHostService;

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

        await session.SendTurnAsync("Initialize this local test session.", timeout.Token);
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
    }

    [NativePromptCapacityFact]
    public async Task ByokNativeRuntimeFailsAnIrreducibleLatestPromptInsteadOfTheAcceptingFakeEndpoint()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var harness = await NativePromptHarness.StartAsync(timeout.Token);
        await using var session = await harness.CreateSessionAsync(1024, 8192, timeout.Token);
        await session.SendTurnAsync("Initialize this local test session.", timeout.Token);

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
        Assert.Equal(session.Facts.ModelId, context.ModelName);
        Assert.Equal("openai", context.Provider?.Id);
    }

    private sealed class NativePromptHarness : IAsyncDisposable
    {
        private const string ExpectedArchiveSha512 =
            "c33a2d65fbc7908b6eb5c88b14c5d94f693d4228a2e136bcb6c54308c43b08ff213f157dd45c89d727f7f8515249f3ef5ab7d833a04e022a88b97f8b8e046e8b";
        private readonly string _runtimeRoot;
        private readonly string _testRoot;
        private readonly string _certificatePath;
        private readonly X509Certificate2 _certificate;

        private NativePromptHarness(
            string runtimeRoot,
            string testRoot,
            string certificatePath,
            X509Certificate2 certificate,
            LoopbackInferenceEndpoint inferenceEndpoint,
            string executablePath,
            string runtimeVersion)
        {
            _runtimeRoot = runtimeRoot;
            _testRoot = testRoot;
            _certificatePath = certificatePath;
            _certificate = certificate;
            InferenceEndpoint = inferenceEndpoint;
            ExecutablePath = executablePath;
            RuntimeVersion = runtimeVersion;
        }

        public LoopbackInferenceEndpoint InferenceEndpoint { get; }
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
            try
            {
                await File.WriteAllTextAsync(certificatePath, certificate.ExportCertificatePem(), cancellationToken);
                inferenceEndpoint = await LoopbackInferenceEndpoint.StartAsync(certificate, cancellationToken);
                return new NativePromptHarness(runtimeRoot, testRoot, certificatePath, certificate,
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
                new SecretCredential("loopback-only-test-key", DateTimeOffset.UtcNow.AddMinutes(5)),
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
        private int _requestCount;
        private int _failedRequests;

        private LoopbackInferenceEndpoint(WebApplication application, int port)
        {
            _application = application;
            BaseUrl = new Uri($"https://127.0.0.1:{port}/v1");
        }

        public Uri BaseUrl { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public int FailedRequests => Volatile.Read(ref _failedRequests);

        public static async Task<LoopbackInferenceEndpoint> StartAsync(
            X509Certificate2 certificate, CancellationToken cancellationToken)
        {
            var port = ReserveLoopbackPort();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
            var application = builder.Build();
            var endpoint = new LoopbackInferenceEndpoint(application, port);
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
                var stream = request.RootElement.TryGetProperty("stream", out var streamValue) &&
                    streamValue.ValueKind == JsonValueKind.True;
                if (stream)
                {
                    context.Response.ContentType = "text/event-stream";
                    await WriteStreamChunkAsync(context, "Short local response.", null);
                    await WriteStreamChunkAsync(context, "", "stop");
                    await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
                }
                else
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        id = "chatcmpl-agentweaver-local",
                        @object = "chat.completion",
                        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        model = "prompt-test-model",
                        choices = new[]
                        {
                            new
                            {
                                index = 0,
                                message = new { role = "assistant", content = "Short local response." },
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

        private static async Task WriteStreamChunkAsync(
            HttpContext context, string content, string? finishReason)
        {
            var data = finishReason is null
                ? JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-agentweaver-local",
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
                    id = "chatcmpl-agentweaver-local",
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
