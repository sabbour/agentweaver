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

        var candidate = PlatformEgressAcsCompiler.CompileCandidate(
            artifact, "canonical-policy", "source-revision-1");
        Assert.True(candidate.IsSuccess, candidate.Failure?.Code);
        var compiledCandidate = candidate.Candidate!;
        Assert.Equal(expectedMode, compiledCandidate.Mode);
        Assert.Equal(artifact.ContentSha256, compiledCandidate.SourceHash);
        Assert.Equal(
            ["a.example.com", "z.example.com"],
            compiledCandidate.ApprovedDestinations.Select(destination => destination.Host));
        Assert.Equal(
            SystemHosts.OrderBy(host => host, StringComparer.Ordinal),
            compiledCandidate.SystemDestinations.Select(destination => destination.Host));
    }

    [Theory]
    [InlineData("Allowlist", true)]
    [InlineData("Allowlist", false)]
    [InlineData("PublicHttps", true)]
    [InlineData("PublicHttps", false)]
    public void CompilesSystemOnlyCandidateAsAllowlistWithValidatedMetadata(
        string sourceMode,
        bool includeApproval)
    {
        string[] approved = includeApproval ? ["approved.example.com"] : [];
        var artifact = CreateArtifact(sourceMode, approved);

        var result = PlatformEgressAcsCompiler.CompileSystemOnlyCandidate(
            artifact,
            "canonical-policy",
            "source-revision-1");

        Assert.True(result.IsSuccess, result.Failure?.Code);
        var candidate = result.Candidate!;
        Assert.Equal(PlatformEgressMode.Allowlist, candidate.Mode);
        Assert.Empty(candidate.ApprovedDestinations);
        Assert.Equal(
            SystemHosts.OrderBy(host => host, StringComparer.Ordinal),
            candidate.SystemDestinations.Select(destination => destination.Host));
        Assert.All(candidate.SystemDestinations, destination => Assert.Equal(443, destination.Port));
        Assert.Equal("canonical-policy", candidate.SourceId);
        Assert.Equal("source-revision-1", candidate.SourceRevision);
        Assert.Equal(artifact.ContentSha256, candidate.SourceHash);
        Assert.Equal(PlatformEgressAcsCompiler.CompilerVersion, candidate.CompilerVersion);
    }

    [Fact]
    public void SystemOnlyCompilationPreservesTheNormalCandidate()
    {
        var artifact = CreateArtifact("PublicHttps", ["approved.example.com"]);
        var original = PlatformEgressAcsCompiler.CompileCandidate(
            artifact, "canonical-policy", "source-revision-1").Candidate!;
        var approvedBefore = original.ApprovedDestinations
            .Select(destination => (destination.Host, destination.Port))
            .ToArray();
        var systemBefore = original.SystemDestinations
            .Select(destination => (destination.Host, destination.Port))
            .ToArray();

        var systemOnly = PlatformEgressAcsCompiler.CompileSystemOnlyCandidate(
            artifact, "canonical-policy", "source-revision-1");
        var after = PlatformEgressAcsCompiler.CompileCandidate(
            artifact, "canonical-policy", "source-revision-1").Candidate!;

        Assert.True(systemOnly.IsSuccess, systemOnly.Failure?.Code);
        Assert.Equal(PlatformEgressMode.PublicHttps, original.Mode);
        Assert.Equal(approvedBefore, original.ApprovedDestinations
            .Select(destination => (destination.Host, destination.Port)));
        Assert.Equal(systemBefore, original.SystemDestinations
            .Select(destination => (destination.Host, destination.Port)));
        Assert.Equal(original.Mode, after.Mode);
        Assert.Equal(approvedBefore, after.ApprovedDestinations
            .Select(destination => (destination.Host, destination.Port)));
        Assert.Equal(systemBefore, after.SystemDestinations
            .Select(destination => (destination.Host, destination.Port)));
    }

    [Fact]
    public void SystemOnlyCompilationRejectsInvalidApprovalsAndOverlap()
    {
        var invalidApproval = PlatformEgressAcsCompiler.CompileSystemOnlyCandidate(
            CreateArtifact("PublicHttps", ["contoso.vault.azure.net"]),
            "canonical-policy",
            "source-revision-1");
        var overlap = PlatformEgressAcsCompiler.CompileSystemOnlyCandidate(
            CreateArtifact("Allowlist", ["api.github.com"]),
            "canonical-policy",
            "source-revision-1");

        Assert.False(invalidApproval.IsSuccess);
        Assert.Null(invalidApproval.Candidate);
        Assert.Equal("network_destination_invalid", invalidApproval.Failure?.Code);
        Assert.False(overlap.IsSuccess);
        Assert.Null(overlap.Candidate);
        Assert.Equal("network_destination_duplicate", overlap.Failure?.Code);
    }

    [Fact]
    public void SystemOnlyCompilationRejectsTamperedCatalogAndMalformedArtifact()
    {
        var valid = CreateArtifact("PublicHttps", ["approved.example.com"]);
        var data = Encoding.UTF8.GetString(
            valid.Files.Single(file => file.RelativePath == "policy/data.json").Bytes.AsSpan());
        var tampered = ReplaceData(
            valid,
            data.Replace("api.business.githubcopilot.com", "added.example.com", StringComparison.Ordinal));
        var malformed = ReplaceData(valid, "{");

        var catalogResult = PlatformEgressAcsCompiler.CompileSystemOnlyCandidate(
            tampered, "canonical-policy", "source-revision-1");
        var malformedResult = PlatformEgressAcsCompiler.CompileSystemOnlyCandidate(
            malformed, "canonical-policy", "source-revision-1");

        Assert.False(catalogResult.IsSuccess);
        Assert.Null(catalogResult.Candidate);
        Assert.Equal("network_catalog_mismatch", catalogResult.Failure?.Code);
        Assert.False(malformedResult.IsSuccess);
        Assert.Null(malformedResult.Candidate);
        Assert.Equal("network_data_json", malformedResult.Failure?.Code);
    }

    [Fact]
    public void ReorderedApprovalsKeepSortedDestinationsAndEachArtifactHash()
    {
        var firstArtifact = CreateArtifact("PublicHttps", ["z.example.com", "a.example.com"]);
        var secondArtifact = CreateArtifact("PublicHttps", ["a.example.com", "z.example.com"]);
        var firstNormal = PlatformEgressAcsCompiler.CompileCandidate(
            firstArtifact, "canonical-policy", "source-revision-1").Candidate!;
        var secondNormal = PlatformEgressAcsCompiler.CompileCandidate(
            secondArtifact, "canonical-policy", "source-revision-1").Candidate!;
        var firstSystemOnly = PlatformEgressAcsCompiler.CompileSystemOnlyCandidate(
            firstArtifact, "canonical-policy", "source-revision-1").Candidate!;
        var secondSystemOnly = PlatformEgressAcsCompiler.CompileSystemOnlyCandidate(
            secondArtifact, "canonical-policy", "source-revision-1").Candidate!;

        Assert.NotEqual(firstArtifact.ContentSha256, secondArtifact.ContentSha256);
        Assert.Equal(firstArtifact.ContentSha256, firstNormal.SourceHash);
        Assert.Equal(secondArtifact.ContentSha256, secondNormal.SourceHash);
        Assert.Equal(firstArtifact.ContentSha256, firstSystemOnly.SourceHash);
        Assert.Equal(secondArtifact.ContentSha256, secondSystemOnly.SourceHash);
        Assert.Equal(
            firstNormal.ApprovedDestinations.Select(destination => (destination.Host, destination.Port)),
            secondNormal.ApprovedDestinations.Select(destination => (destination.Host, destination.Port)));
        Assert.Equal(
            firstNormal.SystemDestinations.Select(destination => (destination.Host, destination.Port)),
            secondNormal.SystemDestinations.Select(destination => (destination.Host, destination.Port)));
        Assert.Equal(
            firstSystemOnly.SystemDestinations.Select(destination => (destination.Host, destination.Port)),
            secondSystemOnly.SystemDestinations.Select(destination => (destination.Host, destination.Port)));
    }

    [Fact]
    public void ApprovedOnlyChangeKeepsSystemBaselineAndChangesSourceHash()
    {
        var withApproval = CreateArtifact("Allowlist", ["approved.example.com"]);
        var withoutApproval = CreateArtifact("Allowlist", []);
        var first = PlatformEgressAcsCompiler.CompileSystemOnlyCandidate(
            withApproval, "canonical-policy", "source-revision-1").Candidate!;
        var second = PlatformEgressAcsCompiler.CompileSystemOnlyCandidate(
            withoutApproval, "canonical-policy", "source-revision-1").Candidate!;

        Assert.NotEqual(first.SourceHash, second.SourceHash);
        Assert.Equal(
            first.SystemDestinations.Select(destination => (destination.Host, destination.Port)),
            second.SystemDestinations.Select(destination => (destination.Host, destination.Port)));
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

            var candidate = PlatformEgressAcsCompiler.CompileCandidate(
                artifact, "canonical-policy", "source-revision-1");
            Assert.False(candidate.IsSuccess);
            Assert.Null(candidate.Candidate);
            Assert.Equal(code, candidate.Failure?.Code);
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
