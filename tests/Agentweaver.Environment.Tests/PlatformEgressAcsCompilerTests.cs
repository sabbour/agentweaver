using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Policy.Acs;

namespace Agentweaver.Environment.Tests;

public sealed class PlatformEgressAcsCompilerTests
{
    private static readonly string[] SystemHosts =
    [
        "api.business.githubcopilot.com",
        "api.enterprise.githubcopilot.com",
        "api.github.com",
        "api.githubcopilot.com",
        "api.individual.githubcopilot.com"
    ];

    [Theory]
    [InlineData("Allowlist", PlatformEgressMode.Allowlist)]
    [InlineData("PublicHttps", PlatformEgressMode.PublicHttps)]
    public void CompilesApprovedAndProtectedSystemDestinations(
        string sourceMode,
        PlatformEgressMode expectedMode)
    {
        var artifact = CreateArtifact(sourceMode, ["z.example.com", "a.example.com"]);

        var result = Compile(artifact);

        Assert.True(result.IsSuccess, result.Failure?.Code);
        Assert.Null(result.Failure);
        Assert.Equal(expectedMode, result.Snapshot!.Mode);
        Assert.Equal(PlatformEgressAcsCompiler.CompilerVersion, result.Snapshot.CompilerVersion);
        Assert.Equal("source-revision-1", result.Snapshot.SourceRevision);
        Assert.Equal("admission-checkpoint-1", result.Snapshot.Checkpoint);
        Assert.Equal(artifact.ContentSha256, result.Snapshot.SourceHash);
        Assert.Null(result.Snapshot.Validate());
        Assert.Equal(
            SystemHosts.Append("a.example.com").Append("z.example.com").OrderBy(host => host, StringComparer.Ordinal),
            result.Snapshot.Destinations.Select(destination => destination.Host));
        Assert.All(result.Snapshot.Destinations, destination => Assert.Equal(443, destination.Port));
    }

    [Fact]
    public void EmptyApprovalSetRetainsSystemRulesAndRemovalChangesTheProjection()
    {
        var withApproval = Compile(CreateArtifact("Allowlist", ["approved.example.com"])).Snapshot!;
        var withoutApproval = Compile(CreateArtifact("Allowlist", [])).Snapshot!;

        Assert.Equal(SystemHosts.OrderBy(host => host, StringComparer.Ordinal),
            withoutApproval.Destinations.Select(destination => destination.Host));
        Assert.DoesNotContain(withoutApproval.Destinations, destination => destination.Host == "approved.example.com");
        Assert.Contains(withApproval.Destinations, destination => destination.Host == "approved.example.com");
    }

    [Fact]
    public void SourceOnlyChangeUpdatesTheSourceHashButNotTheSnapshotIntent()
    {
        var artifact = CreateArtifact("Allowlist", ["approved.example.com"]);
        var data = artifact.Files.Single(file => file.RelativePath == "policy/data.json");
        var whitespaceArtifact = ReplaceData(
            artifact,
            Encoding.UTF8.GetString(data.Bytes.AsSpan()) + "\n  ");

        var first = Compile(artifact).Snapshot!;
        var second = Compile(whitespaceArtifact).Snapshot!;

        Assert.NotEqual(first.SourceHash, second.SourceHash);
        Assert.Equal(first.Mode, second.Mode);
        Assert.Equal(
            first.Destinations.Select(destination => destination.Host),
            second.Destinations.Select(destination => destination.Host));
    }

