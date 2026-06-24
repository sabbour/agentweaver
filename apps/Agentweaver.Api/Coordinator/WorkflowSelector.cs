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
    IReadOnlyList<WorkflowDefinition> AvailableWorkflows);

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

        string? response;
        try
        {
            var prompt = BuildPrompt(context);
            response = await _model.CompleteAsync(prompt, context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Workflow selection model call failed for project {ProjectId}; falling back to default '{WorkflowId}'.",
                context.ProjectId, fallback.Id);
            return new WorkflowSelectionResult(fallback,
                $"Defaulted to '{fallback.Name}' because workflow selection was unavailable.",
                WasAutoSelected: true);
        }

        if (!TryParse(response, out var selectedId, out var rationale))
        {
            _logger.LogWarning(
                "Workflow selection model returned no parseable choice for project {ProjectId}; falling back to default '{WorkflowId}'.",
                context.ProjectId, fallback.Id);
            return new WorkflowSelectionResult(fallback,
                $"Defaulted to '{fallback.Name}' because the model response could not be parsed.",
                WasAutoSelected: true);
        }

        var selected = available.FirstOrDefault(w => string.Equals(w.Id, selectedId, StringComparison.Ordinal));
        if (selected is null)
        {
            _logger.LogWarning(
                "Workflow selection model chose unknown workflow id '{SelectedId}' for project {ProjectId}; falling back to default '{WorkflowId}'.",
                selectedId, context.ProjectId, fallback.Id);
            return new WorkflowSelectionResult(fallback,
                $"Defaulted to '{fallback.Name}' because the model chose an unavailable workflow ('{selectedId}').",
                WasAutoSelected: true);
        }

        return new WorkflowSelectionResult(selected, rationale, WasAutoSelected: true);
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

        workflowId = match.Groups["id"].Value;
        return true;
    }

    /// <summary>The deterministic default: the built-in "default" workflow if present, else the first.</summary>
    private static WorkflowDefinition ResolveDefault(IReadOnlyList<WorkflowDefinition> available) =>
        available.FirstOrDefault(w => string.Equals(w.Id, BuiltInWorkflows.DefaultWorkflowId, StringComparison.Ordinal))
        ?? available[0];

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
            sb.Append("- ").Append(wf.Id).Append(": ").Append(wf.Name).Append(" — ").AppendLine(description);
        }
        sb.AppendLine();
        sb.AppendLine("Reply with JSON: {\"selected\": \"<workflow-id>\", \"rationale\": \"<1-2 sentences why>\"}");
        sb.Append("Select the workflow whose description best matches the task and team.");
        return sb.ToString();
    }

    /// <summary>Tolerant JSON extraction: pulls the first balanced object out of the model response.</summary>
    private static bool TryParse(string? response, out string selectedId, out string rationale)
    {
        selectedId = string.Empty;
        rationale = string.Empty;
        if (string.IsNullOrWhiteSpace(response)) return false;

        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start) return false;

        try
        {
            using var doc = JsonDocument.Parse(response[start..(end + 1)]);
            var root = doc.RootElement;
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
}
