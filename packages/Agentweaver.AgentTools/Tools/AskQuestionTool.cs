using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Agentweaver.Domain;

namespace Agentweaver.AgentTools.Tools;

/// <summary>
/// Blocking HITL tool that lets an agent bubble a clarifying question or permission request to
/// the operator (or, for a coordinator child, the coordinator watcher) instead of silently
/// guessing. On invoke it emits <see cref="EventTypes.AgentQuestionAsked"/> on the run stream,
/// suspends on the <see cref="IQuestionGate"/> until the question is answered (via
/// POST /api/runs/{id}/questions/{requestId}/answer) or the wait times out, then resumes and
/// returns the answer text to the model.
/// </summary>
internal sealed class AskQuestionTool : ISandboxTool
{
    public string Name => "ask_question";

    // Agents may legitimately block for a long time awaiting a human answer; keep this generous.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    private const string NoGateFallback =
        "No question channel is available in this context. Proceed using your best judgement.";

    private const string TimeoutFallback =
        "No answer was provided in time. Proceed using your best judgement and note the assumption you made.";

    public AIFunction CreateFunction(SandboxToolContext ctx) =>
        AIFunctionFactory.Create(
            async (
                [Description("The clarifying question or permission request to bubble up to the user before proceeding.")] string question,
                CancellationToken ct = default) =>
            {
                if (ctx.QuestionGate is null || string.IsNullOrEmpty(ctx.RunId))
                {
                    ctx.Logger.LogWarning(
                        "ask_question invoked with no question gate or run id — returning fallback. runId={RunId}",
                        ctx.RunId);
                    return (object?)NoGateFallback;
                }

                var requestId = Guid.NewGuid().ToString("N");

                ctx.Logger.LogInformation(
                    "ask_question bubbling — runId={RunId} requestId={RequestId} questionLength={Length}",
                    ctx.RunId, requestId, question.Length);

                ctx.EmitEvent?.Invoke(EventTypes.AgentQuestionAsked, new { requestId, question });

                var answer = await ctx.QuestionGate
                    .AskAsync(ctx.RunId, requestId, question, DefaultTimeout, ct)
                    .ConfigureAwait(false);

                var timedOut = answer is null;
                var resolved = answer ?? TimeoutFallback;

                ctx.EmitEvent?.Invoke(EventTypes.AgentQuestionAnswered, new { requestId, answer = resolved, timedOut });

                return (object?)resolved;
            },
            Name,
            "Ask the user a clarifying question or request permission, and wait for the answer before continuing. " +
            "Use this instead of guessing when you hit a material decision you cannot infer or an action that needs the user's permission.");
}
