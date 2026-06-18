using FluentAssertions;
using Scaffolder.Api.Coordinator;

namespace Scaffolder.Tests.Coordinator;

/// <summary>
/// Unit tests for <see cref="CoordinatorOrchestratorExecutor.IsDispatchable"/>.
/// Verifies that infrastructure/built-in agents (Scribe, Ralph, Rai) are never
/// eligible for subtask dispatch, while legitimate work-doer roles pass through.
/// </summary>
public sealed class RosterDispatchFilterTests
{
    // -----------------------------------------------------------------------
    // Built-in agents — must be excluded (by name, role id, and title variants)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Scribe", "scribe", "Scribe")]
    [InlineData("scribe", "scribe", "scribe")]
    [InlineData("SCRIBE", "SCRIBE", "SCRIBE")]
    public void Scribe_ByExactName_IsNotDispatchable(string name, string roleId, string roleTitle)
        => CoordinatorOrchestratorExecutor.IsDispatchable(name, roleId, roleTitle).Should().BeFalse();

    [Fact]
    public void Scribe_ByNameOnly_IsNotDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("Scribe", "something-else", "Something Else").Should().BeFalse();

    [Fact]
    public void Scribe_ByRoleId_IsNotDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("AnyName", "scribe", "Anything").Should().BeFalse();

    [Fact]
    public void Scribe_ByRoleTitle_IsNotDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("AnyName", "any-id", "Scribe").Should().BeFalse();

    [Fact]
    public void Scribe_TitleWithSuffix_IsNotDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("AnyName", "any-id", "Scribe (silent)").Should().BeFalse();

    [Fact]
    public void Ralph_ByExactName_IsNotDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("Ralph", "ralph", "Ralph").Should().BeFalse();

    [Fact]
    public void Ralph_ByRoleId_IsNotDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("SomeName", "ralph", "Some Title").Should().BeFalse();

    [Fact]
    public void Ralph_TitleWithSuffix_IsNotDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("SomeName", "some-id", "Ralph (reviewer)").Should().BeFalse();

    [Fact]
    public void Rai_ByExactName_IsNotDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("Rai", "rai", "Rai").Should().BeFalse();

    [Fact]
    public void Rai_ByRoleId_IsNotDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("SomeName", "rai", "Some Title").Should().BeFalse();

    [Fact]
    public void Rai_TitleWithSuffix_IsNotDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("SomeName", "some-id", "Rai (assistant)").Should().BeFalse();

    // -----------------------------------------------------------------------
    // Prefix-match boundary: similar-but-distinct names must NOT be excluded
    // -----------------------------------------------------------------------

    [Fact]
    public void Scribner_IsDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("Scribner", "scribner", "Scribner").Should().BeTrue();

    [Fact]
    public void Raimond_IsDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("Raimond", "raimond", "Raimond").Should().BeTrue();

    [Fact]
    public void Ralphs_IsDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("Ralphs", "ralphs", "Ralphs").Should().BeTrue();

    // -----------------------------------------------------------------------
    // Legitimate work-doer roles — must pass through
    // -----------------------------------------------------------------------

    [Fact]
    public void BackendDev_IsDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("Morpheus", "backend-dev", "Backend Dev").Should().BeTrue();

    [Fact]
    public void QaTester_IsDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("Tester", "qa", "QA Tester").Should().BeTrue();

    [Fact]
    public void FrontendDev_IsDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("Trinity", "frontend-dev", "Frontend Developer").Should().BeTrue();

    [Fact]
    public void NullFields_AreDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable(null, null, null).Should().BeTrue();

    [Fact]
    public void EmptyFields_AreDispatchable()
        => CoordinatorOrchestratorExecutor.IsDispatchable("", "", "").Should().BeTrue();
}
