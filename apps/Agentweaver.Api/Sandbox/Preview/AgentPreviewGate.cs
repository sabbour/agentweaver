using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Api.Sandbox.Preview;

/// <summary>Outcome of an agent-initiated preview approval request.</summary>
public enum PreviewApprovalOutcome
{
    /// <summary>The preview was approved (auto-approved or granted by an operator).</summary>
    Approved,

    /// <summary>The preview was denied by an operator.</summary>
    Denied,

    /// <summary>The approval window timed out.</summary>
    TimedOut,
}

/// <summary>
/// Human-in-the-loop approval gate for the agent-initiated <c>start_preview</c> tool. A running
/// agent calls <c>start_preview(port)</c> which routes here: the request is auto-approved when an
/// auto-approve source is on, otherwise a <see cref="EventTypes.ToolApprovalRequired"/> card is
/// emitted onto the run stream and the call suspends on the shared <see cref="IToolApprovalGate"/>
/// until an operator grants it (POST /api/runs/{id}/tool-approvals) or the approval window times
/// out. The wait window is configurable via <c>Sandbox:Preview:ApprovalTimeoutMinutes</c> / env
/// <c>SANDBOX_PREVIEW_APPROVAL_TIMEOUT_MINUTES</c>; missing or invalid values fall back to 15
/// minutes and non-positive values clamp to 1 minute.
///
/// <para>Auto-approve sources (any true ⇒ auto-grant, prod default is human-gated):</para>
/// <list type="number">
///   <item><c>Sandbox:Preview:AutoApprove</c> config / env <c>SANDBOX_PREVIEW_AUTO_APPROVE</c> (default false).</item>
///   <item>Per-run <see cref="RunOptions.AutoApproveTools"/> (operator live toggle).</item>
///   <item>An existing run/always-scoped policy on the shared approval gate.</item>
/// </list>
/// This is the seam that lets an automated demo run grant the preview unattended while production
/// stays human-gated.
/// </summary>
public sealed class AgentPreviewGate
{
    /// <summary>The tool name surfaced on HITL cards and approval-policy lookups.</summary>
    public const string ToolName = "start_preview";
    private const int DefaultApprovalTimeoutMinutes = 15;
    private const int MinimumApprovalTimeoutMinutes = 1;

    private readonly IToolApprovalGate _approvalGate;
    private readonly IRunOptionsStore _runOptions;
    private readonly RunStreamStore _streams;
    private readonly bool _autoApproveConfigured;
    private readonly TimeSpan _approvalTimeout;
    private readonly ILogger<AgentPreviewGate> _logger;

    /// <summary>
    /// Builds the preview approval gate, resolving the global auto-approve flag and approval
    /// timeout from <c>Sandbox:Preview</c> configuration. The approval timeout defaults to 15
    /// minutes, falls back to the env var helper name, and clamps configured non-positive values
    /// to 1 minute.
    /// </summary>
    public AgentPreviewGate(
        IToolApprovalGate approvalGate,
        IRunOptionsStore runOptions,
        RunStreamStore streams,
        IConfiguration configuration,
        ILogger<AgentPreviewGate> logger)
        : this(
            approvalGate,
            runOptions,
            streams,
            ResolveAutoApprove(configuration),
            logger,
            ResolveApprovalTimeout(configuration))
    {
    }

    /// <summary>
    /// Test seam: inject the resolved auto-approve flag and timeout directly. When the timeout is
    /// omitted, the same 15-minute default is used.
    /// </summary>
    internal AgentPreviewGate(
        IToolApprovalGate approvalGate,
        IRunOptionsStore runOptions,
        RunStreamStore streams,
        bool autoApproveConfigured,
        ILogger<AgentPreviewGate> logger,
        TimeSpan? approvalTimeout = null)
    {
        _approvalGate = approvalGate;
        _runOptions = runOptions;
        _streams = streams;
        _autoApproveConfigured = autoApproveConfigured;
        _logger = logger;
        _approvalTimeout = approvalTimeout ?? TimeSpan.FromMinutes(DefaultApprovalTimeoutMinutes);
    }

    /// <summary>
    /// Returns true if the preview should be granted without an operator: the global config/env
    /// flag, the per-run auto-approve-tools option, or an existing scoped allow policy.
    /// </summary>
    public bool IsAutoApproved(string runId) =>
        _autoApproveConfigured
        || _runOptions.Get(runId).AutoApproveTools
        || _approvalGate.IsAutoApproved(runId, ToolName, null);

