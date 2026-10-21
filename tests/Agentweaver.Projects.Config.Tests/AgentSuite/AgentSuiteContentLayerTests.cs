using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using JsonArray = System.Text.Json.Nodes.JsonArray;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using JsonObject = System.Text.Json.Nodes.JsonObject;
using Agentweaver.Projects.Config.AgentSuite;
using Xunit;

namespace Agentweaver.Projects.Config.Tests.AgentSuite;

/// <summary>Hand-built content layers checked against the exact upstream content.go and validate.go messages.</summary>
public sealed class AgentSuiteContentLayerTests
{
    [Fact]
    public void MinimalContentLayerIsAccepted()
    {
        var layer = AgentSuiteArchives.Gzip(AgentSuiteArchives.Tar(MinimalEntries()));
        var result = Validate(layer);

        Assert.True(result.Accepted, string.Join("\n", result.NativeErrors));
        Assert.Null(result.ManifestDigest);
        Assert.Equal(AgentSuiteArchives.Digest(layer), result.ContentDigest);
        Assert.Equal(AgentSuiteArchives.Digest(layer), result.SourceDigest);
        Assert.Contains(result.Blobs, blob => blob.Digest == result.ContentDigest);
        var profile = Assert.Single(result.Graph!.BuildProfiles);
        Assert.All(profile.ImageReferences, reference => Assert.StartsWith("registry.example/", reference, StringComparison.Ordinal));
    }

    [Fact]
    public void GzipModificationTimeMustBeUnset() =>
        AssertRejected(AgentSuiteArchives.Gzip(AgentSuiteArchives.Tar(MinimalEntries()), modTime: 1),
            "content gzip modification time must be unset");

    [Fact]
    public void TarModificationTimeMustBeEpoch() =>
        AssertRejected(Mutate(entry => entry with { ModTime = 1 }),
            "agentsuite.json tar modification time must be the Unix epoch");

    [Fact]
    public void PaxAccessTimeMustBeUnset() =>
        AssertRejected(Mutate(entry => entry with { Pax = new Dictionary<string, string> { ["atime"] = "1" } }),
            "agentsuite.json tar access and change times must be unset");

    [Fact]
    public void SetuidModeIsRejected() =>
        AssertRejected(Mutate(entry => entry with { Mode = 0x9ED }), "agentsuite.json has setid or sticky mode 04755");

    [Fact]
    public void WorldWritableFileIsRejected() =>
        AssertRejected(Mutate(entry => entry with { Mode = 0x1B6 }), "agentsuite.json is group or world writable");

    [Fact]
    public void SymlinkEscapeIsRejected() =>
        AssertRejected(Append(new TarEntry("link", [], '2') { LinkName = "../outside" }), "link link target escapes the content root");

    [Fact]
    public void AbsoluteSymlinkTargetIsRejected() =>
        AssertRejected(Append(new TarEntry("link", [], '2') { LinkName = "/etc/passwd" }), "link has unsafe link target \"/etc/passwd\"");

    [Fact]
    public void HardlinkMustTargetEarlierRegularFile()
    {
        var entries = MinimalEntries();
        entries.Insert(0, new TarEntry("aaa", [], '1') { LinkName = "agentsuite.json" });
        AssertRejected(Layer(entries), "aaa hardlink target agentsuite.json is not an earlier regular file");
    }

    [Fact]
    public void DeviceEntryIsRejected() =>
        AssertRejected(Append(new TarEntry("dev", [], '3')), "dev has unsupported tar type 51");

    [Fact]
    public void DuplicatePathIsRejected() =>
        AssertRejected(Append(TarEntry.File("agentsuite.json", "{}")), "duplicate content path agentsuite.json");

    [Fact]
    public void CaseFoldingCollisionIsRejected() =>
        AssertRejected(Append(TarEntry.File("README.md", "a"), TarEntry.File("readme.md", "b")),
            "case-folding path collision between README.md and readme.md");

    [Fact]
    public void DataAfterTarArchiveIsRejected() =>
        AssertRejected(AgentSuiteArchives.Gzip(AgentSuiteArchives.Tar(MinimalEntries(), trailing: "A"u8.ToArray())),
            "content gzip contains data after the tar archive");

    [Theory]
    [InlineData("/abs.txt", "content path must be relative POSIX syntax: \"/abs.txt\"")]
    [InlineData("a/../b.txt", "content path is not normalized below the root: \"a/../b.txt\"")]
    [InlineData("../b.txt", "content path is not normalized below the root: \"../b.txt\"")]
    [InlineData("a\\b.txt", "content path must be relative POSIX syntax: \"a\\\\b.txt\"")]
    public void UnsafeContentPathIsRejected(string path, string message) =>
        AssertRejected(Append(TarEntry.File(path, "x")), message);

    [Fact]
    public void NoncanonicalAgentManifestIsRejected()
    {
        var files = Minimal();
        files["agents/writer.json"] = [.. "{ "u8, .. files["agents/writer.json"].AsSpan(1)];
        Rebind(files, "agents/writer.json");
        Assert.Contains("agent writer manifest digest mismatch", ValidateFiles(files).NativeErrors);
    }

    [Fact]
    public void DuplicateSuiteKeyIsRejected() =>
        AssertSuiteError(text => text.Replace("\"name\": \"minimal\"", "\"name\": \"minimal\", \"name\": \"minimal\"", StringComparison.Ordinal),
            "agentsuite.json: duplicate JSON key \"name\"");

