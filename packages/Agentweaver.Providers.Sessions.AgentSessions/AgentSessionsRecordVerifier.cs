using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentsessions.V1;
using Google.Protobuf;

namespace Agentweaver.Providers.Sessions.AgentSessions;

/// <summary>Pinned AgentSessions source and contract identity for the P2 proof of concept.</summary>
public static class AgentSessionsPin
{
    /// <summary>Upstream repository that defines the record format.</summary>
    public const string SourceRepository = "https://github.com/agent-primitives/sessions";

    /// <summary>Exact upstream commit whose protobuf and canonical hash rules this verifier implements.</summary>
    public const string SourceCommit = "465e82175da7815db39ec14852deb3949ff82670";

    /// <summary>Draft contract version at <see cref="SourceCommit"/>.</summary>
    public const string ContractVersion = "0.2.0-draft";

    /// <summary>Export document format written by <c>tools/agent-primitives</c>.</summary>
    public const string ExportFormat = "agentweaver.agentsessions.export.v1";
}

/// <summary>One exported AgentSessions session. Only <see cref="Records"/> carry hashed data.</summary>
public sealed record AgentSessionsExportDocument(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("upstream")] AgentSessionsExportUpstream Upstream,
    [property: JsonPropertyName("session")] AgentSessionsExportSession Session,
    [property: JsonPropertyName("producer")] AgentSessionsExportProducer? Producer,
    [property: JsonPropertyName("source_head")] AgentSessionsExportHead SourceHead,
    [property: JsonPropertyName("records")] IReadOnlyList<AgentSessionsExportRecord> Records);

/// <summary>Pinned upstream identity recorded by the exporter.</summary>
public sealed record AgentSessionsExportUpstream(
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("contract")] string Contract);

/// <summary>Upstream session identity and fork lineage. Identity is provenance, not authorization.</summary>
public sealed record AgentSessionsExportSession(
    [property: JsonPropertyName("uid")] string Uid,
    [property: JsonPropertyName("parent")] AgentSessionsExportParent? Parent = null,
    [property: JsonPropertyName("origin")] AgentSessionsExportOrigin? Origin = null);

/// <summary>Parent session and completed boundary that a fork child copied.</summary>
public sealed record AgentSessionsExportParent(
    [property: JsonPropertyName("uid")] string Uid,
    [property: JsonPropertyName("fork_seq")] string ForkSeq);

/// <summary>Producer context in the shape of the public <c>agentsessions.v1.Origin</c> message.</summary>
public sealed record AgentSessionsExportOrigin(
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("subject")] string? Subject = null,
    [property: JsonPropertyName("uri")] string? Uri = null,
    [property: JsonPropertyName("attributes")] IReadOnlyDictionary<string, string>? Attributes = null);

/// <summary>Agentweaver native identifiers kept next to, not inside, the upstream records.</summary>
public sealed record AgentSessionsExportProducer(
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("references")] IReadOnlyDictionary<string, string>? References = null);

/// <summary>Source store head observed at export time. It is metadata, not an external anchor.</summary>
public sealed record AgentSessionsExportHead(
    [property: JsonPropertyName("seq")] string Seq,
    [property: JsonPropertyName("hash")] string Hash);

/// <summary>One stored LogRecord: decimal int64 strings and base64 protobuf <c>Event</c> bytes.</summary>
public sealed record AgentSessionsExportRecord(
    [property: JsonPropertyName("seq")] string Seq,
    [property: JsonPropertyName("prev_hash")] string PrevHash,
    [property: JsonPropertyName("content_hash")] string ContentHash,
    [property: JsonPropertyName("fence")] string Fence,
    [property: JsonPropertyName("event")] string Event);

/// <summary>Result of verifying one export. A failure names the first record that broke the chain.</summary>
/// <param name="Verified">True when every record, link, and lineage check passed.</param>
/// <param name="Code">Stable failure reason, or null on success.</param>
/// <param name="Message">Human-readable failure detail, or null on success.</param>
/// <param name="Seq">Sequence number of the failing record, when one applies.</param>
/// <param name="RecordCount">Number of records that verified before the result.</param>
/// <param name="HeadHash">Content hash of the last verified record.</param>
public sealed record AgentSessionsVerification(
    bool Verified, string? Code, string? Message, long? Seq, int RecordCount, string? HeadHash)
{
    internal static AgentSessionsVerification Fail(string code, string message, long? seq, int count) =>
        new(false, code, message, seq, count, null);
}

/// <summary>
/// Independent, non-Go verifier for AgentSessions record chains. It decodes the stored protobuf
/// <c>Event</c>, renders proto3 JSON with proto field names, enum names, and no unpopulated fields,
/// and hashes the RFC 8785 canonical form of <c>{"event":…,"prev_hash":…,"seq":"…"}</c>.
/// Because that projection omits unknown protobuf fields, stored event bytes that carry any field the
/// pinned schema does not define fail with <c>unknown_field</c> instead of verifying.
/// </summary>
/// <remarks>
/// The chain is unkeyed. Verification proves the records are internally consistent from sequence 1.
/// It does not authenticate the producer, and it cannot detect a fully rehashed rewrite or a truncated
/// tail unless the caller compares the head with an anchor it obtained separately.
/// </remarks>
public static class AgentSessionsRecordVerifier
{
    private const int MaxRecords = 100_000;
    private const int MaxEventBytes = 4 * 1024 * 1024;

