using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Diagnostics;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Tests.Coordinator;

public sealed class CoordinatorHeartbeatServiceTests
{
    [Fact]
    public async Task RunTickSafelyAsync_TransientProjectStoreFailure_RecordsErrorThenRetriesSuccessfully()
    {
        var projectStore = new SequencedProjectStore(failFirstList: true);
        var (service, status, logger) = CreateService(projectStore);

        Func<Task> firstTick = () => service.RunTickSafelyAsync(CancellationToken.None);
        await firstTick.Should().NotThrowAsync();

        var failed = status.GetRecentActivity().Should().ContainSingle().Subject;
        failed.ActedCount.Should().Be(0);
        failed.ErrorCount.Should().Be(1);
        failed.Error.Should().Be("Coordinator heartbeat tick failed (InvalidOperationException).");
        failed.DurationMs.Should().BeGreaterThanOrEqualTo(0);
        logger.HasEntryMatching(LogLevel.Warning, "top-level tick failed").Should().BeTrue();

        Func<Task> secondTick = () => service.RunTickSafelyAsync(CancellationToken.None);
        await secondTick.Should().NotThrowAsync();

        var recent = status.GetRecentActivity();
        recent.Should().HaveCount(2);
        recent[0].ErrorCount.Should().Be(0);
        recent[0].Error.Should().BeNull("the successful tick record must not reuse sticky LastError");
        status.LastError.Should().Be(failed.Error);
        projectStore.ListCalls.Should().Be(2);
    }

    [Fact]
    public async Task RunTickSafelyAsync_CanceledStoppingToken_PropagatesCancellation()
    {
        var (service, status, _) = CreateService(new SequencedProjectStore());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> tick = () => service.RunTickSafelyAsync(cts.Token);

        await tick.Should().ThrowAsync<OperationCanceledException>();
        status.GetRecentActivity().Should().BeEmpty();
    }

    [Fact]
    public async Task RunTickSafelyAsync_ScopeFailure_LogsWarningAndRecordsRedactedError()
    {
        const string sensitiveMessage = "Host=secret.example;Password=do-not-log";
        var status = CreateStatusStore();
        var logger = new CapturingLogger();
        var service = new CoordinatorHeartbeatService(
            new ThrowingScopeFactory(sensitiveMessage),
            new ConfigurationBuilder().Build(),
            status,
            logger.For<CoordinatorHeartbeatService>());

        Func<Task> tick = () => service.RunTickSafelyAsync(CancellationToken.None);

        await tick.Should().NotThrowAsync();
        var failed = status.GetRecentActivity().Should().ContainSingle().Subject;
        failed.ErrorCount.Should().Be(1);
        failed.Error.Should().Be("Coordinator heartbeat tick failed (InvalidOperationException).");
        failed.Error.Should().NotContain("secret.example").And.NotContain("Password");
        logger.HasEntryMatching(LogLevel.Warning, "top-level tick failed").Should().BeTrue();
        logger.Entries.Should().NotContain(entry => entry.Message.Contains(sensitiveMessage, StringComparison.Ordinal));
    }

    private static (CoordinatorHeartbeatService Service, HeartbeatStatusStore Status, CapturingLogger Logger)
        CreateService(IProjectStore projectStore)
    {
        var services = new ServiceCollection();
        services.AddSingleton(projectStore);
        var provider = services.BuildServiceProvider();
        var status = CreateStatusStore();
        var logger = new CapturingLogger();
        var service = new CoordinatorHeartbeatService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            status,
            logger.For<CoordinatorHeartbeatService>());
        return (service, status, logger);
    }

    private static HeartbeatStatusStore CreateStatusStore() =>
        new(new ConfigurationBuilder().Build());

    private sealed class ThrowingScopeFactory(string message) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException(message);
    }

    private sealed class SequencedProjectStore(bool failFirstList = false) : IProjectStore
    {
        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ListCalls++;
            return failFirstList && ListCalls == 1
                ? Task.FromException<IReadOnlyList<Project>>(
                    new InvalidOperationException("transient project-store failure"))
                : Task.FromResult<IReadOnlyList<Project>>([]);
        }

        public Task InsertAsync(Project project, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateNameAsync(ProjectId id, string name, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateProviderSettingsAsync(ProjectId id, ProjectProviderSettings settings, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateOriginAsync(ProjectId id, ProjectOrigin origin, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateGenerationModelSettingsAsync(ProjectId id, string? blueprintGenerationModel, string? workflowGenerationModel, string? outcomeSpecGenerationModel, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TryBeginDeleteAsync(ProjectId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(ProjectId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdatePickupSettingsAsync(ProjectId id, int maxReadyPerHeartbeat, bool autopilot, bool autoApproveTools, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateDefaultWorkflowAsync(ProjectId id, string? workflowId, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateActiveReviewPolicyAsync(ProjectId id, string? policyName, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateSandboxProfileAsync(ProjectId id, string? sandboxProfile, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateSourceBlueprintAsync(ProjectId id, string? blueprintId, string? blueprintType, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAllowedWorkflowIdsAsync(ProjectId id, IReadOnlyList<string>? allowedWorkflowIds, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IProjectTeamMutationLease?> TryBeginTeamMutationAsync(ProjectId id, long expectedRevision, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
