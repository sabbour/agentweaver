using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Sandbox;

// ── 1. SandboxAgentOptions.ParseMode() ──────────────────────────────────────

/// <summary>
/// spec-018 P1: ParseMode round-trip — every supported string value, case variants,
/// and unknown/empty/null inputs must default to InApi.
/// </summary>
public sealed class Spec018SandboxAgentOptionsTests
{
    [Theory]
    [InlineData("in-api",        AgentExecutionMode.InApi)]
    [InlineData("IN-API",        AgentExecutionMode.InApi)]
    [InlineData("In-Api",        AgentExecutionMode.InApi)]
    [InlineData("pod-per-run",   AgentExecutionMode.PodPerRun)]
    [InlineData("POD-PER-RUN",   AgentExecutionMode.PodPerRun)]
    [InlineData("podperrun",     AgentExecutionMode.PodPerRun)]
    [InlineData("PODPERRUN",     AgentExecutionMode.PodPerRun)]
    // PascalCase "PodPerRun" → lower = "podperrun" → PodPerRun
    [InlineData("PodPerRun",     AgentExecutionMode.PodPerRun)]
    public void ParseMode_KnownValues_ReturnExpectedMode(string raw, AgentExecutionMode expected)
    {
        SandboxAgentOptions.ParseMode(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("in_api")]         // underscore variant — not a recognized alias
    [InlineData("pod_per_run")]    // underscore variant
    [InlineData("PodPerRuns")]     // trailing 's'
    [InlineData("remote")]
    public void ParseMode_UnrecognisedOrEmpty_DefaultsToInApi(string? raw)
    {
        SandboxAgentOptions.ParseMode(raw).Should().Be(AgentExecutionMode.InApi);
    }

    [Fact]
    public void ParseMode_NullInput_DefaultsToInApi()
    {
        SandboxAgentOptions.ParseMode(null).Should().Be(AgentExecutionMode.InApi);
    }
}

// ── 2. RunEventDataPartCodec ─────────────────────────────────────────────────

/// <summary>
/// spec-018 P1: RunEventDataPartCodec round-trip + malformed-input handling.
/// NOTE: The source-generated RunEventDtoJsonContext serializes RunEventDto.Payload as object?.
/// Tests use null, string, or int payloads which are always serializable; anonymous types
/// are not supported by the strict source-generated context.
/// </summary>
public sealed class Spec018RunEventDataPartCodecTests
{
    // --- Encoding ---

    [Fact]
    public void EncodeRunEvent_ProducesDataContentWithCorrectMediaType()
    {
        var ev = new RunEvent(1, "tool.call", "bash-call");
        var content = RunEventDataPartCodec.EncodeRunEvent(ev);

        content.MediaType.Should().BeEquivalentTo(RunEventDataPartCodec.MediaType);
    }

    [Fact]
    public void EncodeRunEvent_ProducesNonEmptyBytes()
    {
        var ev = new RunEvent(5, "run.output", "hello world");
        var content = RunEventDataPartCodec.EncodeRunEvent(ev);

        content.Data.IsEmpty.Should().BeFalse();
    }

    // --- Round-trip ---

    [Theory]
    [InlineData(1, "tool.call")]
    [InlineData(0, "run.output")]
    [InlineData(99, "checkpoint.written")]
    [InlineData(int.MaxValue, "some.event")]
    public void RoundTrip_Sequence_And_Type_PreservedExactly(int sequence, string type)
    {
        var original = new RunEvent(sequence, type, null!);
        var encoded = RunEventDataPartCodec.EncodeRunEvent(original);
        var decoded = RunEventDataPartCodec.TryDecodeRunEvent(encoded);

        decoded.Should().NotBeNull();
        decoded!.Sequence.Should().Be(sequence);
        decoded.Type.Should().Be(type);
    }

    [Fact]
    public void RoundTrip_StringPayload_Survives()
    {
        var original = new RunEvent(3, "tool.output", "exit_code:0");
        var encoded = RunEventDataPartCodec.EncodeRunEvent(original);
        var decoded = RunEventDataPartCodec.TryDecodeRunEvent(encoded);

        decoded.Should().NotBeNull();
        decoded!.Sequence.Should().Be(3);
        decoded.Type.Should().Be("tool.output");
        decoded.Payload.Should().NotBeNull();
    }

