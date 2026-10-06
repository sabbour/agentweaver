using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Agentweaver.EventsAndSessions")]

namespace Agentweaver.Abstractions;

public static class SessionsContractVersions
{
    public const int CurrentSchemaVersion = 1;
    public const int InitialEventVersion = 1;
    public const int CurrentEventVersion = 2;
    public const int PolicyEvaluationEventVersion = 2;
}

public enum SessionEventKind
{
    Turn,
    ToolCall,
    PolicyEvaluation,
    DecisionAccepted,
    EffectAccepted,
    ArtifactReference,
    CacheReference
}

public enum PolicyEvaluationOutcome
{
    Allow,
    Deny,
    Error
}

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

public readonly record struct SessionIdentity
{
    public string ProjectId { get; }
    public string RunId { get; }
    public string SessionId { get; }

    public SessionIdentity(string projectId, string runId, string sessionId)
    {
        ProjectId = ValidateIdentity(projectId, nameof(projectId));
        RunId = ValidateIdentity(runId, nameof(runId));
        SessionId = ValidateIdentity(sessionId, nameof(sessionId));
    }

    private static string ValidateIdentity(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("A valid session identity is required.", name);
        return value;
    }
}

public sealed record SessionObjectReference(
    [property: JsonConverter(typeof(SessionObjectKeyJsonConverter))] ObjectKey Key,
    string Purpose,
    long? ByteLength = null);

public sealed class SessionObjectKeyJsonConverter : JsonConverter<ObjectKey>
{
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

    public override void Write(Utf8JsonWriter writer, ObjectKey value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

public sealed record SessionObjectRetention(string OwnerId, DateTimeOffset RetainUntil);

public sealed record StoredSessionObjectReference(
    SessionObjectReference Reference,
    SessionObjectRetention Retention);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TurnSessionPayload), "turn")]
[JsonDerivedType(typeof(ToolCallSessionPayload), "tool_call")]
[JsonDerivedType(typeof(PolicyEvaluationSessionPayload), "policy_evaluation")]
[JsonDerivedType(typeof(AcceptedDecisionSessionPayload), "decision_accepted")]
[JsonDerivedType(typeof(AcceptedEffectSessionPayload), "effect_accepted")]
[JsonDerivedType(typeof(ArtifactReferenceSessionPayload), "artifact_reference")]
[JsonDerivedType(typeof(CacheReferenceSessionPayload), "cache_reference")]
public abstract record SessionEventPayload;

public sealed record TurnSessionPayload(string Role, SessionObjectReference Content) : SessionEventPayload;

public sealed record ToolCallSessionPayload(
    string CallId,
    string ToolName,
    string State,
    SessionObjectReference? Arguments,
    SessionObjectReference? Result) : SessionEventPayload;

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

public sealed record AcceptedDecisionSessionPayload(
    string DecisionId,
    string DecisionType,
    string SelectedOption,
    SessionObjectReference? Rationale,
    ImmutableArray<string> AcceptedEffectIds) : SessionEventPayload;

public sealed record AcceptedEffectSessionPayload(
    string EffectId,
    string EffectType,
    SessionObjectReference? Receipt) : SessionEventPayload;

public sealed record ArtifactReferenceSessionPayload(SessionObjectReference Artifact) : SessionEventPayload;

public sealed record CacheReferenceSessionPayload(
    SessionObjectReference Cache,
    string RuntimeVersion,
    string BindingId) : SessionEventPayload;

public sealed record AppendSessionEvent(
    Guid EventId,
    int SchemaVersion,
    int EventVersion,
    SessionEventPayload Payload);

public sealed class SessionEventEnvelope
{
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
    public SessionEventKind Kind => SessionEventPayloadValidation.KindOf(Payload);
    public SessionEventPayload Payload { get; }
    public ImmutableArray<StoredSessionObjectReference> ObjectReferences { get; }
}

public sealed record SessionRecord(SessionIdentity Identity, DateTimeOffset CreatedAt, long RunLastPosition);

public sealed class SessionProviderBinding
{
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

public sealed record SessionEventPageRequest(string SessionId, string? Cursor = null, int Limit = 100);

public sealed record SessionRunEventPageRequest(
    string ProjectId, string RunId, string? Cursor = null, int Limit = 100);

public sealed record SessionRunSubscriptionRequest(
    string ProjectId,
    string RunId,
    string? Cursor = null,
    int MaximumEvents = 1000,
    int MaximumDurationSeconds = 300);

public sealed record SessionEventPage(
    ImmutableArray<SessionEventEnvelope> Events,
    string? NextCursor,
    bool HasMore);

public sealed record SessionEventDelivery
{
    public SessionEventDelivery(SessionEventEnvelope @event, string nextCursor)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentException.ThrowIfNullOrWhiteSpace(nextCursor);
        Event = @event;
        NextCursor = nextCursor;
    }

