using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Agentweaver.EventsAndSessions")]

namespace Agentweaver.Abstractions;

/// <summary>Schema and event versions owned by the Sessions journal contract.</summary>
public static class SessionsContractVersions
{
    public const int CurrentSchemaVersion = 1;
    public const int InitialEventVersion = 1;
    public const int CurrentEventVersion = 2;
    public const int PolicyEvaluationEventVersion = 2;
}

/// <summary>
/// Names the event-journal features a Sessions provider can advertise and a binding can negotiate.
/// Every Sessions provider uses these identifiers, so a run requirement means the same thing for each one.
/// </summary>
public static class SessionsCapabilities
{
    public const string Append = "sessions.events.append";
    public const string Replay = "sessions.events.replay";
    public const string Subscribe = "sessions.events.subscribe";
    public const string ObjectReferences = "sessions.objects.reference";
    public const string ToolCalls = "sessions.tool_calls";
    public const string PolicyEvaluations = "sessions.policy.evaluations";
    public const string AcceptedDecisions = "sessions.decisions.accepted";
    public const string AcceptedEffects = "sessions.effects.accepted";
    public const string Fork = "sessions.events.fork";

    /// <summary>Every capability the native PostgreSQL journal provides.</summary>
    public static ImmutableHashSet<string> All { get; } = ImmutableHashSet.Create(
        StringComparer.Ordinal, Append, Replay, Subscribe, ObjectReferences,
        ToolCalls, PolicyEvaluations, AcceptedDecisions, AcceptedEffects, Fork);
}

/// <summary>Discriminator for the typed payload stored in a session journal event.</summary>
public enum SessionEventKind
{
    Turn,
    ToolCall,
    PolicyEvaluation,
    DecisionAccepted,
    EffectAccepted,
    ArtifactReference,
    CacheReference,
    AddressedMessage,
    ProducedRunCapture,
    RuntimeConfiguration
}

/// <summary>Outcome of a policy decision captured as session evidence.</summary>
public enum PolicyEvaluationOutcome
{
    Allow,
    Deny,
    Error
}

/// <summary>Reason explaining an allow, deny, or error outcome in policy evidence.</summary>
public enum PolicyEvaluationReasonCode
{
    Allowed,
    DefaultDeny,
    NoEffectiveGrant,
    PlatformRuleDenied,
    ProjectRuleNarrowed,
    StaleFence,
    ProviderUnavailable,
    EvaluationFailed
}

/// <summary>Project/run/session identity used by the Events &amp; Sessions owner.</summary>
public readonly record struct SessionIdentity
{
    /// <summary>Project that owns the session.</summary>
    public string ProjectId { get; }
    /// <summary>Run that contains the session.</summary>
    public string RunId { get; }
    /// <summary>Stable session identity within the run.</summary>
    public string SessionId { get; }

    /// <summary>Creates an identity after validating each bounded ASCII identifier.</summary>
    /// <exception cref="ArgumentException">Any identity component is empty, too long, or contains unsupported characters.</exception>
    [JsonConstructor]
    public SessionIdentity(string projectId, string runId, string sessionId)
    {
        ProjectId = ValidateIdentity(projectId, nameof(projectId));
        RunId = ValidateIdentity(runId, nameof(runId));
        SessionId = ValidateIdentity(sessionId, nameof(sessionId));
    }

    /// <summary>Checks one session identity component against the canonical identifier format.</summary>
    private static string ValidateIdentity(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("A valid session identity is required.", name);
        return value;
    }
}

/// <summary>Reference to an object stored outside the journal, optionally bound to private runtime material.</summary>
public sealed record SessionObjectReference(
    [property: JsonConverter(typeof(SessionObjectKeyJsonConverter))] ObjectKey Key,
    string Purpose,
    long? ByteLength = null)
{
    /// <summary>Binding used for private typed material; absent for ordinary artifact references.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SessionMaterialBinding? Material { get; init; }
}

