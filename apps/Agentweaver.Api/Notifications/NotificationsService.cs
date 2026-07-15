using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Notifications;

/// <summary>
/// Aggregates the signed-in user's pending Human Review + Tool Approval requests across every
/// project/run they own, for the global notification center (#247).
///
/// DELIVERY CHOICE (issue open question #2): polling over a new SSE stream. <c>GET /api/notifications</c>
/// is a plain DB-backed read (same shape as <c>DashboardReadService.GetOverviewAsync</c>), so it is
/// trivially correct across api/worker pod restarts and replicas — there is no per-connection state to
/// reconnect or replay. A dedicated <c>/api/notifications/stream</c> SSE endpoint would need its own
/// fan-out/broadcast plumbing (today's SSE infra in <see cref="Endpoints.RunEndpoints"/> is run-scoped,
/// not user-scoped) for a marginal latency win over a short poll interval. Given the MVP guidance to
/// prefer the less invasive option, polling wins; a follow-up can add push delivery once a user-scoped
/// event bus exists (tracked as a fast-follow below).
///
/// SCOPE: Human Review is derivable straight from the durable `runs` table (`status =
/// awaiting_review`), which already survives pod restarts with no extra plumbing. Tool Approval
/// (#321) is sourced from the SAME signal <see cref="PendingToolApprovalRunsQuery"/> uses for the
/// board's "Approval needed" badge (<c>tool.approval_required</c> run events with no matching
/// <c>tool.result</c>/<c>tool.error</c> callId yet) rather than a parallel detection mechanism —
/// candidate runs are the caller's owned, non-archived, in-progress runs.
/// </summary>
public sealed class NotificationsService
{
    private readonly IRunStore _runStore;
    private readonly IProjectStore _projectStore;
    private readonly PendingToolApprovalRunsQuery _pendingApprovalQuery;

    public NotificationsService(
        IRunStore runStore,
        IProjectStore projectStore,
        PendingToolApprovalRunsQuery pendingApprovalQuery)
    {
        _runStore = runStore;
        _projectStore = projectStore;
        _pendingApprovalQuery = pendingApprovalQuery;
    }

    public async Task<NotificationsResponseDto> GetPendingAsync(CallerContext caller, CancellationToken ct = default)
    {
        var ownedProjectNames = (await _projectStore.ListAsync(ct).ConfigureAwait(false))
            .Where(project => caller.Owns(project.Owner))
            .ToDictionary(project => project.Id.ToString(), project => project.Name, StringComparer.Ordinal);

        var awaitingReview = await _runStore.GetByStatusAsync(RunStatus.AwaitingReview, ct).ConfigureAwait(false);

        var ownedAwaitingReview = awaitingReview
            .Where(run => run.ArchivedAt is null && run.ProjectId is not null)
            .Where(run => ownedProjectNames.ContainsKey(run.ProjectId!.ToString()!))
            .ToList();

        // Tool approval gates fire mid-execution, so the candidate pool is the caller's owned,
        // non-archived InProgress runs (mirrors the Human Review candidate pool above, just against
        // a different RunStatus — the pending-approval signal itself is never derivable from status
        // alone, hence the PendingToolApprovalRunsQuery lookup).
        var inProgress = await _runStore.GetByStatusAsync(RunStatus.InProgress, ct).ConfigureAwait(false);
        var ownedInProgress = inProgress
            .Where(run => run.ArchivedAt is null && run.ProjectId is not null)
            .Where(run => ownedProjectNames.ContainsKey(run.ProjectId!.ToString()!))
            .ToList();

        var pendingApprovals = await _pendingApprovalQuery
            .GetPendingApprovalDetailsAsync(ownedInProgress.Select(run => run.Id.ToString()).ToList(), ct)
            .ConfigureAwait(false);

        var notifications = ownedAwaitingReview
            .Select(run => ToHumanReviewNotification(run, ownedProjectNames))
            .Concat(ownedInProgress
                .Where(run => pendingApprovals.ContainsKey(run.Id.ToString()))
                .Select(run => ToToolApprovalNotification(run, pendingApprovals[run.Id.ToString()], ownedProjectNames)))
            .OrderByDescending(notification => notification.CreatedUtc)
            .ToList();

        return new NotificationsResponseDto
        {
            GeneratedUtc = DateTimeOffset.UtcNow,
            Notifications = notifications,
        };
    }

    private static NotificationDto ToHumanReviewNotification(
        Run run,
        IReadOnlyDictionary<string, string> ownedProjectNames)
    {
        var projectId = run.ProjectId!.ToString()!;
        // Mirrors the frontend's own fallback (ProjectPage.tsx): workflow_run_id when present,
        // otherwise the execution id, so the CTA always lands on a resolvable orchestration route.
        var deepLinkRunId = run.WorkflowRunId ?? run.Id.ToString();
        var title = string.IsNullOrWhiteSpace(run.Task)
            ? "A run is awaiting your review"
            : Truncate(run.Task, 120);

        return new NotificationDto
        {
            Id = $"review:{run.Id}",
            Type = "human_review",
            RunId = run.Id.ToString(),
            ProjectId = projectId,
            ProjectName = ownedProjectNames.GetValueOrDefault(projectId, "Unknown project"),
            AgentName = run.AgentName,
            Title = title,
            CreatedUtc = run.EndedAt ?? run.StartedAt,
            CtaPath = $"/projects/{projectId}/orchestrations/{deepLinkRunId}",
        };
    }

    private static NotificationDto ToToolApprovalNotification(
        Run run,
        PendingToolApproval approval,
        IReadOnlyDictionary<string, string> ownedProjectNames)
    {
        var projectId = run.ProjectId!.ToString()!;
        // Same CTA deep-link convention as Human Review — the board's RunCard navigates a pending
        // tool-approval run to the same orchestration route (workflow_run_id when present, otherwise
        // the execution id); the approval UI itself lives inline on that route.
        var deepLinkRunId = run.WorkflowRunId ?? run.Id.ToString();
        var title = string.IsNullOrWhiteSpace(approval.ToolName)
            ? "A run needs tool approval"
            : Truncate($"Approval needed to run \"{approval.ToolName}\"", 120);

        return new NotificationDto
        {
            Id = $"tool_approval:{run.Id}:{approval.RequestId}",
            Type = "tool_approval",
            RunId = run.Id.ToString(),
            ProjectId = projectId,
            ProjectName = ownedProjectNames.GetValueOrDefault(projectId, "Unknown project"),
            AgentName = run.AgentName,
            Title = title,
            CreatedUtc = approval.CreatedUtc,
            CtaPath = $"/projects/{projectId}/orchestrations/{deepLinkRunId}",
        };
    }

    private static string Truncate(string value, int max)
    {
        value = value.Trim();
        return value.Length <= max ? value : value[..max].TrimEnd() + "\u2026";
    }
}

