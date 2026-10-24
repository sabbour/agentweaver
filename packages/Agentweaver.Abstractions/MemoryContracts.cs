using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>Provider capabilities for durable knowledge records and accepted-effect delivery.</summary>
public static class MemoryProviderCapabilities
{
    public const string Read = "memory.records.read";
    public const string Write = "memory.records.write";
    public const string Search = "memory.records.search";
    public const string Revisions = "memory.records.revisions";
    public const string PromoteProposals = "memory.proposals.promote";
    public const string ComposeContext = "memory.context.compose";
    public const string AcceptedEffectDelivery = "memory.accepted-effects.delivery";

    /// <summary>All capability identifiers supported by this contract version.</summary>
    public static ImmutableHashSet<string> All { get; } = ImmutableHashSet.Create(
        StringComparer.Ordinal, Read, Write, Search, Revisions, PromoteProposals, ComposeContext,
        AcceptedEffectDelivery);
}

/// <summary>Domain category of a project knowledge record.</summary>
public enum KnowledgeRecordKind
{
    Memory,
    Proposal,
    Decision,
    SessionContext
}

/// <summary>Lifecycle state of a knowledge record, including proposal and replacement states.</summary>
public enum KnowledgeRecordState
{
    Pending,
    Active,
    Rejected,
    Archived,
    Promoted,
    Superseded
}

/// <summary>Trust review state that is distinct from the record's operational lifecycle state.</summary>
public enum KnowledgeTrustState
{
    Pending,
    Approved,
    Rejected,
    Legacy
}

/// <summary>
/// Current Memory-owned record for a project and agent. Revision and revision ID identify
/// its current immutable history entry; source and promotion fields retain provenance.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KnowledgeRecord(
    Guid RecordId,
    string ProjectId,
    string AgentId,
    KnowledgeRecordKind Kind,
    string Type,
    string? Title,
    string Content,
    string? Rationale,
    string Importance,
    ImmutableArray<string> Tags,
    KnowledgeRecordState State,
    KnowledgeTrustState TrustState,
    int Revision,
    Guid RevisionId,
    Guid? PreviousRevisionId,
    string? SourceRunId,
    string? SourceSessionId,
    Guid? PromotedDecisionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? SupersededByRecordId = null);

/// <summary>Immutable historical content and state for one revision of a knowledge record.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KnowledgeRecordRevision(
    Guid RecordId,
    int Revision,
    Guid RevisionId,
    Guid? PreviousRevisionId,
    KnowledgeRecordKind Kind,
    string Type,
    string? Title,
    string Content,
    string? Rationale,
    string Importance,
    ImmutableArray<string> Tags,
    KnowledgeRecordState State,
    KnowledgeTrustState TrustState,
    string? Reason,
    DateTimeOffset CreatedAt,
    Guid? SupersededByRecordId = null,
    string? SourceRunId = null,
    string? SourceSessionId = null,
    string? ActorFingerprint = null,
    string? ChangeKind = null);

/// <summary>Project filter with an optional agent, kind, text query, and paging inputs.</summary>
public sealed record KnowledgeRecordQuery(
    string ProjectId,
    string? AgentId,
    KnowledgeRecordKind? Kind = null,
    string? Query = null,
    bool IncludeInactive = false,
    int Page = 1,
    int PageSize = 50);