/// <summary>Serializes an opaque object key as one JSON string and validates it on read.</summary>
public sealed class SessionObjectKeyJsonConverter : JsonConverter<ObjectKey>
{
    /// <summary>Reads a valid object-key string, translating key validation errors into JSON errors.</summary>
    /// <exception cref="JsonException">The JSON value is not a valid opaque object key.</exception>
    public override ObjectKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || reader.GetString() is not { } value)
            throw new JsonException("An opaque object key string is required.");
        try
        {
            return new ObjectKey(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The opaque object key is invalid.", exception);
        }
    }

    /// <summary>Writes only the opaque object-key value.</summary>
    public override void Write(Utf8JsonWriter writer, ObjectKey value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>Retention owner and minimum retention time for a stored event object.</summary>
public sealed record SessionObjectRetention(string OwnerId, DateTimeOffset RetainUntil);

/// <summary>Object reference together with the owner-controlled retention metadata.</summary>
public sealed record StoredSessionObjectReference(
    SessionObjectReference Reference,
    SessionObjectRetention Retention);

/// <summary>Typed base for all JSON-polymorphic event payloads accepted by the journal.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TurnSessionPayload), "turn")]
[JsonDerivedType(typeof(ToolCallSessionPayload), "tool_call")]
[JsonDerivedType(typeof(PolicyEvaluationSessionPayload), "policy_evaluation")]
[JsonDerivedType(typeof(AcceptedDecisionSessionPayload), "decision_accepted")]
[JsonDerivedType(typeof(AcceptedEffectSessionPayload), "effect_accepted")]
[JsonDerivedType(typeof(ArtifactReferenceSessionPayload), "artifact_reference")]
[JsonDerivedType(typeof(CacheReferenceSessionPayload), "cache_reference")]
[JsonDerivedType(typeof(AddressedMessageSessionPayload), "addressed_message")]
[JsonDerivedType(typeof(ProducedRunCaptureSessionPayload), "produced_run_capture")]
[JsonDerivedType(typeof(RuntimeConfigurationSessionPayload), "runtime_configuration")]
public abstract record SessionEventPayload;

/// <summary>Turn message whose potentially large content is stored by object reference.</summary>
public sealed record TurnSessionPayload(string Role, SessionObjectReference Content) : SessionEventPayload;

/// <summary>Tool-call lifecycle record with optional references to its arguments and result.</summary>
public sealed record ToolCallSessionPayload(
    string CallId,
    string ToolName,
    string State,
    SessionObjectReference? Arguments,
    SessionObjectReference? Result) : SessionEventPayload;

/// <summary>Policy evaluation result and provider/grant revisions captured for audit.</summary>
public sealed record PolicyEvaluationSessionPayload(
    string ActorId,
    string TenantId,
    string StepId,
    string GrantId,
    string GrantRevision,
    string Purpose,
    string ActionId,
    PolicyEvaluationOutcome Outcome,
    PolicyEvaluationReasonCode ReasonCode,
    long Fence,
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision) : SessionEventPayload;

/// <summary>Accepted coordinator decision, including optional rationale and effect IDs.</summary>
public sealed record AcceptedDecisionSessionPayload(
    string DecisionId,
    string DecisionType,
    string SelectedOption,
    SessionObjectReference? Rationale,
    ImmutableArray<string> AcceptedEffectIds) : SessionEventPayload;

/// <summary>Accepted effect identity and optional stored receipt.</summary>
public sealed record AcceptedEffectSessionPayload(
    string EffectId,
    string EffectType,
    SessionObjectReference? Receipt) : SessionEventPayload;

/// <summary>Reference to an artifact associated with a session event.</summary>
public sealed record ArtifactReferenceSessionPayload(SessionObjectReference Artifact) : SessionEventPayload;

/// <summary>Reference to an SDK cache together with its runtime version and binding identity.</summary>
public sealed record CacheReferenceSessionPayload(
    SessionObjectReference Cache,
    string RuntimeVersion,
    string BindingId) : SessionEventPayload;

/// <summary>Optional reference to the accepted runtime configuration for a session.</summary>
public sealed record RuntimeConfigurationSessionPayload(SessionObjectReference? Configuration) : SessionEventPayload;

/// <summary>Journal link to a routed message, including its thread order and purpose.</summary>
public sealed record AddressedMessageSessionPayload(
    Guid MessageId,
    SessionIdentity Sender,
    SessionIdentity Recipient,
    Guid ThreadId,
    long ThreadSequence,
    AddressedMessagePurpose Purpose) : SessionEventPayload;

/// <summary>Caller-supplied append command with stable event ID and supported schema versions.</summary>
public sealed record AppendSessionEvent(
    Guid EventId,
    int SchemaVersion,
    int EventVersion,
    SessionEventPayload Payload);

/// <summary>
/// Owner-produced journal event with assigned position, timestamp, identity, typed payload,
/// and stored references. Callers cannot choose the persisted position or owner metadata.
/// </summary>
public sealed class SessionEventEnvelope
{
    /// <summary>Creates the envelope from validated owner-assigned journal fields.</summary>
    [JsonConstructor]
    internal SessionEventEnvelope(
        int schemaVersion,
        int eventVersion,
        Guid eventId,
        SessionIdentity identity,
        long position,
        DateTimeOffset occurredAt,
        SessionEventPayload payload,
        ImmutableArray<StoredSessionObjectReference> objectReferences)
    {
        SchemaVersion = schemaVersion;
        EventVersion = eventVersion;
        EventId = eventId;
        Identity = identity;
        Position = position;
        OccurredAt = occurredAt;
        Payload = payload;
        ObjectReferences = objectReferences;
    }

    public int SchemaVersion { get; }
    public int EventVersion { get; }
    public Guid EventId { get; }
    public SessionIdentity Identity { get; }
    public long Position { get; }
    public DateTimeOffset OccurredAt { get; }
    /// <summary>Type discriminator derived from the concrete payload type.</summary>
    public SessionEventKind Kind => SessionEventPayloadValidation.KindOf(Payload);
    public SessionEventPayload Payload { get; }
    public ImmutableArray<StoredSessionObjectReference> ObjectReferences { get; }
}

/// <summary>Session identity, creation time, and latest run event position returned by the owner.</summary>
public sealed record SessionRecord(SessionIdentity Identity, DateTimeOffset CreatedAt, long RunLastPosition);

/// <summary>
/// Immutable run-scoped Sessions provider binding. The resource generation and negotiated
/// capabilities pin which provider instance serves the journal for this run.
/// </summary>
public sealed class SessionProviderBinding
{
    /// <summary>Creates a validated provider binding for one project and run.</summary>
    /// <exception cref="ArgumentException">The provider, revisions, resource, generation, or capabilities are invalid.</exception>
    public SessionProviderBinding(
        string projectId,
        string runId,
        string providerId,
        Version adapterVersion,
        int optionsSchemaVersion,
        string optionsRevision,
        string resourceId,
        long resourceGeneration,
        ImmutableHashSet<string> negotiatedCapabilities)
    {
        var identity = new SessionIdentity(projectId, runId, "_");
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(adapterVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionsRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentNullException.ThrowIfNull(negotiatedCapabilities);
        if (providerId.Length > 256 || providerId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) ||
            optionsSchemaVersion < 1 || optionsRevision.Length > 128 ||
            optionsRevision.Any(char.IsControl) || resourceId.Length > 256 ||
            resourceId.Any(char.IsControl) || resourceGeneration < 1 ||
            negotiatedCapabilities.Any(capability =>
                string.IsNullOrWhiteSpace(capability) || capability.Length > 256 || capability.Any(char.IsControl)))
            throw new ArgumentException("The pinned Sessions provider binding is invalid.");

        ProjectId = identity.ProjectId;
        RunId = identity.RunId;
        ProviderId = providerId;
        AdapterVersion = adapterVersion;
        OptionsSchemaVersion = optionsSchemaVersion;
        OptionsRevision = optionsRevision;
        ResourceId = resourceId;
        ResourceGeneration = resourceGeneration;
        NegotiatedCapabilities = negotiatedCapabilities.ToImmutableHashSet(StringComparer.Ordinal);
    }

    public string ProjectId { get; }
    public string RunId { get; }
    public string ProviderId { get; }
    public Version AdapterVersion { get; }
    public int OptionsSchemaVersion { get; }
    public string OptionsRevision { get; }
    public string ResourceId { get; }
    public long ResourceGeneration { get; }
    public ImmutableHashSet<string> NegotiatedCapabilities { get; }

    /// <summary>Compares all provider and resource pins, including capability-set equality.</summary>
    public bool Matches(SessionProviderBinding other) =>
        other is not null &&
        ProjectId == other.ProjectId &&
        RunId == other.RunId &&
        ProviderId == other.ProviderId &&
        AdapterVersion == other.AdapterVersion &&
        OptionsSchemaVersion == other.OptionsSchemaVersion &&
        OptionsRevision == other.OptionsRevision &&
        ResourceId == other.ResourceId &&
        ResourceGeneration == other.ResourceGeneration &&
        NegotiatedCapabilities.SetEquals(other.NegotiatedCapabilities);
}

/// <summary>Page-read request for events in one session, optionally continuing from a cursor.</summary>
public sealed record SessionEventPageRequest(string SessionId, string? Cursor = null, int Limit = 100);

/// <summary>Page-read request spanning a run's sessions, optionally continuing from a cursor.</summary>
public sealed record SessionRunEventPageRequest(
    string ProjectId, string RunId, string? Cursor = null, int Limit = 100);

/// <summary>Bounded subscription request for event deliveries across a run.</summary>
public sealed record SessionRunSubscriptionRequest(
    string ProjectId,
    string RunId,
    string? Cursor = null,
    int MaximumEvents = 1000,
    int MaximumDurationSeconds = 300);

/// <summary>Read page of journal envelopes plus the cursor and indication of remaining data.</summary>
public sealed record SessionEventPage(
    ImmutableArray<SessionEventEnvelope> Events,
    string? NextCursor,
    bool HasMore);

/// <summary>One live or replayed event paired with the cursor required to acknowledge its position.</summary>
public sealed record SessionEventDelivery
{
    /// <summary>Creates a delivery only when both the event and continuation cursor are present.</summary>
    /// <exception cref="ArgumentNullException">The event is null.</exception>
    /// <exception cref="ArgumentException">The cursor is empty.</exception>
    public SessionEventDelivery(SessionEventEnvelope @event, string nextCursor)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentException.ThrowIfNullOrWhiteSpace(nextCursor);
        Event = @event;
        NextCursor = nextCursor;
    }

    /// <summary>Delivered journal event.</summary>
    public SessionEventEnvelope Event { get; }
    /// <summary>Opaque continuation cursor after this event.</summary>
    public string NextCursor { get; }
}

