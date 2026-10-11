using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class PostgresFixtureCleanupTests
{
    private const string DatabaseName = "controlled_fixture";

    [Fact]
    public async Task ZeroConnectionsCompleteAfterOneObservation()
    {
        var observations = 0;
        var remaining = await PostgresContainerFixture.WaitForNoConnectionsAsync(
            _ => Task.FromResult(Interlocked.Increment(ref observations) == 1 ? 0L : 1L),
            DatabaseName, TimeSpan.FromSeconds(2));

        Assert.Equal(0, remaining);
        Assert.Equal(1, observations);
    }

    [Fact]
    public async Task PendingBackendExitsMustReachZeroBeforeCleanupCompletes()
    {
        var counts = new Queue<long>([2, 1, 0]);
        var observations = 0;
        var remaining = await PostgresContainerFixture.WaitForNoConnectionsAsync(
            _ =>
            {
                observations++;
                return Task.FromResult(counts.Dequeue());
            }, DatabaseName, TimeSpan.FromSeconds(2));

        Assert.Equal(0, remaining);
        Assert.Equal(3, observations);
        Assert.Empty(counts);
    }

    [Fact]
    public async Task LeakedConnectionsFailWithExactDatabaseAndRemainingCount()
    {
        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            PostgresContainerFixture.WaitForNoConnectionsAsync(
                _ => Task.FromResult(7L), DatabaseName, TimeSpan.FromSeconds(1)));

        Assert.Contains($"database '{DatabaseName}'", failure.Message);
        Assert.Contains("remaining connections=7", failure.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
    }

    [Fact]
    public async Task ObservationFailuresAreNotReportedAsSuccessfulCleanup()
    {
        var expected = new InvalidOperationException("controlled observation failure");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgresContainerFixture.WaitForNoConnectionsAsync(
                _ => Task.FromException<long>(expected), DatabaseName, TimeSpan.FromSeconds(2)));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task DeadlineCancelsPendingObservationAndPreservesLastCount()
    {
        var observations = 0;
        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            PostgresContainerFixture.WaitForNoConnectionsAsync(async cancellationToken =>
            {
                if (++observations == 1)
                    return 3;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }, DatabaseName, TimeSpan.FromSeconds(1)));

        Assert.Equal(2, observations);
        Assert.Contains("remaining connections=3", failure.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
    }

    [Fact]
    public async Task CancellationUnrelatedToDeadlineIsNotHidden()
    {
        var expected = new OperationCanceledException("controlled unrelated cancellation");
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            PostgresContainerFixture.WaitForNoConnectionsAsync(
                _ => Task.FromException<long>(expected), DatabaseName, TimeSpan.FromSeconds(2)));

        Assert.Same(expected, actual);
    }
}