    private static readonly JsonFormatter ProtoJson =
        new(JsonFormatter.Settings.Default.WithPreserveProtoFieldNames(true).WithFormatDefaultValues(false));

    private static readonly MessageParser<Event> DiscardUnknownParser = Event.Parser.WithDiscardUnknownFields(true);

    private static readonly JsonSerializerOptions DocumentOptions = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>Returns the exact canonical bytes that <c>content_hash</c> covers.</summary>
    public static byte[] CanonicalRecord(string prevHash, long seq, Event ev)
    {
        ArgumentNullException.ThrowIfNull(prevHash);
        ArgumentNullException.ThrowIfNull(ev);
        var eventJson = Encoding.UTF8.GetBytes(ProtoJson.Format(ev));
        var buffer = new ArrayBufferWriter<byte>();
        buffer.Write("{\"event\":"u8);
        buffer.Write(CanonicalJson.Canonicalize(eventJson));
        buffer.Write(",\"prev_hash\":"u8);
        CanonicalJson.WriteString(buffer, prevHash);
        buffer.Write(",\"seq\":"u8);
        CanonicalJson.WriteString(buffer, seq.ToString(CultureInfo.InvariantCulture));
        buffer.Write("}"u8);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Returns the lowercase hex SHA-256 <c>content_hash</c> of one record.</summary>
    public static string ComputeContentHash(string prevHash, long seq, Event ev) =>
        Convert.ToHexStringLower(SHA256.HashData(CanonicalRecord(prevHash, seq, ev)));

    /// <summary>Parses an export document strictly. Duplicate or unknown members are rejected.</summary>
    /// <exception cref="JsonException">The document is not a well-formed export.</exception>
    public static AgentSessionsExportDocument ParseExport(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<AgentSessionsExportDocument>(utf8Json, DocumentOptions)
            ?? throw new JsonException("Export document is null.");

    /// <summary>Parses and verifies one export document.</summary>
    public static AgentSessionsVerification Verify(ReadOnlySpan<byte> utf8Json)
    {
        AgentSessionsExportDocument document;
        try
        {
            document = ParseExport(utf8Json);
        }
        catch (JsonException exception)
        {
            return AgentSessionsVerification.Fail("invalid_document", exception.Message, null, 0);
        }
        return Verify(document);
    }

    /// <summary>Verifies every record from sequence 1 with an empty previous hash, plus fork lineage.</summary>
    public static AgentSessionsVerification Verify(AgentSessionsExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Format != AgentSessionsPin.ExportFormat)
            return AgentSessionsVerification.Fail("unsupported_format", $"Export format '{document.Format}' is not supported.", null, 0);
        if (document.Upstream.Commit != AgentSessionsPin.SourceCommit || document.Upstream.Contract != AgentSessionsPin.ContractVersion)
            return AgentSessionsVerification.Fail("unsupported_source",
                $"Records come from {document.Upstream.Commit} ({document.Upstream.Contract}); this verifier implements {AgentSessionsPin.SourceCommit} ({AgentSessionsPin.ContractVersion}).", null, 0);
        if (document.Records.Count == 0)
            return AgentSessionsVerification.Fail("empty", "Export has no records.", null, 0);
        if (document.Records.Count > MaxRecords)
            return AgentSessionsVerification.Fail("too_large", $"Export exceeds {MaxRecords} records.", null, 0);

        long? forkSeq = null;
        if (document.Session.Parent is { } parent)
        {
            if (!TryParseInt64(parent.ForkSeq, out var parsed) || parsed < 1)
                return AgentSessionsVerification.Fail("invalid_encoding", "Parent fork_seq is not a positive decimal int64.", null, 0);
            forkSeq = parsed;
        }

        var previous = string.Empty;
        for (var index = 0; index < document.Records.Count; index++)
        {
            var record = document.Records[index];
            var expectedSeq = index + 1L;
            if (!TryParseInt64(record.Seq, out var seq))
                return AgentSessionsVerification.Fail("invalid_encoding", "Record seq is not a canonical decimal int64.", expectedSeq, index);
            if (seq != expectedSeq)
                return AgentSessionsVerification.Fail("sequence", $"Expected seq {expectedSeq}, found {seq}; records are missing, duplicated, or out of order.", seq, index);
            if (!TryParseInt64(record.Fence, out _))
                return AgentSessionsVerification.Fail("invalid_encoding", "Record fence is not a canonical decimal int64.", seq, index);
            if (!IsHash(record.ContentHash) || (record.PrevHash.Length > 0 && !IsHash(record.PrevHash)))
                return AgentSessionsVerification.Fail("invalid_encoding", "Hashes must be 64 lowercase hex characters.", seq, index);
            if (record.PrevHash != previous)
                return AgentSessionsVerification.Fail("link", $"prev_hash does not match the content_hash of seq {seq - 1}.", seq, index);
            if (!TryDecodeEvent(record.Event, out var ev, out var decodeCode, out var decodeError))
                return AgentSessionsVerification.Fail(decodeCode, decodeError, seq, index);
            if (ev.Kind == EventKind.EventParked)
                return AgentSessionsVerification.Fail("transport_event", "EVENT_PARKED is transport-only and is never journaled.", seq, index);
            string computed;
            try
            {
                computed = ComputeContentHash(record.PrevHash, seq, ev);
            }
            catch (Exception exception) when (exception is CanonicalJsonException or InvalidOperationException)
            {
                return AgentSessionsVerification.Fail("invalid_encoding", exception.Message, seq, index);
            }
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(record.ContentHash)))
                return AgentSessionsVerification.Fail("content_hash", $"Stored content_hash {record.ContentHash} does not match recomputed {computed}.", seq, index);
            if (forkSeq is { } boundary && seq == boundary + 1 &&
                (ev.Kind != EventKind.EventLifecycle || ev.Lifecycle?.Kind != Lifecycle.Types.Kind.LifecycleFork ||
                 ev.Lifecycle.Detail != $"parent@{boundary.ToString(CultureInfo.InvariantCulture)}"))
                return AgentSessionsVerification.Fail("fork_marker", $"Seq {seq} is not the FORK marker for parent@{boundary}.", seq, index);
            previous = record.ContentHash;
        }
        if (forkSeq is { } fork && fork >= document.Records.Count)
            return AgentSessionsVerification.Fail("fork_marker", "Child export ends before its FORK marker.", null, document.Records.Count);
        if (document.SourceHead.Seq != document.Records[^1].Seq || document.SourceHead.Hash != previous)
            return AgentSessionsVerification.Fail("head_mismatch", "source_head metadata does not match the last exported record.", null, document.Records.Count);
        return new(true, null, null, null, document.Records.Count, previous);
    }

    /// <summary>
    /// Verifies both chains and proves the child copied the parent's records 1 through the fork boundary
    /// byte for byte, followed by its FORK marker.
    /// </summary>
    public static AgentSessionsVerification VerifyFork(AgentSessionsExportDocument parent, AgentSessionsExportDocument child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        var parentResult = Verify(parent);
        if (!parentResult.Verified)
            return parentResult;
        var childResult = Verify(child);
        if (!childResult.Verified)
            return childResult;
        if (child.Session.Parent is not { } lineage || lineage.Uid != parent.Session.Uid)
            return AgentSessionsVerification.Fail("fork_lineage", "Child export does not name this parent session.", null, 0);
        var boundary = long.Parse(lineage.ForkSeq, CultureInfo.InvariantCulture);
        if (boundary > parent.Records.Count)
            return AgentSessionsVerification.Fail("fork_prefix", "Fork boundary is beyond the exported parent head.", boundary, 0);
        for (var index = 0; index < boundary; index++)
        {
            if (parent.Records[index] with { Fence = "0" } != child.Records[index] with { Fence = "0" })
                return AgentSessionsVerification.Fail("fork_prefix", "Child prefix differs from the parent record.", index + 1, index);
        }
        return childResult;
    }

    private static bool TryDecodeEvent(string base64, out Event ev, out string code, out string error)
    {
        ev = new Event();
        code = "invalid_encoding";
        error = string.Empty;
        var bytes = new byte[base64.Length];
        if (!Convert.TryFromBase64String(base64, bytes, out var written) || Convert.ToBase64String(bytes, 0, written) != base64)
        {
            error = "Event is not canonical padded base64.";
            return false;
        }
        if (written > MaxEventBytes)
        {
            error = $"Event exceeds {MaxEventBytes} bytes.";
            return false;
        }
        try
        {
            ev = Event.Parser.ParseFrom(bytes, 0, written);
            // Proto3 JSON omits unknown fields, so the content hash does not cover them. Compare with a parse
            // that drops unknown fields at every depth and reject any difference, so extra bytes cannot hide
            // behind a valid hash.
            var known = DiscardUnknownParser.ParseFrom(bytes, 0, written);
            if (known.CalculateSize() != ev.CalculateSize())
            {
                code = "unknown_field";
                error = "Event bytes contain fields that agentsessions.v1.Event does not define.";
                return false;
            }
            return true;
        }
        catch (InvalidProtocolBufferException exception)
        {
            error = $"Event bytes are not a valid agentsessions.v1.Event: {exception.Message}";
            return false;
        }
    }

    private static bool TryParseInt64(string? text, out long value)
    {
        value = 0;
        return !string.IsNullOrEmpty(text) &&
            long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) &&
            value.ToString(CultureInfo.InvariantCulture) == text;
    }

    private static bool IsHash(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
