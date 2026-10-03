using System.Diagnostics;
using Agentweaver.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Agentweaver.Telemetry.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public void NativeExportersReceiveAllSignalsAndServiceIdentity()
    {
        var traces = new RecordingExporter<Activity>();
        var metrics = new RecordingExporter<Metric>();
        var logs = new RecordingExporter<LogRecord>();
        using var services = new ServiceCollection()
            .AddAgentweaverTelemetry("agentweaver.test",
                tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(traces)),
                metric => metric.AddReader(new PeriodicExportingMetricReader(metrics, 60000)),
                logging => logging.AddProcessor(new SimpleLogRecordExportProcessor(logs)))
            .BuildServiceProvider();
        var tracer = services.GetRequiredService<TracerProvider>();
        var meter = services.GetRequiredService<MeterProvider>();
        using (var activity = TelemetrySignals.Activities.StartActivity("provider.resolve"))
        {
            Assert.NotNull(activity);
            activity.SetTag("provider.seam", "Compute");
        }
        TelemetrySignals.Metrics.CreateCounter<long>("agentweaver.test.operations")
            .Add(1, new KeyValuePair<string, object?>("provider.seam", "Compute"));
        services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Agentweaver.Tests")
            .LogInformation("Provider resolved");
        Assert.True(tracer.ForceFlush());
        Assert.True(meter.ForceFlush());

        Assert.Contains(traces.Items, a => a.OperationName == "provider.resolve" &&
            a.GetTagItem("provider.seam")?.ToString() == "Compute");
        Assert.Contains(metrics.Items, metric => metric.Name == "agentweaver.test.operations");
        Assert.NotEmpty(logs.Items);
        Assert.All(traces.ServiceNames, name => Assert.Equal("agentweaver.test", name));
        Assert.All(metrics.ServiceNames, name => Assert.Equal("agentweaver.test", name));
        Assert.All(logs.ServiceNames, name => Assert.Equal("agentweaver.test", name));
    }

    [Fact]
    public void FailedExportersCannotInterruptWorkOrHealthySignalPipelines()
    {
        var healthyTraces = new RecordingExporter<Activity>();
        var healthyMetrics = new RecordingExporter<Metric>();
        var healthyLogs = new RecordingExporter<LogRecord>();
        var failedTraces = new RecordingExporter<Activity>(fail: true);
        var throwingTraces = new RecordingExporter<Activity>(throwOnExport: true);
        var failedMetrics = new RecordingExporter<Metric>(fail: true);
        var throwingMetrics = new RecordingExporter<Metric>(throwOnExport: true);
        var failedLogs = new RecordingExporter<LogRecord>(fail: true);
        var throwingLogs = new RecordingExporter<LogRecord>(throwOnExport: true);
        using var services = new ServiceCollection()
            .AddAgentweaverTelemetry("agentweaver.test",
                tracing => tracing
                    .AddProcessor(new SimpleActivityExportProcessor(failedTraces))
                    .AddProcessor(new SimpleActivityExportProcessor(throwingTraces))
                    .AddProcessor(new SimpleActivityExportProcessor(healthyTraces)),
                metrics => metrics
                    .AddReader(new PeriodicExportingMetricReader(failedMetrics, 60000))
                    .AddReader(new PeriodicExportingMetricReader(throwingMetrics, 60000))
                    .AddReader(new PeriodicExportingMetricReader(healthyMetrics, 60000)),
                logging => logging
                    .AddProcessor(new SimpleLogRecordExportProcessor(failedLogs))
                    .AddProcessor(new SimpleLogRecordExportProcessor(throwingLogs))
                    .AddProcessor(new SimpleLogRecordExportProcessor(healthyLogs)))
            .BuildServiceProvider();
        var tracer = services.GetRequiredService<TracerProvider>();
        var meter = services.GetRequiredService<MeterProvider>();
        using (var activity = TelemetrySignals.Activities.StartActivity("provider.resolve"))
            Assert.NotNull(activity);
        TelemetrySignals.Metrics.CreateCounter<long>("agentweaver.test.isolation")
            .Add(1, new KeyValuePair<string, object?>("provider.seam", "Compute"));
        services.GetRequiredService<ILoggerFactory>().CreateLogger("Agentweaver.Tests")
            .LogInformation("Provider resolved");

        Assert.Single(healthyTraces.Items);
        tracer.ForceFlush();
        meter.ForceFlush();
        Assert.True(failedTraces.ExportCalls > 0);
        Assert.True(throwingTraces.ExportCalls > 0);
        Assert.True(failedMetrics.ExportCalls > 0);
        Assert.True(throwingMetrics.ExportCalls > 0);
        Assert.True(failedLogs.ExportCalls > 0);
        Assert.True(throwingLogs.ExportCalls > 0);
        Assert.Contains(healthyMetrics.Items, metric => metric.Name == "agentweaver.test.isolation");
        Assert.Single(healthyLogs.Items);
    }

    [Fact]
    public void DisposalStopsAllSignalDelivery()
    {
        var traces = new RecordingExporter<Activity>();
        var metrics = new RecordingExporter<Metric>();
        var logs = new RecordingExporter<LogRecord>();
        var services = new ServiceCollection()
            .AddAgentweaverTelemetry("agentweaver.test",
                tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(traces)),
                metric => metric.AddReader(new PeriodicExportingMetricReader(metrics, 60000)),
                logging => logging.AddProcessor(new SimpleLogRecordExportProcessor(logs)))
            .BuildServiceProvider();
        var tracer = services.GetRequiredService<TracerProvider>();
        var meter = services.GetRequiredService<MeterProvider>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Agentweaver.Tests");
        var counter = TelemetrySignals.Metrics.CreateCounter<long>("agentweaver.test.disposal");
        using (var activity = TelemetrySignals.Activities.StartActivity("provider.resolve"))
            Assert.NotNull(activity);
        counter.Add(1);
        logger.LogInformation("Before disposal");
        Assert.True(tracer.ForceFlush());
        Assert.True(meter.ForceFlush());
        Assert.Single(traces.Items);
        Assert.Contains(metrics.Items, metric => metric.Name == "agentweaver.test.disposal");
        Assert.Single(logs.Items);
        services.Dispose();
        var traceCount = traces.Items.Count;
        var metricCount = metrics.Items.Count;
        var logCount = logs.Items.Count;
        using (TelemetrySignals.Activities.StartActivity("provider.resolve"))
        {
        }
        counter.Add(1);
        logger.LogInformation("After disposal");
        Assert.Equal(traceCount, traces.Items.Count);
        Assert.Equal(metricCount, metrics.Items.Count);
        Assert.Equal(logCount, logs.Items.Count);
    }

    [Fact]
    public void RejectsMissingServiceIdentity()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddAgentweaverTelemetry(" "));
    }

    private sealed class RecordingExporter<T>(bool fail = false, bool throwOnExport = false) : BaseExporter<T> where T : class
    {
        public List<T> Items { get; } = [];
        public List<string?> ServiceNames { get; } = [];
        public int ExportCalls { get; private set; }

        public override ExportResult Export(in Batch<T> batch)
        {
            ExportCalls++;
            if (throwOnExport)
                throw new InvalidOperationException("Exporter unavailable");
            foreach (var item in batch)
            {
                Items.Add(item);
                ServiceNames.Add(ParentProvider.GetResource().Attributes
                    .FirstOrDefault(attribute => attribute.Key == "service.name").Value?.ToString());
            }
            return fail ? ExportResult.Failure : ExportResult.Success;
        }
    }
}