    [Fact]
    public void RoundTrip_NullPayload_PreservesSequenceAndType()
    {
        var original = new RunEvent(7, "run.started", null!);
        var encoded = RunEventDataPartCodec.EncodeRunEvent(original);
        var decoded = RunEventDataPartCodec.TryDecodeRunEvent(encoded);

        decoded.Should().NotBeNull();
        decoded!.Sequence.Should().Be(7);
        decoded.Type.Should().Be("run.started");
    }

    // --- Malformed inputs ---

    [Fact]
    public void TryDecodeRunEvent_WrongMediaType_ReturnsNull()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { sequence = 1, type = "x", payload = (object?)null });
        var wrong = new DataContent(new ReadOnlyMemory<byte>(bytes), "application/json");

        RunEventDataPartCodec.TryDecodeRunEvent(wrong).Should().BeNull();
    }

    [Fact]
    public void TryDecodeRunEvent_EmptyData_ReturnsNull()
    {
        var empty = new DataContent(new ReadOnlyMemory<byte>([]), RunEventDataPartCodec.MediaType);

        RunEventDataPartCodec.TryDecodeRunEvent(empty).Should().BeNull();
    }

    [Fact]
    public void TryDecodeRunEvent_MalformedJson_ReturnsNull()
    {
        var garbage = new DataContent(new ReadOnlyMemory<byte>("{not valid json"u8.ToArray()), RunEventDataPartCodec.MediaType);

        RunEventDataPartCodec.TryDecodeRunEvent(garbage).Should().BeNull();
    }

    [Fact]
    public void TryDecodeRunEvent_MissingTypeField_ReturnsNull()
    {
        // DTO with null type — decoder must reject it
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { sequence = 1 });
        var dc = new DataContent(new ReadOnlyMemory<byte>(bytes), RunEventDataPartCodec.MediaType);

        RunEventDataPartCodec.TryDecodeRunEvent(dc).Should().BeNull();
    }

    [Fact]
    public void MediaTypeConstant_HasExpectedValue()
    {
        RunEventDataPartCodec.MediaType.Should().Be("application/x-agentweaver-run-event+json");
    }
}

// ── 3. AgentSetupParams serialization round-trip ─────────────────────────────

/// <summary>
/// spec-018 P1: AgentSetupParams serializes and deserializes symmetrically via the
/// source-generated JSON context (<c>AgentSetupParamsJsonContext</c>).
/// </summary>
public sealed class Spec018AgentSetupParamsTests
{
    [Fact]
    public void Roundtrip_AllFields_Preserved()
    {
        var original = new AgentSetupParams
        {
            WorkingDirectory  = "/workspace/my-project",
            RepositoryPath    = "/workspace/my-project/.git",
            RunId             = "run-abc-123",
            ModelId           = "gpt-4o",
            SystemPromptContext = "You are a coding agent.",
            ProjectId         = "proj-xyz",
            AgentName         = "worker",
            ApiBaseUrl        = "https://api.example.com",
            ApiKey            = "key-secret",
            UserId            = "user@example.com",
            IsRevision        = true,
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            original, AgentSetupParamsJsonContext.Default.AgentSetupParams);
        var restored = JsonSerializer.Deserialize(
            bytes, AgentSetupParamsJsonContext.Default.AgentSetupParams);

        restored.Should().NotBeNull();
        restored!.WorkingDirectory.Should().Be(original.WorkingDirectory);
        restored.RepositoryPath.Should().Be(original.RepositoryPath);
        restored.RunId.Should().Be(original.RunId);
        restored.ModelId.Should().Be(original.ModelId);
        restored.SystemPromptContext.Should().Be(original.SystemPromptContext);
        restored.ProjectId.Should().Be(original.ProjectId);
        restored.AgentName.Should().Be(original.AgentName);
        restored.ApiBaseUrl.Should().Be(original.ApiBaseUrl);
        restored.ApiKey.Should().Be(original.ApiKey);
        restored.UserId.Should().Be(original.UserId);
        restored.IsRevision.Should().Be(original.IsRevision);
    }

