using Agentweaver.Api.Infrastructure;
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
/// SCOPE (issue open question #5): Human Review is fully covered here — it is derivable straight from
/// the durable `runs` table (`status = awaiting_review`), which already survives pod restarts with no
/// extra plumbing. Tool Approval is NOT included in this MVP:
/// <see cref="IToolApprovalGate"/> only exposes single-request lookups
/// (<c>GetRequestState(runId, requestId)</c>) and has no "list all pending approvals" query; the
/// in-memory implementation is per-pod, and even the durable implementation
/// (<see cref="Endpoints.RunEndpoints"/> / DurableToolApprovalGate) is keyed by run+request, not
/// enumerable by owner. Building a durable, owner-queryable "all my pending tool approvals" index is a
/// real fast-follow (naturally pairs with the durable in-flight-state work referenced in #246), not a
/// quick addition here — so it is explicitly deferred rather than bolted on unsafely.
/// </summary>
public sealed class NotificationsService
{
    private readonly IRunStore _runStore;
    private readonly IProjectStore _projectStore;

    public NotificationsService(IRunStore runStore, IProjectStore projectStore)
    {
        _runStore = runStore;
        _projectStore = projectStore;
    }

    public async Task<NotificationsResponseDto> GetPendingAsync(CallerContext caller, CancellationToken ct = default)
    {
        var ownedProjectNames = (await _projectStore.ListAsync(ct).ConfigureAwait(false))
            .Where(project => caller.Owns(project.Owner))
            .ToDictionary(project => project.Id.ToString(), project => project.Name, StringComparer.Ordinal);

        var awaitingReview = await _runStore.GetByStatusAsync(RunStatus.AwaitingReview, ct).ConfigureAwait(false);

        var notifications = awaitingReview
            .Where(run => run.ArchivedAt is null && run.ProjectId is not null)
            .Where(run => ownedProjectNames.ContainsKey(run.ProjectId!.ToString()!))
            .Select(run => ToHumanReviewNotification(run, ownedProjectNames))
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

    private static string Truncate(string value, int max)
    {
        value = value.Trim();
        return value.Length <= max ? value : value[..max].TrimEnd() + "\u2026";
    }
}
