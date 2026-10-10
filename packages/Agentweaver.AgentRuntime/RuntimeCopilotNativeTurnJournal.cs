using System.Collections.Immutable;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace Agentweaver.AgentRuntime;

internal sealed record RuntimeCopilotNativeTurnResult(
    string Response,
    RuntimeNativeTurnObservation Receipt);

#pragma warning disable GHCP001 // This private native receipt bridge is pinned to SDK 1.0.18.
internal static class RuntimeCopilotNativeTurnJournal
{
    internal const int MaximumEvents = 512;

    internal static async Task<RuntimeNativeTurnObservation> ReadAsync(
        CopilotSession session,
        SdkSessionFacts source,
        string startCursor,
        string nativeMessageId,
        string response,
        CancellationToken cancellationToken)
    {
        ValidateCursor(startCursor);
        var events = ImmutableArray.CreateBuilder<SessionEvent>();
        var cursor = startCursor;
        for (var page = 0; page < MaximumEvents / 128; page++)
        {
            var batch = await session.Rpc.EventLog.ReadAsync(
                cursor: cursor,
                max: 128,
                waitMs: TimeSpan.Zero,
                types: "*",
                agentScope: EventsAgentScope.All,
                direction: EventsReadDirection.Forward,
                includeEphemeral: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (batch.CursorStatus != EventsCursorStatus.Ok || batch.Events is null ||
                batch.Events.Count > 128 || batch.HasMore && batch.Cursor == cursor)
                throw Unavailable();
            ValidateCursor(batch.Cursor);
            events.AddRange(batch.Events);
            cursor = batch.Cursor;
            if (!batch.HasMore)
                return Create(source, nativeMessageId, response, cursor, events.ToImmutable());
        }
        throw new RuntimeAuthorizationException("runtime_native_turn_journal_limit_exceeded");
    }

    internal static RuntimeNativeTurnObservation Create(
        SdkSessionFacts source,
        string nativeMessageId,
        string response,
        string durableCursor,
        ImmutableArray<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateCursor(durableCursor);
        if (!Guid.TryParse(nativeMessageId, out var messageId) || messageId == Guid.Empty ||
            response is null || response.Length > AddressedMessageValidation.MaximumTextLength ||
            events.IsDefaultOrEmpty || events.Length > MaximumEvents ||
            events.Any(item => item is null || item.Id == Guid.Empty || item.Ephemeral == true) ||
            events.Select(item => item.Id).Distinct().Count() != events.Length)
            throw Unavailable();

        var userIndex = IndexOf(events, item =>
            item is UserMessageEvent && item.Id == messageId && item.AgentId is null);
        var receipts = events.Select((item, index) => (Event: item, Index: index))
            .Where(item => item.Event is SessionCompletionReceiptEvent receipt &&
                receipt.AgentId is null && receipt.Data.EventRange?.StartEventId == nativeMessageId)
            .ToArray();
        if (userIndex < 0 || receipts.Length != 1)
            throw Unavailable();
        var completion = (SessionCompletionReceiptEvent)receipts[0].Event;
        var receiptIndex = receipts[0].Index;
        if (completion.Data.SchemaVersion != 1 || completion.Data.Attempt < 1 ||
            completion.Data.SuccessfulToolCount < 0 || completion.Data.FailedToolCount < 0 ||
            completion.Data.SourceEventId != completion.Data.EventRange.EndEventId ||
            !Guid.TryParse(completion.Data.SourceEventId, out var endEventId))
            throw Unavailable();
        var endIndex = IndexOf(events, item => item.Id == endEventId);
        if (endIndex <= userIndex || endIndex >= receiptIndex ||
            events[endIndex] is not AssistantTurnEndEvent end ||
            end.AgentId is not null || end.Data.ParentToolCallId is not null ||
            end.Data.Model is not null && end.Data.Model != source.ModelId ||
            string.IsNullOrWhiteSpace(end.Data.TurnId) || end.Data.TurnId.Length > 256 ||
            end.Data.TurnId.Any(char.IsControl))
            throw Unavailable();
        var starts = events.Skip(userIndex + 1).Take(endIndex - userIndex - 1)
            .OfType<AssistantTurnStartEvent>()
            .Where(start => start.AgentId is null && start.Data.ParentToolCallId is null &&
                start.Data.TurnId == end.Data.TurnId)
            .ToArray();
        if (starts.Length != 1 ||
            starts[0].Data.Model is not null && starts[0].Data.Model != source.ModelId)
            throw Unavailable();

        var output = new StringBuilder();
        foreach (var assistant in events.Skip(userIndex + 1).Take(endIndex - userIndex - 1)
                     .OfType<AssistantMessageEvent>().Where(item => item.AgentId is null))
        {
            if (assistant.Data.Content is null ||
                output.Length + assistant.Data.Content.Length > AddressedMessageValidation.MaximumTextLength)
                throw Unavailable();
            output.Append(assistant.Data.Content);
        }
        if (!string.Equals(output.ToString(), response, StringComparison.Ordinal))
            throw new RuntimeAuthorizationException("runtime_native_turn_output_mismatch");

        RuntimeNativeAccountingCheckpoint? accounting = null;
        foreach (var checkpoint in events.Skip(endIndex + 1).OfType<SessionUsageCheckpointEvent>()
                     .Where(item => item.AgentId is null))
        {
            var data = checkpoint.Data;
            if (data.UsageAccountingWatermarks is not { Count: > 0 } watermarks ||
                watermarks.Count > 256 || watermarks.Any(item =>
                    string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 256 ||
                    item.Key.Any(char.IsControl) || item.Value < 0))
                throw new RuntimeAuthorizationException("runtime_native_accounting_checkpoint_invalid");
            accounting = new(checkpoint.Id,
                RuntimeCopilotSession.NullableDecimal(data.TotalNanoAiu)!.Value,
                watermarks.ToImmutableDictionary(StringComparer.Ordinal));
        }

        // A reported snapshot is an observed floor, not proof that no later usage exists.
        return new(source, nativeMessageId, end.Data.TurnId, starts[0].Id, end.Id,
            completion.Id, completion.Timestamp, durableCursor,
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(response)), accounting)
        {
            UsageEventIds = events.Skip(userIndex + 1).Take(receiptIndex - userIndex - 1)
                .OfType<AssistantUsageEvent>().Where(item => item.AgentId is null)
                .Select(item => item.Id).ToImmutableArray()
        };
    }

    private static int IndexOf(ImmutableArray<SessionEvent> events, Func<SessionEvent, bool> predicate)
    {
        for (var index = 0; index < events.Length; index++)
            if (predicate(events[index]))
                return index;
        return -1;
    }

    private static void ValidateCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 4096 || cursor.Any(char.IsControl))
            throw Unavailable();
    }

    private static RuntimeAuthorizationException Unavailable() =>
        new("runtime_native_turn_receipt_unavailable");
}
#pragma warning restore GHCP001