    [Fact]
    public void Roundtrip_NullOptionalFields_RemainsNull()
    {
        var minimal = new AgentSetupParams
        {
            WorkingDirectory = "/workspace",
            RepositoryPath   = "/repo",
            RunId            = "run-min",
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            minimal, AgentSetupParamsJsonContext.Default.AgentSetupParams);
        var restored = JsonSerializer.Deserialize(
            bytes, AgentSetupParamsJsonContext.Default.AgentSetupParams);

        restored.Should().NotBeNull();
        restored!.ModelId.Should().BeNull();
        restored.SystemPromptContext.Should().BeNull();
        restored.ProjectId.Should().BeNull();
        restored.AgentName.Should().BeNull();
        restored.ApiBaseUrl.Should().BeNull();
        restored.ApiKey.Should().BeNull();
        restored.UserId.Should().BeNull();
        restored.IsRevision.Should().BeFalse();
    }

    [Fact]
    public void MediaTypeConstant_HasExpectedValue()
    {
        AgentSetupParams.MediaType.Should().Be("application/x-agentweaver-agent-setup+json");
    }
}

// ── 4. NoOpSandboxAgentEndpointResolver ──────────────────────────────────────

/// <summary>
/// spec-018 P1: NoOpSandboxAgentEndpointResolver always returns null as designed —
/// it is the safe fallback outside Kubernetes and must never produce a usable endpoint.
/// </summary>
public sealed class Spec018NoOpResolverTests
{
    [Fact]
    public async Task TryResolveEndpointAsync_AlwaysReturnsNull()
    {
        var resolver = new NoOpSandboxAgentEndpointResolver();
        var result = await resolver.TryResolveEndpointAsync("run-123", CancellationToken.None);
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("run-abc")]
    [InlineData("")]
    [InlineData("  ")]
    public async Task TryResolveEndpointAsync_AnyRunId_ReturnsNull(string runId)
    {
        var resolver = new NoOpSandboxAgentEndpointResolver();
        var result = await resolver.TryResolveEndpointAsync(runId, CancellationToken.None);
        result.Should().BeNull();
    }
}

// ── 5. Factory selection ─────────────────────────────────────────────────────

/// <summary>
/// spec-018 P1: IWorkflowAgentFactory registration mirrors the last-wins override pattern
/// in Program.cs. Tests confirm that:
/// - in-api mode → the default factory (NOT RemoteWorkflowAgentFactory) is kept,
/// - pod-per-run mode → RemoteWorkflowAgentFactory is the resolved implementation.
/// Uses a minimal ServiceCollection that mirrors the Program.cs selection block.
/// </summary>
public sealed class Spec018FactorySelectionTests
{
    [Fact]
    public void FactorySelection_PodPerRun_LastDescriptorUsesRemoteFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton<ISandboxAgentEndpointResolver>(new NoOpSandboxAgentEndpointResolver());

        // Simulate AddAgentRuntime() base registration (a placeholder factory for the test)
        services.AddSingleton<IWorkflowAgentFactory, Spec018StubFactory>();

        // Mirror Program.cs: pod-per-run adds the remote override (last-wins)
        services.AddSingleton<RemoteWorkflowAgentFactory>();
        services.AddSingleton<IWorkflowAgentFactory>(sp => sp.GetRequiredService<RemoteWorkflowAgentFactory>());

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IWorkflowAgentFactory>();

