using Agentweaver.Projects.Config.AgentSuite;
using Xunit;

namespace Agentweaver.Projects.Config.Tests.AgentSuite;

/// <summary>Agentweaver profile refusals, the pinned schema bundle and the image reference grammar.</summary>
public sealed class AgentSuiteProfileTests
{
    [Fact]
    public void ArchiveAboveUploadCapIsRefused()
    {
        // A zero-filled buffer one byte over the cap: the size check must run before any parsing.
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
    // The outer OCI layout tar gets the same path safety rules as the content layer.
    public void UnsafeOuterArchivePathIsRefused(string path) =>
        AssertArchiveInvalid(ValidLayoutEntries().Append(TarEntry.File(path, "x")));

    [Fact]
    // The outer layout may only contain regular files and directories, not links.
    public void OuterSymlinkIsRefused() =>
        AssertArchiveInvalid(ValidLayoutEntries().Append(new TarEntry("link", [], '2') { LinkName = "index.json" }));

    [Fact]
    // A second index.json would make the layout ambiguous.
    public void DuplicateOuterEntryIsRefused() =>
        AssertArchiveInvalid(ValidLayoutEntries().Append(TarEntry.File("index.json", "{}")));

    [Fact]
    // INDEX.json and index.json collide on case-insensitive file systems.
    public void CaseFoldDuplicateOuterEntryIsRefused() =>
        AssertArchiveInvalid(ValidLayoutEntries().Append(TarEntry.File("INDEX.json", "{}")));

    [Fact]
    public void DotSlashPrefixedLayoutIsAccepted()
    {
        // Tools such as "tar -C dir ." write "./index.json"; the prefix must be ignored.
        var entries = ValidLayoutEntries().Select(entry => entry with { Path = "./" + entry.Path });
        var result = AgentSuiteValidator.Validate(AgentSuiteArchiveFormat.OciLayoutTar, AgentSuiteArchives.Tar(entries));
        Assert.True(result.Accepted, string.Join("\n", result.NativeErrors.Concat(result.ProfileErrors.Select(e => e.Message))));
    }

    [Fact]
    public void TooManyContentFilesAreRefused()
    {
        var files = AgentSuiteContentLayerTests.Minimal();
        // The minimal suite already has files, so adding MaxContentFiles more goes over the limit.
        for (var i = 0; i < AgentSuiteValidator.MaxContentFiles; i++)
            files[$"notes/{i:D3}.txt"] = "x"u8.ToArray();
        AssertContentLimit(AgentSuiteContentLayerTests.ValidateFiles(files));
    }

    [Fact]
    public void ContentFileAboveOneMebibyteIsRefused()
    {
        var files = AgentSuiteContentLayerTests.Minimal();
        // One byte over the per-file size limit.
        files["notes/large.bin"] = new byte[AgentSuiteValidator.MaxContentFileBytes + 1];
        AssertContentLimit(AgentSuiteContentLayerTests.ValidateFiles(files));
    }

    [Fact]
    public void ContentPathAboveLimitIsRefused()
    {
        // The name is longer than the 100-byte ustar field, so it is carried in a PAX "path" record.
        var name = new string('a', AgentSuiteValidator.MaxContentPathBytes + 1);
        var entries = AgentSuiteContentLayerTests.MinimalEntries();
        entries.Add(new TarEntry(name, "x"u8.ToArray()) { Pax = new Dictionary<string, string> { ["path"] = name } });
        AssertContentLimit(AgentSuiteContentLayerTests.Validate(AgentSuiteArchives.Gzip(AgentSuiteArchives.Tar(entries))));
    }

    [Fact]
    public void ContentAtLimitsIsAccepted()
    {
        var files = AgentSuiteContentLayerTests.Minimal();
        // Exactly at the per-file size limit: the limit is inclusive, so this must pass.
        files["notes/large.bin"] = new byte[AgentSuiteValidator.MaxContentFileBytes];
        var result = AgentSuiteContentLayerTests.ValidateFiles(files);
        Assert.True(result.Accepted, string.Join("\n", result.NativeErrors.Concat(result.ProfileErrors.Select(e => e.Message))));
    }

    [Fact]
    public void SchemaBundleMatchesPins()
    {
        // The embedded schemas must match the pinned upstream commit and spec version.
        AgentSuiteSchemaBundle.Verify();
        Assert.Equal("a09950a75359cdc7de4d3361694582613f24c732", AgentSuiteSchemaBundle.Commit);
        Assert.Equal("1.0.0-draft", AgentSuiteSchemaBundle.SpecVersion);
        Assert.NotEmpty(AgentSuiteSchemaBundle.PinnedDigests);
        foreach (var name in AgentSuiteSchemaBundle.PinnedDigests.Keys)
        {
            var data = AgentSuiteSchemaBundle.Read(name);
            Assert.True(AgentSuiteSchemaBundle.MatchesPin(name, data));
            // Flipping one bit in the last byte must break the pin.
            var tampered = data.ToArray();
            tampered[^1] ^= 0x01;
            Assert.False(AgentSuiteSchemaBundle.MatchesPin(name, tampered));
        }
        // A schema name without a pin never matches.
        Assert.False(AgentSuiteSchemaBundle.MatchesPin("unknown.schema.json", "{}"u8));
    }

    [Theory]
    // Valid: a registry host (name, localhost with port, or bracketed IPv6) plus a sha256 digest.
    // Invalid: no registry host, empty host label, port above 65535, tag together with digest,
    // tag without digest, and a non-hex digest.
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
        // Outer archive problems give one archive-invalid profile error, with no graph and no blobs.
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
