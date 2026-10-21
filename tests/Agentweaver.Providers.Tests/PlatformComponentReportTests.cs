using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.Providers.Tests;

/// <summary>Tests the platform component catalog, report builder, readiness probe, and owner reporting.</summary>
public sealed class PlatformComponentReportTests
{
    private static readonly Version Adapter = new(1, 2, 0);
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static ProviderRegistration Entry(string id, ProviderSeam area, bool enabled = true) =>
        new(new ProviderDescriptor(area, id, Adapter, 1, ProviderHostingPattern.InProcess,
            ImmutableHashSet<string>.Empty), enabled, $"secret-options-revision-{id}", 1);

    private static ProviderCatalog Catalog(
        ProviderRegistration[] entries,
        ProviderSelection[]? defaults = null,
        ProviderOrderedSelection[]? ordered = null,
        ProviderLayerSelection[]? layers = null,
        ProviderMeterSourceSelection[]? meters = null)
    {
        var result = ProviderCatalog.Create(entries, defaults ?? [], [], ordered, layers, meters);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }

    [Fact]
    public void CatalogHasSixteenUniqueAreasWithMatchingProviderCardinality()
    {
        var areas = PlatformComponentCatalog.Areas;
        Assert.Equal(16, areas.Length);
        Assert.Equal(16, areas.Select(area => area.Area).Distinct(StringComparer.Ordinal).Count());
        foreach (var area in areas.Where(area => area.ProviderArea is not null))
        {
            var expected = ProviderSeams.Cardinality(area.ProviderArea!.Value) switch
            {
                ProviderCardinality.Exclusive => PlatformComponentCardinalities.Exclusive,
                ProviderCardinality.OrderedComposite => PlatformComponentCardinalities.OrderedComposite,
                ProviderCardinality.PlatformSingleton => PlatformComponentCardinalities.PlatformSingleton,
                ProviderCardinality.Layered => PlatformComponentCardinalities.Layered,
                ProviderCardinality.KeyedByMeterSource => PlatformComponentCardinalities.KeyedByMeterSource,
                ProviderCardinality.PerApplication => PlatformComponentCardinalities.PerApplication,
                var other => other.ToString(),
            };
            Assert.Equal(expected, area.Cardinality);
        }
        Assert.Equal(PlatformComponentOwners.None, PlatformComponentCatalog.Get(PlatformComponentAreaIds.Canvas).Owner);
    }

    [Fact]
    public void CatalogMatchesArchitectureCatalogTable()
    {
        var rows = ReadArchitectureCatalogRows();
        foreach (var area in PlatformComponentCatalog.Areas)
        {
            Assert.True(rows.TryGetValue(area.Name, out var row), $"Missing architecture row for {area.Name}.");
            Assert.Equal(row.Purpose, area.Purpose);
            Assert.Equal(NormalizeCardinality(row.Cardinality), area.Cardinality);
        }
        Assert.Equal(PlatformComponentCatalog.Areas.Length, rows.Count);
    }

    [Fact]
    public void ComposeAlwaysReturnsEveryAreaAndMarksMissingOwnersUnknown()
    {
        var knowledge = new PlatformComponentOwnerReport(PlatformComponentOwners.Knowledge, Now,
        [
            PlatformComponentCatalogReporter.DescribeArea(
                Catalog([Entry("postgres.native-memory", ProviderSeam.Memory)],
                    [new(ProviderSeam.Memory, "postgres.native-memory")]),
                ProviderSeam.Memory, PlatformComponentReadinessResult.Ready, Now),
        ]);

        var report = PlatformComponentReportBuilder.Compose(
            new Dictionary<string, PlatformComponentOwnerReport?>
            {
                [PlatformComponentOwners.Knowledge] = knowledge,
                [PlatformComponentOwners.Orchestrator] = null,
            },
            Now);

        Assert.Equal(PlatformComponentCatalog.Areas.Select(area => area.Area), report.Areas.Select(area => area.Area));
        var memory = Assert.Single(report.Areas, area => area.Area == PlatformComponentAreaIds.Memory);
        Assert.Equal(PlatformComponentReadiness.Ready, memory.Readiness);
        Assert.Equal("postgres.native-memory", Assert.Single(memory.Active).ImplementationId);
        var canvas = Assert.Single(report.Areas, area => area.Area == PlatformComponentAreaIds.Canvas);
        Assert.Equal(PlatformComponentReadiness.NotAvailable, canvas.Readiness);
        Assert.Equal(PlatformComponentReasons.NoBackendOwner, canvas.Reason);
        foreach (var area in report.Areas.Where(area =>
            area.Owner is not PlatformComponentOwners.Knowledge and not PlatformComponentOwners.None))
        {
            Assert.Equal(PlatformComponentReadiness.Unknown, area.Readiness);
            Assert.Equal(PlatformComponentReasons.OwnerUnavailable, area.Reason);
            Assert.Empty(area.Active);
            Assert.Empty(area.Available);
        }
    }

