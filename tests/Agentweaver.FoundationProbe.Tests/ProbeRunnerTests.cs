using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Agentweaver.FoundationProbe;
using Agentweaver.Telemetry;
using Agentweaver.Telemetry.AzureMonitor;
using Azure.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Xunit;

namespace Agentweaver.FoundationProbe.Tests;

public sealed class ProbeRunnerTests
{
    [Fact]
    public async Task HostSamplingExportsTheExactProbeRequestEnvelopeWithoutNetwork()
    {
        var handler = new EnvelopeHandler();
        using var client = new HttpClient(handler);
        await using var services = new ServiceCollection()
            .AddAgentweaverAzureMonitorTelemetry("foundation-probe",
                "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://example.invalid/",
                configureExporter: options =>
                {
                    options.Transport = new HttpClientTransport(client);
                    options.Retry.MaxRetries = 0;
                    options.DisableOfflineStorage = true;
                },
                configureTracing: Program.ConfigureTracing)
            .BuildServiceProvider();
        var tracer = services.GetRequiredService<TracerProvider>();
        var target = ProbeFixtures.Target();
        var runner = new ProbeRunner(new FakeOperations(target), () => tracer.ForceFlush(),
            () => new string('e', 32));

        var receipt = await runner.RunAsync(target, ProbeFixtures.Source, ProbeFixtures.Identity(target),
            ProbeFixtures.MonitorConfiguration(target), CancellationToken.None);

        var envelope = Assert.Single(handler.Envelopes, item =>
            item.GetProperty("data").GetProperty("baseType").GetString() == "RequestData");
        var data = envelope.GetProperty("data").GetProperty("baseData");
        Assert.Equal(receipt.Telemetry.Name, data.GetProperty("name").GetString());
        Assert.Equal(receipt.Telemetry.SpanId, data.GetProperty("id").GetString());
        Assert.Equal(receipt.Telemetry.TraceId, envelope.GetProperty("tags").GetProperty("ai.operation.id").GetString());
        Assert.Equal(receipt.Telemetry.StartedAt, envelope.GetProperty("time").GetDateTimeOffset());
        var properties = data.GetProperty("properties");
        Assert.Equal(receipt.SourceSha, properties.GetProperty("probe.source_sha").GetString());
        Assert.Equal(receipt.SourceTree, properties.GetProperty("probe.source_tree").GetString());
        Assert.Equal(receipt.Nonce, properties.GetProperty("probe.nonce").GetString());
    }

    [Fact]
    public async Task SuccessfulProbeBindsFreshNonceProviderPinsAndMonitorCompatibleActivity()
    {
        var target = ProbeFixtures.Target();
        var operations = new FakeOperations(target);
        Activity? stopped = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySignals.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped = activity,
        };
        ActivitySource.AddActivityListener(listener);
        var runner = new ProbeRunner(operations, () => true, () => new string('e', 32));

        var receipt = await runner.RunAsync(target, ProbeFixtures.Source, ProbeFixtures.Identity(target),
            ProbeFixtures.MonitorConfiguration(target), CancellationToken.None);

