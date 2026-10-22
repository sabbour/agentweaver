using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentsessions.V1;

namespace Agentweaver.Providers.Sessions.AgentSessions;

/// <summary>One AgentSessions export and the Sessions session ID that serves it in a run.</summary>
/// <param name="SessionId">Session ID inside the bound project run.</param>
/// <param name="Document">Export document. The journal verifies it before it serves any event.</param>
public sealed record AgentSessionsJournalSession(string SessionId, AgentSessionsExportDocument Document);

/// <summary>Reports an export that failed record verification, with the verifier result.</summary>
public sealed class AgentSessionsVerificationException(string sessionId, AgentSessionsVerification verification)
    : FormatException($"AgentSessions export for session '{sessionId}' failed verification: {verification.Code}.")
{
    /// <summary>Session whose export failed.</summary>
    public string SessionId { get; } = sessionId;

    /// <summary>Verifier result, including the stable failure code and sequence.</summary>
    public AgentSessionsVerification Verification { get; } = verification;
}

/// <summary>
/// Read-only <see cref="ISessionsJournal"/> over verified AgentSessions record exports for one run.
/// </summary>
/// <remarks>
/// <para>
/// The journal verifies every export, and every fork against its parent when that parent is in the
/// same run, before it serves an event. Positions are run-wide, in the order the sessions are supplied.
/// </para>
/// <para>
/// Each record maps to one envelope. A message becomes a turn, a tool call or tool result becomes a
/// tool-call payload, and every other event kind becomes an artifact reference. Each payload refers to
/// the stored record by the object key <c>agentsessions/{uid}/{seq}</c>, so the full event stays in the
/// export rather than in the envelope. Create, append, and fork aren't supported, because an
/// AgentSessions log is appended only by its own store under compare-and-swap and fencing rules that the
/// Sessions contract doesn't express.
/// </para>
/// </remarks>
public sealed class AgentSessionsRecordJournal : ISessionsJournal
{
    /// <summary>Object-reference purpose for a stored AgentSessions record.</summary>
    public const string RecordPurpose = "agentsessions.record";

    private const string CursorPrefix = "agentsessions:";
    private const int MaxPageSize = 1000;
    private const string SubjectClaim = "sub";
    private const string ProjectIdClaim = "project_id";
    private const string RunIdClaim = "run_id";

    // SessionEventEnvelope has no public constructor. Its JSON form is the public wire contract, so the
    // journal builds envelopes from that form.
    private static readonly JsonSerializerOptions EnvelopeJson = new(JsonSerializerDefaults.Web);

    private readonly SessionProviderBinding _binding;
    private readonly ImmutableArray<SessionEventEnvelope> _events;
    private readonly ImmutableDictionary<string, SessionRecord> _sessions;

    private AgentSessionsRecordJournal(
        SessionProviderBinding binding,
        ImmutableArray<SessionEventEnvelope> events,
        ImmutableDictionary<string, SessionRecord> sessions) =>
        (_binding, _events, _sessions) = (binding, events, sessions);

    /// <summary>Verifies the exports and creates a journal for the bound run.</summary>
    /// <param name="binding">Run binding. It must name <see cref="AgentSessionsSessionsProvider.ProviderId"/>.</param>
    /// <param name="sessions">Exports in the order the run serves them. Session IDs must be unique.</param>
    /// <exception cref="ArgumentException">The binding or session list is invalid.</exception>
    /// <exception cref="AgentSessionsVerificationException">An export or a fork boundary failed verification.</exception>
    public static AgentSessionsRecordJournal Create(
        SessionProviderBinding binding, IReadOnlyList<AgentSessionsJournalSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(sessions);
        if (binding.ProviderId != AgentSessionsSessionsProvider.ProviderId)
            throw new ArgumentException("The binding doesn't name the AgentSessions provider.", nameof(binding));

        var byUid = new Dictionary<string, AgentSessionsExportDocument>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            ArgumentNullException.ThrowIfNull(session?.Document, nameof(sessions));
            var verification = AgentSessionsRecordVerifier.Verify(session.Document);
            if (!verification.Verified)
                throw new AgentSessionsVerificationException(session.SessionId, verification);
            // Session UIDs determine record object keys and event IDs, so each UID must identify one export.
            if (!byUid.TryAdd(session.Document.Session.Uid, session.Document))
                throw new ArgumentException("AgentSessions session UIDs must be unique.", nameof(sessions));
        }

        foreach (var session in sessions)
        {
            if (session.Document.Session.Parent is { } parent && byUid.TryGetValue(parent.Uid, out var parentDocument))
            {
                var fork = AgentSessionsRecordVerifier.VerifyFork(parentDocument, session.Document);
                if (!fork.Verified)
                    throw new AgentSessionsVerificationException(session.SessionId, fork);
            }
        }

