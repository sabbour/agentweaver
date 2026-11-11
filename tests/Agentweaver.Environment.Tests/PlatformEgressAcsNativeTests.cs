using System.Text.Json;
using AgentControlSpecification;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Policy.Acs;
using Xunit;

namespace Agentweaver.Environment.Tests;

[CollectionDefinition("Native egress ACS", DisableParallelization = true)]
public sealed class NativeEgressAcsTestCollection
{
}

[Collection("Native egress ACS")]
public sealed class PlatformEgressAcsNativeTests
{
    private static readonly string[] SystemHosts =
    [
        "api.business.githubcopilot.com",
        "api.enterprise.githubcopilot.com",
        "api.github.com",
        "api.githubcopilot.com",
        "api.individual.githubcopilot.com"
    ];

    private static readonly string WorkRoot =
        Path.Combine(AppContext.BaseDirectory, "native-egress-acs-work");

    [LinuxNativeAcsFact]
    public async Task NativeDecisionsMatchTheCompiledIntentForBothModes()
    {
        var opaPath = System.Environment.GetEnvironmentVariable("ACS_OPA_PATH")!;
        var evaluator = new NativeAcsPolicyEvaluator(
            new NativeAcsArtifactValidator(opaPath),
            WorkRoot);

        foreach (var mode in new[] { "PublicHttps", "Allowlist" })
        {
            foreach (var approved in new[]
                     {
                         new[] { "approved.example.com" },
                         Array.Empty<string>()
                     })
            {
                var artifact = CreateArtifact(mode, approved);
                var validation = new NativeAcsArtifactValidator(opaPath).Validate(artifact);
                Assert.True(
                    validation.IsValid,
                    string.Join("; ", validation.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));

                var compiled = PlatformEgressAcsCompiler.Compile(
                    artifact,
                    "canonical-policy",
                    "revision-7",
                    "checkpoint-7");
                Assert.True(compiled.IsSuccess, compiled.Failure?.Code);
                Assert.Equal(artifact.ContentSha256, compiled.Snapshot!.SourceHash);
                Assert.Equal("revision-7", compiled.Snapshot.SourceRevision);
                Assert.Equal("checkpoint-7", compiled.Snapshot.Checkpoint);
                Assert.Equal(
                    compiled.Snapshot.Destinations.Select(destination => destination.Host),
                    compiled.Snapshot.Destinations
                        .OrderBy(destination => destination.Host, StringComparer.Ordinal)
                        .Select(destination => destination.Host));

                var cases = new[]
                {
                    (Kind: "dns", Value: "approved.example.com", Check: "packet", Protocol: "TCP", Port: 443),
                    (Kind: "dns", Value: SystemHosts[0], Check: "packet", Protocol: "TCP", Port: 443),
                    (Kind: "dns", Value: "outside.example.com", Check: "packet", Protocol: "TCP", Port: 443),
                    (Kind: "dns", Value: "approved.example.com", Check: "known_destination_use", Protocol: "TCP", Port: 443),
                    (Kind: "dns", Value: SystemHosts[0], Check: "known_destination_use", Protocol: "TCP", Port: 443),
                    (Kind: "dns", Value: "outside.example.com", Check: "known_destination_use", Protocol: "TCP", Port: 443),
                    (Kind: "ip", Value: "20.42.0.1", Check: "packet", Protocol: "TCP", Port: 443),
                    (Kind: "ip", Value: "169.254.169.254", Check: "packet", Protocol: "TCP", Port: 443),
                    (Kind: "ip", Value: "20.42.0.1", Check: "known_destination_use", Protocol: "TCP", Port: 443),
                    (Kind: "dns", Value: "approved.example.com", Check: "packet", Protocol: "UDP", Port: 443),
                    (Kind: "dns", Value: "approved.example.com", Check: "packet", Protocol: "TCP", Port: 80),
                    (Kind: "cidr", Value: "20.42.0.1", Check: "packet", Protocol: "TCP", Port: 443),
                    (Kind: "dns", Value: "approved.example.com", Check: "other", Protocol: "TCP", Port: 443)
                };

                foreach (var network in cases)
                {
                    var inCompiledIntent = CompiledDecision(compiled.Snapshot, network);
                    var native = await evaluator.EvaluateAsync(
                        artifact,
                        InterventionPoint.PreModelCall,
                        Snapshot() with
                        {
                            Network = new NativeAcsNetworkEvaluationFacts(
                                network.Check,
                                network.Protocol,
                                network.Port,
                                new NativeAcsNetworkDestinationFacts(network.Kind, network.Value))
                        });

                    Assert.True(
                        native.Outcome is PolicyEvaluationOutcome.Allow or PolicyEvaluationOutcome.Deny,
                        $"Native outcome was {native.Outcome}: {native.DiagnosticCode ?? "no diagnostic"}.");
                    Assert.True(
                        native.Outcome ==
                            (inCompiledIntent ? PolicyEvaluationOutcome.Allow : PolicyEvaluationOutcome.Deny),
                        $"{mode} {network.Check} {network.Protocol}/{network.Port} {network.Kind}:{network.Value} yielded {native.Outcome}.");
                    Assert.Equal(artifact.ContentSha256, native.ArtifactSha256);
                }

                var missingFacts = await evaluator.EvaluateAsync(
                    artifact,
                    InterventionPoint.PreModelCall,
                    Snapshot());
                Assert.Equal(PolicyEvaluationOutcome.Deny, missingFacts.Outcome);

                var malformedFacts = await evaluator.EvaluateAsync(
                    artifact,
                    InterventionPoint.PreModelCall,
                    Snapshot() with
                    {
                        Network = new NativeAcsNetworkEvaluationFacts("packet", "TCP", 443, null!)
                    });
                Assert.Equal(PolicyEvaluationOutcome.Deny, malformedFacts.Outcome);
            }
        }

        Assert.DoesNotContain(
            Directory.EnumerateDirectories(WorkRoot),
            path => Guid.TryParseExact(Path.GetFileName(path), "N", out _));
    }

    private static NativeAcsArtifact CreateArtifact(string mode, IEnumerable<string> approved) =>
        NativeAcsFiniteNetworkProfile.CreateArtifact(JsonSerializer.Serialize(new
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
        }));

    private static NativeAcsEvaluationSnapshot Snapshot() =>
        new(new SessionIdentity("project-1", "run-1", "session-1"), "tenant-1", "agent-1", "network");

    private static bool CompiledDecision(
        PlatformEgressPolicySnapshot snapshot,
        (string Kind, string Value, string Check, string Protocol, int Port) network)
    {
        if (network.Check is not ("packet" or "known_destination_use") ||
            network.Protocol != "TCP" ||
            network.Port != 443)
            return false;
        if (network.Check == "packet" && snapshot.Mode == PlatformEgressMode.PublicHttps)
            return network.Kind is "dns" or "ip";
        return network.Kind == "dns" &&
            snapshot.Destinations.Any(destination =>
                destination.Host == network.Value && destination.Port == network.Port);
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class LinuxNativeAcsFactAttribute : FactAttribute
{
    public LinuxNativeAcsFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "The pinned ACS package has a Linux-x64 native library.";
        else if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("ACS_OPA_PATH")) ||
                 !File.Exists(System.Environment.GetEnvironmentVariable("ACS_OPA_PATH")))
            Skip = "Set ACS_OPA_PATH to the pinned OPA 0.70.0 executable.";
    }
}