        Assert.Equal(ProbeFixtures.Source.Sha, receipt.SourceSha);
        Assert.Equal(ProbeFixtures.Source.Tree, receipt.SourceTree);
        Assert.Matches("^[0-9a-f]{40}$", receipt.SourceTree);
        Assert.Equal(target.SourceHash, receipt.SourceHash);
        Assert.NotEqual(receipt.SourceTree, receipt.SourceHash);
        Assert.Equal(new string('e', 32), receipt.Nonce);
        Assert.Matches("^[0-9a-f]{32}$", receipt.Nonce);
        Assert.Equal(target.ResourceGroupId, receipt.Deployment.ResourceGroupId);
        Assert.Equal(target.AksOidcIssuerUrl, receipt.WorkloadIdentity.Issuer);
        Assert.Equal(target.FoundationResources.AppInsightsResourceId, receipt.MonitorConfiguration.AppInsightsResourceId);
        Assert.Equal("https://eastus-1.in.applicationinsights.azure.com/",
            receipt.MonitorConfiguration.IngestionEndpoint);
        Assert.True(receipt.MonitorConfiguration.InstrumentationKeyConfigured);
        Assert.Equal(target.FoundationResources.KeyVaultId, receipt.KeyVault.ResourceId);
        Assert.Equal(target.Runtime.KeyVaultSecretVersion, receipt.KeyVault.SecretVersion);
        Assert.True(receipt.KeyVault.Redeemed);
        Assert.Equal(new[] { "Secrets", "ObjectStore", "Telemetry" },
            receipt.ProviderBindings.Select(binding => binding.Seam));
        Assert.Equal("foundation-probe", receipt.Telemetry.Name);
        Assert.Equal(32, receipt.Telemetry.TraceId.Length);
        Assert.Equal(16, receipt.Telemetry.SpanId.Length);
        Assert.NotNull(stopped);
        Assert.Equal("foundation-probe", stopped!.DisplayName);
        Assert.Equal(ProbeFixtures.Source.Sha, stopped.GetTagItem("probe.source_sha"));
        Assert.Equal(ProbeFixtures.Source.Tree, stopped.GetTagItem("probe.source_tree"));
        Assert.Equal(new string('e', 32), stopped.GetTagItem("probe.nonce"));
        Assert.Equal("Admitted", receipt.Postgres.InboxDisposition);
        Assert.True(receipt.Blob.CleanupConfirmed);
    }

    [Fact]
    public async Task UnrecordedActivityFailsBeforeAnyResourceOperationOrFlush()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySignals.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        var target = ProbeFixtures.Target();
        var operations = new FakeOperations(target);
        var flushed = false;
        var runner = new ProbeRunner(operations, () => flushed = true);

        var failure = await Assert.ThrowsAsync<ProbeException>(() =>
            runner.RunAsync(target, ProbeFixtures.Source, ProbeFixtures.Identity(target),
                ProbeFixtures.MonitorConfiguration(target), CancellationToken.None));

        Assert.Equal("telemetry_activity_not_recorded", failure.Code);
        Assert.Equal(0, operations.SecretCalls);
        Assert.Equal(0, operations.BlobCalls);
        Assert.Equal(0, operations.PostgresCalls);
        Assert.False(flushed);
    }

    [Fact]
    public async Task SourceOrIdentityMismatchFailsBeforeAnyResourceOperation()
    {
        using var listener = Listen();
        var target = ProbeFixtures.Target();
        var operations = new FakeOperations(target);
        var runner = new ProbeRunner(operations, () => true, () => new string('e', 32));

        Assert.Equal("source_mismatch", (await Assert.ThrowsAsync<ProbeException>(() =>
            runner.RunAsync(target with { SourceTree = new string('f', 40) }, ProbeFixtures.Source,
                ProbeFixtures.Identity(target), ProbeFixtures.MonitorConfiguration(target), CancellationToken.None))).Code);
        Assert.Equal("workload_identity_claim_mismatch", (await Assert.ThrowsAsync<ProbeException>(() =>
            runner.RunAsync(target, ProbeFixtures.Source,
                ProbeFixtures.Identity(target) with { Audience = "wrong" },
                ProbeFixtures.MonitorConfiguration(target), CancellationToken.None))).Code);
        Assert.Equal("monitor_configuration_invalid", (await Assert.ThrowsAsync<ProbeException>(() =>
            runner.RunAsync(target, ProbeFixtures.Source, ProbeFixtures.Identity(target),
                ProbeFixtures.MonitorConfiguration(target) with
                {
                    AppInsightsResourceId = "/subscriptions/other/providers/Microsoft.Insights/components/other",
                }, CancellationToken.None))).Code);
        Assert.Equal(0, operations.SecretCalls);
    }

    [Fact]
    public async Task SecretFailureStopsTheProbeAndCancellationDoesNotProduceReceipt()
    {
        using var listener = Listen();
        var target = ProbeFixtures.Target();
        var operations = new FakeOperations(target) { SecretFailure = new ProbeException("secret_denied") };
        var runner = new ProbeRunner(operations, () => true, () => new string('e', 32));
        var failure = await Assert.ThrowsAsync<ProbeException>(() =>
            runner.RunAsync(target, ProbeFixtures.Source, ProbeFixtures.Identity(target),
                ProbeFixtures.MonitorConfiguration(target), CancellationToken.None));
        Assert.Equal("secret_denied", failure.Code);
        Assert.Equal(0, operations.BlobCalls);
        Assert.Equal(0, operations.PostgresCalls);

        operations.SecretFailure = null;
        operations.CancelDuringBlob = true;
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(target, ProbeFixtures.Source, ProbeFixtures.Identity(target),
                ProbeFixtures.MonitorConfiguration(target), cancellation.Token));
        Assert.Equal(0, operations.PostgresCalls);
    }

    [Fact]
    public async Task CleanupAndExternalTelemetryFailuresBlockReceipt()
    {
        using var listener = Listen();
        var target = ProbeFixtures.Target();
        var operations = new FakeOperations(target) { UncleanBlob = true };
        var runner = new ProbeRunner(operations, () => true, () => new string('e', 32));
        Assert.Equal("blob_evidence_mismatch", (await Assert.ThrowsAsync<ProbeException>(() =>
            runner.RunAsync(target, ProbeFixtures.Source, ProbeFixtures.Identity(target),
                ProbeFixtures.MonitorConfiguration(target), CancellationToken.None))).Code);
        Assert.Equal(0, operations.PostgresCalls);

        operations.UncleanBlob = false;
        var telemetryFailure = new ProbeRunner(operations, () => false, () => new string('e', 32));
        Assert.Equal("telemetry_flush_failed", (await Assert.ThrowsAsync<ProbeException>(() =>
            telemetryFailure.RunAsync(target, ProbeFixtures.Source, ProbeFixtures.Identity(target),
                ProbeFixtures.MonitorConfiguration(target), CancellationToken.None))).Code);
    }

    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySignals.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class EnvelopeHandler : HttpMessageHandler
    {
        public List<JsonElement> Envelopes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("example.invalid", request.RequestUri!.Host);
            await using var stream = await request.Content!.ReadAsStreamAsync(cancellationToken);
            using var gzip = request.Content.Headers.ContentEncoding.Contains("gzip")
                ? new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true) : null;
            using var reader = new StreamReader(gzip is null ? stream : gzip);
            var content = await reader.ReadToEndAsync(cancellationToken);
            if (content.TrimStart().StartsWith('['))
            {
                using var document = JsonDocument.Parse(content);
                Envelopes.AddRange(document.RootElement.EnumerateArray().Select(item => item.Clone()));
            }
            else
            {
                foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    using var document = JsonDocument.Parse(line);
                    Envelopes.Add(document.RootElement.Clone());
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"itemsReceived\":1,\"itemsAccepted\":1,\"errors\":[]}"),
            };
        }
    }

    private sealed class FakeOperations(ProbeTarget target) : IProbeOperations
    {
        public int SecretCalls { get; private set; }
        public int BlobCalls { get; private set; }
        public int PostgresCalls { get; private set; }
        public bool UncleanBlob { get; set; }
        public bool CancelDuringBlob { get; set; }
        public Exception? SecretFailure { get; set; }

        public Task<KeyVaultEvidence> RedeemKeyVaultAsync(
            ProbeTarget value, string nonce, CancellationToken cancellationToken)
        {
            SecretCalls++;
            Assert.Same(target, value);
            if (SecretFailure is not null) throw SecretFailure;
            return Task.FromResult(new KeyVaultEvidence(
                target.FoundationResources.KeyVaultId,
                target.FoundationResources.VaultUri,
                target.Runtime.KeyVaultSecretName,
                target.Runtime.KeyVaultSecretVersion,
                true));
        }

        public Task<BlobEvidence> RunBlobRoundTripAsync(
            ProbeTarget value, ProbeSource source, string nonce, CancellationToken cancellationToken)
        {
            BlobCalls++;
            Assert.Same(target, value);
            if (CancelDuringBlob)
                throw new OperationCanceledException(cancellationToken);
            return Task.FromResult(new BlobEvidence(
                target.FoundationResources.BlobContainerId,
                target.FoundationResources.BlobContainerUri,
                $"foundation-probe/{nonce}/roundtrip.json",
                nonce,
                "\"etag\"",
                new string('f', 64),
                !UncleanBlob));
        }

        public Task<PostgresEvidence> WritePostgresEffectsAsync(
            ProbeTarget value, ProbeSource source, string nonce, CancellationToken cancellationToken)
        {
            PostgresCalls++;
            Assert.Same(target, value);
            return Task.FromResult(new PostgresEvidence(
                target.FoundationResources.PostgresServerId,
                target.FoundationResources.PostgresHost,
                target.Runtime.DatabaseName,
                target.Runtime.SchemaName,
                target.Runtime.DatabaseRole,
                Guid.NewGuid().ToString("D"),
                nonce,
                "Admitted",
                Guid.NewGuid().ToString("D"),
                1,
                true));
        }
    }
}
