using System.Security.Claims;
using FluentAssertions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Security;
using Agentweaver.Api.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static Agentweaver.Tests.Backlog.BacklogTestData;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Tests for <see cref="WorkflowScheduleTriggerService"/> (issue #53) over REAL
/// <see cref="SqliteProjectStore"/> / <see cref="SqliteBacklogTaskStore"/> / <see cref="WorkflowRegistry"/>
/// (Principle VII: no mocks of the store or its transaction logic). Every tick is driven by an
/// explicit <c>now</c> passed to <see cref="WorkflowScheduleTriggerService.RunTickAsync"/> — never the
/// wall clock — so the whole "due occurrence -> bound Ready backlog task -> idempotent re-tick"
/// pipeline is deterministic and fast.
/// </summary>
public sealed class WorkflowScheduleTriggerServiceTests : IAsyncDisposable
{
    private readonly TestSqliteDb _testDb;
    private readonly SqliteProjectStore _projects;
    private readonly SqliteBacklogTaskStore _backlog;
    private readonly WorkflowRegistry _registry = new();   // parameterless: no catalog, file-based only
    private readonly WorkflowScheduleTriggerService _service;
    private readonly string _workingDir;

    public WorkflowScheduleTriggerServiceTests()
    {
        _testDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _projects = new SqliteProjectStore(_testDb.Db);
        _backlog = new SqliteBacklogTaskStore(_testDb.Db);

        _workingDir = Path.Combine(Path.GetTempPath(), $"agentweaver-trigger-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_workingDir, ".agentweaver", "workflows"));

        var services = new ServiceCollection();
        services.AddSingleton<IProjectStore>(_projects);
        services.AddSingleton<IBacklogTaskStore>(_backlog);
        services.AddSingleton(_registry);
        services.AddScoped<IAutomationInvocationService, AlwaysAvailableAutomationInvocationService>();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var configuration = new ConfigurationBuilder().Build();
        _service = new WorkflowScheduleTriggerService(
            scopeFactory, configuration, NullLogger<WorkflowScheduleTriggerService>.Instance);
    }

    /// <summary>Writes <paramref name="workflowYaml"/> as the project's only workflow file and inserts
    /// the project (defaulting to <see cref="ProjectState.Active"/>).</summary>
    private async Task<Project> SeedProjectAsync(string workflowYaml, ProjectState state = ProjectState.Active)
    {
        await File.WriteAllTextAsync(
            Path.Combine(_workingDir, ".agentweaver", "workflows", "scheduled.yaml"), workflowYaml);

        var project = MakeProject() with { WorkingDirectory = _workingDir };
        await _projects.InsertAsync(project);

        if (state != ProjectState.Active)
        {
            // Only Active -> Deleting is a real transition on IProjectStore; used here purely to get a
            // non-Active project for the "non-active project never fires" test.
            await _projects.TryBeginDeleteAsync(project.Id);
            project = await _projects.GetAsync(project.Id) ?? project;
        }

        return project;
    }

    public async ValueTask DisposeAsync()
    {
        await _testDb.DisposeAsync();
        try { Directory.Delete(_workingDir, recursive: true); } catch { /* best effort */ }
    }

    private const string WeeklyMondayNineAmYaml = """
        id: scheduled-triage
        name: Scheduled Triage
        start: work
        nodes:
          - id: work
            type: prompt
            label: Work
            role: backend-engineer
            prompt: "Triage new issues."
          - id: done
            type: terminal
            label: Done
            role: plumbing
        edges:
          - from: work
            to: done

        trigger:
          type: schedule
          interval: weekly
          day_of_week: monday
          time_of_day: "09:00"
        """;

    private const string DailyNineAmYaml = """
        id: scheduled-triage
        name: Scheduled Triage
        start: work
        nodes:
          - id: work
            type: prompt
            label: Work
            role: backend-engineer
            prompt: "Triage new issues."
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

    private const string MonthlyFirstNineAmYaml = """
        id: scheduled-triage
        name: Scheduled Triage
        start: work
        nodes:
          - id: work
            type: prompt
            label: Work
            role: backend-engineer
            prompt: "Triage new issues."
          - id: done
            type: terminal
            label: Done
            role: plumbing
        edges:
          - from: work
            to: done
        trigger:
          type: schedule
          interval: monthly
          day_of_month: 1
          time_of_day: "09:00"
        """;

    private const string ScheduleAndEventYaml = """
        id: scheduled-and-event-triage
        name: Scheduled and Event Triage
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

    [Fact]
    public async Task RunTick_WhenOccurrenceDue_CreatesReadyBacklogTaskBoundToWorkflow()
    {
        var project = await SeedProjectAsync(WeeklyMondayNineAmYaml);

        // 2026-07-13 is a Monday.
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        await _service.RunTickAsync(now, CancellationToken.None);

        var tasks = await _backlog.ListByProjectAsync(project.Id);
        tasks.Should().ContainSingle();
        var task = tasks.Single();
        task.State.Should().Be(BacklogTaskState.Ready);
        task.WorkflowOverrideId.Should().Be("scheduled-triage");
        task.CapturedBy.Should().Be(WorkflowScheduleTriggerService.CapturedBy);
        task.RunId.Should().BeNull();
    }

    [Fact]
    public async Task RunTick_CoordinatorCannotClaimTaskBetweenCreationAndInvocationBinding()
    {
        var project = await SeedProjectAsync(WeeklyMondayNineAmYaml);
        var invocations = new CoordinatorInterleavingAutomationInvocationService(_backlog);
        await using var provider = new ServiceCollection()
            .AddSingleton<IProjectStore>(_projects)
            .AddSingleton<IBacklogTaskStore>(_backlog)
            .AddSingleton(_registry)
            .AddScoped<IAutomationInvocationService>(_ => invocations)
            .BuildServiceProvider();
        var service = new WorkflowScheduleTriggerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<WorkflowScheduleTriggerService>.Instance);

        await service.RunTickAsync(new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero), CancellationToken.None);

        invocations.ClaimResultDuringBinding.Should().Be(ClaimReserveResult.Lost,
            "the schedule task is not Ready until the invocation-to-task binding is durable");
        var task = (await _backlog.ListByProjectAsync(project.Id)).Should().ContainSingle().Subject;
        task.State.Should().Be(BacklogTaskState.Ready);
    }

    [Fact]
    public async Task RunTick_WhenWorkflowAlsoHasEventTrigger_FiresSchedule()
    {
        var project = await SeedProjectAsync(ScheduleAndEventYaml);

        await _service.RunTickAsync(
            new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        var task = (await _backlog.ListByProjectAsync(project.Id)).Should().ContainSingle().Subject;
        task.WorkflowOverrideId.Should().Be("scheduled-and-event-triage");
        task.CapturedBy.Should().Be(WorkflowScheduleTriggerService.CapturedBy);
    }

    [Fact]
    public async Task RunTick_WhenNotYetDue_CreatesNoTask()
    {
        var project = await SeedProjectAsync(WeeklyMondayNineAmYaml);

        // Same Monday, but before 09:00 — not due yet.
        var now = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        await _service.RunTickAsync(now, CancellationToken.None);

        (await _backlog.ListByProjectAsync(project.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task RunTick_CalledRepeatedlyWithinSameOccurrence_FiresOnlyOnce()
    {
        var project = await SeedProjectAsync(WeeklyMondayNineAmYaml);

        var firstTick = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var secondTick = new DateTimeOffset(2026, 7, 13, 9, 1, 0, TimeSpan.Zero);   // next heartbeat, same occurrence
        var thirdTick = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);   // later in the same week

        await _service.RunTickAsync(firstTick, CancellationToken.None);
        await _service.RunTickAsync(secondTick, CancellationToken.None);
        await _service.RunTickAsync(thirdTick, CancellationToken.None);

        (await _backlog.ListByProjectAsync(project.Id)).Should().ContainSingle(
            because: "the occurrence already fired for this week — repeated ticks must not pile up runs");
    }

    [Theory]
    [InlineData("daily", true)]
    [InlineData("weekly", false)]
    [InlineData("monthly", false)]
    public async Task RunTick_MigratedPreReservationHandoff_RecoversAcrossRolloverExactlyOnce(
        string cadence,
        bool wasBoundBeforeUpgrade)
    {
        var (yaml, interruptedAt, restartAt, interruptedPeriod) = cadence switch
        {
            "daily" => (DailyNineAmYaml,
                new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero), "2026-07-13"),
            "weekly" => (WeeklyMondayNineAmYaml,
                new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero), "2026-07-13"),
            "monthly" => (MonthlyFirstNineAmYaml,
                new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero), "2026-08"),
            _ => throw new ArgumentOutOfRangeException(nameof(cadence)),
        };
        var project = await SeedProjectAsync(yaml);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var invocationDb = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection, options => options.MigrationsAssembly("Agentweaver.Api")).Options);

        await invocationDb.Database.MigrateAsync("20260828203038_AddAutomationInvocationBacklogBinding");
        var activation = await ActivateAsync(invocationDb, project.Id);
        var occurrenceKey = WorkflowScheduleTriggerService.BuildIdempotencyKey("scheduled-triage", interruptedPeriod);
        var taskId = BacklogTaskId.New();
        await invocationDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO automation_invocations
                (id, project_id, activation_id, backlog_task_id, occurrence_key, delivery_id, event_name,
                 installation_id, repository_id, outcome, received_at, completed_at)
            VALUES
                ({SnapshotRef.Create().Value}, {project.Id.ToString()}, {activation.Id},
                 {(wasBoundBeforeUpgrade ? taskId.ToString() : null)}, {occurrenceKey}, NULL, 'schedule',
                 1, 10, 0, {interruptedAt}, NULL);
            """);
        await _backlog.InsertAsync(new BacklogTask
        {
            Id = taskId,
            ProjectId = project.Id,
            Title = "Scheduled run: Scheduled Triage",
            Description = "Interrupted before publication.",
            State = BacklogTaskState.Backlog,
            OrderKey = "n",
            CapturedBy = WorkflowScheduleTriggerService.CapturedBy,
            CreatedAt = interruptedAt,
            WorkflowOverrideId = "scheduled-triage",
            SourceFilePath = occurrenceKey,
            IsAutomationInvocationPending = true,
        });

        await invocationDb.Database.MigrateAsync();
        var recoveryRegistry = new WorkflowRegistry();
        await using var provider = new ServiceCollection()
            .AddSingleton<IProjectStore>(_projects)
            .AddSingleton<IBacklogTaskStore>(_backlog)
            .AddSingleton(recoveryRegistry)
            .AddScoped<IAutomationInvocationService>(_ =>
                new AutomationInvocationService(invocationDb, new GitHubConnectionsPersistenceStore(invocationDb)))
            .BuildServiceProvider();
        var service = new WorkflowScheduleTriggerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<WorkflowScheduleTriggerService>.Instance);

        await service.RunTickAsync(restartAt, CancellationToken.None);
        await service.RunTickAsync(restartAt, CancellationToken.None);

        var tasks = await _backlog.ListByProjectAsync(project.Id);
        tasks.Should().HaveCount(2, "the recovered occurrence and the new occurrence each publish once");
        tasks.Should().OnlyContain(task => task.State == BacklogTaskState.Ready && !task.IsAutomationInvocationPending);
        tasks.Count(task => task.Id == taskId).Should().Be(1, "legacy staging must be adopted, not duplicated");
        var legacyInvocation = await invocationDb.AutomationInvocations.SingleAsync(x => x.OccurrenceKey == occurrenceKey);
        legacyInvocation.BacklogTaskId.Should().Be(taskId.ToString());
        legacyInvocation.PendingBacklogTaskId.Should().BeNull();
    }

    [Fact]
    public async Task RunTick_NextOccurrence_FiresAgain()
    {
        var project = await SeedProjectAsync(WeeklyMondayNineAmYaml);

        var week1 = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var week2 = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);

        await _service.RunTickAsync(week1, CancellationToken.None);
        await _service.RunTickAsync(week2, CancellationToken.None);

        var tasks = await _backlog.ListByProjectAsync(project.Id);
        tasks.Should().HaveCount(2);
        tasks.Should().OnlyContain(t => t.State == BacklogTaskState.Ready && t.WorkflowOverrideId == "scheduled-triage");
    }

    [Fact]
    public async Task RunTick_WorkflowWithoutTrigger_NeverFires()
    {
        const string noTriggerYaml = """
            id: manual-only
            name: Manual Only
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
            """;

        var project = await SeedProjectAsync(noTriggerYaml);

        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        await _service.RunTickAsync(now, CancellationToken.None);

        (await _backlog.ListByProjectAsync(project.Id)).Should().BeEmpty(
            because: "a workflow with no trigger must continue to start ONLY via manual/on-demand paths");
    }

    [Fact]
    public async Task RunTick_NonActiveProject_NeverFires()
    {
        var project = await SeedProjectAsync(WeeklyMondayNineAmYaml, state: ProjectState.Deleting);

        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        await _service.RunTickAsync(now, CancellationToken.None);

        (await _backlog.ListByProjectAsync(project.Id)).Should().BeEmpty();
    }

    /// <summary>
    /// End-to-end proof that the missing production entry point this change adds
    /// (<see cref="AutomationActivationSnapshotService.ActivateAsync"/>, reachable via
    /// <c>POST /api/projects/{id}/automation/activate</c>) is what makes
    /// <see cref="WorkflowScheduleTriggerService"/> able to fire at all, and that
    /// <see cref="AutomationActivationSnapshotService.DeactivateAsync"/> (via
    /// <c>.../automation/deactivate</c>) turns it back off without touching the underlying
    /// repository grant or Copilot binding: activate -> tick fires -> deactivate -> tick no longer
    /// fires -> reactivate -> tick fires again.
    /// </summary>
    [Fact]
    public async Task RunTick_OnlyFiresWhileAutomationIsActivated_ViaTheRealActivationService()
    {
        var project = await SeedProjectAsync(WeeklyMondayNineAmYaml);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var invocationDb = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection, options => options.MigrationsAssembly("Agentweaver.Api")).Options);
        await invocationDb.Database.MigrateAsync();

        invocationDb.Projects.Add(new ProjectRecord { ProjectId = project.Id.ToString() });
        invocationDb.GitHubInstallations.Add(new GitHubInstallationRecord
        {
            InstallationId = 1, AppKind = GitHubAppKind.Repo, ProjectId = project.Id.ToString(), CreatedAt = DateTimeOffset.UtcNow,
        });
        invocationDb.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
        {
            InstallationId = 1, RepositoryId = 10, ProjectId = project.Id.ToString(), FullNameDisplay = "owner/repository",
            PermissionDigest = "repo-digest", GrantedAt = DateTimeOffset.UtcNow,
        });
        invocationDb.ProjectCopilotBindings.Add(new ProjectCopilotBindingRecord
        {
            Id = "binding", ProjectId = project.Id.ToString(), EntraObjectId = "owner",
            CredentialReference = "credential", CredentialVersion = "version", GrantDigest = "copilot-digest",
            Status = GitHubBindingStatus.Active, BoundAt = DateTimeOffset.UtcNow,
        });
        await invocationDb.SaveChangesAsync();

        var roles = new SingleOwnerRoleStore(project.Id, "owner");
        var activationService = new AutomationActivationSnapshotService(
            new GitHubConnectionsPersistenceStore(invocationDb), roles);
        var caller = new CallerContext { User = "owner", EntraObjectId = "owner" };
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "owner")], "test"));

        var registry = new WorkflowRegistry();
        await using var provider = new ServiceCollection()
            .AddSingleton<IProjectStore>(_projects)
            .AddSingleton<IBacklogTaskStore>(_backlog)
            .AddSingleton(registry)
            .AddScoped<IAutomationInvocationService>(_ =>
                new AutomationInvocationService(invocationDb, new GitHubConnectionsPersistenceStore(invocationDb)))
            .BuildServiceProvider();
        var service = new WorkflowScheduleTriggerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<WorkflowScheduleTriggerService>.Instance);

        var week1 = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var week2 = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var week3 = new DateTimeOffset(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);

        // Before any activation exists at all (the bug this change fixes), the tick must not fire.
        await service.RunTickAsync(week1, CancellationToken.None);
        (await _backlog.ListByProjectAsync(project.Id)).Should().BeEmpty(
            "no AutomationActivationRecord exists yet — nothing has ever called ActivateAsync in production before this change");

        // Activate via the real (now-reachable) service -> the due occurrence fires.
        (await activationService.ActivateAsync(caller, principal, project.Id)).Outcome
            .Should().Be(AutomationActivationOutcome.Activated);
        await service.RunTickAsync(week1, CancellationToken.None);
        (await _backlog.ListByProjectAsync(project.Id)).Should().ContainSingle(
            because: "activation is now live, so the due weekly occurrence must fire");

        // Deactivate -> the next due occurrence must NOT fire.
        (await activationService.DeactivateAsync(caller, principal, project.Id))
            .Should().Be(AutomationDeactivationOutcome.Deactivated);
        await service.RunTickAsync(week2, CancellationToken.None);
        (await _backlog.ListByProjectAsync(project.Id)).Should().ContainSingle(
            "automation was deactivated, so the week 2 occurrence must not publish a new backlog task");

        // Reactivate -> ticks fire again (the underlying repository grant/Copilot binding were
        // never touched by deactivation, so reactivation needs only a fresh fence, not new authority).
        (await activationService.ActivateAsync(caller, principal, project.Id)).Outcome
            .Should().Be(AutomationActivationOutcome.Activated);
        await service.RunTickAsync(week2, CancellationToken.None);
        await service.RunTickAsync(week3, CancellationToken.None);
        var tasks = await _backlog.ListByProjectAsync(project.Id);
        tasks.Should().HaveCount(3, "week 2 (recovered after reactivation) and week 3 must each publish once");
    }

    /// <summary>Minimal <see cref="IProjectRoleAssignmentStore"/> fake granting exactly one project
    /// Owner, for the real <see cref="AutomationActivationSnapshotService"/> authority check in the
    /// end-to-end test above.</summary>
    private sealed class SingleOwnerRoleStore(ProjectId projectId, string ownerSubject) : IProjectRoleAssignmentStore
    {
        public Task<ProjectRoleAssignment?> GetAsync(ProjectId requestedProjectId, string principalId, CancellationToken ct = default) =>
            Task.FromResult(requestedProjectId == projectId && principalId == ownerSubject
                ? new ProjectRoleAssignment { ProjectId = projectId, PrincipalId = ownerSubject, Role = ProjectRole.Owner, GrantedBy = "test", GrantedAt = DateTimeOffset.UtcNow }
                : null);
        public Task UpsertAsync(ProjectRoleAssignment assignment, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProjectRoleAssignmentStoreMutationResult> UpsertEnsuringOwnerInvariantAsync(ProjectRoleAssignment assignment, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectRoleAssignment>> ListByProjectAsync(ProjectId requestedProjectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectRoleAssignment>> ListByPrincipalAsync(string principalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(ProjectId requestedProjectId, string principalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProjectRoleAssignmentStoreMutationResult> DeleteEnsuringOwnerInvariantAsync(ProjectId requestedProjectId, string principalId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static async Task<AutomationActivationRecord> ActivateAsync(MemoryDbContext db, ProjectId projectId)
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
        await db.SaveChangesAsync();
        // Inserted (and returned) via raw SQL/manual construction rather than
        // db.AutomationActivations.Add + SaveChanges/query because this helper runs against a
        // deliberately pre-migration physical schema (see the pinned MigrateAsync("20260828203038_...")
        // call above) that predates later automation_activations columns (e.g.
        // byok_provider_id/model_provider_source). Using EF's current compiled model to INSERT or
        // SELECT would reference columns that don't exist yet on the pinned schema.
        var activatedAt = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO automation_activations
                (id, project_id, installation_id, repository_id, repository_grant_digest,
                 copilot_binding_id, copilot_binding_grant_digest, automation_key, status, activated_at)
            VALUES
                ('activation', {projectId.ToString()}, 1, 10, 'repo-digest',
                 'binding', 'copilot-digest', 'automation', 0, {activatedAt});
            """);
        return new AutomationActivationRecord
        {
            Id = "activation", ProjectId = projectId.ToString(), InstallationId = 1, RepositoryId = 10,
            RepositoryGrantDigest = "repo-digest", CopilotBindingId = "binding",
            CopilotBindingGrantDigest = "copilot-digest", AutomationKey = "automation",
            Status = AutomationActivationStatus.Active, ActivatedAt = activatedAt,
        };
    }
}