    [Fact]
    public void ComposeRejectsMismatchedOwnerAndInvalidReadinessAndKeepsCatalogText()
    {
        var spoofed = new PlatformComponentOwnerReport(PlatformComponentOwners.Orchestrator, Now,
        [
            new PlatformComponentAreaReport(PlatformComponentAreaIds.Memory, "changed", "changed", "changed",
                PlatformComponentReadiness.Ready, null, [], []),
        ]);
        var invalid = new PlatformComponentOwnerReport(PlatformComponentOwners.Orchestrator, Now,
        [
            new PlatformComponentAreaReport(PlatformComponentAreaIds.Policy, "changed", "changed", "changed",
                "great", null, [], []),
            new PlatformComponentAreaReport(PlatformComponentAreaIds.Guardrails, "changed", "changed", "changed",
                PlatformComponentReadiness.Disabled, PlatformComponentReasons.NotSelected, [], []),
        ]);

        var report = PlatformComponentReportBuilder.Compose(
            new Dictionary<string, PlatformComponentOwnerReport?>
            {
                [PlatformComponentOwners.Knowledge] = spoofed,
                [PlatformComponentOwners.Orchestrator] = invalid,
            },
            Now);

        var memory = report.Areas.Single(area => area.Area == PlatformComponentAreaIds.Memory);
        Assert.Equal(PlatformComponentReasons.OwnerUnavailable, memory.Reason);
        var policy = report.Areas.Single(area => area.Area == PlatformComponentAreaIds.Policy);
        Assert.Equal(PlatformComponentReadiness.Unknown, policy.Readiness);
        var guardrails = report.Areas.Single(area => area.Area == PlatformComponentAreaIds.Guardrails);
        Assert.Equal(PlatformComponentReadiness.Disabled, guardrails.Readiness);
        Assert.Equal(PlatformComponentCatalog.Get(PlatformComponentAreaIds.Guardrails).Purpose, guardrails.Purpose);
        Assert.Equal(PlatformComponentOwners.Orchestrator, guardrails.Owner);
    }