    /// <summary>
    /// Requests approval for exposing <paramref name="port"/> on <paramref name="runId"/>. Returns
    /// immediately as <see cref="PreviewApprovalOutcome.Approved"/> when auto-approved; otherwise
    /// emits a HITL card and suspends until an operator grants/denies or the timeout elapses.
    /// </summary>
    public async Task<PreviewApprovalOutcome> RequestApprovalAsync(
        string runId,
        int port,
        CancellationToken ct,
        int? workPlanId = null,
        string? treeHash = null)
    {
        if (IsAutoApproved(runId))
        {
            _logger.LogInformation(
                "start_preview auto-approved (config/run-option/policy) — port={Port} runId={RunId}", port, runId);
            return PreviewApprovalOutcome.Approved;
        }

        var requestId = Guid.NewGuid().ToString("n");
        var displayId = requestId[..8];

        // Register the gate BEFORE emitting the card so an immediate operator grant is not lost.
        var approvalTask = _approvalGate.WaitForApprovalAsync(
            runId, requestId, ToolName, $"sandbox-preview:{port}", _approvalTimeout, ct);

        // Surface a HITL card on the run timeline so an operator can approve via
        // POST /api/runs/{runId}/tool-approvals with this request_id.
        _streams.Get(runId)?.RecordNext(EventTypes.ToolApprovalRequired, new
        {
            requestId,
            displayId,
            toolName = ToolName,
            url = $"sandbox-preview:{port}",
            message = $"The agent wants to expose a preview server on port {port}. Operator approval required.",
        });
        _streams.Get(runId)?.RecordNext(EventTypes.SandboxPreviewPending, new
        {
            run_id = runId,
            work_plan_id = workPlanId,
            tree_hash = treeHash,
            target_port = port,
            approval = "pending",
            request_id = requestId,
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
        });
        _streams.Get(runId)?.RecordNext(EventTypes.WorkflowStep, new
        {
            step = "preview",
            status = "pending",
            label = "Preview",
            message = "Waiting for preview approval.",
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
        });

        _logger.LogInformation(
            "start_preview HITL gate — waiting for operator approval: requestId={RequestId} port={Port} runId={RunId}",
            displayId, port, runId);

        var approved = await approvalTask.ConfigureAwait(false);
        if (approved)
            return PreviewApprovalOutcome.Approved;

        return _approvalGate.GetRequestState(runId, requestId) == ToolApprovalRequestState.Expired
            ? PreviewApprovalOutcome.TimedOut
            : PreviewApprovalOutcome.Denied;
    }

    /// <summary>
    /// Resolves the global auto-approve flag from <c>Sandbox:Preview:AutoApprove</c> or the
    /// <c>SANDBOX_PREVIEW_AUTO_APPROVE</c> environment variable (so the exact env name works even
    /// though it does not use the ASP.NET <c>__</c> hierarchy separator). Default false.
    /// </summary>
    internal static bool ResolveAutoApprove(IConfiguration configuration) =>
        ParseBool(configuration["Sandbox:Preview:AutoApprove"])
        || ParseBool(Environment.GetEnvironmentVariable("SANDBOX_PREVIEW_AUTO_APPROVE"));

    /// <summary>
    /// Resolves the preview approval timeout from <c>Sandbox:Preview:ApprovalTimeoutMinutes</c> or
    /// the <c>SANDBOX_PREVIEW_APPROVAL_TIMEOUT_MINUTES</c> environment variable. Missing or
    /// invalid values default to 15 minutes; non-positive values clamp to 1 minute.
    /// </summary>
    internal static TimeSpan ResolveApprovalTimeout(IConfiguration configuration) =>
        ResolveApprovalTimeoutMinutes(configuration["Sandbox:Preview:ApprovalTimeoutMinutes"])
        ?? ResolveApprovalTimeoutMinutes(Environment.GetEnvironmentVariable("SANDBOX_PREVIEW_APPROVAL_TIMEOUT_MINUTES"))
        ?? TimeSpan.FromMinutes(DefaultApprovalTimeoutMinutes);

    private static bool ParseBool(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.Ordinal)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static TimeSpan? ResolveApprovalTimeoutMinutes(string? value)
    {
        if (!int.TryParse(value, out var minutes))
            return null;

        return TimeSpan.FromMinutes(Math.Max(MinimumApprovalTimeoutMinutes, minutes));
    }
}
