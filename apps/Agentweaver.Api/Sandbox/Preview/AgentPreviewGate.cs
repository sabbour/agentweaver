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

/// <summary>Completed preview approval decision plus its durable request identity.</summary>
public sealed record PreviewApprovalResult(
    PreviewApprovalOutcome Outcome,
    string? RequestId,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// A newly armed approval request. The request identity is available immediately so retry endpoints
/// can return without waiting for the operator decision.
/// </summary>
public sealed record PreviewApprovalAttempt(
    string? RequestId,
    DateTimeOffset? ExpiresAt,
    Task<PreviewApprovalResult> Completion);

/// <summary>
/// Human-in-the-loop approval gate for the agent-initiated <c>start_preview</c> tool. A running
/// agent calls <c>start_preview(port)</c> which routes here: the request is auto-approved when an
/// auto-approve source is on, otherwise a <see cref="EventTypes.ToolApprovalRequired"/> card is
/// emitted onto the run stream and the call suspends on the shared <see cref="IToolApprovalGate"/>
/// until an operator grants it (POST /api/runs/{id}/tool-approvals) or the approval window times
/// out. Each project stores its own approval window (24 hours by default). The global
/// <c>Sandbox:Preview:ApprovalTimeoutMinutes</c> / <c>SANDBOX_PREVIEW_APPROVAL_TIMEOUT_MINUTES</c>
/// value remains the fallback for legacy/non-project runs.
///
/// <para>Auto-approve sources (any true ⇒ auto-grant, prod default is human-gated):</para>
/// <list type="number">
///   <item><c>Sandbox:Preview:AutoApprove</c> config / env <c>SANDBOX_PREVIEW_AUTO_APPROVE</c> (default false).</item>
///   <item>The run's immutable <c>auto_approve_tools</c> launch policy (default false).</item>
///   <item>An existing run/always-scoped policy on the shared approval gate.</item>
/// </list>
/// This is the seam that lets an automated demo run grant the preview unattended while production
/// stays human-gated.
/// </summary>
public sealed class AgentPreviewGate
{
    /// <summary>The tool name surfaced on HITL cards and approval-policy lookups.</summary>
    public const string ToolName = "start_preview";
    public const int DefaultApprovalTimeoutMinutes = 1440;
    private const int MinimumApprovalTimeoutMinutes = 1;
    private const int MaximumApprovalTimeoutMinutes = 1440;

    private readonly IToolApprovalGate _approvalGate;
    private readonly IRunOptionsStore _runOptions;
    private readonly RunStreamStore _streams;
    private readonly bool _autoApproveConfigured;
    private readonly TimeSpan _fallbackApprovalTimeout;
    private readonly IRunStore? _runStore;
    private readonly IProjectStore? _projectStore;
    private readonly ILogger<AgentPreviewGate> _logger;
    private readonly TimeSpan _completionGrace;

    /// <summary>
    /// Builds the preview approval gate, resolving the global auto-approve flag and approval
    /// timeout fallback from <c>Sandbox:Preview</c> configuration. Project-backed runs use their
    /// project setting; legacy/non-project runs use this fallback, which defaults to 30 minutes.
    /// </summary>
    public AgentPreviewGate(
        IToolApprovalGate approvalGate,
        IRunOptionsStore runOptions,
        RunStreamStore streams,
        IRunStore runStore,
        IProjectStore projectStore,
        IConfiguration configuration,
        ILogger<AgentPreviewGate> logger)
        : this(
            approvalGate,
            runOptions,
            streams,
            ResolveAutoApprove(configuration),
            logger,
            ResolveApprovalTimeout(configuration),
            runStore,
            projectStore)
    {
    }

    /// <summary>
    /// Test seam: inject the resolved auto-approve flag and timeout directly. When the timeout is
    /// omitted, the same 30-minute default is used.
    /// </summary>
    internal AgentPreviewGate(
        IToolApprovalGate approvalGate,
        IRunOptionsStore runOptions,
        RunStreamStore streams,
        bool autoApproveConfigured,
        ILogger<AgentPreviewGate> logger,
        TimeSpan? approvalTimeout = null,
        IRunStore? runStore = null,
        IProjectStore? projectStore = null,
        TimeSpan? completionGrace = null)
    {
        _approvalGate = approvalGate;
        _runOptions = runOptions;
        _streams = streams;
        _autoApproveConfigured = autoApproveConfigured;
        _logger = logger;
        _fallbackApprovalTimeout = approvalTimeout ?? TimeSpan.FromMinutes(DefaultApprovalTimeoutMinutes);
        _runStore = runStore;
        _projectStore = projectStore;
        _completionGrace = completionGrace ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Requests approval for exposing <paramref name="port"/> on <paramref name="runId"/>. Returns
    /// immediately as <see cref="PreviewApprovalOutcome.Approved"/> when auto-approved; otherwise
    /// emits a HITL card and suspends until an operator grants/denies or the timeout elapses.
    /// </summary>
    public async Task<PreviewApprovalResult> RequestApprovalAsync(
        string runId,
        int port,
        CancellationToken ct,
        int? workPlanId = null,
        string? treeHash = null)
    {
        var attempt = await BeginApprovalAsync(runId, port, ct, workPlanId, treeHash)
            .ConfigureAwait(false);
        return await attempt.Completion.ConfigureAwait(false);
    }

    /// <summary>
    /// Arms a fresh approval attempt and returns its request id immediately. A retry always receives
    /// a new request id; the prior request remains expired in the audit trail.
    /// </summary>
    public async Task<PreviewApprovalAttempt> BeginApprovalAsync(
        string runId,
        int port,
        CancellationToken ct,
        int? workPlanId = null,
        string? treeHash = null,
        string? retryOfRequestId = null)
    {
        var snapshot = await ResolvePolicySnapshotAsync(runId, ct).ConfigureAwait(false);
        var runPolicyApproved = IsRunPolicyApproved(runId, snapshot);
        if (_autoApproveConfigured
            || runPolicyApproved
            || _approvalGate.IsAutoApproved(runId, ToolName, null))
        {
            var approvalSource = runPolicyApproved
                ? "run_policy"
                : _autoApproveConfigured
                    ? "preview_configuration"
                    : "scoped_tool_policy";
            var decisionId = Guid.NewGuid().ToString("n");
            _logger.LogInformation(
                "start_preview auto-approved ({ApprovalSource}) — port={Port} runId={RunId} policySnapshotId={PolicySnapshotId}",
                approvalSource, port, runId, snapshot?.SnapshotId);
            _streams.Get(runId)?.RecordNext(EventTypes.ToolAutoApproved, new
            {
                decisionId,
                runId,
                toolName = ToolName,
                risk = ToolApprovalPolicySemantics.RiskFor(ToolName),
                approvalSource,
                policySnapshotId = snapshot?.SnapshotId,
                previewTarget = "run_sandbox",
                targetPort = port,
                workPlanId,
                treeHash,
                retryOfRequestId,
                decidedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            var retryRequestId = retryOfRequestId is null ? null : Guid.NewGuid().ToString("n");
            if (retryRequestId is not null)
            {
                _streams.Get(runId)?.RecordNext(EventTypes.SandboxPreviewPending, new
                {
                    run_id = runId,
                    work_plan_id = workPlanId,
                    tree_hash = treeHash,
                    target_port = port,
                    approval = "auto_approved",
                    request_id = retryRequestId,
                    retry_of_request_id = retryOfRequestId,
                    timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
                });
            }

            return new PreviewApprovalAttempt(
                retryRequestId,
                null,
                Task.FromResult(new PreviewApprovalResult(
                    PreviewApprovalOutcome.Approved,
                    retryRequestId,
                    null)));
        }

        var approvalTimeout = await ResolveApprovalTimeoutForRunAsync(runId, ct).ConfigureAwait(false);
        var requestId = Guid.NewGuid().ToString("n");
        var displayId = requestId[..8];
        var requestedAt = DateTimeOffset.UtcNow;
        var expiresAt = requestedAt.Add(approvalTimeout);

        // Register the gate BEFORE emitting the card so an immediate operator grant is not lost.
        var approvalTask = _approvalGate.WaitForApprovalAsync(
            runId, requestId, ToolName, $"sandbox-preview:{port}", approvalTimeout, ct);

        // Surface a HITL card on the run timeline so an operator can approve via
        // POST /api/runs/{runId}/tool-approvals with this request_id.
        _streams.Get(runId)?.RecordNext(EventTypes.ToolApprovalRequired, new
        {
            requestId,
            displayId,
            toolName = ToolName,
            url = $"sandbox-preview:{port}",
            message = $"The agent wants to expose a preview server on port {port}. Operator approval required.",
            requestedAt = requestedAt.ToString("O"),
            expiresAt = expiresAt.ToString("O"),
            timeoutMinutes = (int)approvalTimeout.TotalMinutes,
            retryOfRequestId,
            approvalPolicySnapshotId = snapshot?.SnapshotId,
        });
        _streams.Get(runId)?.RecordNext(EventTypes.SandboxPreviewPending, new
        {
            run_id = runId,
            work_plan_id = workPlanId,
            tree_hash = treeHash,
            target_port = port,
            approval = "pending",
            request_id = requestId,
            retry_of_request_id = retryOfRequestId,
            expires_at = expiresAt.ToString("O"),
            timeout_minutes = (int)approvalTimeout.TotalMinutes,
            approval_policy_snapshot_id = snapshot?.SnapshotId,
            timestamp_utc = requestedAt.ToString("O"),
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
            "start_preview HITL gate — waiting for operator approval: requestId={RequestId} port={Port} runId={RunId} approvalPolicySnapshotId={ApprovalPolicySnapshotId}",
            displayId,
            port,
            runId,
            snapshot?.SnapshotId);

        return new PreviewApprovalAttempt(
            requestId,
            expiresAt,
            CompleteAsync(runId, requestId, expiresAt, approvalTask));
    }

    private bool IsRunPolicyApproved(string runId, RunApprovalPolicySnapshot? snapshot) =>
        snapshot?.Policy.AllowsAutoApproval(ToolName)
        ?? (_runStore is null && _runOptions.GetLaunchPolicy(runId).AllowsAutoApproval(ToolName));

    private async Task<RunApprovalPolicySnapshot?> ResolvePolicySnapshotAsync(
        string runId,
        CancellationToken ct)
    {
        if (_runStore is null || !RunId.TryParse(runId, out var parsedRunId))
            return null;

        var run = await _runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false);
        var snapshot = run?.GetApprovalPolicySnapshot();
        if (snapshot is not null || string.IsNullOrWhiteSpace(run?.ParentRunId))
            return snapshot;

        return RunId.TryParse(run.ParentRunId, out var parentRunId)
            ? (await _runStore.GetAsync(parentRunId, ct).ConfigureAwait(false))?.GetApprovalPolicySnapshot()
            : null;
    }

    private async Task<PreviewApprovalResult> CompleteAsync(
        string runId,
        string requestId,
        DateTimeOffset expiresAt,
        Task<bool> approvalTask)
    {
        var remaining = expiresAt - DateTimeOffset.UtcNow + _completionGrace;
        if (remaining < _completionGrace)
            remaining = _completionGrace;

        bool approved;
        try
        {
            approved = await approvalTask.WaitAsync(remaining).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var terminalState = _approvalGate.GetRequestState(runId, requestId);
            if (terminalState is ToolApprovalRequestState.Approved
                or ToolApprovalRequestState.Denied
                or ToolApprovalRequestState.Expired)
            {
                return new PreviewApprovalResult(
                    terminalState == ToolApprovalRequestState.Approved
                        ? PreviewApprovalOutcome.Approved
                        : terminalState == ToolApprovalRequestState.Expired
                            ? PreviewApprovalOutcome.TimedOut
                            : PreviewApprovalOutcome.Denied,
                    requestId,
                    expiresAt);
            }

            _logger.LogError(
                "start_preview approval waiter exceeded its completion deadline: requestId={RequestId} runId={RunId}",
                requestId.Length >= 8 ? requestId[..8] : requestId,
                runId);
            EmitBackstopTimeout(runId, requestId);
            _ = approvalTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return new PreviewApprovalResult(PreviewApprovalOutcome.TimedOut, requestId, expiresAt);
        }

        var outcome = approved
            ? PreviewApprovalOutcome.Approved
            : _approvalGate.GetRequestState(runId, requestId) == ToolApprovalRequestState.Expired
                ? PreviewApprovalOutcome.TimedOut
                : PreviewApprovalOutcome.Denied;
        _logger.LogInformation(
            "start_preview approval completed: requestId={RequestId} runId={RunId} outcome={Outcome}",
            requestId.Length >= 8 ? requestId[..8] : requestId,
            runId,
            outcome);
        return new PreviewApprovalResult(outcome, requestId, expiresAt);
    }

    private void EmitBackstopTimeout(string runId, string requestId)
    {
        var stream = _streams.Get(runId);
        if (stream is null || stream.GetSnapshotSince(0).Events.Any(evt =>
            {
                if (evt.Type != EventTypes.ToolApprovalResolved)
                    return false;
                var payload = System.Text.Json.JsonSerializer.SerializeToElement(evt.Payload);
                return payload.TryGetProperty("requestId", out var persistedRequestId)
                    && string.Equals(persistedRequestId.GetString(), requestId, StringComparison.Ordinal);
            }))
        {
            return;
        }

        stream.RecordNext(EventTypes.ToolApprovalResolved, new
        {
            requestId,
            runId,
            approved = false,
            expired = true,
            reason = "approval_waiter_timeout",
        });
    }

    internal async Task<TimeSpan> ResolveApprovalTimeoutForRunAsync(string runId, CancellationToken ct)
    {
        if (_runStore is null || _projectStore is null || !RunId.TryParse(runId, out var parsedRunId))
            return _fallbackApprovalTimeout;

        var run = await _runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false);
        if (run?.ProjectId is null)
            return _fallbackApprovalTimeout;

        var project = await _projectStore.GetAsync(run.ProjectId.Value, ct).ConfigureAwait(false);
        var minutes = project?.PreviewApprovalTimeoutMinutes ?? (int)_fallbackApprovalTimeout.TotalMinutes;
        return TimeSpan.FromMinutes(Math.Clamp(
            minutes,
            MinimumApprovalTimeoutMinutes,
            MaximumApprovalTimeoutMinutes));
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
    /// invalid values default to 24 hours; values clamp to the supported project range.
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

        return TimeSpan.FromMinutes(Math.Clamp(
            minutes,
            MinimumApprovalTimeoutMinutes,
            MaximumApprovalTimeoutMinutes));
    }
}
