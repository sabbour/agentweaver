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

/// <summary>Text submitted by an authenticated user to a running project coordinator.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectRunMessageRequest(string Text, string? TargetSessionId = null);

/// <summary>Identity of the authenticated user that submitted a run message.</summary>
public sealed record ProjectRunMessageSender(string Issuer, string Subject);

/// <summary>Stable Events admission receipt for a user message addressed within one run.</summary>
public sealed record ProjectRunMessageReceipt(
    Guid OwnerMessageId,
    Guid MessageId,
    Guid ThreadId,
    long ThreadSequence,
    string ProjectId,
    string RunId,
    string RootSessionId,
    string RecipientSessionId,
    string Status,
    ProjectRunMessageSender Sender);
