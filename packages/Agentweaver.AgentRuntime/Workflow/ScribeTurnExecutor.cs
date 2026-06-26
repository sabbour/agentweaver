using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Runs Scribe as a real agent turn after a project run completes.
/// Scribe receives a structured task and uses memory API tools to review
/// the inbox, merge learnings, and export to .squad/.
/// Its charter is read dynamically from <c>.squad/agents/scribe/charter.md</c>.
/// Best-effort: exceptions never abort the workflow.
/// </summary>
public sealed class ScribeTurnExecutor : Executor<ScribeTurnInput, ScribeTurnInput>, IWorkflowNodeMeta
{
    /// <inheritdoc />
    public string LogicalNodeId => "scribe";
    /// <inheritdoc />
    public string DisplayLabel => "Scribe";
    /// <inheritdoc />
    public string Role => "scribe";
    /// <inheritdoc />
    public string NodeType => "agent";
    /// <inheritdoc />
    public bool Hidden => false;
    /// <inheritdoc />
    public string NodeKind => "live";

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
        Architectural and scope entries are promoted by the Coordinator during finalization,
        not by a per-run Scribe pass.
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
    private readonly IWorkflowAgentFactory? _agentFactory;

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
        string? apiKey = null,
        IWorkflowAgentFactory? agentFactory = null)
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
        _agentFactory = agentFactory;
    }

    public override async ValueTask<ScribeTurnInput> HandleAsync(
        ScribeTurnInput input, IWorkflowContext context, CancellationToken ct)
    {
        // Skip only when both fields are missing — truly no context to act on.
        if (string.IsNullOrEmpty(input.ProjectId) && string.IsNullOrEmpty(input.AgentName))
        {
            _logger.LogWarning(
                "Scribe skipped for run {RunId} — both ProjectId and AgentName are empty",
                input.RunId);
            WorkflowStepEvents.Emit(_getRecordingWriter(input.RunId), _logger, input.RunId, "scribe", "skipped", "Scribe pass");
            return input;
        }

        // ProjectId present but AgentName missing: proceed with "unknown" so update_session
        // and export_memory still run and the session record is not lost.
        if (string.IsNullOrEmpty(input.AgentName))
        {
            _logger.LogWarning(
                "Scribe run {RunId} — AgentName missing, proceeding with 'unknown'; ProjectId='{ProjectId}'",
                input.RunId, input.ProjectId);
            input = input with { AgentName = "unknown" };
        }

        if (string.IsNullOrEmpty(input.ProjectId))
        {
            _logger.LogWarning(
                "Scribe run {RunId} — ProjectId missing, AgentName='{AgentName}'; proceeding without project context",
                input.RunId, input.AgentName);
        }

        var writer = _getRecordingWriter(input.RunId);
        WorkflowStepEvents.Emit(writer, _logger, input.RunId, "scribe", "started", "Scribe pass");

        var subRunId = input.RunId + "-scribe";
        var subWriter = _createSubStream?.Invoke(subRunId, "scribe");

        IWorkflowTurnAgent? agent = null;
        var scribeFailed = false;
        try
        {
            var isCoordinator = string.Equals(input.AgentName, "coordinator", StringComparison.OrdinalIgnoreCase);

            // The Coordinator owns architectural/scope promotion: as part of finalization it reviews
            // those pending entries and merges the ones it endorses. A per-run worker Scribe leaves
            // them untouched. A deterministic backstop promotes any the model misses.
            var reviewStep = isCoordinator
                ? "2. Review every entry. For type learning, pattern, or update: call merge_inbox_entry(entryId).\n"
                  + "                   For type architectural or scope: call merge_inbox_entry(entryId) for the ones you endorse as a team boundary."
                : "2. For each entry of type learning, pattern, or update: call merge_inbox_entry(entryId).\n"
                  + "                   Leave architectural and scope entries for the Coordinator to promote.";

            var task = $$"""
                You are Scribe. A project run has reached terminal state: {{input.TerminalStatus ?? "completed"}}.

                Run: {{input.RunId}}
                Project: {{input.ProjectId}}
                Agent: {{input.AgentName}}
                Run started at: {{input.RunStartedAt:O}}

                Complete these post-run steps using the native memory tools available to you:

                1. Call list_inbox(forAgent: "{{input.AgentName}}") to see pending entries.
                {{reviewStep}}
                3. Call update_session(summary: one sentence describing the run terminal state ({{input.TerminalStatus ?? "completed"}}) and what {{input.AgentName}} accomplished or attempted).
                4. Call export_memory() to write the updated memory state to .squad/.

                Be systematic and concise. Do not write code or read project files.
                """;

            var charter = (BuiltInCharterResolver.Resolve(input.RepositoryPath, "scribe") ?? FallbackCharter)
                          + MemoryToolsRuntimeNote;

            agent = _agentFactory?.CreateScribeAgent()
                ?? new ScribeAIAgent(
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

            await agent.RunTurnAsync(task, isRevision: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            scribeFailed = true;
            _logger.LogWarning(ex,
                "Scribe agent turn failed for run {RunId} — workflow proceeds normally", input.RunId);
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, "scribe", "failed", "Scribe pass");
            writer?.TryWrite(new RunEvent(0, "run.scribe_failed", new
            {
                reason = ex.Message,
                timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
            }));
        }
        finally
        {
            if (agent is not null)
                await agent.DisposeAsync().ConfigureAwait(false);
            _completeSubStream?.Invoke(subRunId);
        }

        if (scribeFailed)
            return input with { TerminalStatus = "scribe_failed", MergeResult = "scribe_failed" };

        WorkflowStepEvents.Emit(writer, _logger, input.RunId, "scribe", "completed", "Scribe pass");
        return input;
    }
}
