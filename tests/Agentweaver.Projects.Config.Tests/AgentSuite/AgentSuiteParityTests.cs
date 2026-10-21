using System.Text.Json;
using Agentweaver.Projects.Config.AgentSuite;
using Xunit;

namespace Agentweaver.Projects.Config.Tests.AgentSuite;

/// <summary>Compares the .NET port with verdicts the pinned upstream Go validator produced for each fixture.</summary>
public sealed class AgentSuiteParityTests
{
    public static TheoryData<string> Fixtures()
    {
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

        var expected = (verdict.Error ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, result.NativeErrors.ToArray());
        Assert.Equal(verdict.Accepted, result.NativeErrors.IsEmpty);
        Assert.Equal(verdict.ManifestDigest, result.ManifestDigest);
        Assert.Equal(verdict.Accepted, result.Graph is not null);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void UploadedLayoutTarMatchesDirectoryResult(string name)
    {
        var files = AgentSuiteArchives.ReadDirectory(Path.Combine(AgentSuiteArchives.FixturesRoot, name));
        var archive = AgentSuiteArchives.Tar(AgentSuiteArchives.Entries(files));
        var direct = AgentSuiteValidator.ValidateOciLayout(files);
        var uploaded = AgentSuiteValidator.Validate(AgentSuiteArchiveFormat.OciLayoutTar, archive);

        Assert.Equal(direct.NativeErrors.ToArray(), uploaded.NativeErrors.ToArray());
        Assert.Equal(direct.ProfileErrors.ToArray(), uploaded.ProfileErrors.ToArray());
        Assert.Equal(direct.ManifestDigest, uploaded.ManifestDigest);
        Assert.Equal(direct.ContentDigest, uploaded.ContentDigest);
        Assert.Equal(AgentSuiteArchives.Digest(archive), uploaded.SourceDigest);
        Assert.Equal(direct.Blobs.Select(blob => blob.Digest), uploaded.Blobs.Select(blob => blob.Digest));
    }

    [Fact]
    public void RetaggedLayoutKeepsIdentity()
    {
        var valid = Validate("valid");
        var retagged = Validate("valid-retagged");
        Assert.True(valid.Accepted);
        Assert.True(retagged.Accepted);
        Assert.Equal(valid.ManifestDigest, retagged.ManifestDigest);
        Assert.Equal(valid.ContentDigest, retagged.ContentDigest);
        Assert.Equal(valid.Graph!.SuiteManifestDigest, retagged.Graph!.SuiteManifestDigest);
    }

    [Fact]
    public void ValidFixtureGraphAndBlobsCoverEveryContentFile()
    {
        var result = Validate("valid");
        Assert.True(result.Accepted);
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
        var graph = result.Graph!;
        Assert.Equal(["lead", "writer", "reviewer"], graph.Agents.Select(agent => agent.Id));
        var lead = graph.Agents[0];
        Assert.True(lead.Model.CapabilityBased);
        Assert.Equal(["writer", "reviewer"], lead.Invokes.Select(invoke => invoke.Agent));
        Assert.True(lead.HasPolicy);
        Assert.True(graph.Agents[1].HasPolicy);
        Assert.False(graph.Agents[2].HasPolicy);
        var provider = Assert.Single(graph.ToolProviders);
        Assert.Equal("search", provider.Id);
        Assert.True(provider.HasRemoteMcp);
        Assert.False(provider.HasBundledVariants);

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
        Assert.Empty(result.NativeErrors);
        Assert.NotNull(result.Graph);
        Assert.False(result.Accepted);
        var error = Assert.Single(result.ProfileErrors);
        Assert.Equal("agentsuite_secret_reference", error.Code);
        Assert.Contains("'writer'", error.Message, StringComparison.Ordinal);
        AssertBlobsCoverContent("team-model-secret-refs", result);
    }

    private static void AssertBlobsCoverContent(string fixture, AgentSuiteValidationResult result)
    {
        var digests = result.Blobs.Select(blob => blob.Digest).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, data) in AgentSuiteArchives.FixtureContent(fixture))
            Assert.True(digests.Contains(AgentSuiteArchives.Digest(data)), $"Blobs omit {path}.");
        foreach (var blob in result.Blobs)
            Assert.Equal(blob.Digest, AgentSuiteArchives.Digest(blob.Data.ToArray()));
    }

    private static AgentSuiteValidationResult Validate(string fixture) =>
        AgentSuiteValidator.ValidateOciLayout(AgentSuiteArchives.ReadDirectory(Path.Combine(AgentSuiteArchives.FixturesRoot, fixture)));

    private static IReadOnlyList<Verdict> Verdicts() =>
        JsonSerializer.Deserialize<List<Verdict>>(
            File.ReadAllBytes(Path.Combine(AgentSuiteArchives.FixturesRoot, "parity.json")),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

    private sealed record Verdict(string Name, bool Accepted, string? Error, string ManifestDigest);
}