    [Fact]
    public void RejectsUnsupportedGrammarAndProtectedDestinationsWithoutPartialSnapshot()
    {
        var valid = CreateArtifact("Allowlist", []);
        var data = Encoding.UTF8.GetString(valid.Files.Single(file => file.RelativePath == "policy/data.json").Bytes.AsSpan());
        var invalidPortArtifact = CreateArtifact("Allowlist", ["invalid-port.example.com"]);
        var invalidPortData = Encoding.UTF8.GetString(
            invalidPortArtifact.Files.Single(file => file.RelativePath == "policy/data.json").Bytes.AsSpan());
        var cases = new[]
        {
            (Artifact: ReplaceData(valid, data.Replace("finite-network-v1", "finite-network-v2", StringComparison.Ordinal)),
                Code: "network_profile_unsupported"),
            (Artifact: ReplaceData(valid, data.Replace("hosted-copilot-v1", "spoofed-catalog", StringComparison.Ordinal)),
                Code: "network_catalog_unsupported"),
            (Artifact: ReplaceData(valid, data.Replace("api.business.githubcopilot.com", "added.example.com", StringComparison.Ordinal)),
                Code: "network_catalog_mismatch"),
            (Artifact: ReplaceData(valid, data.Replace(
                "\"destinations\":[",
                "\"destinations\":[{\"host\":\"added.example.com\",\"port\":443},",
                StringComparison.Ordinal)),
                Code: "network_catalog_mismatch"),
            (Artifact: ReplaceData(valid, data.Replace(
                "{\"host\":\"api.business.githubcopilot.com\",\"port\":443},",
                "",
                StringComparison.Ordinal)),
                Code: "network_catalog_mismatch"),
            (Artifact: ReplaceData(valid, data.Replace("api.business.githubcopilot.com", "login.microsoftonline.com", StringComparison.Ordinal)),
                Code: "network_destination_invalid"),
            (Artifact: CreateArtifact("Allowlist", ["contoso.vault.azure.net"]),
                Code: "network_destination_invalid"),
            (Artifact: CreateArtifact("Allowlist", ["10.0.0.1"]),
                Code: "network_destination_invalid"),
            (Artifact: ReplaceData(
                CreateArtifact("Allowlist", ["non443.example.com"]),
                Encoding.UTF8.GetString(CreateArtifact("Allowlist", ["non443.example.com"])
                    .Files.Single(file => file.RelativePath == "policy/data.json").Bytes.AsSpan())
                    .Replace("\"host\":\"non443.example.com\",\"port\":443",
                        "\"host\":\"non443.example.com\",\"port\":80",
                        StringComparison.Ordinal)),
                Code: "network_destination_invalid"),
            (Artifact: CreateArtifact("Allowlist", ["duplicate.example.com", "duplicate.example.com"]),
                Code: "network_destination_duplicate"),
            (Artifact: ReplaceData(valid, data.Replace(
                "\"mode\":\"Allowlist\"",
                "\"profile\":\"finite-network-v1\",\"mode\":\"Allowlist\"",
                StringComparison.Ordinal)),
                Code: "network_data_duplicate_key"),
            (Artifact: ReplaceData(valid, data.Replace(
                "\"mode\":\"Allowlist\"",
                "\"mode\":\"Allowlist\",\"deny\":[]",
                StringComparison.Ordinal)),
                Code: "network_data_shape"),
            (Artifact: ReplaceData(invalidPortArtifact, invalidPortData.Replace(
                "\"host\":\"invalid-port.example.com\",\"port\":443",
                "\"host\":\"invalid-port.example.com\",\"port\":\"443\"",
                StringComparison.Ordinal)),
                Code: "network_data_shape"),
            (Artifact: ReplaceData(invalidPortArtifact, invalidPortData.Replace(
                "\"host\":\"invalid-port.example.com\",\"port\":443",
                "\"host\":\"invalid-port.example.com\",\"port\":null",
                StringComparison.Ordinal)),
                Code: "network_data_shape"),
            (Artifact: ReplaceData(valid, "{"),
                Code: "network_data_json")
        };

        foreach (var (artifact, code) in cases)
        {
            var result = Compile(artifact);
            Assert.False(result.IsSuccess);
            Assert.Null(result.Snapshot);
            Assert.Equal(code, result.Failure?.Code);
        }
    }

