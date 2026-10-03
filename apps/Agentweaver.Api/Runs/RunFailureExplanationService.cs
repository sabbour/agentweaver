using System.Text.Json;
using Agentweaver.Api.Execution;
using Agentweaver.Api.Infrastructure;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.AspNetCore;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

public sealed class RunFailureExplanationService(
    RunTerminalDiagnosticReader terminalReader,
    TerminalOutcomeProjector terminalProjector,
    IRunStore runStore,
    ExecutionIdentityReader identityReader,
    IRunEventStream eventStream,
    ILogger<RunFailureExplanationService> logger)
{
    public async Task<RunTerminalDiagnosticResponse?> GetAsync(Run run, CancellationToken ct)
    {
        var collectedAt = DateTimeOffset.UtcNow;
        var diagnostic = await terminalReader.GetAsync(run.Id.ToString(), ct).ConfigureAwait(false);
        if (diagnostic is null
            && string.Equals(run.AgentName, "Coordinator", StringComparison.Ordinal)
            && run.Status is RunStatus.Failed or RunStatus.MergeFailed)
        {
            var pending = (await runStore.GetUnprojectedTerminalOutcomesAsync(ct).ConfigureAwait(false))
                .SingleOrDefault(outcome => outcome.RunId == run.Id
                    && outcome.LifecycleGeneration == run.LifecycleGeneration);
            if (pending is not null)
                await terminalProjector.ProjectAsync(pending, ct).ConfigureAwait(false);
            // Another projector may have committed and marked this outcome between the first
            // diagnostic read and the pending lookup.
            diagnostic = await terminalReader.GetAsync(run.Id.ToString(), ct).ConfigureAwait(false);
        }
        if (diagnostic is null
            && string.Equals(run.AgentName, "Coordinator", StringComparison.Ordinal)
            && run.Status is RunStatus.Failed or RunStatus.MergeFailed)
        {
            diagnostic = RunTerminalDiagnosticReader.CreateFallback(run);
        }
        if (diagnostic is null)
            return null;

        IReadOnlyList<RunEvent> events = [];
        var eventSource = new DiagnosticEvidenceSource(
            "durable_run_events", "available", "complete", collectedAt);
        try
        {
            events = await eventStream.GetPersistedEventsAsync(run.Id.ToString(), 0, ct)
                .ConfigureAwait(false);
            events = events.OrderBy(evt => evt.Sequence).ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not collect durable diagnostic events for run {RunId}", run.Id);
            eventSource = new(
                "durable_run_events", "collection_error", "unavailable", collectedAt,
                "Durable event collection failed; the persisted terminal projection remains available.");
        }

        ExecutionIdentityProjection? identity = null;
        var identitySource = new DiagnosticEvidenceSource(
            "execution_identity", "available", "complete", collectedAt);
        try
        {
            identity = await identityReader.GetAsync(run, ct).ConfigureAwait(false);
            identitySource = new(
                "execution_identity",
                identity.EvidenceState == "missing_legacy_descriptor" ? "unsupported" : "available",
                identity.EvidenceState == "complete" ? "complete" : "partial",
                collectedAt,
                identity.EvidenceState == "complete" ? null : identity.EvidenceState);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not collect execution identity evidence for run {RunId}", run.Id);
            identitySource = new(
                "execution_identity", "collection_error", "unavailable", collectedAt,
                "Execution identity collection failed.");
        }

        var attemptBoundary = FindAttemptBoundary(events, run.LifecycleGeneration);
        var terminalEvent = FindTerminalEvent(events, diagnostic.Code, attemptBoundary);
        var references = new List<DiagnosticEvidenceReference>();
        var facts = new List<DiagnosticStatement>();
        var interpretations = new List<DiagnosticStatement>();
        var unknowns = new List<DiagnosticStatement>();

        string? terminalReferenceId = null;
        string? terminalToolCallId = null;
        if (terminalEvent is not null)
        {
            terminalReferenceId = $"event-{terminalEvent.Sequence}";
            terminalToolCallId = SafeIdentifier(
                GetString(ToJson(terminalEvent.Payload), "toolCallId")
                ?? GetString(ToJson(terminalEvent.Payload), "callId"));
            references.Add(new(
                terminalReferenceId,
                "durable_run_events",
                terminalEvent.Type,
                terminalEvent.Sequence,
                Timestamp(terminalEvent),
                terminalToolCallId));
            facts.Add(new(
                "terminal_failure_recorded",
                $"The durable run history recorded terminal code '{diagnostic.Code}'.",
                [terminalReferenceId]));
            interpretations.Add(new(
                "terminal_classification",
                $"Agentweaver's terminal classifier assigned this failure to '{diagnostic.Component}'.",
                [terminalReferenceId]));
        }
        else
        {
            unknowns.Add(new(
                "terminal_event_reference_unavailable",
                "The bounded terminal projection is available, but its durable event reference could not be collected.",
                []));
        }

        var attemptEvents = AttemptEvents(events, run.LifecycleGeneration, attemptBoundary, terminalEvent?.Sequence);
        AddToolErrorFacts(attemptEvents, references, facts, unknowns);

        DiagnosticDenialGate? denialGate = null;
        if (terminalToolCallId is not null && identity is not null)
        {
            var matchingDecision = identity.Decisions
                .Where(decision => decision.Sequence <= (terminalEvent?.Sequence ?? int.MaxValue))
                .LastOrDefault(decision =>
                    string.Equals(decision.ToolCallId, terminalToolCallId, StringComparison.Ordinal));
            if (matchingDecision is not null)
            {
                var referenceId = $"decision-{matchingDecision.Sequence}";
                references.Add(new(
                    referenceId,
                    "execution_identity",
                    "tool_decision",
                    matchingDecision.Sequence,
                    matchingDecision.TimestampUtc,
                    matchingDecision.ToolCallId,
                    matchingDecision.CorrelationState == "synthetic"));

                if (matchingDecision.Outcome == "denied")
                {
                    denialGate = new(
                        matchingDecision.Gate,
                        matchingDecision.Outcome,
                        matchingDecision.ReasonCode,
                        matchingDecision.ToolCallId,
                        matchingDecision.ToolName,
                        matchingDecision.Operation,
                        matchingDecision.PermissionBindingId,
                        matchingDecision.PermissionBindingVersion,
                        matchingDecision.PermissionBindingSource,
                        referenceId);
                    interpretations.Add(new(
                        "attributable_denial",
                        $"The terminal failure references a tool call denied by the '{matchingDecision.Gate}' gate.",
                        [terminalReferenceId!, referenceId]));
                }
                else if (matchingDecision.Outcome == "succeeded")
                {
                    unknowns.Add(new(
                        "conflicting_tool_evidence",
                        "The terminal failure references a tool call that also has a recorded success outcome.",
                        [terminalReferenceId!, referenceId]));
                }
            }
        }

        if (denialGate is null)
        {
            unknowns.Add(new(
                "root_cause_not_attributable",
                "No recorded gate or tool decision is directly referenced by the terminal failure, so nearby errors are not asserted as its root cause.",
                terminalReferenceId is null ? [] : [terminalReferenceId]));
        }

        if (attemptBoundary is null && run.LifecycleGeneration > 1)
        {
            unknowns.Add(new(
                "attempt_boundary_unavailable",
                $"Evidence for execution attempt {run.LifecycleGeneration} has no attributable sequence boundary.",
                []));
        }

        var sources = new[] { eventSource, identitySource };
        var completeness = sources.All(source => source.Completeness == "complete")
            && unknowns.All(item => item.Code != "attempt_boundary_unavailable")
                ? "complete"
                : "partial";

        return RunFailureDiagnosticSanitizer.Sanitize(diagnostic with
        {
            Attempt = run.LifecycleGeneration,
            ObservedAt = collectedAt,
            Completeness = completeness,
            EvidenceSources = sources,
            EvidenceReferences = references,
            ObservedFacts = facts,
            SupportedInterpretations = interpretations,
            Unknowns = unknowns,
            DenialGate = denialGate,
            NextActions = BuildNextActions(diagnostic, denialGate, unknowns),
            ExecutionDescriptorId = identity?.Descriptor?.DescriptorId,
            ExecutionIdentityEvidenceState = identity?.EvidenceState ?? "unavailable",
        });
    }

    private static IReadOnlyList<DiagnosticNextAction> BuildNextActions(
        RunTerminalDiagnosticResponse diagnostic,
        DiagnosticDenialGate? denialGate,
        IReadOnlyList<DiagnosticStatement> unknowns)
    {
        var actions = new List<DiagnosticNextAction>();
        if (denialGate is not null)
        {
            actions.Add(new(
                "authorization_or_configuration_repair",
                "Review the effective capability and the identified denial gate.",
                ["Confirm the requested operation is intended.", "Change authorization or configuration outside this diagnostic result."],
                "A later attempt can pass the same gate if the effective capability permits it."));
        }
        else if (diagnostic.Retryable == true)
        {
            actions.Add(new(
                "safe_retry",
                "Start a fresh retry after confirming the external dependency or transient condition is ready.",
                ["The run remains terminal.", "Retry is still offered by the run API."],
                "Creates a distinct execution attempt while preserving this evidence."));
        }

        if (unknowns.Count > 0)
        {
            actions.Add(new(
                "investigate_unknown",
                "Inspect the referenced durable events and execution identity without assuming a nearby error was causal.",
                ["Viewer access to the run is still authorized."],
                "Narrows the unknown evidence without mutating the run or its policy."));
        }
        return actions;
    }

    private static void AddToolErrorFacts(
        IReadOnlyList<RunEvent> events,
        List<DiagnosticEvidenceReference> references,
        List<DiagnosticStatement> facts,
        List<DiagnosticStatement> unknowns)
    {
        var outcomes = events
            .Where(evt => evt.Type is EventTypes.ToolResult or EventTypes.ToolError)
            .Select(evt => (Event: evt, Payload: ToJson(evt.Payload)))
            .Select(item => (
                item.Event,
                CallId: SafeIdentifier(GetString(item.Payload, "callId") ?? GetString(item.Payload, "requestId"))))
            .Where(item => item.CallId is not null)
            .ToArray();

        foreach (var failure in outcomes.Where(item => item.Event.Type == EventTypes.ToolError))
        {
            var recovered = outcomes.Any(item =>
                item.Event.Sequence > failure.Event.Sequence
                && item.Event.Type == EventTypes.ToolResult
                && string.Equals(item.CallId, failure.CallId, StringComparison.Ordinal));
            var referenceId = $"event-{failure.Event.Sequence}";
            references.Add(new(
                referenceId,
                "durable_run_events",
                EventTypes.ToolError,
                failure.Event.Sequence,
                Timestamp(failure.Event),
                failure.CallId));
            facts.Add(new(
                recovered ? "tool_error_recovered" : "tool_error_observed",
                recovered
                    ? "A tool error was followed by a success for the same call."
                    : "A tool error was recorded before the terminal failure.",
                [referenceId]));
            if (!recovered)
            {
                unknowns.Add(new(
                    "tool_error_causality_unknown",
                    "The tool error is not directly referenced by the terminal failure.",
                    [referenceId]));
            }
        }
    }

    private static IReadOnlyList<RunEvent> AttemptEvents(
        IReadOnlyList<RunEvent> events,
        int attempt,
        int? boundary,
        int? terminalSequence)
    {
        if (attempt > 1 && boundary is null)
            return [];
        return [.. events.Where(evt =>
            evt.Sequence >= (boundary ?? 0)
            && evt.Sequence <= (terminalSequence ?? int.MaxValue))];
    }

    private static int? FindAttemptBoundary(IReadOnlyList<RunEvent> events, int attempt)
    {
        foreach (var evt in events
                     .Where(evt => evt.Type == EventTypes.PermissionBindingBound)
                     .OrderBy(evt => evt.Sequence))
        {
            try
            {
                var binding = ToJson(evt.Payload).Deserialize<EffectivePermissionBinding>(
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (binding?.Attempt == attempt)
                    return evt.Sequence;
            }
            catch (JsonException)
            {
                // Malformed binding evidence cannot establish an attempt boundary.
            }
        }
        return attempt == 1 ? 0 : null;
    }

    private static RunEvent? FindTerminalEvent(
        IReadOnlyList<RunEvent> events,
        string code,
        int? boundary)
    {
        if (boundary is null)
            return null;

        foreach (var evt in events
                     .Where(evt => evt.Sequence >= boundary.Value)
                     .OrderByDescending(evt => evt.Sequence))
        {
            if (evt.Type == EventTypes.RunFailed
                && GetString(ToJson(evt.Payload), "errorCode") is { } rawCode
                && string.Equals(
                    StructuredRunFailureTerminal.NormalizeErrorCode(rawCode),
                    code,
                    StringComparison.Ordinal))
            {
                return evt;
            }

            if ((evt.Type == EventTypes.CoordinatorAssemblyBlocked && code == "assembly_blocked")
                || (evt.Type == EventTypes.CoordinatorAssemblyFailed && code == "assembly_failed"))
            {
                return evt;
            }
        }
        return null;
    }

    private static DateTimeOffset? Timestamp(RunEvent evt) =>
        evt.TimestampUtc == default ? null : evt.TimestampUtc;

    private static JsonElement ToJson(object payload) =>
        payload is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string? GetString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? SafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':')
            ? value
            : null;
}
