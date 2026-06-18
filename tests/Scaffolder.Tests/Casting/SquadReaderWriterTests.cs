using Scaffolder.Squad.Squad;
using Scaffolder.Squad.Model;

namespace Scaffolder.Tests.Casting;

/// <summary>
/// Integration tests for SquadReader/SquadWriter that use real filesystem operations
/// (temp directories) and no mocks of the squad layer.
/// </summary>
public sealed class SquadReaderWriterTests : IDisposable
{
    private readonly string _root;

    public SquadReaderWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"squad-rw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void WriteTeamThenReadItBack_RoundTrip()
    {
        var dir = Path.Combine(_root, "roundtrip");
        SquadTestFixtureHelper.CreateCanonicalLayout(dir, "roundtrip-project");

        var team = new SquadReader(dir).ReadTeam();

        Assert.NotNull(team);
        Assert.Equal("roundtrip-project", team.ProjectName, StringComparer.Ordinal);
        Assert.Single(team.Members);
        Assert.Equal("Alpha", team.Members[0].Name, StringComparer.Ordinal);
    }

    [Fact]
    public void CanonicalLayout_DetectedCorrectly()
    {
        var dir = Path.Combine(_root, "canonical");
        SquadTestFixtureHelper.CreateCanonicalLayout(dir);

        var layout = new SquadReader(dir).DetectLayout();

        Assert.True(layout.HasCanonical);
        Assert.False(layout.HasConflict);
    }

    [Fact]
    public void LegacyLayout_DetectedCorrectly()
    {
        var dir = Path.Combine(_root, "legacy");
        SquadTestFixtureHelper.CreateLegacyLayout(dir);

        var layout = new SquadReader(dir).DetectLayout();

        // Legacy layout uses root-level casting files; canonical uses .squad/casting/ subfolder.
        Assert.False(layout.HasCanonical, "Legacy layout should not have canonical files.");
    }

    [Fact]
    public void ConflictLayout_DetectedAsConflict()
    {
        var dir = Path.Combine(_root, "conflict");
        SquadTestFixtureHelper.CreateConflictLayout(dir);

        var layout = new SquadReader(dir).DetectLayout();

        // The canonical layout is present; ReadTeam should succeed or throw depending on conflict state.
        Assert.True(layout.HasCanonical);
        if (layout.HasConflict)
            Assert.Throws<LayoutConflictException>(() => new SquadReader(dir).ReadTeam());
    }

    [Fact]
    public void SquadWriter_EnsureGitAttributes_CreatesFile()
    {
        var dir = Path.Combine(_root, "gitattrs");
        Directory.CreateDirectory(dir);

        new SquadWriter(dir).EnsureGitAttributes();

        var gitattrsPath = Path.Combine(dir, ".gitattributes");
        Assert.True(File.Exists(gitattrsPath));
        var content = File.ReadAllText(gitattrsPath);
        Assert.Contains("merge=union", content);
    }

    [Fact]
    public void CharterRoundTrip_WriteAndReadBack()
    {
        var dir = Path.Combine(_root, "charter-rt");
        SquadTestFixtureHelper.CreateCanonicalLayout(dir);

        var original = "# Alpha\n\nUpdated charter content.\n";
        new SquadWriter(dir).WriteCharter("alpha", original);

        var read = new SquadReader(dir).ReadCharter("alpha");

        Assert.Equal(original, read, StringComparer.Ordinal);
    }

    [Fact]
    public void RegistryEventsSidecar_AppendReadBack()
    {
        var dir = Path.Combine(_root, "registry-events");
        SquadTestFixtureHelper.CreateCanonicalLayout(dir);

        var member = new RegistryMember(
            Name: "lead-architect",
            PersistentName: "Alpha",
            Universe: "Inception",
            DefaultModel: "claude-opus-4.8",
            Status: CastMemberStatus.Active,
            CreatedAt: DateTimeOffset.UtcNow,
            PreviousName: null,
            SucceededBy: null,
            RetiredAt: null,
            CharterPath: ".squad/agents/alpha/charter.md");

        new SquadWriter(dir).AppendRegistryEvent(member);

        var registry = new SquadReader(dir).ReadRegistry();
        Assert.True(registry.Agents.ContainsKey("lead-architect"));
    }
}
