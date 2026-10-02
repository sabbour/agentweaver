using System.Text.Json;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Contracts;
using FluentAssertions;

namespace Agentweaver.Tests;

public sealed class CurrentSandboxBindingTests
{
    private const string Run = "6eb78548-a627-4897-bf2d-941d8d42a9f6";
    private const string Claim = """
        {"metadata":{"name":"agent-6eb78548a627","namespace":"agentweaver","uid":"claim-new","resourceVersion":"42","annotations":{"agentweaver.io/run-id":"6eb78548-a627-4897-bf2d-941d8d42a9f6","agentweaver.io/run-lifecycle-generation":"1","agentweaver.io/agent-host-holder-token":"4","agentweaver.io/source-repository":"/repo","agentweaver.io/source-ref":"integration","agentweaver.io/source-base-commit":"abcdef","agentweaver.io/source-tree":"tree","agentweaver.io/working-directory":"/worktree"}},"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"pod-new"}}}
        """;
    private const string Sandbox = """
        {"metadata":{"name":"pod-new","namespace":"agentweaver","uid":"sandbox-new","labels":{"agents.x-k8s.io/claim-uid":"claim-new"},"ownerReferences":[{"kind":"SandboxClaim","name":"agent-6eb78548a627","uid":"claim-new","controller":true}]}}
        """;
    private const string Pod = """
        {"metadata":{"name":"pod-new","namespace":"agentweaver","uid":"pod-uid-new","ownerReferences":[{"kind":"Sandbox","name":"pod-new","uid":"sandbox-new","controller":true}]},"status":{"phase":"Running","conditions":[{"type":"Ready","status":"True"}]}}
        """;

    private static CurrentSandboxBindingResult Verify(
        string claim = Claim, string sandbox = Sandbox, string pod = Pod,
        string boundPod = "pod-new", string? attestedPodUid = "pod-uid-new",
        long? leaseToken = 4, int leaseGeneration = 1, string tree = "tree")
    {
        using var c = JsonDocument.Parse(claim);
        using var s = JsonDocument.Parse(sandbox);
        using var p = JsonDocument.Parse(pod);
        return CurrentSandboxBindingVerifier.Verify(
            Run, "agentweaver", 1, tree, leaseToken, leaseGeneration,
            boundPod, "claim-new", "sandbox-new", attestedPodUid,
            "4", "/repo", "integration", "abcdef", "/worktree",
            c.RootElement, s.RootElement, p.RootElement);
    }

    [Fact]
    public void Configured_current_claim_with_owner_chain_is_verified()
    {
        var binding = Verify();
        binding.State.Should().Be("verified");
        binding.RunId.Should().Be(Run);
        binding.PodName.Should().Be("pod-new");
        binding.PodUid.Should().Be("pod-uid-new");
        binding.ClaimUid.Should().Be("claim-new");
    }

    [Theory]
    [InlineData("kata-exec-sidecar")]
    [InlineData("kubernetes-sandbox-claim")]
    public void Provisioner_proof_does_not_relabel_kata_or_legacy_executor(string backend)
    {
        var historical = new SandboxStatusDto
        {
            Backend = backend,
            IsRealIsolation = backend == "kubernetes-sandbox-claim",
            ClaimName = "agent-6eb78548a627",
            PodName = "pod-old",
            CurrentBinding = Verify(),
        };
        historical.Backend.Should().Be(backend);
        historical.PodName.Should().Be("pod-old");
        historical.CurrentBinding!.PodName.Should().Be("pod-new");
        historical.CurrentBinding.Provisioner.Should().Be("kubernetes-sandbox-claim");
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(3L, 1)]
    [InlineData(4L, 2)]
    public void No_current_matching_lease_is_unavailable(long? token, int generation) =>
        Verify(leaseToken: token, leaseGeneration: generation).State.Should().NotBe("verified");

    [Fact]
    public void Historical_binding_never_selects_an_old_pod() =>
        Verify(boundPod: "pod-old").State.Should().Be("conflict");

    [Fact]
    public void Early_bound_and_old_attestation_cannot_prove_rotated_pod()
    {
        RunCurrentBindingReader.AttestationFollowsCurrentBinding(44, 0).Should().BeFalse();
        RunCurrentBindingReader.AttestationFollowsCurrentBinding(200, 175).Should().BeFalse();
        RunCurrentBindingReader.AttestationFollowsCurrentBinding(200, 201).Should().BeTrue();
    }

    [Fact]
    public void Pod_replacement_or_wrong_owner_fails_closed()
    {
        Verify(attestedPodUid: "pod-uid-old").State.Should().Be("conflict");
        Verify(sandbox: Sandbox.Replace("claim-new", "foreign", StringComparison.Ordinal))
            .State.Should().Be("conflict");
        Verify(pod: Pod.Replace("sandbox-new", "foreign", StringComparison.Ordinal))
            .State.Should().Be("conflict");
    }

