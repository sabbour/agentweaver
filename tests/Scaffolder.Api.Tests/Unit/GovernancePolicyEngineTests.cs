using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Scaffolder.Api.Agent.Governance;
using Scaffolder.Api.Persistence;
using Xunit;

namespace Scaffolder.Api.Tests.Unit;

/// <summary>
/// T071: Unit tests for GovernancePolicyEngine.
/// Covers model-source validation, tool allowlist, human-approval gate (SC-010).
/// </summary>
public sealed class GovernancePolicyEngineTests
{
    private readonly IOperationalRecordRepository _opRecords =
        Substitute.For<IOperationalRecordRepository>();

    private GovernancePolicyEngine CreateEngine() =>
        new(_opRecords, NullLogger<GovernancePolicyEngine>.Instance);

    // ------------------------------------------------------------------
    // Model-source validation
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(ModelSource.CopilotSdk)]
    [InlineData(ModelSource.MicrosoftFoundry)]
    public void ValidateModelSource_SupportedProviders_ReturnsNull(ModelSource source)
    {
        var engine = CreateEngine();
        var result = engine.ValidateModelSource(source);
        result.Should().BeNull("supported model sources must pass validation");
    }

    [Fact]
    public void ValidateModelSource_InvalidValue_ReturnsErrorMessage()
    {
        var engine = CreateEngine();
        // Cast an out-of-range integer to ModelSource to simulate an invalid value
        var invalid = (ModelSource)999;
        var result = engine.ValidateModelSource(invalid);
        result.Should().NotBeNull("unsupported model sources must be rejected");
        result.Should().Contain("Unsupported");
    }

    // ------------------------------------------------------------------
    // Tool allowlist
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("read_file")]
    [InlineData("write_file")]
    public void IsToolAllowed_SupportedTools_ReturnsTrue(string toolName)
    {
        var engine = CreateEngine();
        engine.IsToolAllowed(toolName).Should().BeTrue();
    }

    [Theory]
    [InlineData("delete_file")]
    [InlineData("execute_shell")]
    [InlineData("network_request")]
    [InlineData("")]
    public void IsToolAllowed_DisallowedTools_ReturnsFalse(string toolName)
    {
        var engine = CreateEngine();
        engine.IsToolAllowed(toolName).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Human-approval gate
    // ------------------------------------------------------------------

    [Fact]
    public void ValidateHumanApprovalGate_ApprovedStatus_ReturnsNull()
    {
        var engine = CreateEngine();
        // Approved status means the run has been approved and merge can proceed
        var result = engine.ValidateHumanApprovalGate(RunStatus.Approved);
        result.Should().BeNull("Approved runs should pass the human-approval gate for merge");
    }

    [Theory]
    [InlineData(RunStatus.Queued)]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Completed)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Merged)]
    public void ValidateHumanApprovalGate_WrongStatus_ReturnsError(RunStatus status)
    {
        var engine = CreateEngine();
        var result = engine.ValidateHumanApprovalGate(status);
        result.Should().NotBeNull("merge must only be attempted on Approved runs");
    }
}
