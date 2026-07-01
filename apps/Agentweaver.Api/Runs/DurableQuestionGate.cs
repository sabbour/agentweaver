using System.Text.Json;
using Agentweaver.Api.Contracts;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

public sealed class DurableQuestionGate(DurableRunControlState state) : IQuestionGate
{
    private const string QuestionAsked = "question.asked";
    private const string QuestionAnswered = "question.answered";
    private const string QuestionsCleared = "question.cleared";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly DurableRunControlState _state = state;

    public async Task<string?> AskAsync(
        string runId,
        string requestId,
        string question,
        TimeSpan timeout,
        CancellationToken ct)
    {
        _state.Append(runId, QuestionAsked, new QuestionContext(requestId, question));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (LatestQuestion(runId, requestId) is null)
                return null;

            var answer = LatestAnswer(runId, requestId);
            if (answer is not null)
                return answer.Answer;

            try { await Task.Delay(PollInterval, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        if (LatestQuestion(runId, requestId) is not null && LatestAnswer(runId, requestId) is null)
            _state.Append(runId, QuestionAnswered, new QuestionAnswer(requestId, null));

        return null;
    }

    public bool Answer(string runId, string requestId, string answer)
    {
        if (LatestQuestion(runId, requestId) is null || LatestAnswer(runId, requestId) is not null)
            return false;

        _state.Append(runId, QuestionAnswered, new QuestionAnswer(requestId, answer));
        return true;
    }

    public void Clear(string runId) =>
        _state.Append(runId, QuestionsCleared, new { });

    private QuestionContext? LatestQuestion(string runId, string requestId) =>
        _state.Load(runId, QuestionAsked, QuestionsCleared)
            .TakeLastQuestionEventsAfterClear()
            .Where(e => e.EventType == QuestionAsked)
            .Select(e => JsonSerializer.Deserialize<QuestionContext>(e.PayloadJson, JsonDefaults.Options))
            .LastOrDefault(q => q?.RequestId == requestId);

    private QuestionAnswer? LatestAnswer(string runId, string requestId) =>
        _state.Load(runId, QuestionAnswered, QuestionsCleared)
            .TakeLastQuestionEventsAfterClear()
            .Where(e => e.EventType == QuestionAnswered)
            .Select(e => JsonSerializer.Deserialize<QuestionAnswer>(e.PayloadJson, JsonDefaults.Options))
            .LastOrDefault(a => a?.RequestId == requestId);

    private sealed record QuestionContext(string RequestId, string Question);
    private sealed record QuestionAnswer(string RequestId, string? Answer);
}

file static class DurableQuestionEventExtensions
{
    public static IEnumerable<RunEventRecord> TakeLastQuestionEventsAfterClear(this IReadOnlyList<RunEventRecord> events)
    {
        var lastClear = events.LastOrDefault(e => e.EventType == "question.cleared");
        return lastClear is null ? events : events.Where(e => e.Sequence > lastClear.Sequence);
    }
}