    [Fact]
    public void Source_tree_and_readiness_are_required()
    {
        Verify(tree: "old-tree").State.Should().Be("conflict");
        Verify(claim: Claim.Replace("\"True\"", "\"False\"", StringComparison.Ordinal))
            .State.Should().Be("unavailable");
        Verify(pod: Pod.Replace("\"phase\":\"Running\"", "\"phase\":\"Pending\"", StringComparison.Ordinal))
            .Reason.Should().Be("pod_not_ready");
        Verify(pod: Pod.Replace("\"status\":\"True\"", "\"status\":\"False\"", StringComparison.Ordinal))
            .Reason.Should().Be("pod_not_ready");
        Verify(claim: Claim.Replace("\"uid\":\"claim-new\"",
            "\"uid\":\"claim-new\",\"deletionTimestamp\":\"2026-10-02T00:43:00Z\"", StringComparison.Ordinal))
            .Reason.Should().Be("binding_resource_deleting");
        Verify(sandbox: Sandbox.Replace("\"uid\":\"sandbox-new\"",
            "\"uid\":\"sandbox-new\",\"deletionTimestamp\":\"2026-10-02T00:43:00Z\"", StringComparison.Ordinal))
            .Reason.Should().Be("binding_resource_deleting");
        Verify(pod: Pod.Replace("\"uid\":\"pod-uid-new\"",
            "\"uid\":\"pod-uid-new\",\"deletionTimestamp\":\"2026-10-02T00:43:00Z\"", StringComparison.Ordinal))
            .Reason.Should().Be("binding_resource_deleting");
    }

    [Theory]
    [InlineData("agentweaver.io/run-id", "foreign")]
    [InlineData("agentweaver.io/run-lifecycle-generation", "2")]
    [InlineData("agentweaver.io/agent-host-holder-token", "3")]
    [InlineData("agentweaver.io/source-ref", "old-ref")]
    [InlineData("agentweaver.io/source-base-commit", "old-commit")]
    [InlineData("agentweaver.io/source-repository", "/foreign")]
    [InlineData("agentweaver.io/working-directory", "/old")]
    public void Foreign_run_generation_holder_or_source_is_conflict(string key, string replacement)
    {
        using var document = JsonDocument.Parse(Claim);
        var original = document.RootElement.GetProperty("metadata").GetProperty("annotations")
            .GetProperty(key).GetString()!;
        Verify(claim: Claim.Replace($"\"{key}\":\"{original}\"",
            $"\"{key}\":\"{replacement}\"", StringComparison.Ordinal)).State.Should().Be("conflict");
    }

    [Fact]
    public void Missing_claim_uid_label_or_missing_owner_never_verifies()
    {
        Verify(sandbox: Sandbox.Replace("agents.x-k8s.io/claim-uid\":\"claim-new", "agents.x-k8s.io/claim-uid\":\"old-claim", StringComparison.Ordinal))
            .State.Should().Be("conflict");
        Verify(sandbox: Sandbox.Replace("\"controller\":true", "\"controller\":false", StringComparison.Ordinal))
            .State.Should().Be("conflict");
    }

    [Fact]
    public void Rotation_between_reads_rejects_new_bound_unbound_attestation_lease_and_claim()
    {
        RunCurrentBindingReader.FenceUnchanged(5, 6, 5, 6, 4, 4, "uid", "42", "uid", "42", "sandbox", "sandbox", "pod", "pod")
            .Should().BeTrue();
        RunCurrentBindingReader.FenceUnchanged(5, 6, 7, 6, 4, 4, "uid", "42", "uid", "42", "sandbox", "sandbox", "pod", "pod")
            .Should().BeFalse();
        RunCurrentBindingReader.FenceUnchanged(5, 6, 5, 8, 4, 4, "uid", "42", "uid", "42", "sandbox", "sandbox", "pod", "pod")
            .Should().BeFalse();
        RunCurrentBindingReader.FenceUnchanged(5, 6, 5, 6, 4, null, "uid", "42", "uid", "42", "sandbox", "sandbox", "pod", "pod")
            .Should().BeFalse();
        RunCurrentBindingReader.FenceUnchanged(5, 6, 5, 6, 4, 4, "uid", "42", "replacement", "42", "sandbox", "sandbox", "pod", "pod")
            .Should().BeFalse();
        RunCurrentBindingReader.FenceUnchanged(5, 6, 5, 6, 4, 4, "uid", "42", "uid", "43", "sandbox", "sandbox", "pod", "pod")
            .Should().BeFalse();
        RunCurrentBindingReader.FenceUnchanged(5, 6, 5, 6, 4, 4, "uid", "42", "uid", "42", "sandbox", "replacement", "pod", "pod")
            .Should().BeFalse();
        RunCurrentBindingReader.FenceUnchanged(5, 6, 5, 6, 4, 4, "uid", "42", "uid", "42", "sandbox", "sandbox", "pod", "replacement")
            .Should().BeFalse();
    }
}
