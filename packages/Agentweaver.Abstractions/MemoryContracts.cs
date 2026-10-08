using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

public static class MemoryProviderCapabilities
{
    public const string Read = "memory.records.read";
    public const string Write = "memory.records.write";
    public const string Search = "memory.records.search";
    public const string Revisions = "memory.records.revisions";
    public const string PromoteProposals = "memory.proposals.promote";
    public const string ComposeContext = "memory.context.compose";
    public const string AcceptedEffectDelivery = "memory.accepted-effects.delivery";

    public static ImmutableHashSet<string> All { get; } = ImmutableHashSet.Create(
        StringComparer.Ordinal, Read, Write, Search, Revisions, PromoteProposals, ComposeContext,
        AcceptedEffectDelivery);
}

public enum KnowledgeRecordKind
{
    Memory,
    Proposal,
    Decision,
    SessionContext
}

public enum KnowledgeRecordState
{
    Pending,
    Active,
    Rejected,
    Archived,
    Promoted
}

public enum KnowledgeTrustState
{
    Pending,
    Approved,
    Rejected,
    Legacy
}

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
    DateTimeOffset UpdatedAt);

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
    string Reason,
    DateTimeOffset CreatedAt);

public sealed record KnowledgeRecordQuery(
    string ProjectId,
    string AgentId,
    KnowledgeRecordKind? Kind = null,
    string? Query = null,
    bool IncludeInactive = false,
    int Page = 1,
    int PageSize = 50);

public sealed record KnowledgeRecordPage(
    ImmutableArray<KnowledgeRecord> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record KnowledgeRecordRevisionPage(
    ImmutableArray<KnowledgeRecordRevision> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record KnowledgeContextCandidates(
    ImmutableArray<KnowledgeRecord> Records);

public enum KnowledgeWriteStatus
{
    Created,
    Updated,
    NotFound,
    Stale,
    IdempotencyConflict,
    InvalidState
}

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
    string Reason);

public sealed record KnowledgeRecordWriteResult(
    KnowledgeWriteStatus Status,
    KnowledgeRecord? Record,
    int? CurrentRevision = null,
    bool IsDuplicate = false);

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

public sealed record AcceptedEffectDeliveryRequest(
    Guid ReceiptId,
    int SchemaVersion,
    int EventVersion)
{
    public string? ProjectId { get; init; }

    public string? RunId { get; init; }
}

public sealed record AcceptedEffectDeliveryState(
    AcceptedEffectReceipt Receipt,
    bool IsDelivered);

public sealed record AcceptedEffectDeliveryLease(
    AcceptedEffectReceipt Receipt,
    Guid LeaseToken);

public sealed record ProjectFactAcknowledgment(
    Guid ReceiptId,
    int SchemaVersion,
    int EventVersion,
    Guid FactId,
    string ProjectId,
    long Sequence);

public static class AcceptedEffectContractVersions
{
    public const int CurrentSchemaVersion = 1;
    public const int CurrentEventVersion = 1;
}

public interface IMemoryProvider
{
    Task<ResourceNegotiation> NegotiateAsync(
        ProviderCandidate candidate,
        CancellationToken cancellationToken = default);

    Task<KnowledgeRecordPage> SearchAsync(
        KnowledgeRecordQuery query,
        CancellationToken cancellationToken = default);

    Task<KnowledgeRecord?> ReadAsync(
        string projectId,
        Guid recordId,
        CancellationToken cancellationToken = default);

    Task<KnowledgeRecordRevisionPage> ReadRevisionsAsync(
        string projectId,
        Guid recordId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<KnowledgeRecordWriteResult> CreateAsync(
        KnowledgeRecordCreate input,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<KnowledgeRecordWriteResult> UpdateAsync(
        KnowledgeRecordUpdate input,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<KnowledgeRecordWriteResult> RejectProposalAsync(
        string projectId,
        string runId,
        Guid proposalId,
        int expectedRevision,
        string actorFingerprint,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<KnowledgeProposalPromotionResult> PromoteProposalAsync(
        string projectId,
        string runId,
        Guid proposalId,
        int expectedRevision,
        string actorFingerprint,
        AcceptedEffectAuthorizationBounds authorization,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<AcceptedEffectReceipt?> ReadAcceptedEffectReceiptAsync(
        Guid receiptId,
        CancellationToken cancellationToken = default);

    Task<AcceptedEffectReceipt?> ReadAcceptedEffectReceiptAsync(
        string projectId,
        string runId,
        Guid receiptId,
        CancellationToken cancellationToken = default);

    Task<AcceptedEffectDeliveryState?> ReadAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        CancellationToken cancellationToken = default);

    Task<AcceptedEffectDeliveryLease?> ClaimAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> ReleaseAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);

    Task<bool> AcknowledgeAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);

    Task<KnowledgeContextCandidates> ReadContextCandidatesAsync(
        string projectId,
        string agentId,
        string runId,
        int maximumRecords,
        CancellationToken cancellationToken = default);
}
