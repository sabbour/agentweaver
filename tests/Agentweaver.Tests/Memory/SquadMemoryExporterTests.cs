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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
