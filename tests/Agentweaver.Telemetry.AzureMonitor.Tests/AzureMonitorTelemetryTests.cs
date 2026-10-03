using System.Diagnostics;
using System.Net;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Monitor.OpenTelemetry.Exporter;
using Agentweaver.Telemetry;
using Agentweaver.Telemetry.AzureMonitor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Agentweaver.Telemetry.AzureMonitor.Tests;

public sealed class AzureMonitorTelemetryTests
{
    private const string ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://example.invalid/";

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("InstrumentationKey=not-a-guid")]
    [InlineData("InstrumentationKey=00000000-0000-0000-0000-000000000001;InstrumentationKey=00000000-0000-0000-0000-000000000002")]
    [InlineData("InstrumentationKey=00000000-0000-0000-0000-000000000001;BadField")]
    public void RejectsInvalidConfigurationBeforeRegisteringAnything(string connectionString)
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() =>
            services.AddAgentweaverAzureMonitorTelemetry("test", connectionString));
        Assert.Empty(services);
    }

    [Fact]
    public void RejectsMissingServiceIdentityAndServices()
    {
        Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddAgentweaverAzureMonitorTelemetry(" ", ConnectionString));
        Assert.Throws<ArgumentNullException>(() =>
            AzureMonitorTelemetryExtensions.AddAgentweaverAzureMonitorTelemetry(null!, "test", ConnectionString));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void AllSignalsComposeWithInjectedCredentialAndOtherExportersWithoutNetwork(HttpStatusCode status)
    {
        var handler = new RecordingHandler(status);
        var credential = new RecordingCredential();
        var optionsSeen = new List<AzureMonitorExporterOptions>();
        var traces = new RecordingExporter<Activity>();
        var metrics = new RecordingExporter<Metric>();
        var logs = new RecordingExporter<LogRecord>();
        var services = new ServiceCollection()
            .AddAgentweaverAzureMonitorTelemetry("agentweaver.test", ConnectionString, credential,
                options =>
                {
                    options.Transport = new HttpClientTransport(new HttpClient(handler, disposeHandler: false));
                    options.Retry.MaxRetries = 0;
                    options.DisableOfflineStorage = true;
                    options.SamplingRatio = 1.0F;
                    optionsSeen.Add(options);
                },
                configureTracing: tracing => tracing.SetSampler(new AlwaysOnSampler())
                    .AddProcessor(new SimpleActivityExportProcessor(traces)),
                configureMetrics: builder => builder.AddReader(new PeriodicExportingMetricReader(metrics, 60000)),
                configureLogging: logging => logging.AddProcessor(new SimpleLogRecordExportProcessor(logs)))
            .BuildServiceProvider();
        var tracer = services.GetRequiredService<TracerProvider>();
        var meter = services.GetRequiredService<MeterProvider>();
        using (var activity = TelemetrySignals.Activities.StartActivity("test.operation"))
        {
            Assert.NotNull(activity);
            Assert.True(activity.Recorded);
        }
        TelemetrySignals.Metrics.CreateCounter<long>("test.counter").Add(1);
        services.GetRequiredService<ILoggerFactory>().CreateLogger("test")
            .LogWarning("safe event");
        Assert.True(tracer.ForceFlush());
        meter.ForceFlush();
        services.Dispose();
        Assert.Single(traces.Items);
        Assert.Contains(metrics.Items, metric => metric.Name == "test.counter");
        Assert.Single(logs.Items);

        Assert.Equal(3, optionsSeen.Count);
        Assert.All(optionsSeen, options =>
        {
            Assert.Equal(ConnectionString, options.ConnectionString);
            Assert.Same(credential, options.Credential);
        });
        Assert.True(handler.Requests >= 3, $"Expected all three SDK signal exporters, got {handler.Requests} requests.");
        Assert.True(credential.Calls >= 1);
    }

    private sealed class RecordingExporter<T> : BaseExporter<T> where T : class
    {
        public List<T> Items { get; } = [];

        public override ExportResult Export(in Batch<T> batch)
        {
            foreach (var item in batch)
                Items.Add(item);
            return ExportResult.Success;
        }
    }

    private sealed class RecordingCredential : TokenCredential
    {
        public int Calls { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        private int requests;
        public int Requests => Volatile.Read(ref requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"itemsReceived\":1,\"itemsAccepted\":1,\"errors\":[]}")
            });
        }
    }
}