/// <summary>Append result with the owner-issued event and idempotent duplicate indicator.</summary>
public sealed record SessionAppendResult(SessionEventEnvelope Event, bool IsDuplicate);

/// <summary>Bounded subscription request for one session.</summary>
public sealed record SessionSubscriptionRequest(
    string SessionId,
    string? Cursor = null,
    int MaximumEvents = 1000,
    int MaximumDurationSeconds = 300);

/// <summary>Request to fork a session from one explicitly selected source event and cursor.</summary>
public sealed record SessionForkRequest(
    string TargetSessionId,
    Guid SourceEventId,
    string SourceCursor,
    string IdempotencyKey);

/// <summary>Owner-issued lineage proving the source event, schema, provider binding, and cursor of a fork.</summary>
public sealed record SessionForkLineage(
    SessionIdentity Source,
    Guid SourceEventId,
    long SourceSequence,
    int CursorVersion,
    int SourceSchemaVersion,
    int SourceEventVersion,
    string ProviderBindingHash,
    string SourceCursor);

/// <summary>Forked session and lineage, with a duplicate flag for retried commands.</summary>
public sealed record SessionForkResult(
    SessionRecord Target,
    SessionForkLineage Lineage,
    bool IsDuplicate);

/// <summary>Admission proof passed to the owner before it creates a fork from explicit event history.</summary>
public sealed record SessionForkAdmissionReceipt(
    Guid CommandId,
    SessionIdentity Source,
    string TargetSessionId,
    Guid SourceEventId,
    string SourceCursor,
    string IdempotencyKey,
    long ExecutionFence,
    string ActorIssuer,
    string ActorSubject,
    string AcceptedSelectionHash);

