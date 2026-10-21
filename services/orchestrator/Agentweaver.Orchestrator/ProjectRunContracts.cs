using System.Text.Json.Serialization;

namespace Agentweaver.Orchestrator;

/// <summary>Durable interactive run input, saved before the browser obtains its run-bound token.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateProjectRunRequest(
    string FirstMessage,
    long ExpectedProjectConfigRevision,
    string? Title = null,
    string? WorkflowId = null);

/// <summary>Selectors for the existing Broker authorization-code flow.</summary>
public sealed record ProjectRunBinding(string ProjectId, string RunId);

/// <summary>Stable identity returned after the Projects selection has been created.</summary>
public sealed record CreateProjectRunResponse(
    string RunId, string RootSessionId, string State, ProjectRunBinding Binding);

/// <summary>Identifies the durable run and root after first-message delivery.</summary>
public sealed record StartProjectRunResponse(string RunId, string RootSessionId, string State);

/// <summary>Optional explanation recorded with a cancellation, not an instruction to a runtime.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CancelProjectRunRequest(string? Reason = null);

/// <summary>Stable receipt; terminal no-ops do not advance the fence or invent a cancellation timestamp.</summary>
public sealed record CancelProjectRunResponse(
    string RunId, string State, DateTimeOffset? CancelledAt, bool AlreadyTerminal = false);
