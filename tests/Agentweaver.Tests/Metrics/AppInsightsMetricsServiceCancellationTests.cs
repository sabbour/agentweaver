using System.Reflection;
using Agentweaver.Api.Metrics;
using Agentweaver.Tests.Helpers;
using Azure;
using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Agentweaver.Tests.Metrics;

/// <summary>
/// Regression tests for issue #208: <c>AppInsightsMetricsService.QueryAsync</c> must not classify
/// caller-requested cancellation as a genuine Application Insights dependency failure (no Warning/Error
/// telemetry), and genuine failures across the 8-way <see cref="AppInsightsMetricsService.GetProjectMetricsAsync"/>
/// fan-out must be logged once per batch, not once per subquery.
/// </summary>
public class AppInsightsMetricsServiceCancellationTests
{
    private static AppInsightsMetricsService CreateService(LogsQueryClient fakeClient, CapturingLogger logger)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "WorkspaceId=fake-workspace-id;",
            })
            .Build();

        var service = new AppInsightsMetricsService(configuration, new TypedLoggerAdapter(logger));
        service.SetClientForTesting(fakeClient);
        return service;
    }

    /// <summary>Adapts the untyped <see cref="CapturingLogger"/> (<c>ILogger&lt;object&gt;</c>) to the
    /// <c>ILogger&lt;AppInsightsMetricsService&gt;</c> the constructor requires, forwarding every call so
    /// assertions can still inspect <see cref="CapturingLogger.Entries"/>.</summary>
    private sealed class TypedLoggerAdapter(CapturingLogger inner) : ILogger<AppInsightsMetricsService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    [Fact]
    public async Task GetProjectMetricsAsync_WhenTokenCanceled_LogsNoWarningOrError()
    {
        using var cts = new CancellationTokenSource();
        var fakeClient = new CancelingLogsQueryClient(cts);
        var logger = new CapturingLogger();
        var service = CreateService(fakeClient, logger);

        // The fake client cancels the token itself on first call, simulating a request-aborted
        // (HttpContext.RequestAborted) scenario: the query throws OperationCanceledException tied to the
        // very token the caller supplied.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetProjectMetricsAsync("project-1", from: null, to: null, cts.Token));

        Assert.False(
            logger.Entries.Any(e => e.Level is LogLevel.Warning or LogLevel.Error),
            $"Expected no Warning/Error log entries for caller-requested cancellation, but found: " +
            string.Join(" | ", logger.Entries.Select(e => $"[{e.Level}] {e.Message}")));
    }

    [Fact]
    public async Task GetProjectMetricsAsync_WhenSubqueriesFailGenuinely_LogsOncePerBatchNotPerSubquery()
    {
        var fakeClient = new AlwaysThrowingLogsQueryClient(new InvalidOperationException("simulated Azure Monitor outage"));
        var logger = new CapturingLogger();
        var service = CreateService(fakeClient, logger);

        var result = await service.GetProjectMetricsAsync("project-1", from: null, to: null, CancellationToken.None);

        Assert.NotNull(result);

        var errorEntries = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Single(errorEntries);
        Assert.Contains("genuine (non-cancellation)", errorEntries[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetProjectMetricsAsync", errorEntries[0].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fake <see cref="LogsQueryClient"/> that cancels the supplied <see cref="CancellationTokenSource"/>
    /// and throws <see cref="OperationCanceledException"/> tied to that same token — mirroring a request
    /// abort mid-query. Uses the SDK's mockable-client pattern (protected parameterless ctor + virtual
    /// members) so no real Azure credential or workspace is required.
    /// </summary>
    private sealed class CancelingLogsQueryClient : LogsQueryClient
    {
        private readonly CancellationTokenSource _cts;

        public CancelingLogsQueryClient(CancellationTokenSource cts) : base()
        {
            _cts = cts;
        }

        public override Task<Response<LogsQueryResult>> QueryWorkspaceAsync(
            string workspaceId,
            string query,
            QueryTimeRange timeRange,
            LogsQueryOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>Fake <see cref="LogsQueryClient"/> that always throws a fixed non-cancellation exception,
    /// simulating a genuine Azure Monitor dependency failure on every subquery in the fan-out.</summary>
    private sealed class AlwaysThrowingLogsQueryClient : LogsQueryClient
    {
        private readonly Exception _exception;

        public AlwaysThrowingLogsQueryClient(Exception exception) : base()
        {
            _exception = exception;
        }

        public override Task<Response<LogsQueryResult>> QueryWorkspaceAsync(
            string workspaceId,
            string query,
            QueryTimeRange timeRange,
            LogsQueryOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw _exception;
        }
    }
}