/// <summary>Page of current knowledge records and the total count for the query.</summary>
public sealed record KnowledgeRecordPage(
    ImmutableArray<KnowledgeRecord> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>One recent Memory record shown in a project's agent history panel.</summary>
public sealed record ProjectAgentHistoryEntry(
    Guid RecordId,
    string Type,
    string? Title,
    string Content,
    DateTimeOffset UpdatedAt);

/// <summary>Current, caller-visible Memory records for one configured project agent.</summary>
public sealed record ProjectAgentHistoryResponse(
    string ProjectId,
    string AgentId,
    ImmutableArray<ProjectAgentHistoryEntry> Items);

/// <summary>Limits the recent Memory records returned to the agent details drawer.</summary>
public static class ProjectAgentHistoryContract
{
    public const int MaximumItems = 10;
    public const int MaximumResponseBytes = 4 * 1024 * 1024;
}

/// <summary>Page of immutable record revisions and the total count for the history query.</summary>
public sealed record KnowledgeRecordRevisionPage(
    ImmutableArray<KnowledgeRecordRevision> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>Candidate records returned for composing bounded context for one agent run.</summary>
public sealed record KnowledgeContextCandidates(
    ImmutableArray<KnowledgeRecord> Records);

/// <summary>Outcome classes returned by Memory write and proposal operations.</summary>
public enum KnowledgeWriteStatus
{
    Created,
    Updated,
    NotFound,
    Stale,
    IdempotencyConflict,
    InvalidState,
    InvalidReplacement,
    ReplacementCycle
}

/// <summary>Input to create a knowledge record with provenance and actor reason.</summary>
public sealed record KnowledgeRecordCreate(
    string ProjectId,
    string AgentId,
    KnowledgeRecordKind Kind,
    string Type,
    string? Title,
    string Content,
    string? Rationale,
    string Importance,
    ImmutableArray<string> Tags,
    string? SourceRunId,
    string? SourceSessionId,
    string ActorFingerprint,
    string Reason);

/// <summary>Revision-fenced update to record content, tags, state, or supersession link.</summary>
public sealed record KnowledgeRecordUpdate(
    string ProjectId,
    Guid RecordId,
    int ExpectedRevision,
    string Type,
    string? Title,
    string Content,
    string? Rationale,
    string Importance,
    ImmutableArray<string> Tags,
    KnowledgeRecordState State,
    string ActorFingerprint,
    string? Reason = null,
    Guid? SupersededByRecordId = null);

/// <summary>Revision-fenced request to restore an earlier record revision.</summary>
public sealed record KnowledgeRecordRestore(
    string ProjectId,
    Guid RecordId,
    int ExpectedRevision,
    int Revision,
    string ActorFingerprint,
    string? Reason);

/// <summary>Revision-fenced approval request for a decision record.</summary>
public sealed record KnowledgeDecisionApproval(
    string ProjectId,
    Guid RecordId,
    int ExpectedRevision,
    string ActorFingerprint,
    string? Reason);

/// <summary>
/// Portable, bounded export of one agent's project knowledge records and their histories.
/// The schema and project/agent scope are validated during import.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KnowledgeRecordTransferBundle(
    string Format,
    int SchemaVersion,
    string ProjectId,
    string AgentId,
    ImmutableArray<KnowledgeRecordTransferEntry> Records);

/// <summary>One transferred current record together with its immutable revision history.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KnowledgeRecordTransferEntry(
    KnowledgeRecord Record,
    ImmutableArray<KnowledgeRecordRevision> Revisions);

/// <summary>Records imported by a Memory owner and whether the request was an idempotent replay.</summary>
public sealed record KnowledgeRecordImportResult(
    ImmutableArray<KnowledgeRecord> Records,
    bool IsDuplicate = false);

/// <summary>Format, schema, and maximum size limits for knowledge transfer bundles.</summary>
public static class KnowledgeRecordTransferContract
{
    /// <summary>Stable identifier for the transfer format.</summary>
    public const string Format = "agentweaver.knowledge-transfer.v1";
    /// <summary>Current transfer bundle schema version.</summary>
    public const int SchemaVersion = 1;
    public const int MaximumRecords = 25;
    public const int MaximumRevisions = 500;
    public const int MaximumBytes = 1_048_576;
}

/// <summary>Write outcome with the current record when one exists and duplicate/revision metadata.</summary>
public sealed record KnowledgeRecordWriteResult(
    KnowledgeWriteStatus Status,
    KnowledgeRecord? Record,
    int? CurrentRevision = null,
    bool IsDuplicate = false);

/// <summary>
/// Result of promoting a proposal into an approved decision and enqueueing accepted-effect
/// delivery. Delivery fields report the outbox outcome; they are not proof of downstream completion.
/// </summary>
public sealed record KnowledgeProposalPromotionResult(
    KnowledgeWriteStatus Status,
    KnowledgeRecord? Proposal,
    KnowledgeRecord? Decision,
    Guid? OutboxEventId,
    bool IsDuplicate = false,
    int? CurrentRevision = null,
    string? Delivery = null,
    string? DeliveryCode = null,
    string? RequiredAudienceSubject = null,
    string? RequiredAudience = null,
    ProjectFactAcknowledgment? DeliveryAcknowledgment = null);

/// <summary>
/// Authorization snapshot copied into an accepted-effect receipt so delivery can recheck
/// the exact actor, resource, membership, project, and configuration revisions.
/// </summary>
public sealed record AcceptedEffectAuthorizationBounds(
    string Issuer,
    string Subject,
    string TenantId,
    string? BoundProjectId,
    string? BoundRunId,
    ProjectAuthorityResourceType AuthorizationResourceType,
    string AuthorizationResourceId,
    long AuthorizationRevision,
    long MembershipRevision,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    string ContextRevision);

/// <summary>Memory-issued durable receipt for an accepted decision effect and its authorization snapshot.</summary>
public sealed record AcceptedEffectReceipt(
    Guid ReceiptId,
    int SchemaVersion,
    int EventVersion,
    string ProjectId,
    string RunId,
    Guid EffectId,
    Guid RecordId,
    int RecordVersion,
    string Issuer,
    string Subject,
    string TenantId,
    string? BoundProjectId,
    string? BoundRunId,
    ProjectAuthorityResourceType AuthorizationResourceType,
    string AuthorizationResourceId,
    long AuthorizationRevision,
    long MembershipRevision,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    string ContextRevision,
    DateTimeOffset AcceptedAt);

/// <summary>Identifies a receipt and expected event schema for an accepted-effect delivery lookup.</summary>
public sealed record AcceptedEffectDeliveryRequest(
    Guid ReceiptId,
    int SchemaVersion,
    int EventVersion)
{
    public string? ProjectId { get; init; }

    public string? RunId { get; init; }
}