        var events = ImmutableArray.CreateBuilder<SessionEventEnvelope>();
        var records = ImmutableDictionary.CreateBuilder<string, SessionRecord>(StringComparer.Ordinal);
        long position = 0;
        foreach (var session in sessions)
        {
            var identity = new SessionIdentity(binding.ProjectId, binding.RunId, session.SessionId);
            if (records.ContainsKey(identity.SessionId))
                throw new ArgumentException("Session IDs must be unique.", nameof(sessions));

            var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
            DateTimeOffset? createdAt = null;
            foreach (var record in session.Document.Records)
            {
                var ev = Event.Parser.ParseFrom(Convert.FromBase64String(record.Event));
                var occurredAt = ev.Ts?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch;
                createdAt ??= occurredAt;
                var reference = new SessionObjectReference(
                    new ObjectKey($"agentsessions/{Uri.EscapeDataString(session.Document.Session.Uid)}/{record.Seq}"),
                    RecordPurpose);
                events.Add(CreateEnvelope(
                    EventIdOf(session.Document.Session.Uid, record.ContentHash), identity, ++position, occurredAt,
                    MapPayload(ev, reference, toolNames), reference, binding.ResourceId));
            }

            records.Add(identity.SessionId, new SessionRecord(identity, createdAt ?? DateTimeOffset.UnixEpoch, 0));
        }

