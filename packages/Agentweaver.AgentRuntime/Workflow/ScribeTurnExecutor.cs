using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Runs Scribe as a real agent turn after a project run completes.
/// Scribe receives a structured task and may inspect the inbox. Durable mutation is
/// performed afterward by the server-side Scribe finalizer.
/// Its charter is read dynamically from <c>.squad/agents/scribe/charter.md</c>.
/// Failures propagate so the owning Scribe child attempt is failed truthfully.
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
        Scribe is read-only. Durable decision, memory, session, history, inbox, and export
        housekeeping is owned by the server-side finalizer after the model turn succeeds.
        """;


    private readonly GitHubCopilotClientFactory _copilotClientFactory;
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
    private readonly Func<ScribeTurnInput, bool, CancellationToken, Task>? _finalizeHousekeeping;

    public ScribeTurnExecutor(
        GitHubCopilotClientFactory copilotClientFactory,
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
        IWorkflowAgentFactory? agentFactory = null,
        Func<ScribeTurnInput, bool, CancellationToken, Task>? finalizeHousekeeping = null)
        : base(name)
    {
        _copilotClientFactory = copilotClientFactory;
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
        _finalizeHousekeeping = finalizeHousekeeping;
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

        // ProjectId present but AgentName missing: preserve the legacy fallback identity while the
        // server-side finalizer still validates the persisted run scope.
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
        try
        {
            var isCoordinator = string.Equals(input.AgentName, "coordinator", StringComparison.OrdinalIgnoreCase);

            var reviewStep = isCoordinator
                ? "2. Review every entry and identify durable team boundaries in your summary."
                : "2. Review learning, pattern, and update entries; leave architectural and scope authority to the Coordinator.";

            var task = $$"""
                You are Scribe. A project run has reached terminal state: {{input.TerminalStatus ?? "completed"}}.

                Run: {{input.RunId}}
                Project: {{input.ProjectId}}
                Agent: {{input.AgentName}}
                Run started at: {{input.RunStartedAt:O}}

                Complete these read-only post-run steps:

                1. Call list_inbox(forAgent: "{{input.AgentName}}") to see pending entries.
                {{reviewStep}}
                3. Return one concise sentence describing the terminal state and what {{input.AgentName}} accomplished or attempted.

                Do not mutate state, write code, or read project files.
                """;

            var charter = (BuiltInCharterResolver.Resolve(input.RepositoryPath, "scribe") ?? FallbackCharter)
                          + MemoryToolsRuntimeNote;

            agent = _agentFactory?.CreateScribeAgent()
                ?? EphemeralCopilotAIAgent.CreateScribe(
                    _copilotClientFactory,
                    _sandboxExecutor,
                    _sandboxPolicyStore,
                    _approvalStore,
                    _toolApprovalGate,
                    _loggerFactory.CreateLogger<CopilotAIAgent>());
            if (agent is IProviderBoundWorkflowTurnAgent providerBoundAgent
                && !string.IsNullOrWhiteSpace(input.ModelSource))
            {
                providerBoundAgent.ConfigureProviderBoundary(
                    ModelSourceExtensions.FromApiString(input.ModelSource),
                    input.ByokProviderFingerprint);
            }

            await agent.SetupAsync(
                workingDirectory: input.RepositoryPath,
                repositoryPath: input.RepositoryPath,
                // The coordinator's AgentHost endpoint is keyed by the parent run; the
                // "-scribe" suffix is only the event stream identity.
                runId: input.RunId,
                modelId: input.ModelId,
                systemPromptContext: charter,
                streamWriter: subWriter,
                projectId: input.ProjectId,
                agentName: input.AgentName,
                apiBaseUrl: _apiBaseUrl,
                apiKey: _apiKey,
                ct,
                input.SubmittingUser).ConfigureAwait(false);

            await agent.RunTurnAsync(task, isRevision: false, ct).ConfigureAwait(false);
            if (_finalizeHousekeeping is null)
                throw new InvalidOperationException("Scribe housekeeping finalizer is unavailable.");
            await _finalizeHousekeeping(input, isCoordinator, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var diagnostic = Classify(ex);
            _logger.LogWarning(
                "Scribe agent turn failed for run {RunId}; code={FailureCode}; retryable={Retryable}",
                input.RunId, diagnostic.Code, diagnostic.Retryable);
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, "scribe", "failed", "Scribe pass");
            writer?.TryWrite(new RunEvent(0, "run.scribe_failed", new
            {
                code = diagnostic.Code,
                retryable = diagnostic.Retryable,
                timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
            }));
            throw new ScribeTurnException(diagnostic.Code, diagnostic.Retryable);
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

    private static (string Code, bool Retryable) Classify(Exception exception) =>
        exception switch
        {
            OperationCanceledException => ("scribe_timeout", true),
            HttpRequestException => ("scribe_transport_failure", true),
            AgentProviderException provider => ("scribe_provider_failure", provider.IsRetryable),
            WorkflowAgentInfrastructureException infrastructure =>
                ("scribe_infrastructure_failure", infrastructure.IsRetryable ?? false),
            UnauthorizedAccessException => ("scribe_authorization_failure", false),
            _ => ("scribe_internal_failure", false),
        };
}

public sealed class ScribeTurnException(
    string code,
    bool retryable)
    : Exception("Scribe execution failed.")
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