/// <summary>
/// Durable journal owner contract for session creation, event append/replay/subscription,
/// provider binding reads, and explicitly admitted session forks.
/// </summary>
public interface ISessionsJournal
{
    /// <summary>Creates a session for an authenticated principal and pins its run provider binding.</summary>
    Task<SessionRecord> CreateSessionAsync(
        ClaimsPrincipal principal, string sessionId, SessionProviderBinding binding,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the provider binding for a session the principal may access.</summary>
    Task<SessionProviderBinding> GetProviderBindingAsync(
        ClaimsPrincipal principal, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Reads the one pinned Sessions provider binding for a project run.</summary>
    Task<SessionProviderBinding> GetRunProviderBindingAsync(
        ClaimsPrincipal principal, string projectId, string runId, CancellationToken cancellationToken = default);

    /// <summary>Appends a typed event, returning the prior event when the event ID is a duplicate.</summary>
    Task<SessionAppendResult> AppendAsync(
        ClaimsPrincipal principal, string sessionId, AppendSessionEvent input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Forks from an explicit source event after the supplied admission check succeeds;
    /// the admission callback runs as part of the owner-controlled operation.
    /// </summary>
    Task<SessionForkResult> ForkFromExplicitEventAsync(
        ClaimsPrincipal principal, string sourceSessionId, SessionForkRequest request,
        Func<CancellationToken, Task> validateAdmission,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a bounded page of one session's journal after an optional cursor.</summary>
    Task<SessionEventPage> ReplayAsync(
        ClaimsPrincipal principal, SessionEventPageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a bounded page of journal events across the requested project run.</summary>
    Task<SessionEventPage> ReplayRunAsync(
        ClaimsPrincipal principal, SessionRunEventPageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Streams bounded event deliveries for one session from an optional cursor.</summary>
    IAsyncEnumerable<SessionEventDelivery> SubscribeAsync(
        ClaimsPrincipal principal, SessionSubscriptionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Streams bounded event deliveries across a run from an optional cursor.</summary>
    IAsyncEnumerable<SessionEventDelivery> SubscribeRunAsync(
        ClaimsPrincipal principal, SessionRunSubscriptionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Signals that an append conflicts with an existing event identity or content.</summary>
public sealed class SessionEventConflictException(string message) : Exception(message);

/// <summary>Signals that the selected session fork operation is unsupported.</summary>
public sealed class SessionForkUnsupportedException(string message) : Exception(message);

/// <summary>Signals that a requested provider binding differs from the run's immutable binding.</summary>
public sealed class SessionProviderBindingConflictException(string message) : Exception(message);

/// <summary>Signals that the run-pinned provider is not available to serve the session.</summary>
public sealed class SessionPinnedProviderUnavailableException(string message) : Exception(message);

/// <summary>Signals that the authenticated principal is not authorized for the session operation.</summary>
public sealed class SessionAccessDeniedException(string message) : Exception(message);

/// <summary>Signals that the requested session does not exist within the owner's scope.</summary>
public sealed class SessionNotFoundException(string message) : Exception(message);

/// <summary>Signals an unsupported session schema or event contract version.</summary>
public sealed class SessionContractVersionException(string message) : Exception(message);

/// <summary>Maps event payloads to kinds and validates version and object-reference rules.</summary>
public static class SessionEventPayloadValidation
{
    /// <summary>Returns the event kind for a supported payload type.</summary>
    /// <exception cref="ArgumentException">The payload type is not supported.</exception>
    public static SessionEventKind KindOf(SessionEventPayload payload) => payload switch
    {
        TurnSessionPayload => SessionEventKind.Turn,
        ToolCallSessionPayload => SessionEventKind.ToolCall,
        PolicyEvaluationSessionPayload => SessionEventKind.PolicyEvaluation,
        AcceptedDecisionSessionPayload => SessionEventKind.DecisionAccepted,
        AcceptedEffectSessionPayload => SessionEventKind.EffectAccepted,
        ArtifactReferenceSessionPayload => SessionEventKind.ArtifactReference,
        CacheReferenceSessionPayload => SessionEventKind.CacheReference,
        AddressedMessageSessionPayload => SessionEventKind.AddressedMessage,
        ProducedRunCaptureSessionPayload => SessionEventKind.ProducedRunCapture,
        RuntimeConfigurationSessionPayload => SessionEventKind.RuntimeConfiguration,
        _ => throw new ArgumentException("The session event kind is not supported.", nameof(payload))
    };

    /// <summary>Returns whether an event version can represent the supplied payload type.</summary>
    public static bool SupportsEventVersion(int eventVersion, SessionEventPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return eventVersion switch
        {
            SessionsContractVersions.InitialEventVersion =>
                payload is not (PolicyEvaluationSessionPayload or ProducedRunCaptureSessionPayload or
                    RuntimeConfigurationSessionPayload),
            SessionsContractVersions.CurrentEventVersion => true,
            _ => false
        };
    }

    /// <summary>Validates a payload and returns its unique, validated object references.</summary>
    /// <exception cref="ArgumentException">The payload or any referenced material violates the journal contract.</exception>
    public static ImmutableArray<SessionObjectReference> ValidateAndGetReferences(SessionEventPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        switch (payload)
        {
            case TurnSessionPayload turn:
                RequireChoice(turn.Role, "user", "assistant", "system", "tool");
                return [ValidateReference(turn.Content)];
            case ToolCallSessionPayload toolCall:
                RequireToken(toolCall.CallId, nameof(toolCall.CallId));
                RequireToken(toolCall.ToolName, nameof(toolCall.ToolName));
                RequireChoice(toolCall.State, "requested", "accepted", "completed", "failed");
                return References(toolCall.Arguments, toolCall.Result);
            case PolicyEvaluationSessionPayload policy:
                ValidatePolicyEvaluation(policy);
                return [];
            case AcceptedDecisionSessionPayload decision:
                RequireToken(decision.DecisionId, nameof(decision.DecisionId));
                RequireToken(decision.DecisionType, nameof(decision.DecisionType));
                RequireToken(decision.SelectedOption, nameof(decision.SelectedOption));
                if (decision.AcceptedEffectIds.IsDefault ||
                    decision.AcceptedEffectIds.Length > 100 ||
                    decision.AcceptedEffectIds.Any(value => !IsToken(value)))
                    throw new ArgumentException("Accepted effect identities are invalid.", nameof(payload));
                return References(decision.Rationale);
            case AcceptedEffectSessionPayload effect:
                RequireToken(effect.EffectId, nameof(effect.EffectId));
                RequireToken(effect.EffectType, nameof(effect.EffectType));
                return References(effect.Receipt);
            case ArtifactReferenceSessionPayload artifact:
                return [ValidateReference(artifact.Artifact)];
            case CacheReferenceSessionPayload cache:
                RequireToken(cache.RuntimeVersion, nameof(cache.RuntimeVersion));
                RequireToken(cache.BindingId, nameof(cache.BindingId));
                return [ValidateReference(cache.Cache)];
            case RuntimeConfigurationSessionPayload configuration:
                if (configuration.Configuration is not { } reference)
                    return [];
                SessionMaterialValidation.Validate(reference);
                if (reference.Material!.Kind != SessionMaterialKind.RuntimeConfiguration)
                    throw new ArgumentException("A private Runtime configuration reference is required.", nameof(payload));
                return [ValidateReference(reference)];
            case ProducedRunCaptureSessionPayload capture:
                ProducedRunCaptureContractValidation.Validate(capture);
                return [ValidateReference(capture.Package)];
            case AddressedMessageSessionPayload message:
                if (message.MessageId == Guid.Empty ||
                    message.Sender.ProjectId != message.Recipient.ProjectId ||
                    message.ThreadId == Guid.Empty ||
                    message.ThreadSequence < 1 ||
                    !Enum.IsDefined(message.Purpose))
                    throw new ArgumentException("The addressed-message journal reference is invalid.", nameof(payload));
                return [];
            default:
                throw new ArgumentException("The session event kind is not supported.", nameof(payload));
        }
    }

    /// <summary>Checks policy identity, revisions, provider pins, and valid outcome/reason combinations.</summary>
    private static void ValidatePolicyEvaluation(PolicyEvaluationSessionPayload policy)
    {
        if (        !IsOpaqueIdentifier(policy.ActorId) ||
        !IsOpaqueIdentifier(policy.TenantId) ||
        !IsToken(policy.StepId) ||
        !IsOpaqueIdentifier(policy.GrantId) ||
            !IsRevision(policy.GrantRevision) ||
            !IsToken(policy.Purpose) ||
            !IsToken(policy.ActionId) ||
            !Enum.IsDefined(policy.Outcome) ||
            !Enum.IsDefined(policy.ReasonCode) ||
            policy.Fence < 1 ||
            !IsToken(policy.ProviderId) ||
            !Version.TryParse(policy.AdapterVersion, out _) ||
            policy.OptionsSchemaVersion < 1 ||
            !IsRevision(policy.OptionsRevision) ||
            !IsValidOutcomeReason(policy.Outcome, policy.ReasonCode))
            throw new ArgumentException("Policy evaluation evidence is invalid.", nameof(policy));
    }

    /// <summary>Allows only reason codes that are consistent with the policy outcome.</summary>
    private static bool IsValidOutcomeReason(
        PolicyEvaluationOutcome outcome,
        PolicyEvaluationReasonCode reasonCode) =>
        (outcome, reasonCode) switch
        {
            (PolicyEvaluationOutcome.Allow, PolicyEvaluationReasonCode.Allowed) => true,
            (PolicyEvaluationOutcome.Deny,
                PolicyEvaluationReasonCode.DefaultDeny or
                PolicyEvaluationReasonCode.NoEffectiveGrant or
                PolicyEvaluationReasonCode.PlatformRuleDenied or
                PolicyEvaluationReasonCode.ProjectRuleNarrowed or
                PolicyEvaluationReasonCode.StaleFence) => true,
            (PolicyEvaluationOutcome.Error,
                PolicyEvaluationReasonCode.ProviderUnavailable or
                PolicyEvaluationReasonCode.EvaluationFailed) => true,
            _ => false
        };

    /// <summary>Validates optional object references and rejects repeated key/purpose pairs.</summary>
    private static ImmutableArray<SessionObjectReference> References(params SessionObjectReference?[] values)
    {
        var references = values.Where(value => value is not null)
            .Select(value => ValidateReference(value!))
            .ToImmutableArray();
        var identities = new HashSet<(string Key, string Purpose)>();
        if (references.Any(reference => !identities.Add((reference.Key.Value, reference.Purpose))))
            throw new ArgumentException("Object reference pairs must be unique within an event.", nameof(values));
        return references;
    }

    /// <summary>Checks one reference's opaque key, purpose, byte length, and optional material binding.</summary>
    private static SessionObjectReference ValidateReference(SessionObjectReference? reference)
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference.Purpose) ||
            reference.Purpose.Length > 64 || reference.Purpose.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')) ||
            reference.ByteLength is < 0)
            throw new ArgumentException("An opaque object reference is invalid.", nameof(reference));
        _ = new ObjectKey(reference.Key.Value);
        if (reference.Material is not null)
            SessionMaterialValidation.Validate(reference);
        return reference;
    }

    /// <summary>Requires a string to equal one of the event's supported values.</summary>
    private static void RequireChoice(string value, params string[] choices)
    {
        if (!choices.Contains(value, StringComparer.Ordinal))
            throw new ArgumentException("The session event value is invalid.", nameof(value));
    }

    /// <summary>Checks an event identifier against the supported token characters and length.</summary>
    private static void RequireToken(string value, string name)
    {
        if (!IsToken(value))
            throw new ArgumentException("A bounded session event identifier is required.", name);
    }

    /// <summary>Checks the restricted opaque actor/project identifier format.</summary>
    private static bool IsOpaqueIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    /// <summary>Checks the supported revision identifier format.</summary>
    private static bool IsRevision(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    /// <summary>Checks a bounded event token that may also contain path separators.</summary>
    private static bool IsToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':' or '/');
}
