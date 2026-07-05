using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Workflows;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Selects the most appropriate functional workflow for a task from a project's available set
/// (Feature 015 US5). When a project carries more than one workflow the coordinator grounds an LLM
/// call in the task/goal, the team composition, and each workflow's description, and picks the
/// best-fit one — surfacing the choice with a rationale and a conversational override hint. When a
/// project carries exactly one workflow selection is skipped silently (no model call) and that
/// workflow (the project default) is used.
/// </summary>
public interface IWorkflowSelector
{
    /// <summary>
    /// Selects the most appropriate workflow for a task from the project's available set.
    /// Returns the default if only one workflow is available (no LLM call).
    /// </summary>
    Task<WorkflowSelectionResult> SelectAsync(
        WorkflowSelectionContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Inputs the selector needs to choose a workflow: the task/goal, the team roles, and the project's
/// available workflow definitions. By convention the project default workflow is the FIRST entry of
/// <see cref="AvailableWorkflows"/>; it is the deterministic fall-back when the model is unavailable
/// or returns an unusable answer.
/// </summary>
public sealed record WorkflowSelectionContext(
    string ProjectId,
    string TaskDescription,
    IReadOnlyList<string> TeamRoles,
    IReadOnlyList<WorkflowDefinition> AvailableWorkflows,
    IReadOnlySet<string>? CustomWorkflowIds = null);

/// <summary>
/// The outcome of a selection: the chosen workflow, a 1–2 sentence rationale, and whether the model
/// actually picked it. <see cref="WasAutoSelected"/> is <c>false</c> only when a single workflow was
/// available (pure pass-through); it is <c>true</c> whenever the multi-workflow LLM path ran — even
/// when that path fell back to the default after a parse failure (the rationale explains the fallback).
/// </summary>
public sealed record WorkflowSelectionResult(
    WorkflowDefinition Selected,
    string Rationale,
    bool WasAutoSelected);

/// <summary>
/// The single LLM seam used by <see cref="WorkflowSelector"/>. Implementations run one completion
/// for the supplied prompt and return the raw model text (or <c>null</c> on any failure so the
/// selector can fall back deterministically). Kept narrow so the selection logic is unit-testable
/// with a fake model and no real Copilot dependency.
/// </summary>
public interface IWorkflowSelectionModel
{
    Task<string?> CompleteAsync(string prompt, WorkflowSelectionContext context, CancellationToken ct);
}

/// <inheritdoc cref="IWorkflowSelector"/>
public sealed class WorkflowSelector : IWorkflowSelector
{
    private static readonly Regex OverridePattern =
        new(@"^\s*use\s+(?<id>[A-Za-z0-9._-]+)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Matches reasoning blocks emitted by chain-of-thought / extended-thinking models before the
    /// actual answer. Stripping these prevents the balanced-brace scanner from latching onto any
    /// JSON-like text inside the reasoning and returning it instead of the intended answer object.
    /// </summary>
    private static readonly Regex ThinkBlockPattern =
        new(@"<think>[\s\S]*?</think>|<thinking>[\s\S]*?</thinking>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Total model attempts before falling back to the project default (1 initial + 1 retry).</summary>
    private const int MaxAttempts = 2;

    private readonly IWorkflowSelectionModel _model;
    private readonly ILogger<WorkflowSelector> _logger;

    public WorkflowSelector(IWorkflowSelectionModel model, ILogger<WorkflowSelector> logger)
    {
        _model = model;
        _logger = logger;
    }

    public async Task<WorkflowSelectionResult> SelectAsync(
        WorkflowSelectionContext context, CancellationToken ct = default)
    {
        var available = context.AvailableWorkflows;
        if (available is null || available.Count == 0)
            throw new ArgumentException(
                "Workflow selection requires at least one available workflow.", nameof(context));

        var fallback = ResolveDefault(available);

        // Single workflow: skip selection silently, no LLM call (FR/AC: only-one => no prompt).
        if (available.Count == 1)
            return new WorkflowSelectionResult(fallback, "Only one workflow is available.", WasAutoSelected: false);

        // Give the model up to MaxAttempts tries. On a parse failure or an unknown-id pick we re-prompt
        // ONCE with a stricter format instruction rather than silently defaulting — a transient
        // formatting slip (prose, code fences, a stray display name) should not decide the workflow.
        string? lastResponse = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            string? response;
            try
            {
                var prompt = attempt == 1 ? BuildPrompt(context) : BuildRetryPrompt(context);
                response = await _model.CompleteAsync(prompt, context, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Workflow selection model call failed for project {ProjectId} (attempt {Attempt}/{MaxAttempts}); falling back to default '{WorkflowId}'.",
                    context.ProjectId, attempt, MaxAttempts, fallback.Id);
                return new WorkflowSelectionResult(fallback,
                    $"Defaulted to '{fallback.Name}' because workflow selection was unavailable.",
                    WasAutoSelected: true);
            }

            lastResponse = response;

            if (!TryParse(response, out var selectedId, out var rationale))
            {
                // Last-resort: if exactly one candidate workflow id or normalized name appears
                // verbatim as a whole word in the response, use it rather than defaulting —
                // handles plain-prose answers such as "I recommend bug-fix for this task."
                // Strip think blocks first so an id mentioned only inside a rejected reasoning
                // block does not get falsely selected (mirrors TryParse's own stripping).
                var responseForLastResort = response is not null ? StripThinkBlocks(response) : null;
                if (TryLastResortMatch(responseForLastResort, available, out var lastResort))
                {
                    _logger.LogInformation(
                        "Workflow selection last-resort verbatim match for project {ProjectId} (attempt {Attempt}): '{WorkflowId}'.",
                        context.ProjectId, attempt, lastResort.Id);
                    return new WorkflowSelectionResult(
                        lastResort,
                        "Selected as the only workflow mentioned by name in the model response.",
                        WasAutoSelected: true);
                }

                _logger.LogWarning(
                    "Workflow selection model returned no parseable choice for project {ProjectId} (attempt {Attempt}/{MaxAttempts}). Raw response (truncated): {Response}",
                    context.ProjectId, attempt, MaxAttempts, Truncate(response));
                continue;
            }

            var selected = MatchWorkflow(available, selectedId);
            if (selected is not null)
                return new WorkflowSelectionResult(selected, rationale, WasAutoSelected: true);

            _logger.LogWarning(
                "Workflow selection model chose unknown workflow id '{SelectedId}' for project {ProjectId} (attempt {Attempt}/{MaxAttempts}).",
                selectedId, context.ProjectId, attempt, MaxAttempts);
        }

        // Structured fallback log: the parse_failure property makes silent-wrong-workflow
        // fallbacks queryable in AppInsights (customEvents/traces where parse_failure == true).
        _logger.LogWarning(
            "Workflow selection could not obtain a usable choice for project {ProjectId} after {MaxAttempts} attempts; falling back to default '{WorkflowId}'. parse_failure={parse_failure}. Last raw response (truncated): {Response}",
            context.ProjectId, MaxAttempts, fallback.Id, true, Truncate(lastResponse));
        return new WorkflowSelectionResult(fallback,
            $"Defaulted to '{fallback.Name}' because the model response could not be parsed after {MaxAttempts} attempts.",
            WasAutoSelected: true);
    }

    /// <summary>
    /// Recognizes the conversational override command <c>use {workflow-id}</c> in an incoming user
    /// message. The coordinator checks each message with this before routing to the normal task
    /// handler so an explicit user override always wins over the coordinator's pick.
    /// </summary>
    public static bool TryParseOverride(string? message, [NotNullWhen(true)] out string? workflowId)
    {
        workflowId = null;
        if (string.IsNullOrWhiteSpace(message)) return false;

        var match = OverridePattern.Match(message);
        if (!match.Success) return false;

        workflowId = match.Groups["id"].Value.Trim().ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// The deterministic default. Callers put the project default first, but the retired
    /// <c>code-review</c> workflow must never be chosen as a fallback if a stale definition still
    /// lingers in a project registry — a silent parse fallback into a review-only pipeline runs the
    /// wrong process. Prefer a general-purpose <c>default</c>/<c>standard</c> workflow, then the first
    /// non-code-review entry, and only as a last resort the first entry.
    /// </summary>
    private static WorkflowDefinition ResolveDefault(IReadOnlyList<WorkflowDefinition> available)
    {
        static bool IsCodeReview(WorkflowDefinition w) =>
            string.Equals(Normalize(w.Id), "code-review", StringComparison.Ordinal);

        var preferred = available.FirstOrDefault(w =>
            !IsCodeReview(w)
            && (string.Equals(Normalize(w.Id), "default", StringComparison.Ordinal)
                || string.Equals(Normalize(w.Id), "standard", StringComparison.Ordinal)));

        return preferred
            ?? available.FirstOrDefault(w => !IsCodeReview(w))
            ?? available[0];
    }

    /// <summary>
    /// Resolves a model-supplied choice to an available workflow. Matches on id first, then falls back
    /// to the display name — both compared under a lenient normalization (lower-case, and '_'/spaces
    /// folded to '-') so a model that answers "code_review", "Software Delivery", or "software delivery"
    /// still binds to the intended workflow instead of being treated as an unknown id.
    /// </summary>
    private static WorkflowDefinition? MatchWorkflow(IReadOnlyList<WorkflowDefinition> available, string selectedId)
    {
        var norm = Normalize(selectedId);
        if (norm.Length == 0) return null;

        return available.FirstOrDefault(w => string.Equals(Normalize(w.Id), norm, StringComparison.Ordinal))
            ?? available.FirstOrDefault(w => string.Equals(Normalize(w.Name), norm, StringComparison.Ordinal));
    }

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');

    private static string Truncate(string? value, int max = 500)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var collapsed = value.Trim();
        return collapsed.Length <= max ? collapsed : collapsed[..max] + "…";
    }

    private static string BuildPrompt(WorkflowSelectionContext context)
    {
        var roles = context.TeamRoles is { Count: > 0 }
            ? string.Join(", ", context.TeamRoles)
            : "(none)";

        var sb = new StringBuilder();
        sb.AppendLine("You are selecting the most appropriate workflow for a task.");
        sb.AppendLine();
        sb.Append("Task: ").AppendLine(context.TaskDescription);
        sb.Append("Team roles: ").AppendLine(roles);
        sb.AppendLine();
        sb.AppendLine("Available workflows:");
        foreach (var wf in context.AvailableWorkflows)
        {
            var description = string.IsNullOrWhiteSpace(wf.Description) ? "(no description)" : wf.Description!.Trim();
            var sourceLabel = context.CustomWorkflowIds?.Contains(wf.Id) == true
                ? "project/custom"
                : "built-in/library";
            sb.Append("- ").Append(wf.Id).Append(" [").Append(sourceLabel).Append("]: ")
                .Append(wf.Name).Append(" — ").AppendLine(description);
        }
        sb.AppendLine();
        sb.AppendLine("Selection rules:");
        sb.AppendLine("- Match on PROCESS FIT: what steps the workflow runs and what outputs it produces.");
        sb.AppendLine("- Do NOT select by name similarity or domain-word overlap. A closest-sounding built-in is a bad choice if its process does not fit.");
        sb.AppendLine("- Prefer project/custom workflows over generic built-in/library workflows when a custom workflow can perform the requested process.");
        sb.AppendLine("- If no workflow is a good process fit, select the first listed workflow (the project default) instead of guessing.");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a single JSON object — no markdown, no code fences, no prose, no backticks.");
        sb.AppendLine("Exact format (replace the placeholders):");
        sb.AppendLine("{\"selected\": \"<workflow-id>\", \"rationale\": \"<1-2 sentences why>\"}");
        sb.Append("The value of \"selected\" MUST be exactly one of the workflow ids listed above.");
        return sb.ToString();
    }

    /// <summary>
    /// A stricter re-prompt used after the model's first reply could not be parsed or picked an unknown
    /// id. Repeats the selection context and hammers the output contract (single JSON object, no prose,
    /// no code fences, id from the list) so a transient formatting slip is corrected before we fall back.
    /// </summary>
    private static string BuildRetryPrompt(WorkflowSelectionContext context)
    {
        var sb = new StringBuilder(BuildPrompt(context));
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("IMPORTANT: your previous reply could not be parsed.");
        sb.AppendLine("Reply with ONLY a single JSON object and nothing else — no prose, no explanation, no markdown code fences, no backticks.");
        sb.AppendLine("The value of \"selected\" MUST be exactly one of the workflow ids listed above.");
        sb.Append("Format: {\"selected\": \"<workflow-id>\", \"rationale\": \"<1-2 sentences why>\"}");
        return sb.ToString();
    }

    /// <summary>
    /// Tolerant extraction of the model's choice. Order of attempts, most-strict first:
    /// (1) strip reasoning blocks (<c>&lt;think&gt;…&lt;/think&gt;</c>) that some models emit before
    /// the answer, then strip markdown code fences and surrounding whitespace, then
    /// <see cref="JsonSerializer"/>/<see cref="JsonDocument"/>-parse the whole cleaned text — this
    /// accepts a well-formed object OR a bare top-level JSON string (e.g. <c>"bug-fix"</c>);
    /// (2) if that fails, pull the first balanced <c>{…}</c> object out of surrounding prose and parse
    /// that. Returns false only when nothing usable can be recovered, so the caller can try the
    /// last-resort verbatim-id scan or log the raw response and fall back.
    /// </summary>
    private static bool TryParse(string? response, out string selectedId, out string rationale)
    {
        selectedId = string.Empty;
        rationale = string.Empty;
        if (string.IsNullOrWhiteSpace(response)) return false;

        // Strip reasoning blocks before any other processing. Reasoning models (e.g., o3) emit
        // <think>…</think> ahead of the answer; the thinking content may contain {braces} that
        // would fool the balanced-object scanner and make it return an invalid fragment instead
        // of the intended JSON answer.
        var stripped = StripThinkBlocks(response);
        var cleaned = StripCodeFences(stripped);

        // (1) Fast path: the de-fenced text is itself valid JSON (object or bare string).
        if (TryParseJson(cleaned, out selectedId, out rationale)) return true;

        // (2) Tolerant path: recover the first balanced object embedded in prose and parse that.
        var json = ExtractFirstJsonObject(cleaned);
        if (json is not null && TryParseJson(json, out selectedId, out rationale)) return true;

        return false;
    }

    /// <summary>
    /// Parses a single JSON value into a workflow choice. Accepts a top-level JSON string (used
    /// directly as the id) or an object carrying a string <c>selected</c> (and optional <c>rationale</c>).
    /// </summary>
    private static bool TryParseJson(string json, out string selectedId, out string rationale)
    {
        selectedId = string.Empty;
        rationale = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // A bare top-level string is the selected id, e.g. "bug-fix".
            if (root.ValueKind == JsonValueKind.String)
            {
                var value = root.GetString();
                if (string.IsNullOrWhiteSpace(value)) return false;
                selectedId = value!.Trim();
                rationale = "Selected as the best fit for the task.";
                return true;
            }

            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!root.TryGetProperty("selected", out var selectedEl)
                || selectedEl.ValueKind != JsonValueKind.String)
                return false;

            var id = selectedEl.GetString();
            if (string.IsNullOrWhiteSpace(id)) return false;

            selectedId = id!.Trim();
            rationale = root.TryGetProperty("rationale", out var rationaleEl)
                       && rationaleEl.ValueKind == JsonValueKind.String
                       && !string.IsNullOrWhiteSpace(rationaleEl.GetString())
                ? rationaleEl.GetString()!.Trim()
                : "Selected as the best fit for the task.";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a fenced code block wrapper (```json … ``` / ``` … ```) and surrounding whitespace so the
    /// enclosed JSON parses directly, and trims any stray leading/trailing backticks. Leaves plain text
    /// untouched (the caller then falls back to balanced-object extraction).
    /// </summary>
    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();

        var fenced = Regex.Match(
            trimmed,
            "^```[A-Za-z0-9_-]*[ \\t]*\\r?\\n(?<body>.*?)\\r?\\n?```$",
            RegexOptions.Singleline);
        if (fenced.Success)
            return fenced.Groups["body"].Value.Trim();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
            trimmed = trimmed.TrimStart('`').Trim();
        if (trimmed.EndsWith("```", StringComparison.Ordinal))
            trimmed = trimmed.TrimEnd('`').Trim();

        return trimmed;
    }

    /// <summary>
    /// Strips reasoning blocks emitted by chain-of-thought models before the actual answer.
    /// Handles <c>&lt;think&gt;…&lt;/think&gt;</c> and <c>&lt;thinking&gt;…&lt;/thinking&gt;</c>.
    /// </summary>
    private static string StripThinkBlocks(string text) =>
        ThinkBlockPattern.Replace(text, string.Empty).Trim();

    /// <summary>
    /// Last-resort workflow identification when <see cref="TryParse"/> finds no JSON: if exactly one
    /// candidate workflow id (or its normalized display name) appears verbatim as a whole word in the
    /// response, select it. Zero matches or more than one match is ambiguous — the caller falls back
    /// to the retry / default path.
    /// </summary>
    private static bool TryLastResortMatch(
        string? response,
        IReadOnlyList<WorkflowDefinition> available,
        [NotNullWhen(true)] out WorkflowDefinition? match)
    {
        match = null;
        if (string.IsNullOrWhiteSpace(response) || available.Count == 0) return false;

        var lower = response.ToLowerInvariant();
        WorkflowDefinition? found = null;

        foreach (var wf in available)
        {
            var id = wf.Id.ToLowerInvariant();
            // Also check the underscore variant (models sometimes swap - and _ in identifiers).
            var idUnderscore = id.Replace('-', '_');
            // And the normalized display name (e.g., "Bug Fix" -> "bug-fix").
            var normName = Normalize(wf.Name);

            var seen = ContainsWholeWord(lower, id)
                || (idUnderscore != id && ContainsWholeWord(lower, idUnderscore))
                || (normName != id && ContainsWholeWord(lower, normName));

            if (!seen) continue;
            if (found is not null) return false;  // more than one candidate mentioned -> ambiguous
            found = wf;
        }

        if (found is null) return false;
        match = found;
        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="term"/> appears in <paramref name="text"/> as a whole word —
    /// i.e. not immediately adjacent to an identifier character (letter, digit, <c>-</c>, or <c>_</c>).
    /// Case sensitivity follows whatever normalization the caller applied.
    /// </summary>
    private static bool ContainsWholeWord(string text, string term)
    {
        if (string.IsNullOrEmpty(term)) return false;
        var idx = 0;
        while ((idx = text.IndexOf(term, idx, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = idx == 0 || !IsIdentifierChar(text[idx - 1]);
            var afterOk = idx + term.Length >= text.Length || !IsIdentifierChar(text[idx + term.Length]);
            if (beforeOk && afterOk) return true;
            idx++;
        }
        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '-' || c == '_';

    /// <summary>
    /// Returns the FIRST complete, balanced <c>{…}</c> object embedded in <paramref name="text"/> — string
    /// contents (and escaped quotes) are skipped so braces inside a value don't confuse the scan. This is
    /// more robust than a naive first-'{'/last-'}' slice: it tolerates markdown code fences, leading/trailing
    /// prose, and any trailing text after the object.
    /// </summary>
    private static string? ExtractFirstJsonObject(string text)
    {
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    if (depth == 0) start = i;
                    depth++;
                    break;
                case '}':
                    if (depth > 0)
                    {
                        depth--;
                        if (depth == 0 && start >= 0)
                            return text[start..(i + 1)];
                    }
                    break;
            }
        }

        return null;
    }
}
