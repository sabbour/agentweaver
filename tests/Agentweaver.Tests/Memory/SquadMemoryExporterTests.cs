using Agentweaver.Squad.Memory;
using FluentAssertions;

namespace Agentweaver.Tests.Memory;

public sealed class SquadMemoryExporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"agentweaver-memory-{Guid.NewGuid():N}");

    [Fact]
    public async Task Export_WritesContextLedgersAndReportsWrittenFiles_WhenNoContextItemsExist()
    {
        var exporter = new SquadMemoryExporter(_directory);

        var files = await exporter.ExportAsync([], [], [], null);

        File.Exists(Path.Combine(_directory, ".agentweaver", "context", "boundaries.md"))
            .Should().BeTrue();
        File.Exists(Path.Combine(_directory, ".agentweaver", "context", "patterns.md"))
            .Should().BeTrue();
        files.Should().Contain(".agentweaver/context/boundaries.md");
        files.Should().Contain(".agentweaver/context/patterns.md");
        files.Should().Contain(".squad/decisions.md");
    }

    [Fact]
    public async Task ExportedDecision_RoundTripsHeadingsInsideItsBody_AsOneStableRecord()
    {
        var exporter = new SquadMemoryExporter(_directory);
        var decision = new DecisionExportDto(
            42,
            "coordinator",
            "architectural",
            "active",
            "Use durable records",
            "The decision body has its own headings.\n\n## Implementation\n\nKeep this content intact.\n\n## Validation\n\nVerify round trips.",
            "Avoid parsing visual headings as ledger boundaries.",
            DateTimeOffset.UtcNow);

        await exporter.ExportAsync([decision], [], [], null);

        var records = new SquadMemoryImporter(_directory).ScanAcceptedDecisions();

        records.Should().ContainSingle();
        records[0].RecordId.Should().Be(42);
        records[0].Content.Should().Be(decision.Content);
        records[0].Rationale.Should().Be(decision.Rationale);
        File.ReadAllText(Path.Combine(_directory, ".squad", "decisions.md"))
            .Should().Contain("agentweaver-decision:id=42");
    }

    [Fact]
    public void ScanInbox_RejectsSymbolicLinks()
    {
        var inbox = Path.Combine(_directory, ".squad", "decisions", "inbox");
        Directory.CreateDirectory(inbox);
        var external = Path.Combine(Path.GetTempPath(), $"agentweaver-external-{Guid.NewGuid():N}.md");
        File.WriteAllText(external, "---\nagent: outside\nslug: outside\ntype: process\ntitle: Outside\n---\n\nOutside");
        try
        {
            try
            {
                File.CreateSymbolicLink(Path.Combine(inbox, "outside.md"), external);
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                return; // Windows without developer mode cannot create the fixture.
            }

            var scan = new SquadMemoryImporter(_directory).ScanInbox();

            scan.Entries.Should().BeEmpty();
            scan.Conflicts.Should().ContainSingle(conflict =>
                conflict.Reason.Contains("symbolic links or reparse points", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(external))
                File.Delete(external);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
