using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

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
    private readonly IProjectRoleAuthorizationService _projectRoles;
    private readonly PendingToolApprovalRunsQuery _pendingApprovalQuery;
    private readonly IBacklogTaskStore _backlogStore;
    private readonly MemoryDbContext _db;
    private readonly AuthMode _authMode;

    public NotificationsService(
        IRunStore runStore,
        IProjectStore projectStore,
        IProjectRoleAuthorizationService projectRoles,
        PendingToolApprovalRunsQuery pendingApprovalQuery,
        IBacklogTaskStore backlogStore,
        MemoryDbContext db,
        IConfiguration configuration)
    {
        _runStore = runStore;
        _projectStore = projectStore;
        _projectRoles = projectRoles;
        _pendingApprovalQuery = pendingApprovalQuery;
        _backlogStore = backlogStore;
        _db = db;
        _authMode = AuthModeResolver.Resolve(configuration);
    }

    public async Task<NotificationsResponseDto> GetPendingAsync(CallerContext caller, CancellationToken ct = default)
    {
        var ownedProjectNames = (await ListVisibleProjectsAsync(caller, ct).ConfigureAwait(false))
            .ToDictionary(project => project.Id.ToString(), project => project.Name, StringComparer.Ordinal);

        var awaitingReview = await _runStore.GetByStatusAsync(RunStatus.AwaitingReview, ct).ConfigureAwait(false);

        var ownedAwaitingReview = awaitingReview
            .Where(run => run.ArchivedAt is null && run.ProjectId is not null)
            .Where(run => ownedProjectNames.ContainsKey(run.ProjectId!.ToString()!))
            .ToList();
        var ownedAwaitingReviewIds = ownedAwaitingReview.Select(run => run.Id.ToString()).ToList();
        var reviewRequestedAtByRunId = await _db.RunEvents
            .Where(evt => ownedAwaitingReviewIds.Contains(evt.RunId) && evt.EventType == EventTypes.CoordinatorAssemblyReviewRequested)
            .GroupBy(evt => evt.RunId)
            .Select(group => new
            {
                RunId = group.Key,
                CreatedAt = group.Max(evt => evt.CreatedAt),
            })
            .ToDictionaryAsync(item => item.RunId, item => (DateTimeOffset?)item.CreatedAt, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

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

        var promoted = await BuildBacklogPromotedNotificationsAsync(ownedProjectNames, ct).ConfigureAwait(false);

        var notifications = ownedAwaitingReview
            .Select(run => ToHumanReviewNotification(
                run,
                reviewRequestedAtByRunId.GetValueOrDefault(run.Id.ToString()),
                ownedProjectNames))
            .Concat(ownedInProgress
                .Where(run => pendingApprovals.ContainsKey(run.Id.ToString()))
                .Select(run => ToToolApprovalNotification(run, pendingApprovals[run.Id.ToString()], ownedProjectNames)))
            .Concat(promoted)
            .OrderByDescending(notification => notification.CreatedUtc)
            .ToList();

        var dismissedIds = await _db.DismissedNotifications
            .Where(dismissal => dismissal.User == caller.User)
            .Select(dismissal => dismissal.NotificationId)
            .ToHashSetAsync(ct)
            .ConfigureAwait(false);

        return new NotificationsResponseDto
        {
            GeneratedUtc = DateTimeOffset.UtcNow,
            Notifications = notifications.Where(notification => !dismissedIds.Contains(notification.Id)).ToList(),
        };
    }

    public async Task DismissAsync(CallerContext caller, string notificationId, CancellationToken ct = default)
    {
        if (await _db.DismissedNotifications.FindAsync([caller.User, notificationId], ct).ConfigureAwait(false) is not null)
            return;

        _db.DismissedNotifications.Add(new DismissedNotification
        {
            User = caller.User,
            NotificationId = notificationId,
            DismissedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Recency window for the "N subtasks created" backlog-promotion notice. A delegated coordinator
    /// run is terminal (all stories promoted to the Board), so unlike Human Review / Tool Approval
    /// there is no naturally-bounded pending signal — a time window keeps the derived notice from
    /// resurfacing every old delegated run forever. Dismissals remain durable via
    /// <see cref="DismissedNotification"/>.
    /// </summary>
    private static readonly TimeSpan BacklogPromotedWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Derives "N subtasks created" notifications from durable state (issue: delegated-run Board
    /// notification). When a coordinator run finalizes as <see cref="WorkPlanStatus.Delegated"/> —
    /// every story promoted to an independent Board task, 0 inline subtasks — surface a notice that
    /// deep-links to the project Board. The count comes from the promoted backlog tasks stamped with
    /// the originating run id (<c>ParentPrdRunId</c>), read through the provider-agnostic
    /// <see cref="IBacklogTaskStore"/> (the backlog table is SQLite-raw / Postgres-EF depending on
    /// the provider, so we never touch <c>MemoryDbContext.BacklogTasks</c> directly here). This
    /// reuses the existing poll-derived notification subsystem instead of an event-written store.
    /// </summary>
    private async Task<List<NotificationDto>> BuildBacklogPromotedNotificationsAsync(
        IReadOnlyDictionary<string, string> ownedProjectNames,
        CancellationToken ct)
    {
        if (ownedProjectNames.Count == 0)
            return new List<NotificationDto>();

        var cutoff = DateTimeOffset.UtcNow - BacklogPromotedWindow;

        // Only the Status predicate is evaluated in the database (SQLite can't translate the
        // DateTimeOffset comparison + owned-project membership together); delegated plans are rare,
        // so the recency + ownership filters run client-side. WorkPlans is a memory.db entity mapped
        // under both providers, so this query is provider-agnostic.
        var delegatedPlans = (await _db.WorkPlans
            .Where(plan => plan.Status == WorkPlanStatus.Delegated)
            .Select(plan => new { plan.CoordinatorRunId, plan.ProjectId, plan.UpdatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false))
            .Where(plan => plan.UpdatedAt >= cutoff && ownedProjectNames.ContainsKey(plan.ProjectId))
            .ToList();

        if (delegatedPlans.Count == 0)
            return new List<NotificationDto>();

        var notifications = new List<NotificationDto>();

        // Group by project so we read each project's backlog once, then count promoted tasks per
        // originating run id via the store's durable ParentPrdRunId stamp.
        foreach (var projectGroup in delegatedPlans.GroupBy(plan => plan.ProjectId, StringComparer.Ordinal))
        {
            if (!ProjectId.TryParse(projectGroup.Key, out var projectId))
                continue;

            var runIdsForProject = projectGroup
                .Select(plan => plan.CoordinatorRunId)
                .ToHashSet(StringComparer.Ordinal);

            var promotedCounts = (await _backlogStore.ListByProjectAsync(projectId, ct).ConfigureAwait(false))
                .Where(task => task.ArchivedAt is null
                    && task.ParentPrdRunId is not null
                    && runIdsForProject.Contains(task.ParentPrdRunId.ToString()!))
                .GroupBy(task => task.ParentPrdRunId!.ToString()!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            foreach (var plan in projectGroup)
            {
                if (!promotedCounts.TryGetValue(plan.CoordinatorRunId, out var count) || count <= 0)
                    continue; // no tasks actually landed on the Board — nothing to announce (defensive)

                notifications.Add(new NotificationDto
                {
                    Id = $"backlog_promoted:{plan.CoordinatorRunId}",
                    Type = "backlog_promoted",
                    RunId = plan.CoordinatorRunId,
                    ProjectId = plan.ProjectId,
                    ProjectName = ownedProjectNames.GetValueOrDefault(plan.ProjectId, "Unknown project"),
                    Title = count == 1 ? "1 subtask created" : $"{count} subtasks created",
                    CreatedUtc = plan.UpdatedAt,
                    CtaPath = $"/projects/{plan.ProjectId}/board",
                });
            }
        }

        return notifications;
    }

    private async Task<IReadOnlyList<Project>> ListVisibleProjectsAsync(CallerContext caller, CancellationToken ct)
    {
        var projects = await _projectStore.ListAsync(ct).ConfigureAwait(false);
        if (_authMode == AuthMode.GitHubLegacy)
            return projects.Where(project => caller.Owns(project.Owner)).ToList();

        if (_projectRoles.IsPlatformAdmin(caller))
            return projects;

        var visibleRoles = await _projectRoles.ListExplicitRolesAsync(caller, ct).ConfigureAwait(false);
        return projects.Where(project => visibleRoles.ContainsKey(project.Id)).ToList();
    }

    private static NotificationDto ToHumanReviewNotification(
        Run run,
        DateTimeOffset? reviewReadyAt,
        IReadOnlyDictionary<string, string> ownedProjectNames)
    {
        var projectId = run.ProjectId!.ToString()!;
        var occurrenceAt = reviewReadyAt ?? run.EndedAt ?? run.StartedAt;
        // Run detail routes are keyed by the persisted execution run_id. A workflow_run_id can
        // identify a different workflow record, so never use it to navigate to an approval/review.
        var deepLinkRunId = run.Id.ToString();
        var title = string.IsNullOrWhiteSpace(run.Task)
            ? "A run is awaiting your review"
            : Truncate(run.Task, 120);

        return new NotificationDto
        {
            Id = $"review:{run.Id}:{occurrenceAt.ToUnixTimeMilliseconds()}",
            Type = "human_review",
            RunId = run.Id.ToString(),
            ProjectId = projectId,
            ProjectName = ownedProjectNames.GetValueOrDefault(projectId, "Unknown project"),
            AgentName = run.AgentName,
            Title = title,
            CreatedUtc = occurrenceAt,
            CtaPath = $"/projects/{projectId}/orchestrations/{deepLinkRunId}",
        };
    }


    private static NotificationDto ToToolApprovalNotification(
        Run run,
        PendingToolApproval approval,
        IReadOnlyDictionary<string, string> ownedProjectNames)
    {
        var projectId = run.ProjectId!.ToString()!;
        // The approval event belongs to this exact execution run. Detail routes are run_id-keyed;
        // using workflow_run_id can send the operator to a different active orchestration.
        var deepLinkRunId = run.Id.ToString();
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