        factory.Should().BeOfType<RemoteWorkflowAgentFactory>(
            "pod-per-run mode must resolve RemoteWorkflowAgentFactory as IWorkflowAgentFactory");
    }

    [Fact]
    public void FactorySelection_InApi_RemoteFactoryOverrideIsNotAdded()
    {
        var mode = SandboxAgentOptions.ParseMode("in-api");

        var services = new ServiceCollection();
        // Simulate AddAgentRuntime() default registration
        services.AddSingleton<IWorkflowAgentFactory, Spec018StubFactory>();

        // Mirror Program.cs selection block
        if (mode == AgentExecutionMode.PodPerRun)
        {
            // This block must NOT be entered for in-api mode
            services.AddSingleton<IWorkflowAgentFactory, RemoteWorkflowAgentFactory>();
        }

        // Last descriptor must still be the stub (no pod-per-run override applied)
        var lastDescriptor = services.Last(d => d.ServiceType == typeof(IWorkflowAgentFactory));
        lastDescriptor.ImplementationType.Should().Be(typeof(Spec018StubFactory),
            "in-api mode must NOT register RemoteWorkflowAgentFactory as IWorkflowAgentFactory");
    }

    [Fact]
    public void FactorySelection_PodPerRun_ParseModeProducesPodPerRunMode()
    {
        // Sanity: the config value that activates remote factory parses correctly
        SandboxAgentOptions.ParseMode("pod-per-run").Should().Be(AgentExecutionMode.PodPerRun);
    }

    [Fact]
    public void RemoteWorkflowAgentFactory_CreateWorkerAgent_ReturnsRemoteAgentProxy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton<ISandboxAgentEndpointResolver>(new NoOpSandboxAgentEndpointResolver());
        services.AddSingleton<RemoteWorkflowAgentFactory>();

        var sp = services.BuildServiceProvider();
        var remoteFactory = sp.GetRequiredService<RemoteWorkflowAgentFactory>();

        // All four factory methods must produce a RemoteAgentProxy
        remoteFactory.CreateWorkerAgent().Should().BeOfType<RemoteAgentProxy>();
        remoteFactory.CreateRaiAgent().Should().BeOfType<RemoteAgentProxy>();
        remoteFactory.CreateRubberduckAgent().Should().BeOfType<RemoteAgentProxy>();
        remoteFactory.CreateScribeAgent().Should().BeOfType<RemoteAgentProxy>();
    }

    /// <summary>Minimal stub factory used in place of the full WorkflowAgentFactory in DI tests.</summary>
    private sealed class Spec018StubFactory : IWorkflowAgentFactory
    {
        public IWorkflowTurnAgent CreateWorkerAgent()     => throw new NotImplementedException();
        public IWorkflowTurnAgent CreateRaiAgent()         => throw new NotImplementedException();
        public IWorkflowTurnAgent CreateRubberduckAgent() => throw new NotImplementedException();
        public IWorkflowTurnAgent CreateScribeAgent()     => throw new NotImplementedException();
    }
}

// ── 6. Release-on-suspend (RunWatchLoopService) ───────────────────────────────

/// <summary>
/// spec-018 P1: RunWatchLoopService.ReleasePodOnSuspendSafeAsync (private) must call
/// IAgentHostPodLifecycle.ReleaseAgentHostPodAsync exactly when:
///   - podLifecycle is not null AND
///   - SandboxRuntimeOptions.IsPodPerRun is true AND
///   - SandboxRuntimeOptions.ReleasePodOnSuspend is true.
/// Tests drive the decision seam via reflection on a fully-DI-resolved service instance.
/// Each test creates its own factory to control mode + lifecycle independently.
/// </summary>
public sealed class Spec018PodReleaseTests
{
    private static readonly MethodInfo? ReleasePodMethod =
        typeof(Agentweaver.Api.Runs.RunWatchLoopService)
            .GetMethod("ReleasePodOnSuspendSafeAsync", BindingFlags.NonPublic | BindingFlags.Instance);

    [Fact]
    public void ReleasePodMethod_Exists_OnRunWatchLoopService()
    {
        // Guard: if this fails the reflection-based tests below are vacuously passing
        ReleasePodMethod.Should().NotBeNull(
            "ReleasePodOnSuspendSafeAsync must exist as a private method on RunWatchLoopService");
    }

    [Fact]
    public async Task ReleasePod_WhenPodPerRunAndReleaseEnabled_CallsRelease()
    {
        var lifecycle = new TrackingPodLifecycle();
        using var appFactory = new Spec018PodReleaseWebAppFactory(
            agentMode: "pod-per-run", releasePodOnSuspend: true, podLifecycle: lifecycle);

        var svc = appFactory.Services.GetRequiredService<Agentweaver.Api.Runs.RunWatchLoopService>();
        await InvokeReleasePod(svc, "run-release-001");

        lifecycle.ReleasedRunIds.Should().Contain("run-release-001",
            "lifecycle.ReleaseAgentHostPodAsync must be called when mode=pod-per-run and ReleasePodOnSuspend=true");
    }

    [Fact]
    public async Task ReleasePod_WhenReleasePodOnSuspendFalse_DoesNotCallRelease()
    {
        var lifecycle = new TrackingPodLifecycle();
        using var appFactory = new Spec018PodReleaseWebAppFactory(
            agentMode: "pod-per-run", releasePodOnSuspend: false, podLifecycle: lifecycle);

        var svc = appFactory.Services.GetRequiredService<Agentweaver.Api.Runs.RunWatchLoopService>();
        await InvokeReleasePod(svc, "run-no-release-002");

        lifecycle.ReleasedRunIds.Should().BeEmpty(
            "ReleaseAgentHostPodAsync must NOT be called when ReleasePodOnSuspend=false");
    }