    public SessionEventEnvelope Event { get; }
    public string NextCursor { get; }
}

public sealed record SessionAppendResult(SessionEventEnvelope Event, bool IsDuplicate);

public sealed record SessionSubscriptionRequest(
    string SessionId,
    string? Cursor = null,
    int MaximumEvents = 1000,
    int MaximumDurationSeconds = 300);

public interface ISessionsJournal
{
    Task<SessionRecord> CreateSessionAsync(
        ClaimsPrincipal principal, string sessionId, SessionProviderBinding binding,
        CancellationToken cancellationToken = default);

    Task<SessionProviderBinding> GetProviderBindingAsync(
        ClaimsPrincipal principal, string sessionId, CancellationToken cancellationToken = default);

    Task<SessionProviderBinding> GetRunProviderBindingAsync(
        ClaimsPrincipal principal, string projectId, string runId, CancellationToken cancellationToken = default);

    Task<SessionAppendResult> AppendAsync(
        ClaimsPrincipal principal, string sessionId, AppendSessionEvent input,
        CancellationToken cancellationToken = default);

    Task<SessionEventPage> ReplayAsync(
        ClaimsPrincipal principal, SessionEventPageRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionEventPage> ReplayRunAsync(
        ClaimsPrincipal principal, SessionRunEventPageRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<SessionEventDelivery> SubscribeAsync(
        ClaimsPrincipal principal, SessionSubscriptionRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<SessionEventDelivery> SubscribeRunAsync(
        ClaimsPrincipal principal, SessionRunSubscriptionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class SessionEventConflictException(string message) : Exception(message);

public sealed class SessionProviderBindingConflictException(string message) : Exception(message);

public sealed class SessionPinnedProviderUnavailableException(string message) : Exception(message);

public sealed class SessionAccessDeniedException(string message) : Exception(message);

public sealed class SessionNotFoundException(string message) : Exception(message);

public sealed class SessionContractVersionException(string message) : Exception(message);

public static class SessionEventPayloadValidation
{
    public static SessionEventKind KindOf(SessionEventPayload payload) => payload switch
    {
        TurnSessionPayload => SessionEventKind.Turn,
        ToolCallSessionPayload => SessionEventKind.ToolCall,
        PolicyEvaluationSessionPayload => SessionEventKind.PolicyEvaluation,
        AcceptedDecisionSessionPayload => SessionEventKind.DecisionAccepted,
        AcceptedEffectSessionPayload => SessionEventKind.EffectAccepted,
        ArtifactReferenceSessionPayload => SessionEventKind.ArtifactReference,
        CacheReferenceSessionPayload => SessionEventKind.CacheReference,
        _ => throw new ArgumentException("The session event kind is not supported.", nameof(payload))
    };

    public static bool SupportsEventVersion(int eventVersion, SessionEventPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return eventVersion switch
        {
            SessionsContractVersions.InitialEventVersion => payload is not PolicyEvaluationSessionPayload,
            SessionsContractVersions.CurrentEventVersion => true,
            _ => false
        };
    }

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
            default:
                throw new ArgumentException("The session event kind is not supported.", nameof(payload));
        }
    }

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

    private static SessionObjectReference ValidateReference(SessionObjectReference? reference)
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference.Purpose) ||
            reference.Purpose.Length > 64 || reference.Purpose.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')) ||
            reference.ByteLength is < 0)
            throw new ArgumentException("An opaque object reference is invalid.", nameof(reference));
        _ = new ObjectKey(reference.Key.Value);
        return reference;
    }

    private static void RequireChoice(string value, params string[] choices)
    {
        if (!choices.Contains(value, StringComparer.Ordinal))
            throw new ArgumentException("The session event value is invalid.", nameof(value));
    }

    private static void RequireToken(string value, string name)
    {
        if (!IsToken(value))
            throw new ArgumentException("A bounded session event identifier is required.", name);
    }

    private static bool IsOpaqueIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private static bool IsRevision(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private static bool IsToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':' or '/');
}
