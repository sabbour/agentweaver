using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Knowledge;

internal static class KnowledgeTransferValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static void Validate(
        KnowledgeRecordTransferBundle bundle,
        string projectId,
        string agentId,
        string errorCode = "invalid_knowledge_transfer",
        int errorStatusCode = StatusCodes.Status400BadRequest,
        bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (!string.Equals(bundle.Format, KnowledgeRecordTransferContract.Format, StringComparison.Ordinal) ||
            bundle.SchemaVersion != KnowledgeRecordTransferContract.SchemaVersion)
            throw Invalid(errorCode, "The Knowledge transfer format or schema version is unsupported.",
                errorStatusCode);
        if (!string.Equals(bundle.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(bundle.AgentId, agentId, StringComparison.Ordinal))
            throw Invalid(errorCode, "Knowledge transfer project and agent scope must match the authorized route.",
                errorStatusCode);
        if (bundle.Records.IsDefault)
            throw Invalid(errorCode, "The Knowledge transfer record list is required.", errorStatusCode);
        if (bundle.Records.Length > KnowledgeRecordTransferContract.MaximumRecords)
            throw TooLarge("The Knowledge transfer record count exceeds the supported bound.");
        if (!allowEmpty && bundle.Records.IsEmpty)
            throw Invalid(errorCode, "The Knowledge transfer must include at least one record.", errorStatusCode);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(bundle, JsonOptions);
        if (bytes.Length > KnowledgeRecordTransferContract.MaximumBytes)
            throw TooLarge("The Knowledge transfer exceeds the supported size.");

        var revisionCount = 0;
        var records = new Dictionary<Guid, KnowledgeRecordTransferEntry>();
        var revisionIds = new HashSet<Guid>();
        foreach (var entry in bundle.Records)
        {
            if (entry is null || entry.Record is null || entry.Revisions.IsDefaultOrEmpty)
                throw Invalid(errorCode, "Every transferred record must include a complete revision chain.",
                    errorStatusCode);
            var record = entry.Record;
            if (record.RecordId == Guid.Empty || record.RevisionId == Guid.Empty ||
                !string.Equals(record.ProjectId, projectId, StringComparison.Ordinal) ||
                !string.Equals(record.AgentId, agentId, StringComparison.Ordinal) ||
                record.Kind is not (KnowledgeRecordKind.Memory or KnowledgeRecordKind.Decision) ||
                !Enum.IsDefined(record.State) || !Enum.IsDefined(record.TrustState) ||
                record.State is not (KnowledgeRecordState.Active or KnowledgeRecordState.Archived or
                    KnowledgeRecordState.Superseded) ||
                !IsValidPayload(
                    record.Type,
                    record.Title,
                    record.Content,
                    record.Rationale,
                    record.Importance,
                    record.Tags,
                    record.SourceRunId,
                    record.SourceSessionId) ||
                record.PromotedDecisionId is not null)
                throw Invalid(errorCode, "A transferred record has an unsupported kind, scope, or shape.",
                    errorStatusCode);
            if (!records.TryAdd(record.RecordId, entry))
                throw Invalid(errorCode, "A Knowledge transfer cannot contain duplicate record IDs.", errorStatusCode);

            revisionCount = checked(revisionCount + entry.Revisions.Length);
            if (revisionCount > KnowledgeRecordTransferContract.MaximumRevisions)
                throw TooLarge("The Knowledge transfer revision count exceeds the supported bound.");
            ValidateRevisionChain(entry, revisionIds, errorCode, errorStatusCode);
        }

        var linkedTargets = bundle.Records
            .SelectMany(entry => entry.Revisions
                .Where(revision => revision.SupersededByRecordId is not null)
                .Select(revision => revision.SupersededByRecordId!.Value))
            .Distinct()
            .ToArray();
        if (linkedTargets.Any(target => records.TryGetValue(target, out var linked) &&
                (linked.Record.Kind != KnowledgeRecordKind.Decision ||
                 !string.Equals(linked.Record.AgentId, agentId, StringComparison.Ordinal))))
            throw Invalid(errorCode, "A supersession target must be a Decision in the transferred agent scope.",
                errorStatusCode);
    }

    public static void ValidateRevisionChain(
        KnowledgeRecordTransferEntry entry,
        HashSet<Guid>? allRevisionIds = null,
        string errorCode = "invalid_knowledge_transfer",
        int errorStatusCode = StatusCodes.Status400BadRequest)
    {
        var record = entry.Record;
        var revisions = entry.Revisions;
        if (revisions.IsDefaultOrEmpty ||
            revisions.Length > KnowledgeRecordTransferContract.MaximumRevisions)
            throw Invalid(errorCode, "A Knowledge revision chain is incomplete or contains null entries.",
                errorStatusCode);
        if (revisions.Any(revision => revision is null))
            throw Invalid(errorCode, "A Knowledge revision chain contains a null entry.", errorStatusCode);
        if (
            record.Revision != revisions.Length ||
            record.RevisionId != revisions[^1].RevisionId ||
            record.PreviousRevisionId != revisions[^1].PreviousRevisionId)
            throw Invalid(errorCode, "A Knowledge revision chain is incomplete or does not match its record head.",
                errorStatusCode);

        Guid? previousRevisionId = null;
        for (var index = 0; index < revisions.Length; index++)
        {
            var revision = revisions[index];
            if (revision is null ||
                revision.RecordId != record.RecordId ||
                revision.Revision != index + 1 ||
                revision.RevisionId == Guid.Empty ||
                revision.PreviousRevisionId != previousRevisionId ||
                revision.Kind != record.Kind ||
                revision.Tags.IsDefault ||
                revision.Tags.Length > 32 ||
                !Enum.IsDefined(revision.State) ||
                !Enum.IsDefined(revision.TrustState) ||
                revision.State is not (KnowledgeRecordState.Active or KnowledgeRecordState.Archived or
                    KnowledgeRecordState.Superseded) ||
                (revision.ActorFingerprint is not null &&
                    (revision.ActorFingerprint.Length != 64 ||
                     revision.ActorFingerprint.Any(character => !Uri.IsHexDigit(character)))) ||
                revision.ChangeKind is not null && revision.ChangeKind is not (
                    "created" or "updated" or "proposal_created" or "proposal_rejected" or "proposal_promoted" or
                    "decision_archived" or "decision_approved" or "decision_restored" or "decision_superseded" or
                    "imported") ||
                revision.Reason?.Length > 1_024 ||
                revision.Reason?.Any(char.IsControl) == true ||
                !IsValidPayload(
                    revision.Type,
                    revision.Title,
                    revision.Content,
                    revision.Rationale,
                    revision.Importance,
                    revision.Tags,
                    revision.SourceRunId,
                    revision.SourceSessionId) ||
                (allRevisionIds is not null && !allRevisionIds.Add(revision.RevisionId)))
                throw Invalid(errorCode, "A Knowledge revision chain contains a broken link or duplicate identity.",
                    errorStatusCode);
            ValidateSupersessionState(
                revision.Kind, revision.State, revision.SupersededByRecordId, errorCode, errorStatusCode);
            if (revision.State == KnowledgeRecordState.Superseded &&
                revision.SupersededByRecordId == record.RecordId)
                throw Invalid(errorCode, "A Decision cannot supersede itself.", errorStatusCode);
            previousRevisionId = revision.RevisionId;
        }

        ValidateSupersessionState(
            record.Kind, record.State, record.SupersededByRecordId, errorCode, errorStatusCode);
        var head = revisions[^1];
        if (head.Type != record.Type ||
            head.Title != record.Title ||
            head.Content != record.Content ||
            head.Rationale != record.Rationale ||
            head.Importance != record.Importance ||
            !head.Tags.SequenceEqual(record.Tags, StringComparer.Ordinal) ||
            head.State != record.State ||
            head.TrustState != record.TrustState ||
            head.SupersededByRecordId != record.SupersededByRecordId ||
            (head.SourceRunId is not null && head.SourceRunId != record.SourceRunId) ||
            (head.SourceSessionId is not null && head.SourceSessionId != record.SourceSessionId))
            throw Invalid(errorCode, "The latest immutable revision does not match the transferred record head.",
                errorStatusCode);
    }

    private static bool IsValidPayload(
        string type,
        string? title,
        string content,
        string? rationale,
        string importance,
        ImmutableArray<string> tags,
        string? sourceRunId,
        string? sourceSessionId) =>
        !string.IsNullOrWhiteSpace(type) &&
        type.Length <= 64 &&
        (title is null || title.Length <= 512) &&
        !string.IsNullOrWhiteSpace(content) &&
        content.Length <= 40_000 &&
        (rationale is null || rationale.Length <= 8_000) &&
        importance is "low" or "medium" or "high" &&
        !tags.IsDefault &&
        tags.Length <= 32 &&
        tags.All(tag =>
            !string.IsNullOrWhiteSpace(tag) &&
            tag.Length <= 64 &&
            !tag.Any(char.IsControl)) &&
        (sourceRunId is null || sourceRunId.Length <= 256) &&
        (sourceSessionId is null || sourceSessionId.Length <= 256);

    public static void ValidateSupersessionState(
        KnowledgeRecordKind kind,
        KnowledgeRecordState state,
        Guid? replacementId,
        string errorCode = "invalid_knowledge_transfer",
        int errorStatusCode = StatusCodes.Status400BadRequest)
    {
        if ((state == KnowledgeRecordState.Superseded &&
             (kind != KnowledgeRecordKind.Decision || replacementId is null || replacementId == Guid.Empty)) ||
            (state != KnowledgeRecordState.Superseded && replacementId is not null))
            throw Invalid(errorCode, "Superseded Decisions require exactly one replacement link.", errorStatusCode);
    }

    public static KnowledgeApiException TooLarge(string detail) =>
        new("knowledge_transfer_too_large", detail, StatusCodes.Status413PayloadTooLarge);

    private static KnowledgeApiException Invalid(string code, string detail, int statusCode) =>
        new(code, detail, statusCode);
}
