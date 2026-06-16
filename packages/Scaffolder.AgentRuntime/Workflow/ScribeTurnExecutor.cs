using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.Domain;
using Scaffolder.SandboxExec;

namespace Scaffolder.AgentRuntime.Workflow;

/// <summary>
/// Runs Scribe as a real agent turn after a project run completes.
/// Scribe receives a structured task and uses memory API tools to review
/// the inbox, merge learnings, and export to .squad/.
/// Its charter is read dynamically from <c>.squad/agents/scribe/charter.md</c>.
/// Best-effort: exceptions never abort the workflow.
/// </summary>
public sealed class ScribeTurnExecutor : Executor<ScribeTurnInput, ScribeTurnInput>
{
    private const string FallbackCharter =
        "You are Scribe — the silent memory keeper for this agent team. " +
        "You do not write code or make design decisions. " +
        "You only manage memory: merge, archive, and export. " +
        "Act systematically. Complete every step. Never skip the export.";

    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ISandboxExecutor _sandboxExecutor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly IToolApprovalGate _toolApprovalGate;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ScribeTurnExecutor> _logger;
    private readonly Func<string, ChannelWriter<RunEvent>?> _getRecordingWriter;

    public ScribeTurnExecutor(
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor sandboxExecutor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILoggerFactory loggerFactory,
        Func<string, ChannelWriter<RunEvent>?>? getRecordingWriter = null,
        string name = "scribe-turn")
        : base(name)
    {
        _copilotClientFactory = copilotClientFactory;
        _scopeProvider = scopeProvider;
        _sandboxExecutor = sandboxExecutor;
        _sandboxPolicyStore = sandboxPolicyStore;
        _approvalStore = approvalStore;
        _toolApprovalGate = toolApprovalGate;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ScribeTurnExecutor>();
        _getRecordingWriter = getRecordingWriter ?? (_ => null);
    }

    public override async ValueTask<ScribeTurnInput> HandleAsync(
        ScribeTurnInput input, IWorkflowContext context, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(input.ProjectId) || string.IsNullOrEmpty(input.AgentName))
        {
            _logger.LogDebug("Scribe skipped for run {RunId} — no project/agent context", input.RunId);
            WorkflowStepEvents.Emit(_getRecordingWriter(input.RunId), _logger, "scribe", "skipped", "Scribe pass");
            return input;
        }

        var writer = _getRecordingWriter(input.RunId);
        WorkflowStepEvents.Emit(writer, _logger, "scribe", "started", "Scribe pass");

        ScribeAIAgent? agent = null;
        try
        {
            var task = $$"""
                You are Scribe. A project run has just completed.

                Run: {{input.RunId}}
                Project: {{input.ProjectId}}
                Agent: {{input.AgentName}}
                Run started at: {{input.RunStartedAt:O}}

                Your post-run tasks — use the memory API tools available to you:

                1. GET /api/projects/{{input.ProjectId}}/inbox?agent={{input.AgentName}}&status=pending
                2. For each `learning`, `pattern`, or `update` entry created since {{input.RunStartedAt:O}}:
                   POST /api/projects/{{input.ProjectId}}/inbox/{id}/merge
                3. Leave `architectural` and `scope` entries as pending (coordinator must review these).
                4. POST /api/projects/{{input.ProjectId}}/memory/export
                5. PUT /api/projects/{{input.ProjectId}}/sessions/current  — append a one-sentence summary of what {{input.AgentName}} accomplished.

                Complete this post-run Scribe pass now. Be systematic and concise.
                """;

            var charter = BuiltInCharterResolver.Resolve(input.RepositoryPath, "scribe") ?? FallbackCharter;

            agent = new ScribeAIAgent(
                _copilotClientFactory,
                _scopeProvider,
                _sandboxExecutor,
                _sandboxPolicyStore,
                _approvalStore,
                _toolApprovalGate,
                _loggerFactory.CreateLogger<CopilotAIAgent>());

            await agent.SetupAsync(
                workingDirectory: input.RepositoryPath,
                repositoryPath: input.RepositoryPath,
                runId: input.RunId + "-scribe",
                modelId: input.ModelId,
                systemPromptContext: charter,
                streamWriter: null,
                ct).ConfigureAwait(false);

            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            await agent.ExecuteStreamingLoopAsync(task, session, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Scribe agent turn failed for run {RunId} — workflow proceeds normally", input.RunId);
            WorkflowStepEvents.Emit(writer, _logger, "scribe", "failed", "Scribe pass");
        }
        finally
        {
            if (agent is not null)
                await agent.DisposeAsync().ConfigureAwait(false);
        }

        WorkflowStepEvents.Emit(writer, _logger, "scribe", "completed", "Scribe pass");
        return input;
    }
}
