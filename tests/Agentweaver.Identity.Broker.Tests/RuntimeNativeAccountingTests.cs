using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using GitHub.Copilot;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeNativeAccountingTests
{
    [Theory]
    [InlineData("complete", SdkAiCreditsStatus.Complete)]
    [InlineData("partial", SdkAiCreditsStatus.Partial)]
    [InlineData("unavailable", SdkAiCreditsStatus.Unavailable)]
    public void PreservesActualAccountingIdentityAndExplicitAvailability(
        string nativeStatus, SdkAiCreditsStatus expected)
    {
        var observation = RuntimeCopilotSession.ExtractAccounting(
            Source(), Usage($$"""
                {"sourceSessionId":"native-session","sequence":17,"usageId":"provider-call-42"}
                """, nativeStatus, 0));

        Assert.Equal(new("native-session", 17, "provider-call-42"), observation.Identity);
        Assert.Equal(expected, observation.AiCreditsStatus);
        Assert.True(observation.AiCreditsStatusReported);
    }

    [Fact]
    public void MissingIdentityOrStatusRemainsUnavailableWithoutInventingASequenceOrZeroCharge()
    {
        var observation = RuntimeCopilotSession.ExtractAccounting(
            Source(), Usage("null", null, null));

        Assert.Null(observation.Identity);
        Assert.Equal(SdkAiCreditsStatus.Unavailable, observation.AiCreditsStatus);
        Assert.False(observation.AiCreditsStatusReported);
    }

    [Theory]
    [InlineData("foreign-session", 17, "provider-call-42")]
    [InlineData("native-session", 0, "provider-call-42")]
    [InlineData("native-session", -1, "provider-call-42")]
    [InlineData("native-session", 17, "")]
    [InlineData("native-session", 17, "provider call")]
    public void RejectsForeignSourceOrInvalidNativeAccountingIdentity(
        string sourceSession, long sequence, string usageId)
    {
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeCopilotSession.ExtractAccounting(
                Source(), Usage($$"""
                    {"sourceSessionId":"{{sourceSession}}","sequence":{{sequence}},
                     "usageId":"{{usageId}}"}
                    """, "complete", 1)));
    }

    [Theory]
    [InlineData("complete", null)]
    [InlineData("future-status", 1d)]
    public void RejectsUnsupportedCompletenessOrCompleteWithoutAReportedAmount(
        string nativeStatus, double? reportedAmount)
    {
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeCopilotSession.ExtractAccounting(
                Source(), Usage("null", nativeStatus, reportedAmount)));
    }

    private static SdkSessionFacts Source() =>
        new(Guid.NewGuid(), "native-session", "1.0.18", "1.0.79", "model:selected",
            "native-model", new string('a', 64), 1m, "hosted-copilot",
            SdkMeterSources.CopilotNanoAiu, new string('A', 64), 1);

    private static AssistantUsageEvent Usage(
        string accounting, string? status, double? reportedAmount)
    {
        var nativeStatus = System.Text.Json.JsonSerializer.Serialize(status);
        var cost = reportedAmount is null
            ? "null" : $$"""{"totalNanoAiu":{{reportedAmount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""";
        return (AssistantUsageEvent)SessionEvent.FromJson($$$"""
            {"type":"assistant.usage","id":"{{{Guid.NewGuid():D}}}",
             "timestamp":"2026-10-09T21:00:00Z","parentId":null,
             "data":{"model":"native-model","accounting":{{{accounting}}},
                     "aiCreditsStatus":{{{nativeStatus}}},"copilotUsage":{{{cost}}}}}
            """);
    }
}
