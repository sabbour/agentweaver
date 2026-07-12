using Agentweaver.AgentRuntime;
using Agentweaver.Domain;
using Microsoft.Extensions.Options;

namespace Agentweaver.AgentHost;

/// <summary>
/// Hosted service that provisions the <see cref="CopilotAIAgent"/> for the pod.
///
/// <para>
/// Two launch paths:
/// <list type="bullet">
///   <item><b>Env-var launch</b> (non-warm pod): <see cref="AgentHostOptions.RunId"/> is set at
///   startup, so <see cref="StartAsync"/> runs <c>SetupAsync</c> immediately and the pod is ready
///   when <see cref="StartAsync"/> returns (legacy behaviour).</item>
///   <item><b>Warm pool</b> (Option C): the pod starts with NO RunId and enters <b>standby</b> —
///   <c>SetupAsync</c> is deferred until the executor calls <see cref="ConfigureAsync"/> from the
///   <c>POST /configure</c> handler at run-launch time. The .NET process and Copilot SDK are
///   already warm, so only the per-run setup runs on the request path.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class AgentHostStartupService : IHostedService
{
    private const string SandboxManifestPath = "/etc/agentweaver/sandbox-manifest.json";

    /// <summary>
    /// Sandbox tool manifest baked into the image at build time.
    /// Null when running outside a production AgentHost image (local dev, tests).
    /// </summary>
    private static readonly string? SandboxManifestJson = TryReadManifest();

    private static string? TryReadManifest()
    {
        try { return File.Exists(SandboxManifestPath) ? File.ReadAllText(SandboxManifestPath) : null; }
        catch { return null; }
    }

    private readonly CopilotAIAgent _agent;
    private readonly AgentHostOptions _options;
    private readonly AgentHostRuntimeState _runtimeState;
    private readonly IRunOptionsStore _runOptions;
    private readonly PodLocalWorkspaceManager _workspaceManager;
    private readonly ILogger<AgentHostStartupService> _logger;

    private volatile bool _ready;
    private volatile bool _standby;

    /// <summary>True once <c>SetupAsync</c> has completed and the pod can serve A2A turns.</summary>
    public bool IsReady => _ready;

    /// <summary>True when the pod is warm but not yet configured (awaiting <c>POST /configure</c>).</summary>
    public bool IsStandby => _standby;

    public AgentHostStartupService(
        CopilotAIAgent agent,
        IOptions<AgentHostOptions> options,
        AgentHostRuntimeState runtimeState,
        IRunOptionsStore runOptions,
        ILogger<AgentHostStartupService> logger,
        PodLocalWorkspaceManager? workspaceManager = null)
    {
        _agent = agent;
        _options = options.Value;
        _runtimeState = runtimeState;
        _runOptions = runOptions;
        _logger = logger;
        _workspaceManager = workspaceManager ?? new PodLocalWorkspaceManager(
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PodLocalWorkspaceManager>.Instance);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = _options;
        if (string.IsNullOrWhiteSpace(opts.RunId))
        {
            // Warm-pool path: no per-run context yet. Stay warm and wait for /configure.
            _standby = true;
            _logger.LogInformation(
                "AgentHost in standby mode — waiting for /configure (warm pool, no RunId injected).");
            return;
        }

        // Env-var launch: seed runtime state from options and provision the agent now.
        _runtimeState.InitializeFromOptions(opts);
        await RunSetupAsync(
            new AgentHostRunConfiguration(
                opts.RunId,
                opts.UserId ?? string.Empty,
                opts.TurnBearerToken ?? string.Empty,
                opts.KvUserSecretName,
                GitHubAccessToken: null,
                PreviewRunnerCredential: null,
                SharedWorkingDirectory: null),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Warm-pool deferred provisioning: invoked once from the <c>POST /configure</c> handler with the
    /// per-run context. Runs <c>SetupAsync</c> against the configured values and marks the pod ready.
    /// </summary>
    public async Task ConfigureAsync(
        string runId,
        string userId,
        string turnBearerToken,
        string? kvUserSecretName,
        string? gitHubAccessToken,
        string? workingDirectory,
        bool autoApproveTools,
        CancellationToken ct)
        => await ConfigureAsync(
            new AgentHostRunConfiguration(
                runId,
                userId,
                turnBearerToken,
                kvUserSecretName,
                gitHubAccessToken,
                PreviewRunnerCredential: null,
                SharedWorkingDirectory: workingDirectory),
            autoApproveTools,
            ct).ConfigureAwait(false);

    /// <summary>Warm-pool deferred provisioning with the complete purpose-specific configuration.</summary>
    public async Task ConfigureAsync(
        AgentHostRunConfiguration configuration,
        bool autoApproveTools,
        CancellationToken ct)
    {
        _standby = false;

        // Seed the pod's in-memory run-options store from the per-run flag delivered by the API
        // (bug #221). The pod boots a fresh IRunOptionsStore defaulting AutoApproveTools=false, so
        // without this the CopilotAIAgent HITL gate never auto-approves web_fetch and every request
        // stalls out the 5-minute timeout under autopilot. CopilotAIAgent reads this lazily at
        // tool-call time (after /configure returns), so setting it here is safe.
        _runOptions.SetAutoApproveTools(configuration.RunId, autoApproveTools);

        _logger.LogInformation(
            "AgentHostStartupService: /configure received — provisioning agent for run {RunId} purpose={Purpose} (autoApproveTools={AutoApproveTools}).",
            configuration.RunId, configuration.Purpose, autoApproveTools);
        await RunSetupAsync(configuration, ct).ConfigureAwait(false);
    }

    private async Task RunSetupAsync(AgentHostRunConfiguration configuration, CancellationToken ct)
    {
        var opts = _options;
        var runId = configuration.RunId;
        var workingDirectoryOverride = configuration.SharedWorkingDirectory;

        // Warm pods carry a static AgentHost__WorkingDirectory env (the /workspace mount root). The
        // per-run worktree path delivered via /configure overrides it so the pod's file-tool root
        // matches the directory the run's system prompt references — without this override, sibling
        // agents of one parent write to divergent dirs and later stages cannot find earlier output.
        var workingDirectory = string.IsNullOrWhiteSpace(workingDirectoryOverride)
            ? opts.WorkingDirectory
            : workingDirectoryOverride!;
        var repositoryPath = string.IsNullOrWhiteSpace(workingDirectoryOverride)
            ? opts.RepositoryPath
            : workingDirectoryOverride!;

        if (configuration.WorkspaceMode != ExecutionWorkspaceMode.Shared)
        {
            var prepared = await _workspaceManager.PrepareAsync(
                new PodLocalWorkspaceSpec(
                    configuration.RunId,
                    configuration.SourceRepositoryPath!,
                    configuration.SourceRef!,
                    configuration.BaseCommitSha!,
                    configuration.ExpectedTreeHash!,
                    configuration.WorkspaceMode,
                    configuration.ScratchRoot!),
                ct).ConfigureAwait(false);
            workingDirectory = prepared.WorkspacePath;
            repositoryPath = prepared.WorkspacePath;
        }
        _runtimeState.SetEffectiveWorkingDirectory(workingDirectory);

        // Prepend the sandbox tool manifest (baked into the image) to the per-run system prompt
        // context so every agent knows what tools are available without probing.
        var systemPromptContext = BuildSystemPromptContext(
            opts.SystemPromptContext,
            configuration.Purpose);

        _logger.LogInformation(
            "AgentHostStartupService: calling SetupAsync for run {RunId}, workingDir={WorkingDir} (override={HasOverride}), manifestAttached={ManifestAttached}",
            runId, workingDirectory, !string.IsNullOrWhiteSpace(workingDirectoryOverride), SandboxManifestJson is not null);

        await _agent.SetupAsync(
            workingDirectory: workingDirectory,
            repositoryPath: repositoryPath,
            runId: runId,
            modelId: opts.ModelId,
            systemPromptContext: systemPromptContext,
            streamWriter: null,     // RunEvent side-channel forwarded via A2A DataParts (P1.5)
            projectId: opts.ProjectId,
            agentName: opts.AgentName,
            apiBaseUrl: opts.ApiBaseUrl,
            apiKey: opts.ApiKey,
            ct: ct,
            userId: configuration.UserId,
            purpose: configuration.Purpose).ConfigureAwait(false);

        _ready = true;
        _logger.LogInformation(
            "AgentHostStartupService: agent ready for run {RunId}", runId);
    }

    /// <summary>
    /// Prepends the sandbox tool manifest to <paramref name="configuredContext"/> when the manifest
    /// is available (i.e., running inside a production AgentHost image).
    /// The manifest is baked at image build time; see <c>apps/Agentweaver.AgentHost/Dockerfile</c>.
    /// </summary>
    private static string? BuildSystemPromptContext(
        string? configuredContext,
        Agentweaver.Domain.AgentHostPurpose purpose)
    {
        var sections = new List<string>();
        if (SandboxManifestJson is not null)
        {
            sections.Add(
                $"""
                SANDBOX TOOL MANIFEST
                The following tools are pre-installed in this sandbox (from /etc/agentweaver/sandbox-manifest.json).
                Check this list before attempting to install anything:
                {SandboxManifestJson}
                """);
        }

        if (purpose == Agentweaver.Domain.AgentHostPurpose.AssemblyBuildTest)
        {
            sections.Add(
                """
                ASSEMBLY BUILD/TEST EXECUTION
                The native Copilot shell is disabled for this gate. Use the custom run_command tool.
                Commands are serialized, time-bounded, and must remain in the foreground.
                """);
        }

        if (!string.IsNullOrWhiteSpace(configuredContext))
            sections.Add(configuredContext);

        return sections.Count == 0 ? null : string.Join("\n\n", sections);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        _workspaceManager.CleanupAsync(cancellationToken);
}