    [Fact]
    public async Task ReleasePod_WhenModeIsInApi_DoesNotCallRelease()
    {
        var lifecycle = new TrackingPodLifecycle();
        using var appFactory = new Spec018PodReleaseWebAppFactory(
            agentMode: "in-api", releasePodOnSuspend: true, podLifecycle: lifecycle);

        var svc = appFactory.Services.GetRequiredService<Agentweaver.Api.Runs.RunWatchLoopService>();
        await InvokeReleasePod(svc, "run-in-api-003");

        lifecycle.ReleasedRunIds.Should().BeEmpty(
            "ReleaseAgentHostPodAsync must NOT be called when mode=in-api, even if ReleasePodOnSuspend=true");
    }

    [Fact]
    public async Task ReleasePod_WhenLifecycleIsNull_DoesNotThrow()
    {
        // When IAgentHostPodLifecycle is not registered (null injected), the guard must short-circuit
        using var appFactory = new Spec018PodReleaseWebAppFactory(
            agentMode: "pod-per-run", releasePodOnSuspend: true, podLifecycle: null);

        var svc = appFactory.Services.GetRequiredService<Agentweaver.Api.Runs.RunWatchLoopService>();

        var act = async () => await InvokeReleasePod(svc, "run-no-lifecycle-004");
        await act.Should().NotThrowAsync(
            "null podLifecycle must be a silent no-op, not an exception");
    }

    private static async Task InvokeReleasePod(Agentweaver.Api.Runs.RunWatchLoopService svc, string runId)
    {
        var task = (Task)ReleasePodMethod!.Invoke(svc, [runId])!;
        await task;
    }
}

/// <summary>
/// WebApplicationFactory variant that injects a configurable <see cref="TrackingPodLifecycle"/>
/// and overrides the <c>Sandbox:AgentExecutionMode</c> / <c>Sandbox:ReleasePodOnSuspend</c>
/// config keys, enabling release-on-suspend unit tests against a fully-resolved DI graph.
/// </summary>
public sealed class Spec018PodReleaseWebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _agentMode;
    private readonly bool _releasePodOnSuspend;
    private readonly TrackingPodLifecycle? _podLifecycle;

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"spec018-pr-{Guid.NewGuid():N}.db");
    private readonly string _worktreesPath = Path.Combine(
        Path.GetTempPath(), $"spec018-pr-wt-{Guid.NewGuid():N}");
    private readonly string _checkpointsPath = Path.Combine(
        Path.GetTempPath(), $"spec018-pr-cp-{Guid.NewGuid():N}");
    private readonly string _coordCheckpointsPath = Path.Combine(
        Path.GetTempPath(), $"spec018-pr-ccp-{Guid.NewGuid():N}");

    public Spec018PodReleaseWebAppFactory(
        string agentMode = "pod-per-run",
        bool releasePodOnSuspend = true,
        TrackingPodLifecycle? podLifecycle = null)
    {
        _agentMode = agentMode;
        _releasePodOnSuspend = releasePodOnSuspend;
        _podLifecycle = podLifecycle;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"]                          = _dbPath,
                ["Worktrees:BasePath"]                     = _worktreesPath,
                ["Checkpoints:Path"]                       = _checkpointsPath,
                ["Coordinator:Checkpoints:Path"]           = _coordCheckpointsPath,
                ["Testing:BypassGitHubOrgAuthorization"]   = "true",
                ["Testing:BypassGitHubTokenAuth"]          = "true",
                ["Auth:ApiKey"]                            = "spec018-test-key",
                ["Auth:User"]                              = "spec018-test-user",
                ["Git:Author:Name"]                        = "Test",
                ["Git:Author:Email"]                       = "test@localhost",
                ["Providers:GitHubCopilot:ApiKey"]         = "test-copilot-key",
                ["Providers:GitHubCopilot:Endpoint"]       = "https://api.githubcopilot.com",
                ["Providers:GitHubCopilot:Model"]          = "gpt-4o",
                ["Providers:MicrosoftFoundry:ApiKey"]      = "test-foundry-key",
                ["Providers:MicrosoftFoundry:Endpoint"]    = "https://test.openai.azure.com",
                ["Providers:MicrosoftFoundry:Deployment"]  = "gpt-4o",
                ["RunBounds:MaxSteps"]                     = "50",
                ["RunBounds:MaxMinutes"]                   = "10",
                // spec-018 P1 overrides
                ["Sandbox:AgentExecutionMode"]             = _agentMode,
                ["Sandbox:ReleasePodOnSuspend"]            = _releasePodOnSuspend.ToString().ToLowerInvariant(),
            }));

        if (_podLifecycle is not null)
        {
            builder.ConfigureServices(services =>
                services.AddSingleton<IAgentHostPodLifecycle>(_podLifecycle));
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(p); } catch { /* best-effort */ }

        foreach (var d in new[] { _worktreesPath, _checkpointsPath, _coordCheckpointsPath })
            try { Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
    }
}