    [Fact]
    public void DescribeAreaReportsSelectionByCardinalityWithoutOptionRevisions()
    {
        var catalog = Catalog(
        [
            Entry("cilium", ProviderSeam.NetworkPolicy),
            Entry("gateway-l7", ProviderSeam.NetworkPolicy),
            Entry("shield", ProviderSeam.Guardrails),
            Entry("dlp", ProviderSeam.Guardrails),
            Entry("copilot.usage-cost", ProviderSeam.Cost),
            Entry("unused-cost", ProviderSeam.Cost, enabled: false),
        ],
        ordered: [new(ProviderSeam.Guardrails, ["dlp", "shield"])],
        layers: [new(NetworkPolicyLayer.L3L4, "cilium"), new(NetworkPolicyLayer.L7, "gateway-l7")],
        meters: [new("copilot.nano_aiu", "copilot.usage-cost")]);

        var network = PlatformComponentCatalogReporter.DescribeArea(
            catalog, ProviderSeam.NetworkPolicy, PlatformComponentReadinessResult.Ready, Now);
        Assert.Equal(["l3l4", "l7"], network.Active.Select(item => item.Layer));
        var guardrails = PlatformComponentCatalogReporter.DescribeArea(
            catalog, ProviderSeam.Guardrails, PlatformComponentReadinessResult.Ready, Now);
        Assert.Equal(["dlp", "shield"], guardrails.Active.Select(item => item.ImplementationId));
        Assert.Equal([1, 2], guardrails.Active.Select(item => item.Order!.Value));
        var cost = PlatformComponentCatalogReporter.DescribeArea(
            catalog, ProviderSeam.Cost, PlatformComponentReadinessResult.Ready, Now);
        Assert.Equal("copilot.nano_aiu", Assert.Single(cost.Active).MeterSource);
        Assert.Contains(cost.Available, item => item.ImplementationId == "unused-cost" && !item.Enabled);

        var json = JsonSerializer.Serialize(new[] { network, guardrails, cost }, JsonSerializerOptions.Web);
        Assert.DoesNotContain("revision", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-options", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeAreaWithoutSelectionIsDisabledAndWorstReadinessWins()
    {
        var catalog = Catalog([Entry("agent-sandbox", ProviderSeam.Sandbox)]);
        var sandbox = PlatformComponentCatalogReporter.DescribeArea(
            catalog, ProviderSeam.Sandbox, PlatformComponentReadinessResult.Ready, Now);
        Assert.Equal(PlatformComponentReadiness.Disabled, sandbox.Readiness);
        Assert.Equal(PlatformComponentReasons.NotSelected, sandbox.Reason);
        Assert.Single(sandbox.Available);

        var notReady = new PlatformComponentReadinessResult(
            PlatformComponentReadiness.NotReady, PlatformComponentReasons.ReadinessCheckFailed);
        var area = PlatformComponentReportBuilder.Area(PlatformComponentAreaIds.Guardrails,
        [
            PlatformComponentReportBuilder.Active(Entry("a", ProviderSeam.Guardrails).Descriptor,
                PlatformComponentReadinessResult.Ready, Now, order: 1),
            PlatformComponentReportBuilder.Active(Entry("b", ProviderSeam.Guardrails).Descriptor,
                notReady, Now, order: 2),
        ], []);
        Assert.Equal(PlatformComponentReadiness.NotReady, area.Readiness);
        Assert.Equal(PlatformComponentReasons.ReadinessCheckFailed, area.Reason);
    }

    [Fact]
    public void DescribeSelectedUsesBuiltInDescriptorOrDisabled()
    {
        var descriptor = PlatformComponentCatalogReporter.BuiltInDescriptor(
            ProviderSeam.ObjectStore, "azure-blob", typeof(PlatformComponentReportTests));
        var selected = PlatformComponentCatalogReporter.DescribeSelected(
            PlatformComponentAreaIds.ObjectStore, descriptor, PlatformComponentReadinessResult.Ready, Now);
        var active = Assert.Single(selected.Active);
        Assert.Equal("Azure Blob Storage", active.DisplayName);
        Assert.Equal("inProcess", active.Hosting);

        var none = PlatformComponentCatalogReporter.DescribeSelected(
            PlatformComponentAreaIds.ObjectStore, null, PlatformComponentReadinessResult.Ready, Now);
        Assert.Equal(PlatformComponentReadiness.Disabled, none.Readiness);
    }

    [Fact]
    public async Task ReadinessProbeMapsPassFailThrowAndTimeout()
    {
        Assert.Equal(PlatformComponentReadinessResult.Ready,
            await PlatformComponentReadinessProbe.RunAsync(_ => Task.FromResult(true), TimeSpan.FromSeconds(5), default));
        Assert.Equal(PlatformComponentReasons.ReadinessCheckFailed,
            (await PlatformComponentReadinessProbe.RunAsync(_ => Task.FromResult(false), TimeSpan.FromSeconds(5), default)).Reason);
        Assert.Equal(PlatformComponentReadiness.NotReady,
            (await PlatformComponentReadinessProbe.RunAsync(
                _ => throw new InvalidOperationException(), TimeSpan.FromSeconds(5), default)).Readiness);
        var timedOut = await PlatformComponentReadinessProbe.RunAsync(
            async token => { await Task.Delay(Timeout.Infinite, token); return true; },
            TimeSpan.FromMilliseconds(50), default);
        Assert.Equal(PlatformComponentReadiness.Unknown, timedOut.Readiness);
        Assert.Equal(PlatformComponentReasons.ReadinessTimedOut, timedOut.Reason);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlatformComponentReadinessProbe.RunAsync(
            token => Task.FromCanceled<bool>(token), TimeSpan.FromSeconds(5), cancelled.Token));
    }

    [Fact]
    public async Task CreateOwnerReportAppliesReadinessToEveryArea()
    {
        var report = await PlatformComponentCatalogReporter.CreateOwnerReportAsync(
            PlatformComponentOwners.IdentityBroker,
            _ => Task.FromResult(false),
            (readiness, observedAt) =>
            [
                PlatformComponentCatalogReporter.DescribeSelected(PlatformComponentAreaIds.Secrets,
                    PlatformComponentCatalogReporter.BuiltInDescriptor(
                        ProviderSeam.Secrets, "azure-key-vault", typeof(PlatformComponentReportTests)),
                    readiness, observedAt),
            ],
            TimeProvider.System,
            default);

        Assert.Equal(PlatformComponentOwners.IdentityBroker, report.Owner);
        Assert.Equal(PlatformComponentReadiness.NotReady, Assert.Single(report.Areas).Readiness);
    }

    [Fact]
    public void ServiceAuthenticationOptionsAreOptionalButStrict()
    {
        static IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(value =>
                new KeyValuePair<string, string?>($"PlatformComponents:ServiceAuthentication:{value.Key}", value.Value)))
                .Build();

        Assert.Null(PlatformComponentServiceAuthenticationOptions.ReadOptional(Config(), "https://public.example"));
        var options = PlatformComponentServiceAuthenticationOptions.ReadOptional(
            Config(("Authority", "https://issuer.example"), ("Audience", "https://private.example/platform"),
                ("CallerSubject", "system:serviceaccount:agentweaver:projects")),
            "https://public.example");
        Assert.Equal("system:serviceaccount:agentweaver:projects", options!.CallerSubject);

        Assert.Throws<InvalidOperationException>(() => PlatformComponentServiceAuthenticationOptions.ReadOptional(
            Config(("Authority", "https://issuer.example")), "https://public.example"));
        Assert.Throws<InvalidOperationException>(() => PlatformComponentServiceAuthenticationOptions.ReadOptional(
            Config(("Authority", "http://issuer.example"), ("Audience", "https://private.example"),
                ("CallerSubject", "projects")), "https://public.example"));
        Assert.Throws<InvalidOperationException>(() => PlatformComponentServiceAuthenticationOptions.ReadOptional(
            Config(("Authority", "https://issuer.example"), ("Audience", "https://public.example"),
                ("CallerSubject", "projects")), "https://public.example"));
    }

