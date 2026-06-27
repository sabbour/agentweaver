using Agentweaver.AgentRuntime;
using Microsoft.Extensions.Options;

namespace Agentweaver.AgentHost;

/// <summary>
/// Hosted service that provisions the <see cref="CopilotAIAgent"/> at pod startup.
/// Reads per-run config from <see cref="AgentHostOptions"/> (injected as env vars by the
/// worker at claim time), calls <c>SetupAsync</c> once, and exposes a
/// <see cref="IsReady"/> flag that the A2A endpoint middleware uses to gate requests.
///
/// <para>
/// <c>SetupAsync</c> runs to completion before <see cref="StartAsync"/> returns, so the
/// pod is healthy only after the Copilot client and governance kernel are provisioned.
/// </para>
/// </summary>
internal sealed class AgentHostStartupService : IHostedService
{
    private readonly CopilotAIAgent _agent;
    private readonly AgentHostOptions _options;
    private readonly ILogger<AgentHostStartupService> _logger;

    private volatile bool _ready;

    public bool IsReady => _ready;

    public AgentHostStartupService(
        CopilotAIAgent agent,
        IOptions<AgentHostOptions> options,
        ILogger<AgentHostStartupService> logger)
    {
        _agent = agent;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = _options;
        if (string.IsNullOrWhiteSpace(opts.RunId))
        {
            _logger.LogError("AgentHostStartupService: AgentHost:RunId is not configured. Pod cannot serve agent turns.");
            throw new InvalidOperationException("AgentHost:RunId must be set.");
        }

        _logger.LogInformation(
            "AgentHostStartupService: calling SetupAsync for run {RunId}, workingDir={WorkingDir}",
            opts.RunId, opts.WorkingDirectory);

        await _agent.SetupAsync(
            workingDirectory: opts.WorkingDirectory,
            repositoryPath: opts.RepositoryPath,
            runId: opts.RunId,
            modelId: opts.ModelId,
            systemPromptContext: opts.SystemPromptContext,
            streamWriter: null,     // RunEvent side-channel forwarded via A2A DataParts (P1.5)
            projectId: opts.ProjectId,
            agentName: opts.AgentName,
            apiBaseUrl: opts.ApiBaseUrl,
            apiKey: opts.ApiKey,
            ct: cancellationToken,
            userId: opts.UserId).ConfigureAwait(false);

        _ready = true;
        _logger.LogInformation(
            "AgentHostStartupService: agent ready for run {RunId}", opts.RunId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