/// <summary>
/// Test double for <see cref="IAgentHostPodLifecycle"/> that records which run IDs
/// had their pods released, enabling assertion in pod-release unit tests.
/// </summary>
public sealed class TrackingPodLifecycle : IAgentHostPodLifecycle
{
    private readonly List<string> _released = [];

    public IReadOnlyList<string> ReleasedRunIds => _released;

    public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default)
        => Task.FromResult($"https://fake-pod:8088/a2a/agent");

    public Task CheckAgentHostCapacityAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default)
    {
        _released.Add(runId);
        return Task.CompletedTask;
    }
}

// ── 7. RemoteAgentProxy checkpoint-proxy invariant ───────────────────────────

/// <summary>
/// spec-018 P1 / Q2: <see cref="RemoteAgentProxy"/> must carry no database or checkpoint
/// dependencies — the worker owns all checkpoint and event writes; the pod gets zero DB
/// access (§4.7.5). This reflection-based guard test catches accidental dependency injection
/// of DB-related types into the proxy constructor or fields.
/// </summary>
public sealed class Spec018RemoteAgentProxyInvariantTests
{
    private static readonly string[] ForbiddenTypeNameSubstrings =
    [
        "CheckpointStore",
        "SqliteRun",
        "SqliteDb",
        "DbContext",
        "IRunEventStream",
        "PendingRequestStore",
        "RunStreamStore",
    ];

    [Fact]
    public void RemoteAgentProxy_Constructor_HasNoDbOrCheckpointDependencies()
    {
        var ctors = typeof(RemoteAgentProxy).GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        foreach (var ctor in ctors)
        {
            foreach (var param in ctor.GetParameters())
            {
                var typeName = param.ParameterType.Name;
                typeName.Should().NotContainAny(ForbiddenTypeNameSubstrings,
                    $"constructor parameter '{param.Name}' ({typeName}) must not be a DB/checkpoint type — " +
                    "the proxy must carry no database dependency (spec-018 Q2)");
            }
        }
    }

    [Fact]
    public void RemoteAgentProxy_Fields_ContainNoDbOrCheckpointTypes()
    {
        var fields = typeof(RemoteAgentProxy).GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        foreach (var field in fields)
        {
            var typeName = field.FieldType.Name;
            typeName.Should().NotContainAny(ForbiddenTypeNameSubstrings,
                $"field '{field.Name}' ({typeName}) must not hold a DB/checkpoint reference — " +
                "the proxy must carry no database dependency (spec-018 Q2)");
        }
    }

    [Fact]
    public void RemoteAgentProxy_Constructor_AcceptsExactlyThreeParameters()
    {
        // Constructor: ISandboxAgentEndpointResolver, IHttpClientFactory, ILoggerFactory
        var ctor = typeof(RemoteAgentProxy)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single();

        ctor.GetParameters().Should().HaveCount(3,
            "the proxy's sole public constructor must accept exactly 3 DI dependencies " +
            "(ISandboxAgentEndpointResolver, IHttpClientFactory, ILoggerFactory)");
    }

    [Fact]
    public void RemoteAgentProxy_Constructor_ParameterTypes_AreExpected()
    {
        var ctor = typeof(RemoteAgentProxy)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single();

        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        paramTypes.Should().Contain(typeof(ISandboxAgentEndpointResolver));
        paramTypes.Should().Contain(typeof(IHttpClientFactory));
        paramTypes.Should().Contain(typeof(ILoggerFactory));
    }
}
