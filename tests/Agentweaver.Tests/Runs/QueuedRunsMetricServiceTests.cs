using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Backlog;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Runs;

/// <summary>
/// Regression tests for issue #108's queue-depth signal: the
/// <c>agentweaver.run.queued</c> OTel gauge that the worker HPA is intended to scale on
/// (replacing the CPU-only proxy), and the <see cref="QueuedRunsMetricService"/> poller that keeps
/// it up to date. The signal is the durable Ready backlog awaiting coordinator pickup, not the
/// short-lived <see cref="RunStatus.Pending"/> reservation seam.
/// </summary>
public sealed class QueuedRunsMetricServiceTests
{
    [Fact]
    public async Task PollOnceAsync_CountsOnlyActiveProjectReadyBacklogTasks_AndPublishesToGauge()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        var projectStore = factory.Services.GetRequiredService<IProjectStore>();
        var backlogStore = factory.Services.GetRequiredService<IBacklogTaskStore>();
        var runStore = factory.Services.GetRequiredService<IRunStore>();

        var activeProject = BacklogTestData.MakeProject();
        var deletingProject = BacklogTestData.MakeProject(state: ProjectState.Deleting);
        await projectStore.InsertAsync(activeProject);
        await projectStore.InsertAsync(deletingProject);

        await backlogStore.InsertAsync(BacklogTestData.MakeReadyTask(activeProject.Id, "a"));
        await backlogStore.InsertAsync(BacklogTestData.MakeReadyTask(activeProject.Id, "b"));
        await backlogStore.InsertAsync(BacklogTestData.MakeBacklogTask(activeProject.Id, "c"));
        await backlogStore.InsertAsync(BacklogTestData.MakeReadyTask(deletingProject.Id, "a"));

        await InsertRunAsync(runStore, RunId.New(), RunStatus.Pending, activeProject.Id);
        await InsertRunAsync(runStore, RunId.New(), RunStatus.InProgress, activeProject.Id);

        var service = new QueuedRunsMetricService(backlogStore, NullLogger<QueuedRunsMetricService>.Instance);
        await service.PollOnceAsync(CancellationToken.None);

        ReadCurrentGaugeValue().Should().Be(2);
    }

    [Fact]
    public async Task PollOnceAsync_IgnoresClaimedOrReservationOnlyWork_AndPublishesZero()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        var projectStore = factory.Services.GetRequiredService<IProjectStore>();
        var backlogStore = factory.Services.GetRequiredService<IBacklogTaskStore>();
        var runStore = factory.Services.GetRequiredService<IRunStore>();

        var activeProject = BacklogTestData.MakeProject();
        await projectStore.InsertAsync(activeProject);

        var reservedRunId = RunId.New();
        await backlogStore.InsertAsync(new BacklogTask
        {
            Id = BacklogTaskId.New(),
            ProjectId = activeProject.Id,
            Title = "claimed task",
            Description = "already claimed",
            State = BacklogTaskState.Claimed,
            OrderKey = "a",
            CapturedBy = "alice",
            CreatedAt = DateTimeOffset.UtcNow,
            CommittedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ClaimedAt = DateTimeOffset.UtcNow,
            RunId = reservedRunId,
        });
        await InsertRunAsync(runStore, RunId.New(), RunStatus.Pending, activeProject.Id);

        var service = new QueuedRunsMetricService(backlogStore, NullLogger<QueuedRunsMetricService>.Instance);
        await service.PollOnceAsync(CancellationToken.None);

        ReadCurrentGaugeValue().Should().Be(0);
    }

    [Fact]
    public void SetQueuedRunsCount_UpdatesTheValueTheGaugeReports()
    {
        AgentWeaverMetrics.SetQueuedRunsCount(7);
        ReadCurrentGaugeValue().Should().Be(7);

        AgentWeaverMetrics.SetQueuedRunsCount(0);
        ReadCurrentGaugeValue().Should().Be(0);
    }

    /// <summary>
    /// Reads the current published value of <see cref="AgentWeaverMetrics.QueuedRuns"/> the same
    /// way an OTel exporter would: by invoking the observable-gauge callback via
    /// <c>Instrument.Meter</c>'s recorded measurement stream. We drive it directly here (the
    /// gauge is publicly readable via <see cref="System.Diagnostics.Metrics.ObservableGauge{T}"/>
    /// only through a MeterListener), so register a short-lived listener and collect one snapshot.
    /// </summary>
    private static long ReadCurrentGaugeValue()
    {
        long? observed = null;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Agentweaver" && instrument.Name == "agentweaver.run.queued")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) => observed = measurement);
        listener.Start();
        listener.RecordObservableInstruments();

        observed.Should().NotBeNull("the agentweaver.run.queued gauge callback must have been invoked");
        return observed!.Value;
    }

    private static Task InsertRunAsync(IRunStore runStore, RunId id, RunStatus status, ProjectId? projectId = null) =>
        runStore.InsertAsync(new Run
        {
            Id = id,
            RepositoryPath = "dummy-repo-path",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "queued runs metric test",
            SubmittingUser = AgentweaverWebApplicationFactory.TestUser,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            AgentName = "Coordinator",
        });
}
