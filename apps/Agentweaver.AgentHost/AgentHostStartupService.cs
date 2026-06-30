using Agentweaver.AgentRuntime;
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
    private readonly CopilotAIAgent _agent;
    private readonly AgentHostOptions _options;
    private readonly AgentHostRuntimeState _runtimeState;
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
        ILogger<AgentHostStartupService> logger)
    {
        _agent = agent;
        _options = options.Value;
        _runtimeState = runtimeState;
        _logger = logger;
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
        await RunSetupAsync(opts.RunId, opts.UserId, cancellationToken).ConfigureAwait(false);
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
        CancellationToken ct)
    {
        _standby = false;
        _logger.LogInformation(
            "AgentHostStartupService: /configure received — provisioning agent for run {RunId}.", runId);
        await RunSetupAsync(runId, userId, ct).ConfigureAwait(false);
    }

    private async Task RunSetupAsync(string runId, string? userId, CancellationToken ct)
    {
        var opts = _options;
        _logger.LogInformation(
            "AgentHostStartupService: calling SetupAsync for run {RunId}, workingDir={WorkingDir}",
            runId, opts.WorkingDirectory);

        await _agent.SetupAsync(
            workingDirectory: opts.WorkingDirectory,
            repositoryPath: opts.RepositoryPath,
            runId: runId,
            modelId: opts.ModelId,
            systemPromptContext: opts.SystemPromptContext,
            streamWriter: null,     // RunEvent side-channel forwarded via A2A DataParts (P1.5)
            projectId: opts.ProjectId,
            agentName: opts.AgentName,
            apiBaseUrl: opts.ApiBaseUrl,
            apiKey: opts.ApiKey,
            ct: ct,
            userId: userId).ConfigureAwait(false);

        _ready = true;
        _logger.LogInformation(
            "AgentHostStartupService: agent ready for run {RunId}", runId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
