using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using static Agentweaver.Tests.Backlog.BacklogTestData;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Tests for <see cref="WorkflowEventTriggerService"/> (issue #53, first-pass event trigger
/// mechanism) over REAL <see cref="SqliteBacklogTaskStore"/> / <see cref="WorkflowRegistry"/>
/// (Principle VII: no mocks). Proves: a matching event fires a Ready backlog task bound to the
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
    private readonly string _workingDir;

    public WorkflowEventTriggerServiceTests()
    {
        _testDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _projects = new SqliteProjectStore(_testDb.Db);
        _backlog = new SqliteBacklogTaskStore(_testDb.Db);
        _service = new WorkflowEventTriggerService(
            _backlog, _registry, NullLogger<WorkflowEventTriggerService>.Instance);

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
          event_name: github.issues.opened
        """;

    [Fact]
    public async Task FireEvent_MatchingEventName_CreatesReadyBacklogTaskBoundToWorkflow()
    {
        var project = await SeedProjectAsync(IssueOpenedEventYaml);

        var fired = await _service.FireEventAsync(project, "github.issues.opened", dedupeKey: null, CancellationToken.None);

        fired.Should().BeEquivalentTo(new[] { "on-issue-opened" });

        var tasks = await _backlog.ListByProjectAsync(project.Id);
        tasks.Should().ContainSingle();
        var task = tasks.Single();
        task.State.Should().Be(BacklogTaskState.Ready);
        task.WorkflowOverrideId.Should().Be("on-issue-opened");
        task.CapturedBy.Should().Be(WorkflowEventTriggerService.CapturedBy);
    }

    [Fact]
    public async Task FireEvent_NonMatchingEventName_FiresNothing()
    {
        var project = await SeedProjectAsync(IssueOpenedEventYaml);

        var fired = await _service.FireEventAsync(project, "github.pull_request.opened", dedupeKey: null, CancellationToken.None);

        fired.Should().BeEmpty();
        (await _backlog.ListByProjectAsync(project.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task FireEvent_WithDedupeKey_RepeatedCall_FiresOnlyOnce()
    {
        var project = await SeedProjectAsync(IssueOpenedEventYaml);

        await _service.FireEventAsync(project, "github.issues.opened", "delivery-123", CancellationToken.None);
        await _service.FireEventAsync(project, "github.issues.opened", "delivery-123", CancellationToken.None);

        (await _backlog.ListByProjectAsync(project.Id)).Should().ContainSingle(
            because: "a retried webhook delivery with the same dedupe key must not double-fire");
    }

    [Fact]
    public async Task FireEvent_WithoutDedupeKey_RepeatedCall_FiresEachTime()
    {
        var project = await SeedProjectAsync(IssueOpenedEventYaml);

        await _service.FireEventAsync(project, "github.issues.opened", dedupeKey: null, CancellationToken.None);
        await _service.FireEventAsync(project, "github.issues.opened", dedupeKey: null, CancellationToken.None);

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

        var fired = await _service.FireEventAsync(project, "github.issues.opened", dedupeKey: null, CancellationToken.None);

        fired.Should().BeEmpty();
        (await _backlog.ListByProjectAsync(project.Id)).Should().BeEmpty();
    }
}