    // Reads the catalog table rows keyed by area name from the architecture design document.
    private static Dictionary<string, (string Purpose, string Cardinality)> ReadArchitectureCatalogRows()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (directory is not null &&
               !File.Exists(path = Path.Combine(directory.FullName, "docs", "architecture", "design", "provider-seams.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var rows = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var inTable = false;
        foreach (var line in File.ReadLines(path!))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inTable)
                    break;
                inTable = line.Equals("## Seam catalog", StringComparison.Ordinal);
                continue;
            }
            if (!inTable || !line.StartsWith('|') || line.StartsWith("| ---", StringComparison.Ordinal) ||
                line.StartsWith("| Seam ", StringComparison.Ordinal))
                continue;
            var cells = line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            rows[cells[0]] = (cells[1], cells[5]);
        }
        return rows;
    }

    // Maps the table's descriptive cardinality text to the stable report identifier.
    private static string NormalizeCardinality(string text) =>
        text.StartsWith("Exclusive", StringComparison.Ordinal) ? PlatformComponentCardinalities.Exclusive
        : text.StartsWith("Ordered composite", StringComparison.Ordinal) ? PlatformComponentCardinalities.OrderedComposite
        : text.StartsWith("Platform-singleton", StringComparison.Ordinal) ? PlatformComponentCardinalities.PlatformSingleton
        : text.StartsWith("Layered", StringComparison.Ordinal) ? PlatformComponentCardinalities.Layered
        : text.StartsWith("Keyed by meter source", StringComparison.Ordinal) ? PlatformComponentCardinalities.KeyedByMeterSource
        : text.StartsWith("Per application", StringComparison.Ordinal) ? PlatformComponentCardinalities.PerApplication
        : text.StartsWith("Per canvas", StringComparison.Ordinal) ? PlatformComponentCardinalities.PerCanvas
        : text;
}
