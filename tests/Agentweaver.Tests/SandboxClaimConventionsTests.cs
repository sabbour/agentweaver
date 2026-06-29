using System.Text.Json;
using Agentweaver.Api.Sandbox;
using FluentAssertions;

namespace Agentweaver.Tests;

/// <summary>
/// Unit tests for <see cref="SandboxClaimConventions"/> that pin the bound-pod resolution to the
/// REAL agent-sandbox CRD status schema (v1alpha1/v1beta1):
///   • readiness is signalled by a <c>Ready</c> condition (<c>status.conditions[].type=='Ready'</c>
///     with <c>status=='True'</c>) — there is NO <c>status.phase</c> field;
///   • the bound pod name is <c>status.sandbox.name</c> (Sandbox object name == pod name).
///
/// The previous tests mocked a fake <c>status.phase=="Bound"</c>, which the real controller never
/// emits — that is exactly why the broken readiness signal slipped past review. These tests assert
/// the genuine schema and treat a phase-only status as NOT bound.
/// </summary>
public sealed class SandboxClaimConventionsTests
{
    private static string? Resolve(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SandboxClaimConventions.TryGetBoundPodName(doc.RootElement);
    }

    [Fact]
    public void Returns_pod_name_when_Ready_condition_True_and_sandbox_name_present()
    {
        const string json = """
        {"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}
        """;

        Resolve(json).Should().Be("agent-pod-1");
    }

    [Fact]
    public void Returns_null_when_Ready_condition_False()
    {
        const string json = """
        {"status":{"conditions":[{"type":"Ready","status":"False"}],"sandbox":{"name":"agent-pod-1"}}}
        """;

        Resolve(json).Should().BeNull("a False Ready condition means the claim is not bound");
    }

    [Fact]
    public void Returns_null_when_no_conditions_present()
    {
        Resolve("""{"status":{"sandbox":{"name":"agent-pod-1"}}}""")
            .Should().BeNull("absent conditions means not ready");
    }

    [Fact]
    public void Ignores_legacy_status_phase_Bound_without_Ready_condition()
    {
        // The real CRD has no status.phase; a phase-only status must NOT be treated as bound.
        const string json = """
        {"status":{"phase":"Bound","sandbox":{"name":"agent-pod-1"}}}
        """;

        Resolve(json).Should().BeNull("status.phase is not part of the agent-sandbox CRD schema");
    }

    [Fact]
    public void Returns_null_when_Ready_but_sandbox_name_missing()
    {
        const string json = """
        {"status":{"conditions":[{"type":"Ready","status":"True"}]}}
        """;

        Resolve(json).Should().BeNull("a bound pod requires status.sandbox.name");
    }

    [Fact]
    public void Returns_null_when_status_absent()
    {
        Resolve("""{"metadata":{"name":"c"}}""").Should().BeNull();
    }

    [Theory]
    [InlineData("run-abc-123", "agent-runabc123")]
    [InlineData("abcdefghijklmnop", "agent-abcdefghijkl")]
    public void DeriveAgentHostClaimName_strips_hyphens_truncates_and_prefixes(string runId, string expected)
    {
        SandboxClaimConventions.DeriveAgentHostClaimName(runId).Should().Be(expected);
    }

    private static string? Reconciler(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SandboxClaimConventions.TryGetReconcilerError(doc.RootElement);
    }

    [Fact]
    public void TryGetReconcilerError_detects_exceeded_quota_message()
    {
        const string json = """
        {"status":{"conditions":[{"type":"Ready","status":"False","message":"pods \"x\" is forbidden: exceeded quota"}]}}
        """;

        Reconciler(json).Should().Contain("exceeded quota");
    }

    [Fact]
    public void TryGetReconcilerError_detects_ReconcilerError_type()
    {
        const string json = """
        {"status":{"conditions":[{"type":"ReconcilerError","status":"True","reason":"FailedCreate","message":"boom"}]}}
        """;

        Reconciler(json).Should().Be("boom");
    }

    [Fact]
    public void TryGetReconcilerError_returns_null_for_healthy_claim()
    {
        const string json = """
        {"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}
        """;

        Reconciler(json).Should().BeNull();
    }

    [Fact]
    public void TryGetReconcilerError_returns_null_when_status_absent()
    {
        Reconciler("""{"metadata":{"name":"c"}}""").Should().BeNull();
    }

    [Theory]
    [InlineData("24", true, 24.0)]
    [InlineData("1.5", true, 1.5)]
    [InlineData("500m", true, 0.5)]
    [InlineData("2000m", true, 2.0)]
    [InlineData("", false, 0.0)]
    [InlineData("abc", false, 0.0)]
    public void TryParseCpu_handles_cores_and_millicores(string value, bool expectedOk, double expectedCores)
    {
        var ok = KubernetesSandboxExecutor.TryParseCpu(value, out var cores);
        ok.Should().Be(expectedOk);
        if (expectedOk)
            cores.Should().BeApproximately(expectedCores, 1e-9);
    }
}