    [Fact]
    public void UnknownSuiteFieldIsRejected() =>
        AssertSuiteError(text => text.Replace("\"name\": \"minimal\"", "\"extra\": true, \"name\": \"minimal\"", StringComparison.Ordinal),
            "agentsuite.json: unknown field \"extra\"");

    [Fact]
    public void LossyIntegerAboveTwoToThe53IsRejected() =>
        AssertSuiteError(text => text.Replace("\"size\": 365", "\"size\": 9007199254740993", StringComparison.Ordinal),
            "agentsuite.json: document value changes during JCS canonicalization");

    [Fact]
    public void InstructionsDigestMismatchIsRejected()
    {
        var files = Minimal();
        files["instructions/writer.md"] = "Changed text.\n\n"u8.ToArray();
        Assert.Contains("agent writer instructions do not match instructions/writer.md", ValidateFiles(files).NativeErrors);
    }

    [Fact]
    public void MissingCompositionFileIsRejected()
    {
        var files = Minimal();
        files.Remove("compositions/writer-linux-amd64.json");
        Assert.Contains("composition writer@linux/amd64: required file compositions/writer-linux-amd64.json is missing",
            ValidateFiles(files).NativeErrors);
    }

    [Fact]
    public void BuildProfilePlatformMismatchIsRejected()
    {
        var files = Minimal();
        files["build-profiles/default.json"] = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(files["build-profiles/default.json"]).Replace("amd64", "arm64", StringComparison.Ordinal));
        Rebind(files, "build-profiles/default.json");
        Assert.Contains("composition writer@linux/amd64 build profile \"default\" does not support linux/amd64",
            ValidateFiles(files).NativeErrors);
    }

    [Fact]
    public void UpstreamTestdataSuitesAreAccepted()
    {
        foreach (var name in new[] { "minimal", "coordinator-workers", "policy-binding" })
        {
            var layer = AgentSuiteArchives.DirectoryContentLayer(Path.Combine(AgentSuiteArchives.UpstreamRoot, name));
            var result = Validate(layer);
            Assert.True(result.Accepted, name + ": " + string.Join("\n", result.NativeErrors.Concat(result.ProfileErrors.Select(e => e.Message))));
            Assert.Null(result.ManifestDigest);
            Assert.Equal(AgentSuiteArchives.Digest(layer), result.ContentDigest);
            foreach (var (path, data) in AgentSuiteArchives.ReadDirectory(Path.Combine(AgentSuiteArchives.UpstreamRoot, name)))
                Assert.True(result.Blobs.Any(blob => blob.Digest == AgentSuiteArchives.Digest(data)), $"{name}: Blobs omit {path}.");
        }
    }

    internal static Dictionary<string, byte[]> Minimal() =>
        AgentSuiteArchives.ReadDirectory(Path.Combine(AgentSuiteArchives.UpstreamRoot, "minimal"));

    internal static List<TarEntry> MinimalEntries() => AgentSuiteArchives.Entries(Minimal());

    internal static AgentSuiteValidationResult Validate(byte[] layer) =>
        AgentSuiteValidator.Validate(AgentSuiteArchiveFormat.ContentTarGzip, layer);

    internal static AgentSuiteValidationResult ValidateFiles(Dictionary<string, byte[]> files) =>
        Validate(AgentSuiteArchives.Gzip(AgentSuiteArchives.Tar(AgentSuiteArchives.Entries(files))));

    /// <summary>Updates the agentsuite.json descriptor that references path to the file's current digest and size.</summary>
    internal static void Rebind(Dictionary<string, byte[]> files, string path)
    {
        var suite = JsonNode.Parse(files["agentsuite.json"])!;
        Visit(suite);
        files["agentsuite.json"] = Encoding.UTF8.GetBytes(suite.ToJsonString(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));

        void Visit(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj["path"]?.GetValue<string>() == path && obj["descriptor"] is JsonObject descriptor)
                    {
                        descriptor["digest"] = AgentSuiteArchives.Digest(files[path]);
                        descriptor["size"] = files[path].Length;
                    }
                    foreach (var (_, child) in obj)
                        Visit(child);
                    break;
                case JsonArray array:
                    foreach (var child in array)
                        Visit(child);
                    break;
            }
        }
    }

    private static void AssertSuiteError(Func<string, string> edit, string message)
    {
        var files = Minimal();
        var original = Encoding.UTF8.GetString(files["agentsuite.json"]);
        var edited = edit(original);
        Assert.NotEqual(original, edited);
        files["agentsuite.json"] = Encoding.UTF8.GetBytes(edited);
        var result = ValidateFiles(files);
        Assert.Equal([message], result.NativeErrors.ToArray());
        Assert.Null(result.Graph);
    }

    private static byte[] Layer(IEnumerable<TarEntry> entries) => AgentSuiteArchives.Gzip(AgentSuiteArchives.Tar(entries));

    private static byte[] Mutate(Func<TarEntry, TarEntry> edit) =>
        Layer(MinimalEntries().Select(entry => entry.Path == "agentsuite.json" ? edit(entry) : entry));

    private static byte[] Append(params TarEntry[] extra) => Layer(MinimalEntries().Concat(extra));

    private static void AssertRejected(byte[] layer, string message)
    {
        var result = Validate(layer);
        Assert.Equal([message], result.NativeErrors.ToArray());
        Assert.Null(result.Graph);
        Assert.Empty(result.Blobs);
        Assert.False(result.Accepted);
    }
}
