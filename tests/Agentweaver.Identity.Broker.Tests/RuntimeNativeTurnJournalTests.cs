using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using GitHub.Copilot;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeNativeTurnJournalTests
{
    private const string Model = "controlled-model";
    private static readonly Guid UserId = Guid.Parse("d72f50d7-cdcb-4c73-98b8-223cd9f3b941");
    private static readonly Guid StartId = Guid.Parse("5cda8dce-338a-4187-8f87-9d8c130395bd");
    private static readonly Guid EndId = Guid.Parse("e09d077d-2077-40c1-86ce-8f9f6b845647");
    private static readonly Guid ReceiptId = Guid.Parse("442ffacb-1168-49e4-a5a3-e0c65d4fb74c");

    [Fact]
    public void PreservesActualNativeMessageTurnReceiptAndCursorWithoutInventingAccounting()
    {
        var source = Source();
        var receipt = RuntimeCopilotNativeTurnJournal.Create(
            source, UserId.ToString("D"), "Actual answer.", "native-cursor", Events());

        Assert.Equal(source, receipt.Source);
        Assert.Equal(UserId.ToString("D"), receipt.NativeMessageId);
        Assert.Equal("native-turn-17", receipt.NativeTurnId);
        Assert.Equal(StartId, receipt.NativeTurnStartEventId);
        Assert.Equal(EndId, receipt.NativeTurnEndEventId);
        Assert.Equal(ReceiptId, receipt.NativeCompletionReceiptEventId);
        Assert.Equal("native-cursor", receipt.DurableCursor);
        Assert.Null(receipt.AccountingCheckpoint);
    }

    [Fact]
    public void RetainsAnActualReportedCheckpointAsAnObservedFloor()
    {
        var checkpoint = Event("session.usage_checkpoint", Guid.NewGuid(),
            """{"totalNanoAiu":123.5,"usageAccountingWatermarks":{"native-session":7}}""");
        var receipt = RuntimeCopilotNativeTurnJournal.Create(
            Source(), UserId.ToString("D"), "Actual answer.", "native-cursor", Events().Add(checkpoint));

        Assert.Equal(checkpoint.Id, receipt.AccountingCheckpoint!.NativeEventId);
        Assert.Equal(123.5m, receipt.AccountingCheckpoint.ReportedNanoAiu);
        Assert.Equal(7, receipt.AccountingCheckpoint.SourceWatermarks["native-session"]);
    }

    [Fact]
    public void NativeRequiredAccountingAmountCannotDeserializeMissingAsZero()
    {
        Assert.Throws<System.Text.Json.JsonException>(() =>
            Event("session.usage_checkpoint", Guid.NewGuid(),
                """{"usageAccountingWatermarks":{"native-session":7}}"""));
    }

    [Theory]
    [InlineData("message")]
    [InlineData("end")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("ephemeral")]
    [InlineData("model")]
    [InlineData("missing")]
    public void RejectsMissingReplayedForeignOrMismatchedNativeEvidence(string fault)
    {
        var events = Events();
        var messageId = UserId.ToString("D");
        switch (fault)
        {
            case "message":
                messageId = Guid.NewGuid().ToString("D");
                break;
            case "end":
                events = events.RemoveAt(3);
                break;
            case "duplicate":
                events = events.Add(events[^1]);
                break;
            case "foreign":
                events[^1].AgentId = "foreign-agent";
                break;
            case "ephemeral":
                events[^1].Ephemeral = true;
                break;
            case "model":
                ((AssistantTurnEndEvent)events[3]).Data.Model = "other-model";
                break;
            case "missing":
                events = events.RemoveAt(events.Length - 1);
                break;
        }

        var failure = Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeCopilotNativeTurnJournal.Create(
                Source(), messageId, "Actual answer.", "native-cursor", events));
        Assert.Equal("runtime_native_turn_receipt_unavailable", failure.Code);
    }

    [Fact]
    public void RejectsAnAnswerThatDoesNotMatchTheActualNativeEventRange()
    {
        var failure = Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeCopilotNativeTurnJournal.Create(
                Source(), UserId.ToString("D"), "Invented answer.", "native-cursor", Events()));
        Assert.Equal("runtime_native_turn_output_mismatch", failure.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidReportedAccountingAmounts(double amount)
    {
        var checkpoint = (SessionUsageCheckpointEvent)Event("session.usage_checkpoint", Guid.NewGuid(),
            """{"totalNanoAiu":123.5,"usageAccountingWatermarks":{"native-session":7}}""");
        checkpoint.Data.TotalNanoAiu = amount;
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeCopilotNativeTurnJournal.Create(
                Source(), UserId.ToString("D"), "Actual answer.", "native-cursor", Events().Add(checkpoint)));
    }

    private static SdkSessionFacts Source() =>
        new(Guid.NewGuid(), "native-session", "1.0.18", "1.0.79", "model:selected",
            Model, new string('a', 64), 1m, "hosted-copilot", SdkMeterSources.CopilotNanoAiu,
            new string('A', 64), 1);

    private static ImmutableArray<SessionEvent> Events() =>
        [
            Event("user.message", UserId, """{"content":"Actual request."}"""),
            Event("assistant.turn_start", StartId, $$"""{"turnId":"native-turn-17","model":"{{Model}}"}"""),
            Event("assistant.message", Guid.NewGuid(),
                """{"messageId":"402832a0-5f7c-4e2d-8ad7-cc07e4f2e09b","content":"Actual answer."}"""),
            Event("assistant.turn_end", EndId, $$"""{"turnId":"native-turn-17","model":"{{Model}}"}"""),
            Event("session.completion_receipt", ReceiptId, $$"""
                {"schemaVersion":1,"attempt":1,"sourceEventId":"{{EndId:D}}",
                 "eventRange":{"startEventId":"{{UserId:D}}","endEventId":"{{EndId:D}}"},
                 "stopReason":"natural","successfulToolCount":0,"failedToolCount":0}
                """)
        ];

    private static SessionEvent Event(string type, Guid id, string data) =>
        SessionEvent.FromJson($$"""
            {"type":"{{type}}","id":"{{id:D}}","timestamp":"2026-10-09T21:00:00Z",
             "parentId":null,"data":{{data}}}
            """);
}