    [Fact]
    public void RejectsChangedBindingsModulesAndExtraFiles()
    {
        var artifact = CreateArtifact("PublicHttps", []);
        var extraFile = Rehash(artifact with
        {
            Files = artifact.Files.Add(new NativeAcsArtifactFile(
                "policy/extra.rego",
                Encoding.UTF8.GetBytes("package extra").ToImmutableArray()))
        });
        var changedModule = Rehash(artifact with
        {
            Files = artifact.Files.Select(file => file.RelativePath == "policy/network.rego"
                ? file with { Bytes = Encoding.UTF8.GetBytes("package changed").ToImmutableArray() }
                : file).ToImmutableArray()
        });
        var changedManifest = Rehash(artifact with
        {
            ManifestBytes = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(artifact.ManifestBytes.AsSpan())
                    .Replace("$.model_request", "$.other", StringComparison.Ordinal))
                .ToImmutableArray()
        });

        Assert.Equal("network_artifact_closure", Compile(extraFile).Failure?.Code);
        Assert.Equal("network_artifact_closure", Compile(changedModule).Failure?.Code);
        Assert.Equal("network_manifest_mismatch", Compile(changedManifest).Failure?.Code);
    }

    [Fact]
    public void CompilerKeepsPrivateAddressAndExactHostCapabilityGatesSeparate()
    {
        var result = Compile(CreateArtifact("Allowlist", ["approved.example.com"]));
        Assert.True(result.IsSuccess, result.Failure?.Code);

        var capabilities = new HashSet<string>
        {
            PlatformEgressCapabilities.PrivateRangeExclusion,
            PlatformEgressCapabilities.Status,
            PlatformEgressCapabilities.PublicHttps
        };
        Assert.False(PlatformEgressCapabilities.CanApply(capabilities, result.Snapshot!.Mode));
        capabilities.Add(PlatformEgressCapabilities.ExactHost);
        Assert.True(PlatformEgressCapabilities.CanApply(capabilities, result.Snapshot.Mode));

        var excluded = PlatformEgressExcludedRanges.Create([]);
        Assert.True(PlatformEgressExcludedRanges.Contains(excluded, IPAddress.Parse("10.2.3.4")));
        Assert.True(PlatformEgressExcludedRanges.Contains(excluded, IPAddress.Parse("169.254.169.254")));
        Assert.False(PlatformEgressExcludedRanges.Contains(excluded, IPAddress.Parse("20.42.0.1")));
    }

    [Fact]
    public void CompilerDoesNotInventAdmissionCheckpoint()
    {
        var result = PlatformEgressAcsCompiler.Compile(
            CreateArtifact("PublicHttps", []),
            "canonical-policy",
            "source-revision-1",
            " ");

        Assert.Null(result.Snapshot);
        Assert.Equal("source_metadata_invalid", result.Failure?.Code);
    }

    private static PlatformEgressCompilationResult Compile(NativeAcsArtifact artifact) =>
        PlatformEgressAcsCompiler.Compile(
            artifact,
            "canonical-policy",
            "source-revision-1",
            "admission-checkpoint-1");

    private static NativeAcsArtifact CreateArtifact(string mode, IEnumerable<string> approved)
    {
        var json = JsonSerializer.Serialize(new
        {
            agentweaver = new
            {
                platform_egress = new
                {
                    profile = NativeAcsFiniteNetworkProfile.ProfileId,
                    mode,
                    approved = approved.Select(host => new { host, port = 443 }).ToArray(),
                    system = new
                    {
                        catalog = "hosted-copilot-v1",
                        destinations = SystemHosts.Select(host => new { host, port = 443 }).ToArray()
                    }
                }
            }
        });
        return NativeAcsFiniteNetworkProfile.CreateArtifact(json);
    }

    private static NativeAcsArtifact ReplaceData(NativeAcsArtifact artifact, string json) =>
        Rehash(artifact with
        {
            Files = artifact.Files.Select(file => file.RelativePath == "policy/data.json"
                ? file with { Bytes = Encoding.UTF8.GetBytes(json).ToImmutableArray() }
                : file).ToImmutableArray()
        });

    private static NativeAcsArtifact Rehash(NativeAcsArtifact artifact) =>
        artifact with
        {
            ContentSha256 = NativeAcsArtifactValidator.ComputeContentSha256(artifact)
        };
}