/// <summary>Receipt plus whether its effect was acknowledged as delivered.</summary>
public sealed record AcceptedEffectDeliveryState(
    AcceptedEffectReceipt Receipt,
    bool IsDelivered);

/// <summary>Exclusive, token-fenced claim used by a worker while delivering an accepted effect.</summary>
public sealed record AcceptedEffectDeliveryLease(
    AcceptedEffectReceipt Receipt,
    Guid LeaseToken);

/// <summary>Acknowledges a project fact emitted after an accepted effect is delivered.</summary>
public sealed record ProjectFactAcknowledgment(
    Guid ReceiptId,
    int SchemaVersion,
    int EventVersion,
    Guid FactId,
    string ProjectId,
    long Sequence);

/// <summary>Current schema and event version for accepted-effect receipts.</summary>
public static class AcceptedEffectContractVersions
{
    public const int CurrentSchemaVersion = 1;
    public const int CurrentEventVersion = 1;
}

/// <summary>
/// Provider-neutral owner interface for project knowledge, immutable revisions, proposal
/// promotion, and leased accepted-effect delivery.
/// </summary>
public interface IMemoryProvider
{
    /// <summary>Negotiates a provider resource and its capabilities.</summary>
    Task<ResourceNegotiation> NegotiateAsync(
        ProviderCandidate candidate,
        CancellationToken cancellationToken = default);

    /// <summary>Searches project and agent records using the requested filters and page.</summary>
    Task<KnowledgeRecordPage> SearchAsync(
        KnowledgeRecordQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one current record within its project, or returns null when absent.</summary>
    Task<KnowledgeRecord?> ReadAsync(
        string projectId,
        Guid recordId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a page of immutable revisions for one project record.</summary>
    Task<KnowledgeRecordRevisionPage> ReadRevisionsAsync(
        string projectId,
        Guid recordId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a record once for the supplied idempotency key.</summary>
    Task<KnowledgeRecordWriteResult> CreateAsync(
        KnowledgeRecordCreate input,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Applies an expected-revision update, returning stale rather than overwriting newer content.</summary>
    Task<KnowledgeRecordWriteResult> UpdateAsync(
        KnowledgeRecordUpdate input,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Restores prior content only when the current record revision matches the request.</summary>
    Task<KnowledgeRecordWriteResult> RestoreAsync(
        KnowledgeRecordRestore input,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Approves a decision record if its expected revision is still current.</summary>
    Task<KnowledgeRecordWriteResult> ApproveDecisionAsync(
        KnowledgeDecisionApproval input,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Exports the selected agent's project records and revisions in the bounded transfer format.</summary>
    Task<KnowledgeRecordTransferBundle> ExportAsync(
        string projectId,
        string agentId,
        CancellationToken cancellationToken = default);

    /// <summary>Imports a transfer bundle into a run with actor attribution and idempotent replay handling.</summary>
    Task<KnowledgeRecordImportResult> ImportAsync(
        KnowledgeRecordTransferBundle input,
        string runId,
        string actorFingerprint,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Rejects a proposal only at its expected revision.</summary>
    Task<KnowledgeRecordWriteResult> RejectProposalAsync(
        string projectId,
        string runId,
        Guid proposalId,
        int expectedRevision,
        string actorFingerprint,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Promotes a proposal using current authorization bounds and emits an accepted-effect
    /// receipt for reliable downstream delivery.
    /// </summary>
    Task<KnowledgeProposalPromotionResult> PromoteProposalAsync(
        string projectId,
        string runId,
        Guid proposalId,
        int expectedRevision,
        string actorFingerprint,
        AcceptedEffectAuthorizationBounds authorization,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a receipt by its globally unique identity, or returns null when absent.</summary>
    Task<AcceptedEffectReceipt?> ReadAcceptedEffectReceiptAsync(
        Guid receiptId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a receipt only within the requested project and run.</summary>
    Task<AcceptedEffectReceipt?> ReadAcceptedEffectReceiptAsync(
        string projectId,
        string runId,
        Guid receiptId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads whether a project/run receipt has been acknowledged as delivered.</summary>
    Task<AcceptedEffectDeliveryState?> ReadAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        CancellationToken cancellationToken = default);

    /// <summary>Claims delivery using a time-limited lease so competing workers do not deliver concurrently.</summary>
    Task<AcceptedEffectDeliveryLease?> ClaimAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>Releases only the lease identified by the matching token.</summary>
    Task<bool> ReleaseAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);

    /// <summary>Acknowledges successful delivery only for the currently claimed lease token.</summary>
    Task<bool> AcknowledgeAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);

    /// <summary>Reads bounded context candidates for the selected agent and run.</summary>
    Task<KnowledgeContextCandidates> ReadContextCandidatesAsync(
        string projectId,
        string agentId,
        string runId,
        int maximumRecords,
        CancellationToken cancellationToken = default);
}
