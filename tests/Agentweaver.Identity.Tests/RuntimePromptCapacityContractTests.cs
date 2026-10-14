using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class RuntimePromptCapacityContractTests
{
    [Theory]
    [InlineData(1024, null)]
    [InlineData(200000, null)]
    [InlineData(1024, "implement")]
    [InlineData(200000, "implement")]
    public void AcceptedPromptCapacityPassesTheNumericLimitGate(
        int maxPromptTokens, string? workflowStepId)
    {
        RuntimeContractValidation.RequireSupportedNumericRunLimits(Binding() with
        {
            MaxPromptTokens = maxPromptTokens,
            WorkflowStepId = workflowStepId
        });
    }

    [Fact]
    public void PromptCapacitySupportDoesNotEnableUnboundTurnOrToolCallLimits()
    {
        var binding = Binding();

        Assert.Equal("runtime_numeric_limits_unsupported",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeContractValidation.RequireSupportedNumericRunLimits(binding with
                {
                    MaxModelTurns = 1
                })).Code);
        Assert.Equal("runtime_numeric_limits_unsupported",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeContractValidation.RequireSupportedNumericRunLimits(binding with
                {
                    MaxToolCalls = 1
                })).Code);

        RuntimeContractValidation.RequireSupportedNumericRunLimits(binding with
        {
            WorkflowStepId = "implement",
            MaxModelTurns = 1,
            MaxToolCalls = 1,
            MaxPromptTokens = 8192
        });
    }

    private static RuntimeBinding Binding() => new(
        "https://broker.test/",
        Guid.NewGuid().ToString("D"),
        "tenant",
        "project",
        "run",
        "session",
        "agent",
        "turn",
        1,
        1,
        1,
        "context:1",
        new string('a', 64),
        1,
        "environment",
        "placement-uid",
        1,
        "hosted-profile",
        new Uri("https://runtime.test/configure"),
        new Uri("https://orchestrator.test/runtime/observations"));
}
