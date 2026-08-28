using FluentAssertions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Webhooks;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static Agentweaver.Tests.Backlog.BacklogTestData;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Tests for <see cref="WorkflowEventTriggerService"/> (issue #53, first-pass event trigger
/// mechanism) over REAL <see cref="SqliteBacklogTaskStore"/> / <see cref="WorkflowRegistry"/>
/// (Principle VII: no mocks). Proves: a matching event atomically publishes a Ready backlog task bound to the
/// workflow, a non-matching event name fires nothing, a dedupe key makes a repeated call a no-op, and
/// a workflow with no trigger (or a schedule trigger) never fires on an event.
/// </summary>
public sealed class WorkflowEventTriggerServiceTests : IAsyncDisposable
{
    private readonly TestSqliteDb _testDb;
    private readonly SqliteProjectStore _projects;
    private readonly SqliteBacklogTaskStore _backlog;
    private readonly WorkflowRegistry _registry = new();
    private readonly WorkflowEventTriggerService _service;
    private readonly CapturingLogger _logger = new();
    private readonly string _workingDir;
    private readonly ServiceProvider _serviceProvider;

    public WorkflowEventTriggerServiceTests()
    {
        _testDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _projects = new SqliteProjectStore(_testDb.Db);
        _backlog = new SqliteBacklogTaskStore(_testDb.Db);
        var services = new ServiceCollection();
        services.AddScoped<Agentweaver.Api.Auth.IAutomationInvocationService, AlwaysAvailableAutomationInvocationService>();
        _serviceProvider = services.BuildServiceProvider();
        _service = new WorkflowEventTriggerService(
            _backlog, _registry, new LoggerAdapter<WorkflowEventTriggerService>(_logger),
            _serviceProvider.GetRequiredService<IServiceScopeFactory>());

        _workingDir = Path.Combine(Path.GetTempPath(), $"agentweaver-event-trigger-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_workingDir, ".agentweaver", "workflows"));
    }

    private async Task<Project> SeedProjectAsync(string workflowYaml)
    {
        await File.WriteAllTextAsync(
            Path.Combine(_workingDir, ".agentweaver", "workflows", "on-event.yaml"), workflowYaml);

        var project = MakeProject() with { WorkingDirectory = _workingDir };
        await _projects.InsertAsync(project);
        return project;
    }

    public async ValueTask DisposeAsync()
    {
        await _testDb.DisposeAsync();
        await _serviceProvider.DisposeAsync();
        try { Directory.Delete(_workingDir, recursive: true); } catch { /* best effort */ }
    }

    private const string IssueOpenedEventYaml = """
        id: on-issue-opened
        name: On Issue Opened
        start: work
        nodes:
          - id: work
            type: prompt
            label: Work
            role: backend-engineer
            prompt: "Triage the new issue."
          - id: done
            type: terminal
            label: Done
            role: plumbing
        edges:
          - from: work
            to: done

        trigger:
          type: event
          event_name: issue.opened
        """;

    private const string IssuePredicateYaml = """
        id: on-bug-triage
        name: On Bug Triage
        start: work
        nodes:
          - id: work
            type: prompt
            label: Work
            role: backend-engineer
            prompt: "Triage the labeled issue."
          - id: done
            type: terminal
            label: Done
            role: plumbing
        edges:
          - from: work
            to: done

        trigger:
          type: event
          event_name: github.issues.labeled
          if:
            - has_label: { label: "bug" }
            - has_label: { label: "needs triage" }
        """;

    private const string ScheduleAndIssuePredicateYaml = """
        id: scheduled-and-roadmap-review
        name: Scheduled and Roadmap Review
        start: work
        nodes:
          - id: work
            type: prompt
            label: Work
            role: backend-engineer
            prompt: "Triage roadmap issues."
          - id: done
            type: terminal
            label: Done
            role: plumbing
        edges:
          - from: work
            to: done

        triggers:
          - type: schedule
            interval: weekly
            day_of_week: monday
            time_of_day: "09:00"
          - type: event
            event_name: github.issues.labeled
            if:
              - has_label: { label: "roadmap-review" }
        """;

    private const string PullRequestPredicateYaml = """
        id: on-pr-main-or-release
        name: On PR Main Or Release
        start: work
        nodes:
          - id: work
            type: prompt
            label: Work
            role: backend-engineer
            prompt: "Review the pull request."
          - id: done
            type: terminal
            label: Done
            role: plumbing
        edges:
          - from: work
            to: done

        trigger:
          type: event
          event_name: github.pull_request.opened
          if:
            - or:
                - base_branch: { branch: "main" }
                - base_branch: { branch: "release/v1" }
        """;

    private const string IssueCommentPredicateYaml = """
        id: on-comment-command
        name: On Comment Command
        start: work
        nodes:
          - id: work
            type: prompt
            label: Work
            role: backend-engineer
            prompt: "Handle the command."
          - id: done
            type: terminal
            label: Done
            role: plumbing
        edges:
          - from: work
            to: done

        trigger:
          type: event
          event_name: github.issue_comment.created
          if:
            - comment_matches: { pattern: "^/agentweaver:triage$" }
        """;

    private const string IssueNotPredicateYaml = """
        id: on-unblocked-issue
        name: On Unblocked Issue
        start: work
        nodes:
          - id: work
            type: prompt
            label: Work
            role: backend-engineer
            prompt: "Process the issue."
          - id: done
            type: terminal
            label: Done
            role: plumbing
        edges:
          - from: work
            to: done

        trigger:
          type: event
          event_name: github.issues.opened
          if:
            - not:
                has_label: { label: "blocked" }
        """;

    private static GitHubWebhookPayload IssuePayload(params string[] labels) => new()
    {
        Issue = new GitHubWebhookIssueLike
        {
            Labels = labels.Select(x => new GitHubWebhookLabel { Name = x }).ToList(),
        },
    };

    private static GitHubWebhookPayload PullRequestPayload(string baseBranch) => new()
    {
        PullRequest = new GitHubWebhookPullRequest
        {
            Base = new GitHubWebhookBranchRef { Ref = baseBranch },
        },
    };

    private static GitHubWebhookPayload IssueCommentPayload(string body) => new()
    {
        Comment = new GitHubWebhookComment { Body = body },
    };

    [Fact]
    public async Task FireEvent_MatchingEventName_CreatesReadyBacklogTaskBoundToWorkflow()
    {
        var project = await SeedProjectAsync(IssueOpenedEventYaml);

        var fired = await _service.FireEventAsync(project, "issue.opened", dedupeKey: null, payload: null, CancellationToken.None);

        fired.Should().BeEquivalentTo(new[] { "on-issue-opened" });

        var tasks = await _backlog.ListByProjectAsync(project.Id);
        tasks.Should().ContainSingle();
        var task = tasks.Single();
        task.State.Should().Be(BacklogTaskState.Ready);
        task.WorkflowOverrideId.Should().Be("on-issue-opened");
        task.CapturedBy.Should().Be(WorkflowEventTriggerService.CapturedBy);
    }

    [Fact]
    public async Task FireEvent_CoordinatorCannotClaimTaskBetweenCreationAndInvocationBinding()
    {
        var project = await SeedProjectAsync(IssueOpenedEventYaml);
        var invocations = new CoordinatorInterleavingAutomationInvocationService(_backlog);
        await using var provider = new ServiceCollection()
            .AddScoped<Agentweaver.Api.Auth.IAutomationInvocationService>(_ => invocations)
            .BuildServiceProvider();
        var service = new WorkflowEventTriggerService(
            _backlog, _registry, new LoggerAdapter<WorkflowEventTriggerService>(_logger),
            provider.GetRequiredService<IServiceScopeFactory>());

        await service.FireEventAsync(project, "issue.opened", "interleaved-delivery", null, CancellationToken.None);

        invocations.ClaimResultDuringBinding.Should().Be(ClaimReserveResult.Lost,
            "the task is still Backlog while the trigger writes its durable invocation binding");
        var task = (await _backlog.ListByProjectAsync(project.Id)).Should().ContainSingle().Subject;
        task.State.Should().Be(BacklogTaskState.Ready);
    }

    [Fact]
    public async Task FireEvent_DurableDuplicateClaim_RecoversInterruptedProvisionalTaskExactlyOnce()
    {
        var project = await SeedProjectAsync(IssueOpenedEventYaml);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var invocationDb = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection, options => options.MigrationsAssembly("Agentweaver.Api")).Options);
        await invocationDb.Database.MigrateAsync();
        await ActivateAsync(invocationDb, project.Id);

        var durableInvocations = new AutomationInvocationService(
            invocationDb, new TwoAppPersistenceStore(invocationDb));
        var invocations = new FailFirstBindingInvocationService(durableInvocations);
        await using var provider = new ServiceCollection()
            .AddScoped<IAutomationInvocationService>(_ => invocations)
            .BuildServiceProvider();
        var service = new WorkflowEventTriggerService(
            _backlog, _registry, new LoggerAdapter<WorkflowEventTriggerService>(_logger),
            provider.GetRequiredService<IServiceScopeFactory>());

        await FluentActions.Invoking(() => service.FireEventAsync(
                project, "issue.opened", "retry-delivery", null, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        var provisional = (await _backlog.ListByProjectAsync(project.Id)).Should().ContainSingle().Subject;
        provisional.State.Should().Be(BacklogTaskState.Backlog);
        provisional.IsAutomationInvocationPending.Should().BeTrue(
            "a failed handoff remains server-owned and cannot be claimed before retry recovery");
        (await invocationDb.AutomationInvocations.CountAsync()).Should().Be(1,
            "the first delivery durably claims its occurrence before the handoff is interrupted");

        var retried = await service.FireEventAsync(
            project, "issue.opened", "retry-delivery", null, CancellationToken.None);

        retried.Should().Equal("on-issue-opened");
        var published = (await _backlog.ListByProjectAsync(project.Id)).Should().ContainSingle().Subject;
        published.State.Should().Be(BacklogTaskState.Ready);
        published.Id.Should().Be(provisional.Id, "recovery must publish the original provisional task");
        published.IsAutomationInvocationPending.Should().BeFalse();
        (await invocationDb.AutomationInvocations.CountAsync()).Should().Be(1,
            "the duplicate claim must recover its existing invocation rather than create another");

        var duplicate = await service.FireEventAsync(
            project, "issue.opened", "retry-delivery", null, CancellationToken.None);
        duplicate.Should().BeEmpty();
        (await _backlog.ListByProjectAsync(project.Id)).Should().ContainSingle(
            "a recovered delivery must still publish exactly once");
    }

    [Fact]
    public async Task FireEvent_NonMatchingEventName_FiresNothing()
    {
        var project = await SeedProjectAsync(IssueOpenedEventYaml);

        var fired = await _service.FireEventAsync(project, "pull_request.opened", dedupeKey: null, payload: null, CancellationToken.None);

        fired.Should().BeEmpty();
        (await _backlog.ListByProjectAsync(project.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task FireEvent_WithDedupeKey_RepeatedCall_FiresOnlyOnce()
    {
        var project = await SeedProjectAsync(IssueOpenedEventYaml);

        var first = await _service.FireEventAsync(project, "issue.opened", "delivery-123", payload: null, CancellationToken.None);
        var duplicate = await _service.FireEventAsync(project, "issue.opened", "delivery-123", payload: null, CancellationToken.None);

        first.Should().Equal("on-issue-opened");
        duplicate.Should().BeEmpty("a successfully published delivery remains a no-op on retry");
        (await _backlog.ListByProjectAsync(project.Id)).Should().ContainSingle(
            because: "a retried webhook delivery with the same dedupe key must not double-fire");
    }

    [Fact]
    public async Task FireEvent_WithoutDedupeKey_RepeatedCall_FiresEachTime()
    {
        var project = await SeedProjectAsync(IssueOpenedEventYaml);

        await _service.FireEventAsync(project, "issue.opened", dedupeKey: null, payload: null, CancellationToken.None);
        await _service.FireEventAsync(project, "issue.opened", dedupeKey: null, payload: null, CancellationToken.None);

        (await _backlog.ListByProjectAsync(project.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task FireEvent_WorkflowWithScheduleTrigger_NeverFiresOnEvent()
    {
        const string scheduleYaml = """
            id: scheduled-only
            name: Scheduled Only
            start: work
            nodes:
              - id: work
                type: prompt
                label: Work
                role: backend-engineer
                prompt: "Do work."
              - id: done
                type: terminal
                label: Done
                role: plumbing
            edges:
              - from: work
                to: done

            trigger:
              type: schedule
              interval: daily
              time_of_day: "09:00"
            """;

        var project = await SeedProjectAsync(scheduleYaml);

        var fired = await _service.FireEventAsync(project, "issue.opened", dedupeKey: null, payload: null, CancellationToken.None);

        fired.Should().BeEmpty();
        (await _backlog.ListByProjectAsync(project.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task FireEvent_AllPredicatesMustMatch_UsesImplicitAnd()
    {
        var project = await SeedProjectAsync(IssuePredicateYaml);

        var missingLabel = await _service.FireEventAsync(
            project, "github.issues.labeled", dedupeKey: null, IssuePayload("bug"), CancellationToken.None);
        var matching = await _service.FireEventAsync(
            project, "github.issues.labeled", dedupeKey: null, IssuePayload("bug", "needs triage"), CancellationToken.None);

        missingLabel.Should().BeEmpty();
        matching.Should().BeEquivalentTo(["on-bug-triage"]);
    }

    [Fact]
    public async Task FireEvent_WhenWorkflowAlsoHasScheduleTrigger_FiresMatchingEvent()
    {
        var project = await SeedProjectAsync(ScheduleAndIssuePredicateYaml);

        var fired = await _service.FireEventAsync(
            project,
            "github.issues.labeled",
            dedupeKey: "delivery-713",
            IssuePayload("roadmap-review"),
            CancellationToken.None);

        fired.Should().Equal("scheduled-and-roadmap-review");
        var task = (await _backlog.ListByProjectAsync(project.Id)).Should().ContainSingle().Subject;
        task.WorkflowOverrideId.Should().Be("scheduled-and-roadmap-review");
        task.CapturedBy.Should().Be(WorkflowEventTriggerService.CapturedBy);
    }

    [Fact]
    public async Task FireEvent_OrPredicate_AllowsAnyMatchingChild()
    {
        var project = await SeedProjectAsync(PullRequestPredicateYaml);

        var fired = await _service.FireEventAsync(
            project, "github.pull_request.opened", dedupeKey: null, PullRequestPayload("release/v1"), CancellationToken.None);

        fired.Should().BeEquivalentTo(["on-pr-main-or-release"]);
    }

    [Fact]
    public async Task FireEvent_NotPredicate_NegatesChildPredicate()
    {
        var project = await SeedProjectAsync(IssueNotPredicateYaml);

        var blocked = await _service.FireEventAsync(
            project, "github.issues.opened", dedupeKey: null, IssuePayload("blocked"), CancellationToken.None);
        var unblocked = await _service.FireEventAsync(
            project, "github.issues.opened", dedupeKey: null, IssuePayload("bug"), CancellationToken.None);

        blocked.Should().BeEmpty();
        unblocked.Should().BeEquivalentTo(["on-unblocked-issue"]);
    }

    [Fact]
    public async Task FireEvent_CommentMatches_UsesBooleanMatchOnly_WithoutForwardingRawCommentBody()
    {
        const string rawComment = "/agentweaver:triage capture-this-secret";
        var project = await SeedProjectAsync(IssueCommentPredicateYaml);

        var notMatched = await _service.FireEventAsync(
            project, "github.issue_comment.created", dedupeKey: null, IssueCommentPayload(rawComment), CancellationToken.None);
        var matched = await _service.FireEventAsync(
            project, "github.issue_comment.created", dedupeKey: null, IssueCommentPayload("/agentweaver:triage"), CancellationToken.None);

        notMatched.Should().BeEmpty();
        matched.Should().BeEquivalentTo(["on-comment-command"]);

        var task = (await _backlog.ListByProjectAsync(project.Id)).Should().ContainSingle().Subject;
        task.Title.Should().NotContain("/agentweaver:triage capture-this-secret");
        (task.Description ?? string.Empty).Should().NotContain("/agentweaver:triage capture-this-secret");
        _logger.HasEntryContaining("/agentweaver:triage capture-this-secret").Should().BeFalse(
            because: "commentMatches must reduce the raw comment body to a fire/no-fire boolean only");
    }

    private static async Task ActivateAsync(MemoryDbContext db, ProjectId projectId)
    {
        db.Projects.Add(new ProjectRecord { ProjectId = projectId.ToString() });
        db.GitHubInstallations.Add(new GitHubInstallationRecord
        {
            InstallationId = 1, AppKind = GitHubAppKind.Repo, ProjectId = projectId.ToString(), CreatedAt = DateTimeOffset.UtcNow,
        });
        db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
        {
            InstallationId = 1, RepositoryId = 10, ProjectId = projectId.ToString(), FullNameDisplay = "owner/repository",
            PermissionDigest = "repo-digest", GrantedAt = DateTimeOffset.UtcNow,
        });
        db.ProjectCopilotBindings.Add(new ProjectCopilotBindingRecord
        {
            Id = "binding", ProjectId = projectId.ToString(), EntraObjectId = "owner",
            CredentialReference = "credential", CredentialVersion = "version", GrantDigest = "copilot-digest",
            Status = GitHubBindingStatus.Active, BoundAt = DateTimeOffset.UtcNow,
        });
        db.AutomationActivations.Add(new AutomationActivationRecord
        {
            Id = "activation", ProjectId = projectId.ToString(), InstallationId = 1, RepositoryId = 10,
            RepositoryGrantDigest = "repo-digest", CopilotBindingId = "binding",
            CopilotBindingGrantDigest = "copilot-digest", AutomationKey = "automation",
            Status = AutomationActivationStatus.Active, ActivatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}

file sealed class LoggerAdapter<TCategory>(CapturingLogger inner) : Microsoft.Extensions.Logging.ILogger<TCategory>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => inner.IsEnabled(logLevel);
    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        inner.Log(logLevel, eventId, state, exception, formatter);
}

file sealed class FailFirstBindingInvocationService(IAutomationInvocationService inner) : IAutomationInvocationService
{
    private int _bindings;

    public Task<AutomationInvocationClaim?> TryClaimForProjectAsync(
        ProjectId projectId, string occurrenceKey, string? deliveryId, string? eventName, CancellationToken ct = default) =>
        inner.TryClaimForProjectAsync(projectId, occurrenceKey, deliveryId, eventName, ct);

    public Task<AutomationInvocationClaim?> TryGetClaimedInvocationForProjectAsync(
        ProjectId projectId, string occurrenceKey, string? deliveryId, string? eventName, CancellationToken ct = default) =>
        inner.TryGetClaimedInvocationForProjectAsync(projectId, occurrenceKey, deliveryId, eventName, ct);

    public Task<bool> TryBindBacklogTaskAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default) =>
        Interlocked.Increment(ref _bindings) == 1
            ? Task.FromResult(false)
            : inner.TryBindBacklogTaskAsync(invocationId, projectId, backlogTaskId, ct);

    public Task<Agentweaver.Api.Auth.AutomationInvocationTaskReservation?> TryReserveBacklogTaskAsync(
        string invocationId, ProjectId projectId, CancellationToken ct = default) =>
        inner.TryReserveBacklogTaskAsync(invocationId, projectId, ct);

    public Task<bool> TryAdoptLegacyProvisionalTaskAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default) =>
        inner.TryAdoptLegacyProvisionalTaskAsync(invocationId, projectId, backlogTaskId, ct);

    public Task<IReadOnlyList<OutstandingScheduleInvocation>> ListOutstandingScheduleInvocationsAsync(
        ProjectId projectId, string occurrenceKeyPrefix, IReadOnlyCollection<string> legacyProvisionalOccurrenceKeys,
        int maximumCount, CancellationToken ct = default) =>
        inner.ListOutstandingScheduleInvocationsAsync(
            projectId, occurrenceKeyPrefix, legacyProvisionalOccurrenceKeys, maximumCount, ct);

    public Task<bool> TryCompleteBacklogTaskReservationAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default) =>
        inner.TryCompleteBacklogTaskReservationAsync(invocationId, projectId, backlogTaskId, ct);

    public Task<bool> TryDiscardInvocationForTaskAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default)
        => inner.TryDiscardInvocationForTaskAsync(invocationId, projectId, backlogTaskId, ct);

    public Task<bool> TryPrepareRunAsync(
        ProjectId expectedProjectId, BacklogTaskId backlogTaskId, string runId, CancellationToken ct = default) =>
        inner.TryPrepareRunAsync(expectedProjectId, backlogTaskId, runId, ct);
}
