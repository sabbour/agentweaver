using Agentweaver.Projects.Config.AgentSuite;
using Xunit;

namespace Agentweaver.Projects.Config.Tests.AgentSuite;

/// <summary>Agentweaver profile refusals, the pinned schema bundle and the image reference grammar.</summary>
public sealed class AgentSuiteProfileTests
{
    [Fact]
    public void ArchiveAboveUploadCapIsRefused()
    {
        var result = AgentSuiteValidator.Validate(AgentSuiteArchiveFormat.OciLayoutTar, new byte[AgentSuiteValidator.MaxArchiveBytes + 1]);
        Assert.Equal(AgentSuiteValidator.ArchiveTooLargeCode, Assert.Single(result.ProfileErrors).Code);
        Assert.Null(result.Graph);
        Assert.Empty(result.Blobs);
        Assert.False(result.Accepted);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/index2.json")]
    [InlineData("blobs\\x")]
    public void UnsafeOuterArchivePathIsRefused(string path) =>
        AssertArchiveInvalid(ValidLayoutEntries().Append(TarEntry.File(path, "x")));

    [Fact]
    public void OuterSymlinkIsRefused() =>
        AssertArchiveInvalid(ValidLayoutEntries().Append(new TarEntry("link", [], '2') { LinkName = "index.json" }));

    [Fact]
    public void DuplicateOuterEntryIsRefused() =>
        AssertArchiveInvalid(ValidLayoutEntries().Append(TarEntry.File("index.json", "{}")));

    [Fact]
    public void CaseFoldDuplicateOuterEntryIsRefused() =>
        AssertArchiveInvalid(ValidLayoutEntries().Append(TarEntry.File("INDEX.json", "{}")));

    [Fact]
    public void DotSlashPrefixedLayoutIsAccepted()
    {
        var entries = ValidLayoutEntries().Select(entry => entry with { Path = "./" + entry.Path });
        var result = AgentSuiteValidator.Validate(AgentSuiteArchiveFormat.OciLayoutTar, AgentSuiteArchives.Tar(entries));
        Assert.True(result.Accepted, string.Join("\n", result.NativeErrors.Concat(result.ProfileErrors.Select(e => e.Message))));
    }

    [Fact]
    public void TooManyContentFilesAreRefused()
    {
        var files = AgentSuiteContentLayerTests.Minimal();
        for (var i = 0; i < AgentSuiteValidator.MaxContentFiles; i++)
            files[$"notes/{i:D3}.txt"] = "x"u8.ToArray();
        AssertContentLimit(AgentSuiteContentLayerTests.ValidateFiles(files));
    }

    [Fact]
    public void ContentFileAboveOneMebibyteIsRefused()
    {
        var files = AgentSuiteContentLayerTests.Minimal();
        files["notes/large.bin"] = new byte[AgentSuiteValidator.MaxContentFileBytes + 1];
        AssertContentLimit(AgentSuiteContentLayerTests.ValidateFiles(files));
    }

    [Fact]
    public void ContentPathAboveLimitIsRefused()
    {
        var name = new string('a', AgentSuiteValidator.MaxContentPathBytes + 1);
        var entries = AgentSuiteContentLayerTests.MinimalEntries();
        entries.Add(new TarEntry(name, "x"u8.ToArray()) { Pax = new Dictionary<string, string> { ["path"] = name } });
        AssertContentLimit(AgentSuiteContentLayerTests.Validate(AgentSuiteArchives.Gzip(AgentSuiteArchives.Tar(entries))));
    }

    [Fact]
    public void ContentAtLimitsIsAccepted()
    {
        var files = AgentSuiteContentLayerTests.Minimal();
        files["notes/large.bin"] = new byte[AgentSuiteValidator.MaxContentFileBytes];
        var result = AgentSuiteContentLayerTests.ValidateFiles(files);
        Assert.True(result.Accepted, string.Join("\n", result.NativeErrors.Concat(result.ProfileErrors.Select(e => e.Message))));
    }

    [Fact]
    public void SchemaBundleMatchesPins()
    {
        AgentSuiteSchemaBundle.Verify();
        Assert.Equal("a09950a75359cdc7de4d3361694582613f24c732", AgentSuiteSchemaBundle.Commit);
        Assert.Equal("1.0.0-draft", AgentSuiteSchemaBundle.SpecVersion);
        Assert.NotEmpty(AgentSuiteSchemaBundle.PinnedDigests);
        foreach (var name in AgentSuiteSchemaBundle.PinnedDigests.Keys)
        {
            var data = AgentSuiteSchemaBundle.Read(name);
            Assert.True(AgentSuiteSchemaBundle.MatchesPin(name, data));
            var tampered = data.ToArray();
            tampered[^1] ^= 0x01;
            Assert.False(AgentSuiteSchemaBundle.MatchesPin(name, tampered));
        }
        Assert.False(AgentSuiteSchemaBundle.MatchesPin("unknown.schema.json", "{}"u8));
    }

    [Theory]
    [InlineData("registry.example/agentsuite/harness@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("localhost:5000/agentsuite/harness@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("[2001:db8::1]:5000/agentsuite/harness@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("agentsuite/harness@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("registry..example/agentsuite/harness@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("registry.example:70000/agentsuite/harness@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("registry.example/agentsuite/harness:v1@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("registry.example/agentsuite/harness:v1", false)]
    [InlineData("registry.example/agentsuite/harness@sha256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg", false)]
    public void ImageReferenceGrammarMatchesUpstream(string reference, bool valid) =>
        Assert.Equal(valid, AgentSuiteImageReference.Validate(reference) is null);

    private static IEnumerable<TarEntry> ValidLayoutEntries() =>
        AgentSuiteArchives.Entries(AgentSuiteArchives.ReadDirectory(Path.Combine(AgentSuiteArchives.FixturesRoot, "valid")));

    private static void AssertArchiveInvalid(IEnumerable<TarEntry> entries)
    {
        var result = AgentSuiteValidator.Validate(AgentSuiteArchiveFormat.OciLayoutTar, AgentSuiteArchives.Tar(entries));
        var error = Assert.Single(result.ProfileErrors);
        Assert.Equal(AgentSuiteValidator.ArchiveInvalidCode, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Null(result.Graph);
        Assert.Empty(result.Blobs);
    }

    private static void AssertContentLimit(AgentSuiteValidationResult result)
    {
        Assert.Contains(result.ProfileErrors, error => error.Code == AgentSuiteValidator.ContentLimitCode);
        Assert.False(result.Accepted);
    }
}
