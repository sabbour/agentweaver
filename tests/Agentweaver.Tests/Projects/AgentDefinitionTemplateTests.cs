using FluentAssertions;
using Agentweaver.Api.Projects;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Guards for the embedded agent definition (agent-file-gen):
///   1. The embedded API template never drifts from the committed repo file
///      <c>.github/agents/agentweaver.agent.md</c> (the two copies are kept identical by
///      <c>scripts/gen-docs.mjs</c>, so a stale embedded copy is a build-time bug).
///   2. <see cref="AgentDefinitionTemplate.TryMaterialize"/> writes the file into a project
///      working directory, is non-clobbering, and is idempotent.
/// </summary>
public sealed class AgentDefinitionTemplateTests : IDisposable
{
    private readonly string _testRoot;

    public AgentDefinitionTemplateTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"agentweaver-agentdef-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testRoot, recursive: true); } catch { /* best effort */ }
    }

    // =========================================================================
    // AD-01: The embedded template equals the committed .github agent file.
    // =========================================================================
    [Fact]
    public void EmbeddedTemplate_MatchesCommittedRepoFile()
    {
        var repoFile = FindRepoFile(".github/agents/agentweaver.agent.md");
        repoFile.Should().NotBeNull(
            "the committed .github/agents/agentweaver.agent.md must be discoverable from the test output dir");

        var committed = Normalize(File.ReadAllText(repoFile!));
        var embedded = Normalize(AgentDefinitionTemplate.Content);

        embedded.Should().Be(committed,
            "the embedded API template must be a byte-identical copy of the committed agent file " +
            "(run 'node scripts/gen-docs.mjs' and commit if this fails)");
    }

    // =========================================================================
    // AD-02: TryMaterialize writes the file into a fresh project dir.
    // =========================================================================
    [Fact]
    public void TryMaterialize_WritesFileIntoProjectDir()
    {
        var dir = NewDir();

        var written = AgentDefinitionTemplate.TryMaterialize(dir, out var error);

        written.Should().BeTrue();
        error.Should().BeNull();

        var path = Path.Combine(dir, ".github", "agents", "agentweaver.agent.md");
        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Be(AgentDefinitionTemplate.Content);
    }

    // =========================================================================
    // AD-03: TryMaterialize is idempotent and non-clobbering.
    // =========================================================================
    [Fact]
    public void TryMaterialize_IsIdempotent_AndDoesNotClobberUserEdits()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, ".github", "agents", "agentweaver.agent.md");

        // First call writes the template.
        AgentDefinitionTemplate.TryMaterialize(dir, out _).Should().BeTrue();

        // User edits the file afterwards.
        const string userEdited = "# user-customized agent\n";
        File.WriteAllText(path, userEdited);

        // Second call must NOT overwrite the user's edits and must report "not written".
        var writtenAgain = AgentDefinitionTemplate.TryMaterialize(dir, out var error);

        writtenAgain.Should().BeFalse("an existing agent file must never be clobbered");
        error.Should().BeNull();
        File.ReadAllText(path).Should().Be(userEdited);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private string NewDir()
    {
        var path = Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Normalize(string s) =>
        s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    private static string? FindRepoFile(string relativePath)
    {
        var segments = relativePath.Split('/');
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