        var last = position;
        return new AgentSessionsRecordJournal(
            binding,
            events.ToImmutable(),
            records.ToImmutable().ToImmutableDictionary(
                pair => pair.Key, pair => pair.Value with { RunLastPosition = last }, StringComparer.Ordinal));
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. The journal is read-only.</exception>
    public Task<SessionRecord> CreateSessionAsync(
        ClaimsPrincipal principal, string sessionId, SessionProviderBinding binding,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The AgentSessions record journal is read-only.");

    /// <inheritdoc />
    public Task<SessionProviderBinding> GetProviderBindingAsync(
        ClaimsPrincipal principal, string sessionId, CancellationToken cancellationToken = default)
    {
        Authorize(principal);
        RequireSession(sessionId);
        return Task.FromResult(_binding);
    }

    /// <inheritdoc />
    public Task<SessionProviderBinding> GetRunProviderBindingAsync(
        ClaimsPrincipal principal, string projectId, string runId, CancellationToken cancellationToken = default)
    {
        Authorize(principal);
        if (projectId != _binding.ProjectId || runId != _binding.RunId)
            throw new SessionAccessDeniedException("The caller can't read this project run.");
        return Task.FromResult(_binding);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. The journal is read-only.</exception>
    public Task<SessionAppendResult> AppendAsync(
        ClaimsPrincipal principal, string sessionId, AppendSessionEvent input,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The AgentSessions record journal is read-only.");

    /// <inheritdoc />
    /// <exception cref="SessionForkUnsupportedException">Always. AgentSessions forks are made by the source store.</exception>
    public Task<SessionForkResult> ForkFromExplicitEventAsync(
        ClaimsPrincipal principal, string sourceSessionId, SessionForkRequest request,
        Func<CancellationToken, Task> validateAdmission,
        CancellationToken cancellationToken = default) =>
        throw new SessionForkUnsupportedException("The AgentSessions record journal doesn't create forks.");

    /// <inheritdoc />
    public Task<SessionEventPage> ReplayAsync(
        ClaimsPrincipal principal, SessionEventPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Authorize(principal);
        RequireSession(request.SessionId);
        return Task.FromResult(Page(
            envelope => envelope.Identity.SessionId == request.SessionId, request.Cursor, request.Limit));
    }

    /// <inheritdoc />
    public Task<SessionEventPage> ReplayRunAsync(
        ClaimsPrincipal principal, SessionRunEventPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Authorize(principal);
        if (request.ProjectId != _binding.ProjectId || request.RunId != _binding.RunId)
            throw new SessionAccessDeniedException("The caller can't read this project run.");
        return Task.FromResult(Page(_ => true, request.Cursor, request.Limit));
    }

    /// <inheritdoc />
    /// <remarks>An export is complete, so the stream ends after the stored events.</remarks>
    public async IAsyncEnumerable<SessionEventDelivery> SubscribeAsync(
        ClaimsPrincipal principal, SessionSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Authorize(principal);
        RequireSession(request.SessionId);
        foreach (var delivery in Deliver(
            envelope => envelope.Identity.SessionId == request.SessionId, request.Cursor, request.MaximumEvents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return delivery;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>An export is complete, so the stream ends after the stored events.</remarks>
    public async IAsyncEnumerable<SessionEventDelivery> SubscribeRunAsync(
        ClaimsPrincipal principal, SessionRunSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Authorize(principal);
        if (request.ProjectId != _binding.ProjectId || request.RunId != _binding.RunId)
            throw new SessionAccessDeniedException("The caller can't read this project run.");
        foreach (var delivery in Deliver(_ => true, request.Cursor, request.MaximumEvents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return delivery;
        }

        await Task.CompletedTask;
    }

    private SessionEventPage Page(Func<SessionEventEnvelope, bool> filter, string? cursor, int limit)
    {
        if (limit is < 1 or > MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var after = ParseCursor(cursor);
        var matches = _events.Where(envelope => envelope.Position > after && filter(envelope)).Take(limit + 1).ToArray();
        var page = matches.Take(limit).ToImmutableArray();
        var next = page.IsEmpty ? cursor : FormatCursor(page[^1].Position);
        return new SessionEventPage(page, next, matches.Length > limit);
    }

    private IEnumerable<SessionEventDelivery> Deliver(Func<SessionEventEnvelope, bool> filter, string? cursor, int maximum)
    {
        if (maximum < 1)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        var after = ParseCursor(cursor);
        return _events.Where(envelope => envelope.Position > after && filter(envelope)).Take(maximum)
            .Select(envelope => new SessionEventDelivery(envelope, FormatCursor(envelope.Position)));
    }

    private void RequireSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_sessions.ContainsKey(sessionId))
            throw new SessionNotFoundException("The session doesn't exist in this run.");
    }

    // The native journal reads the same claims through a service-internal helper; see the provider README.
    private void Authorize(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var subjects = principal.FindAll(SubjectClaim).Take(2).ToArray();
        var projects = principal.FindAll(ProjectIdClaim).Take(2).ToArray();
        var runs = principal.FindAll(RunIdClaim).Take(2).ToArray();
        if (!principal.Identities.Any(identity => identity.IsAuthenticated) ||
            subjects.Length != 1 || !Guid.TryParseExact(subjects[0].Value, "D", out _) ||
            projects.Length != 1 || runs.Length != 1 ||
            projects[0].Value != _binding.ProjectId || runs[0].Value != _binding.RunId)
            throw new SessionAccessDeniedException("The caller has no access to this project run.");
    }

    private static long ParseCursor(string? cursor)
    {
        if (cursor is null)
            return 0;
        if (!cursor.StartsWith(CursorPrefix, StringComparison.Ordinal) ||
            !long.TryParse(cursor.AsSpan(CursorPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var position))
            throw new ArgumentException("The cursor is invalid.", nameof(cursor));
        return position;
    }

    private static string FormatCursor(long position) =>
        CursorPrefix + position.ToString(CultureInfo.InvariantCulture);

    // A fork child repeats its parent's records, so the ID covers the session uid as well as the hash.
    private static Guid EventIdOf(string uid, string contentHash) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(uid + "\n" + contentHash)).AsSpan(0, 16));

    private static SessionEventPayload MapPayload(
        Event ev, SessionObjectReference reference, Dictionary<string, string> toolNames)
    {
        switch (ev.BodyCase)
        {
            case Event.BodyOneofCase.Message:
                return new TurnSessionPayload(
                    string.IsNullOrEmpty(ev.Message.Role) ? "user" : ev.Message.Role, reference);
            case Event.BodyOneofCase.Tool:
                toolNames[ev.Tool.Id] = ev.Tool.Tool;
                return new ToolCallSessionPayload(ev.Tool.Id, ev.Tool.Tool, "requested", reference, null);
            case Event.BodyOneofCase.Result:
                return new ToolCallSessionPayload(
                    ev.Result.Id,
                    toolNames.GetValueOrDefault(ev.Result.Id, "unknown"),
                    ev.Result.IsError ? "failed" : "completed",
                    null,
                    reference);
            default:
                return new ArtifactReferenceSessionPayload(reference);
        }
    }

    private static SessionEventEnvelope CreateEnvelope(
        Guid eventId, SessionIdentity identity, long position, DateTimeOffset occurredAt,
        SessionEventPayload payload, SessionObjectReference reference, string retentionOwner)
    {
        var stored = new StoredSessionObjectReference(
            reference, new SessionObjectRetention(retentionOwner, DateTimeOffset.MaxValue));
        var node = new JsonObject
        {
            ["schemaVersion"] = SessionsContractVersions.CurrentSchemaVersion,
            ["eventVersion"] = SessionsContractVersions.CurrentEventVersion,
            ["eventId"] = eventId,
            ["identity"] = JsonSerializer.SerializeToNode(identity, EnvelopeJson),
            ["position"] = position,
            ["occurredAt"] = occurredAt,
            ["payload"] = JsonSerializer.SerializeToNode(payload, EnvelopeJson),
            ["objectReferences"] = JsonSerializer.SerializeToNode(new[] { stored }, EnvelopeJson),
        };
        return node.Deserialize<SessionEventEnvelope>(EnvelopeJson)
            ?? throw new InvalidOperationException("The Sessions envelope couldn't be created.");
    }
}
