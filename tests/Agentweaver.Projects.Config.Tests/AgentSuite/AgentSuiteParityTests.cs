using System.Text.Json;
using Agentweaver.Projects.Config.AgentSuite;
using Xunit;

namespace Agentweaver.Projects.Config.Tests.AgentSuite;

/// <summary>Compares the .NET port with verdicts the pinned upstream Go validator produced for each fixture.</summary>
public sealed class AgentSuiteParityTests
{
    public static TheoryData<string> Fixtures()
    {
        // One test case per fixture listed in parity.json.
        var data = new TheoryData<string>();
        foreach (var verdict in Verdicts())
            data.Add(verdict.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void LayoutMatchesUpstreamVerdict(string name)
    {
        var verdict = Verdicts().Single(item => item.Name == name);
        var result = AgentSuiteValidator.ValidateOciLayout(AgentSuiteArchives.ReadDirectory(Path.Combine(AgentSuiteArchives.FixturesRoot, name)));

        // Go joins its errors with newlines; split and sort them to compare with the port's sorted list.
        var expected = (verdict.Error ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, result.NativeErrors.ToArray());
        // Same accept/reject decision and same manifest identity as Go; a graph exists only when accepted.
        Assert.Equal(verdict.Accepted, result.NativeErrors.IsEmpty);
        Assert.Equal(verdict.ManifestDigest, result.ManifestDigest);
        Assert.Equal(verdict.Accepted, result.Graph is not null);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void UploadedLayoutTarMatchesDirectoryResult(string name)
    {
        // Validate the same layout twice: as a directory map and as an uploaded tar archive.
        var files = AgentSuiteArchives.ReadDirectory(Path.Combine(AgentSuiteArchives.FixturesRoot, name));
        var archive = AgentSuiteArchives.Tar(AgentSuiteArchives.Entries(files));
        var direct = AgentSuiteValidator.ValidateOciLayout(files);
        var uploaded = AgentSuiteValidator.Validate(AgentSuiteArchiveFormat.OciLayoutTar, archive);

        // Unpacking the tar must not change any result.
        Assert.Equal(direct.NativeErrors.ToArray(), uploaded.NativeErrors.ToArray());
        Assert.Equal(direct.ProfileErrors.ToArray(), uploaded.ProfileErrors.ToArray());
        Assert.Equal(direct.ManifestDigest, uploaded.ManifestDigest);
        Assert.Equal(direct.ContentDigest, uploaded.ContentDigest);
        // Only the source digest differs: it is the digest of the uploaded tar bytes.
        Assert.Equal(AgentSuiteArchives.Digest(archive), uploaded.SourceDigest);
        Assert.Equal(direct.Blobs.Select(blob => blob.Digest), uploaded.Blobs.Select(blob => blob.Digest));
    }

    [Fact]
    public void RetaggedLayoutKeepsIdentity()
    {
        // "valid-retagged" points at the same manifest under a different tag in index.json.
        var valid = Validate("valid");
        var retagged = Validate("valid-retagged");
        Assert.True(valid.Accepted);
        Assert.True(retagged.Accepted);
        // Identity comes from the manifest digest, not the tag, so both layouts are the same suite.
        Assert.Equal(valid.ManifestDigest, retagged.ManifestDigest);
        Assert.Equal(valid.ContentDigest, retagged.ContentDigest);
        Assert.Equal(valid.Graph!.SuiteManifestDigest, retagged.Graph!.SuiteManifestDigest);
    }

    [Fact]
    public void ValidFixtureGraphAndBlobsCoverEveryContentFile()
    {
        var result = Validate("valid");
        Assert.True(result.Accepted);
        // Check the parsed graph: one "writer" agent, its instructions text, model and flags.
        var graph = result.Graph!;
        Assert.Equal("agentweaver-poc", graph.Name);
        var writer = Assert.Single(graph.Agents);
        Assert.Equal("writer", writer.Id);
        Assert.Equal("Repeat the user's request in one short sentence.\n", System.Text.Encoding.UTF8.GetString(writer.InstructionsBytes.Span));
        Assert.Equal("agentweaver-echo-1", writer.Model.Model);
        Assert.True(writer.HasEvaluations);
        Assert.False(writer.HasPolicy);
        var composition = Assert.Single(graph.Compositions);
        Assert.Equal("linux/amd64", composition.Platform);
        var profile = Assert.Single(graph.BuildProfiles);
        Assert.Equal(2, profile.ImageReferences.Length);

        // Every original blob is kept with the right media type: OCI manifest, the empty "{}" config,
        // the agent manifest and the instructions file.
        AssertBlobsCoverContent("valid", result);
        Assert.Contains(result.Blobs, blob => blob.Digest == result.ManifestDigest && blob.MediaType == "application/vnd.oci.image.manifest.v1+json");
        Assert.Contains(result.Blobs, blob => blob.Data.Span.SequenceEqual("{}"u8));
        Assert.Contains(result.Blobs, blob => blob.Digest == writer.ManifestDigest && blob.MediaType == "application/vnd.agentsuite.agent.v1+json");
        Assert.Contains(result.Blobs, blob => blob.Digest == writer.InstructionsDigest && blob.MediaType == "text/markdown; charset=utf-8");
    }

    [Fact]
    public void TeamSuiteIsAcceptedWithPoliciesRemoteProviderAndInvokes()
    {
        var result = Validate("team");
        Assert.Empty(result.NativeErrors);
        Assert.Empty(result.ProfileErrors);
        Assert.True(result.Accepted);
        // Agents keep their declared order; "lead" uses a capability-based model and invokes the others.
        var graph = result.Graph!;
        Assert.Equal(["lead", "writer", "reviewer"], graph.Agents.Select(agent => agent.Id));
        var lead = graph.Agents[0];
        Assert.True(lead.Model.CapabilityBased);
        Assert.Equal(["writer", "reviewer"], lead.Invokes.Select(invoke => invoke.Agent));
        // lead and writer have policies; reviewer has none.
        Assert.True(lead.HasPolicy);
        Assert.True(graph.Agents[1].HasPolicy);
        Assert.False(graph.Agents[2].HasPolicy);
        // The single tool provider is a remote MCP server with no bundled binaries.
        var provider = Assert.Single(graph.ToolProviders);
        Assert.Equal("search", provider.Id);
        Assert.True(provider.HasRemoteMcp);
        Assert.False(provider.HasBundledVariants);

        // Provider manifest, both policy versions and the JSON schema are kept as blobs.
        AssertBlobsCoverContent("team", result);
        Assert.Contains(result.Blobs, blob => blob.Digest == provider.ManifestDigest && blob.MediaType == "application/vnd.agentsuite.tool.provider.v1+json");
        Assert.Contains(result.Blobs, blob => blob.MediaType == "application/vnd.agentsuite.policy.v1+json");
        Assert.Contains(result.Blobs, blob => blob.MediaType == "application/vnd.agentsuite.policy.v2+json");
        Assert.Contains(result.Blobs, blob => blob.MediaType == "application/schema+json");
    }

    [Fact]
    public void ModelSecretReferencesAreRefusedByTheProfile()
    {
        var result = Validate("team-model-secret-refs");
        // The suite is valid for upstream Go (no native errors, graph built) ...
        Assert.Empty(result.NativeErrors);
        Assert.NotNull(result.Graph);
        // ... but the Agentweaver profile refuses model secret references, so it is not accepted.
        Assert.False(result.Accepted);
        var error = Assert.Single(result.ProfileErrors);
        Assert.Equal("agentsuite_secret_reference", error.Code);
        Assert.Contains("'writer'", error.Message, StringComparison.Ordinal);
        // Blobs are still returned so the refused suite can be inspected.
        AssertBlobsCoverContent("team-model-secret-refs", result);
    }

    private static void AssertBlobsCoverContent(string fixture, AgentSuiteValidationResult result)
    {
        // Every regular file in the content layer must appear as a blob ...
        var digests = result.Blobs.Select(blob => blob.Digest).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, data) in AgentSuiteArchives.FixtureContent(fixture))
            Assert.True(digests.Contains(AgentSuiteArchives.Digest(data)), $"Blobs omit {path}.");
        // ... and every blob's digest must match its actual bytes.
        foreach (var blob in result.Blobs)
            Assert.Equal(blob.Digest, AgentSuiteArchives.Digest(blob.Data.ToArray()));
    }

    private static AgentSuiteValidationResult Validate(string fixture) =>
        AgentSuiteValidator.ValidateOciLayout(AgentSuiteArchives.ReadDirectory(Path.Combine(AgentSuiteArchives.FixturesRoot, fixture)));

    // parity.json holds the verdicts the pinned Go validator produced for each fixture.
    private static IReadOnlyList<Verdict> Verdicts() =>
        JsonSerializer.Deserialize<List<Verdict>>(
            File.ReadAllBytes(Path.Combine(AgentSuiteArchives.FixturesRoot, "parity.json")),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

    private sealed record Verdict(string Name, bool Accepted, string? Error, string ManifestDigest);
}
