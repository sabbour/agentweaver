using System.Text.RegularExpressions;
using Agentweaver.Api.Coordinator;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Deterministic, hermetic <see cref="IOutcomeSpecReplyClassifier"/> for the coordinator suite. It
/// replaces the production Copilot-backed classifier so the confirm/revise routing can be exercised
/// WITHOUT any model or network call.
///
/// <para>
/// By default it applies a simple keyword heuristic so ordinary affirmatives classify as
/// <see cref="OutcomeSpecReplyKind.Confirm"/> and anything carrying a change/clarification marker (or
/// no affirmation at all) classifies as <see cref="OutcomeSpecReplyKind.Revise"/> — enough to make
/// the wiring tests deterministic. Set <see cref="Override"/> to force a specific result (including
/// <see langword="null"/>) for a single test, e.g. to prove the steering service fails closed to
/// revise when the model is unavailable.
/// </para>
/// </summary>
public sealed class FakeOutcomeSpecReplyClassifier : IOutcomeSpecReplyClassifier
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex TokenSanitizer = new(@"[^a-z0-9']+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] Markers =
    [
        "actually", "also", "add", "adjust", "broaden", "but", "change", "clarify", "except",
        "expand", "however", "include", "instead", "narrow", "plus", "remove", "revise", "scope",
        "tweak", "update", "without",
    ];

    private static readonly string[] Affirmations =
    [
        "yes", "yep", "yeah", "yup", "ok", "okay", "sure", "absolutely", "please", "thanks",
        "proceed", "continue", "confirm", "confirmed", "approve", "approved", "correct", "perfect",
        "great", "good", "fine", "lgtm", "looks good", "sounds good", "go ahead", "ship it",
        "do it", "that works", "works for me",
    ];

    /// <summary>The last context the classifier was asked to judge (for assertions).</summary>
    public OutcomeSpecReplyClassificationContext? LastContext { get; private set; }

    /// <summary>Number of times the classifier was invoked.</summary>
    public int CallCount { get; private set; }

    /// <summary>When set, forces the result for every call (bypasses the default heuristic).</summary>
    public Func<OutcomeSpecReplyClassificationContext, OutcomeSpecReplyKind?>? Override { get; set; }

    public Task<OutcomeSpecReplyKind?> ClassifyAsync(
        OutcomeSpecReplyClassificationContext context, CancellationToken ct)
    {
        LastContext = context;
        CallCount++;

        if (Override is not null)
            return Task.FromResult(Override(context));

        return Task.FromResult<OutcomeSpecReplyKind?>(HeuristicClassify(context.Instruction));
    }

    private static OutcomeSpecReplyKind HeuristicClassify(string instruction)
    {
        var normalized = Whitespace.Replace(
            TokenSanitizer.Replace(instruction.Trim().ToLowerInvariant(), " "), " ").Trim();
        if (normalized.Length == 0)
            return OutcomeSpecReplyKind.Revise;

        if (Markers.Any(m => normalized.Contains(m, StringComparison.Ordinal)))
            return OutcomeSpecReplyKind.Revise;

        // Confirm only when the reply is composed entirely of affirmation phrases/words.
        var remaining = normalized;
        foreach (var phrase in Affirmations.Where(a => a.Contains(' ')).OrderByDescending(a => a.Length))
            remaining = remaining.Replace(phrase, " ");

        var tokens = remaining.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allAffirm = tokens.All(t => Affirmations.Contains(t, StringComparer.Ordinal));
        return allAffirm && normalized.Length > 0 ? OutcomeSpecReplyKind.Confirm : OutcomeSpecReplyKind.Revise;
    }
}
