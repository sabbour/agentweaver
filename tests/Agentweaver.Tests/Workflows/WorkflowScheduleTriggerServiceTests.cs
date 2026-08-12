using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using static Agentweaver.Tests.Backlog.BacklogTestData;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Tests for <see cref="WorkflowScheduleTriggerService"/> (issue #53) over REAL
/// <see cref="SqliteProjectStore"/> / <see cref="SqliteBacklogTaskStore"/> / <see cref="WorkflowRegistry"/>
/// (Principle VII: no mocks of the store or its transaction logic). Every tick is driven by an
/// explicit <c>now</c> passed to <see cref="WorkflowScheduleTriggerService.RunTickAsync"/> — never the
/// wall clock — so the whole "due occurrence -> Ready backlog task -> idempotent re-tick" pipeline is
/// deterministic and fast.
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
}
