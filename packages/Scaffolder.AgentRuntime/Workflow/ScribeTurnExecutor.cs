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

    /// <summary>
    /// Appended to every Scribe charter (including imported repos whose charter predates this
    /// feature) so Scribe always knows which native tools are available for the memory pass.
    /// </summary>
    private const string MemoryToolsRuntimeNote =
        """

        ## Runtime Memory Tools

        The following native tools are available for the post-run memory pass:
        - list_inbox(forAgent?) — list pending inbox entries (returns JSON)
        - merge_inbox_entry(entryId) — merge a learning/pattern/update entry by its numeric id
        - update_session(summary) — record a one-sentence summary of what the agent accomplished
        - export_memory() — write the updated state to .squad/ and .agentweaver/context/

        Only merge entries of type: learning, pattern, update.
        Leave architectural and scope entries for coordinator review.
        """;


    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ISandboxExecutor _sandboxExecutor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly IToolApprovalGate _toolApprovalGate;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ScribeTurnExecutor> _logger;
    private readonly Func<string, ChannelWriter<RunEvent>?> _getRecordingWriter;
    private readonly Func<string, string, ChannelWriter<RunEvent>>? _createSubStream;
    private readonly Action<string>? _completeSubStream;
    private readonly string? _apiBaseUrl;
    private readonly string? _apiKey;

    public ScribeTurnExecutor(
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor sandboxExecutor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILoggerFactory loggerFactory,
        Func<string, ChannelWriter<RunEvent>?>? getRecordingWriter = null,
        string name = "scribe-turn",
        Func<string, string, ChannelWriter<RunEvent>>? createSubStream = null,
        Action<string>? completeSubStream = null,
        string? apiBaseUrl = null,
        string? apiKey = null)
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
        _createSubStream = createSubStream;
        _completeSubStream = completeSubStream;
        _apiBaseUrl = apiBaseUrl;
        _apiKey = apiKey;
    }

    public override async ValueTask<ScribeTurnInput> HandleAsync(
        ScribeTurnInput input, IWorkflowContext context, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(input.ProjectId) || string.IsNullOrEmpty(input.AgentName))
        {
            _logger.LogWarning(
                "Scribe skipped for run {RunId} — missing context: ProjectId='{ProjectId}' AgentName='{AgentName}'",
                input.RunId,
                string.IsNullOrEmpty(input.ProjectId) ? "<empty>" : input.ProjectId,
                string.IsNullOrEmpty(input.AgentName) ? "<empty>" : input.AgentName);
            WorkflowStepEvents.Emit(_getRecordingWriter(input.RunId), _logger, input.RunId, "scribe", "skipped", "Scribe pass");
            return input;
        }

        var writer = _getRecordingWriter(input.RunId);
        WorkflowStepEvents.Emit(writer, _logger, input.RunId, "scribe", "started", "Scribe pass");

        var subRunId = input.RunId + "-scribe";
        var subWriter = _createSubStream?.Invoke(subRunId, "scribe");

        ScribeAIAgent? agent = null;
        try
        {
            var task = $$"""
                You are Scribe. A project run has just completed.

                Run: {{input.RunId}}
                Project: {{input.ProjectId}}
                Agent: {{input.AgentName}}
                Run started at: {{input.RunStartedAt:O}}

                Complete these post-run steps using the native memory tools available to you:

                1. Call list_inbox(forAgent: "{{input.AgentName}}") to see pending entries.
                2. For each entry of type learning, pattern, or update: call merge_inbox_entry(entryId).
                   Skip entries of type architectural or scope — leave them for coordinator review.
                3. Call update_session(summary: one sentence describing what {{input.AgentName}} accomplished).
                4. Call export_memory() to write the updated memory state to .squad/.

                Be systematic and concise. Do not write code or read project files.
                """;

            var charter = (BuiltInCharterResolver.Resolve(input.RepositoryPath, "scribe") ?? FallbackCharter)
                          + MemoryToolsRuntimeNote;

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
                runId: subRunId,
                modelId: input.ModelId,
                systemPromptContext: charter,
                streamWriter: subWriter,
                projectId: input.ProjectId,
                agentName: input.AgentName,
                apiBaseUrl: _apiBaseUrl,
                apiKey: _apiKey,
                ct).ConfigureAwait(false);

            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            await agent.ExecuteStreamingLoopAsync(task, session, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Scribe agent turn failed for run {RunId} — workflow proceeds normally", input.RunId);
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, "scribe", "failed", "Scribe pass");
        }
        finally
        {
            if (agent is not null)
                await agent.DisposeAsync().ConfigureAwait(false);
            _completeSubStream?.Invoke(subRunId);
        }

        WorkflowStepEvents.Emit(writer, _logger, input.RunId, "scribe", "completed", "Scribe pass");
        return input;
    }
}
