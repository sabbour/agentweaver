using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>Associates a usage event with its tenant, project, run, session, agent, and optional turn/dispatch.</summary>
public sealed record UsageAttribution(
    string TenantId, string ProjectId, string RunId, string SessionId, string AgentId)
{
    /// <summary>Turn that produced the measurement, when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TurnId { get; init; }
    /// <summary>Dispatch whose source-completion accounting may include this event.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DispatchId { get; init; }
}

/// <summary>Model, meter, and selection revision used to interpret a usage measurement.</summary>
public sealed record UsageModelBinding(
    string ModelReference, string ModelId, string MeterSource, string SelectionRevision);

/// <summary>Provider-reported counts and units for one usage event; null means the source did not report that measure.</summary>
public sealed record UsageMeasurement(
    long? InputTokens,
    long? OutputTokens,
    long? CachedTokens,
    long? ReasoningTokens,
    long? RequestCount,
    decimal? ProviderUnits,
    string? ProviderUnit,
    decimal? DurationMilliseconds)
{
    /// <summary>Cached input tokens written by the model, distinct from tokens read from cache.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CacheWriteTokens { get; init; }
}

/// <summary>
/// Ingestion input for a single metered event. Native SDK source and event IDs enable
/// source-side deduplication; attribution and model pins bind it to an accepted run.
/// </summary>
public sealed record UsageSubmission(
    Guid EventId,
    DateTimeOffset OccurredAt,
    UsageAttribution Attribution,
    UsageModelBinding ModelBinding,
    UsageMeasurement Measurement)
{
    /// <summary>Runtime SDK session facts used to validate source model and registration pins.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SdkSessionFacts? SdkSource { get; init; }
    /// <summary>Original native SDK event ID retained for source correlation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SdkEventId { get; init; }
    /// <summary>Addressed message that initiated the measured SDK operation, when known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? A2AMessageId { get; init; }
    /// <summary>SDK completeness and stable accounting identity reported with this usage event.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SdkUsageAccountingObservation? SdkAccounting { get; init; }
}

/// <summary>Durable ledger record containing the accepted usage, cost binding, price, and canonical payload hash.</summary>
public sealed record UsageLedgerEntry(
    UsageSubmission Usage, CostBinding? CostBinding, CostPrice Price,
    DateTimeOffset RecordedAt, string CanonicalPayloadHash)
{
    /// <summary>Owner-assigned cursor used to order accounting receipts.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AccountingRevision { get; init; }
}

/// <summary>
/// Reference-only accounting response for one usage event. It exposes priced metadata
/// and source receipt identity but not the original payload or provider credential.
/// </summary>
public sealed record UsageAccountingReceipt(
    Guid EventId,
    UsageAttribution Attribution,
    string CanonicalPayloadHash,
    CostDisposition Disposition,
    decimal? Amount,
    string? Unit,
    string? UnpricedReason,
    string? RateCardId,
    string? RateCardVersion,
    DateTimeOffset RecordedAt)
{
    /// <summary>SHA-256 of the exact runtime rate-card document used for this accounting result.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RateCardContentHash { get; init; }
    /// <summary>Dispatch whose accounting boundary includes this receipt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DispatchId { get; init; }
    /// <summary>Source owner's stable receipt identity, when dispatch reconciliation uses source receipts.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SourceReceiptId { get; init; }
    /// <summary>Monotonic ledger cursor for this accounting revision.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AccountingCursor { get; init; }
}

/// <summary>Stable identity and payload hash for one source receipt included in a dispatch manifest.</summary>
public sealed record UsageDispatchSourceReceiptIdentity(
    Guid SourceReceiptId,
    Guid EventId,
    string CanonicalPayloadHash);

/// <summary>
/// Completion claim from a usage source for all receipts produced by one dispatch.
/// Its revision and digest bind the source's complete receipt inventory to a run fence.
/// </summary>
public sealed record UsageDispatchSourceCompletionManifest(
    string DispatchId,
    string TenantId,
    string ProjectId,
    string RunId,
    string SessionId,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    ImmutableArray<UsageDispatchSourceReceiptIdentity> SourceReceipts,
    string ReceiptDigest,
    Guid CompletionReceiptId,
    long CompletionRevision);

/// <summary>Canonical serialization, digest, and validation rules for source completion manifests.</summary>
public static class UsageDispatchSourceCompletionManifestContract
{
    /// <summary>Serializes a validated manifest with stable field order and receipt ordering.</summary>
    public static string SerializeCanonical(UsageDispatchSourceCompletionManifest manifest)
    {
        Validate(manifest);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("dispatchId", manifest.DispatchId);
            writer.WriteString("tenantId", manifest.TenantId);
            writer.WriteString("projectId", manifest.ProjectId);
            writer.WriteString("runId", manifest.RunId);
            writer.WriteString("sessionId", manifest.SessionId);
            writer.WriteString("runtimeInstanceId", manifest.RuntimeInstanceId.ToString("D"));
            writer.WriteNumber("registrationRevision", manifest.RegistrationRevision);
            writer.WriteNumber("executionFence", manifest.ExecutionFence);
            writer.WritePropertyName("sourceReceipts");
            writer.WriteStartArray();
            foreach (var receipt in manifest.SourceReceipts.OrderBy(
                         item => item.SourceReceiptId.ToString("D"), StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("sourceReceiptId", receipt.SourceReceiptId.ToString("D"));
                writer.WriteString("eventId", receipt.EventId.ToString("D"));
                writer.WriteString("canonicalPayloadHash", receipt.CanonicalPayloadHash);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("receiptDigest", manifest.ReceiptDigest);
            writer.WriteString("completionReceiptId", manifest.CompletionReceiptId.ToString("D"));
            writer.WriteNumber("completionRevision", manifest.CompletionRevision);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Computes a domain-separated digest over manifest identity and sorted receipt identities.</summary>
    public static string ComputeDigest(UsageDispatchSourceCompletionManifest manifest)
    {
        ValidateShape(manifest);
        using var stream = new MemoryStream();
        WriteField(stream, "agentweaver.usage.dispatch-source-completion.v1");
        WriteField(stream, manifest.DispatchId);
        WriteField(stream, manifest.TenantId);
        WriteField(stream, manifest.ProjectId);
        WriteField(stream, manifest.RunId);
        WriteField(stream, manifest.SessionId);
        WriteField(stream, manifest.RuntimeInstanceId.ToString("D"));
        WriteField(stream, manifest.RegistrationRevision.ToString(CultureInfo.InvariantCulture));
        WriteField(stream, manifest.ExecutionFence.ToString(CultureInfo.InvariantCulture));
        WriteField(stream, manifest.CompletionReceiptId.ToString("D"));
        WriteField(stream, manifest.CompletionRevision.ToString(CultureInfo.InvariantCulture));
        WriteField(stream, manifest.SourceReceipts.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var receipt in manifest.SourceReceipts.OrderBy(
                     item => item.SourceReceiptId.ToString("D"), StringComparer.Ordinal))
        {
            WriteField(stream, receipt.SourceReceiptId.ToString("D"));
            WriteField(stream, receipt.EventId.ToString("D"));
            WriteField(stream, receipt.CanonicalPayloadHash);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>Requires valid scope and unique receipts, and verifies the manifest digest.</summary>
    /// <exception cref="ArgumentException">The manifest, receipt set, or digest is invalid.</exception>
    public static void Validate(UsageDispatchSourceCompletionManifest manifest)
    {
        ValidateShape(manifest);
        if (!IsHash(manifest.ReceiptDigest) ||
            !string.Equals(manifest.ReceiptDigest, ComputeDigest(manifest), StringComparison.Ordinal))
            throw new ArgumentException("The dispatch source-completion manifest digest is invalid.", nameof(manifest));
    }

    /// <summary>Checks manifest scope, positive revisions, and unique valid source receipt/event identities.</summary>
    private static void ValidateShape(UsageDispatchSourceCompletionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!Guid.TryParseExact(manifest.DispatchId, "D", out var dispatchId) ||
            dispatchId.ToString("D") != manifest.DispatchId || dispatchId == Guid.Empty ||
            string.IsNullOrWhiteSpace(manifest.TenantId) ||
            string.IsNullOrWhiteSpace(manifest.ProjectId) ||
            string.IsNullOrWhiteSpace(manifest.RunId) ||
            string.IsNullOrWhiteSpace(manifest.SessionId) ||
            manifest.RuntimeInstanceId == Guid.Empty ||
            manifest.RegistrationRevision < 1 || manifest.ExecutionFence < 1 ||
            manifest.SourceReceipts.IsDefault || manifest.CompletionReceiptId == Guid.Empty ||
            manifest.CompletionRevision < 1)
            throw new ArgumentException("The dispatch source-completion manifest is invalid.", nameof(manifest));
        var receiptIds = new HashSet<Guid>();
        var eventIds = new HashSet<Guid>();
        foreach (var receipt in manifest.SourceReceipts)
        {
            if (receipt is null || receipt.SourceReceiptId == Guid.Empty ||
                receipt.EventId == Guid.Empty || !IsHash(receipt.CanonicalPayloadHash) ||
                !receiptIds.Add(receipt.SourceReceiptId) || !eventIds.Add(receipt.EventId))
                throw new ArgumentException("The dispatch source-completion receipt set is invalid.", nameof(manifest));
        }
    }

    /// <summary>Returns whether a string is a canonical lowercase SHA-256 digest.</summary>
    internal static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Writes a UTF-8 field with a big-endian byte-length prefix for canonical hashing.</summary>
    internal static void WriteField(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }
}

/// <summary>Accounting partition for one dispatch, including the meter, unit, and optional source window.</summary>
public sealed record UsageDispatchAccountingScope(
    string TenantId,
    string ProjectId,
    string RunId,
    string MeterSource,
    string Unit,
    string? AccountingWindowId);

/// <summary>How complete the source evidence is for a dispatch accounting witness.</summary>
public enum UsageDispatchAccountingStatus
{
    Unknown,
    Partial,
    SourceComplete
}

/// <summary>
/// Ledger's verified view of usage receipts for one dispatch and accounting scope.
/// The watermark, receipt digest, and optional source-completion revision make the
/// inventory auditable; a partial state is not proof that all source usage arrived.
/// </summary>
public sealed record UsageDispatchAccountingWitness(
    string DispatchId,
    UsageDispatchAccountingScope Scope,
    long AccountingWatermark,
    ImmutableArray<UsageDispatchSourceReceiptIdentity> Receipts,
    string ReceiptDigest,
    decimal KnownPricedSubtotal,
    long PricedReceiptCount,
    long UnpricedReceiptCount,
    UsageDispatchAccountingStatus Status,
    Guid? SourceCompletionReceiptId,
    long? SourceCompletionRevision,
    string? SourceCompletionDigest)
{
    /// <summary>Aggregate totals across the run, included when the caller requested them.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UsageRunTotals? RunTotals { get; init; }
}

/// <summary>Computes a stable digest for a validated dispatch receipt inventory.</summary>
public static class UsageDispatchAccountingWitnessContract
{
    /// <summary>Validates the accounting scope and receipt identities, then hashes them in stable order.</summary>
    /// <exception cref="ArgumentException">The dispatch, scope, or receipt inventory is invalid.</exception>
    public static string ComputeReceiptDigest(
        string dispatchId,
        UsageDispatchAccountingScope scope,
        ImmutableArray<UsageDispatchSourceReceiptIdentity> receipts)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!Guid.TryParseExact(dispatchId, "D", out var parsedDispatchId) ||
            parsedDispatchId == Guid.Empty || parsedDispatchId.ToString("D") != dispatchId ||
            string.IsNullOrWhiteSpace(scope.TenantId) ||
            string.IsNullOrWhiteSpace(scope.ProjectId) ||
            string.IsNullOrWhiteSpace(scope.RunId) ||
            string.IsNullOrWhiteSpace(scope.MeterSource) ||
            string.IsNullOrWhiteSpace(scope.Unit) || receipts.IsDefault)
            throw new ArgumentException("The dispatch accounting witness scope is invalid.");
        var receiptIds = new HashSet<Guid>();
        var eventIds = new HashSet<Guid>();
        foreach (var receipt in receipts)
        {
            if (receipt is null || receipt.SourceReceiptId == Guid.Empty ||
                receipt.EventId == Guid.Empty ||
                !UsageDispatchSourceCompletionManifestContract.IsHash(receipt.CanonicalPayloadHash) ||
                !receiptIds.Add(receipt.SourceReceiptId) || !eventIds.Add(receipt.EventId))
                throw new ArgumentException("The dispatch accounting witness receipt set is invalid.");
        }
        using var stream = new MemoryStream();
        UsageDispatchSourceCompletionManifestContract.WriteField(
            stream, "agentweaver.usage.dispatch-accounting-witness.v1");
        UsageDispatchSourceCompletionManifestContract.WriteField(stream, dispatchId);
        UsageDispatchSourceCompletionManifestContract.WriteField(stream, scope.TenantId);
        UsageDispatchSourceCompletionManifestContract.WriteField(stream, scope.ProjectId);
        UsageDispatchSourceCompletionManifestContract.WriteField(stream, scope.RunId);
        UsageDispatchSourceCompletionManifestContract.WriteField(stream, scope.MeterSource);
        UsageDispatchSourceCompletionManifestContract.WriteField(stream, scope.Unit);
        UsageDispatchSourceCompletionManifestContract.WriteField(
            stream, scope.AccountingWindowId is null ? "null" : "value");
        if (scope.AccountingWindowId is not null)
            UsageDispatchSourceCompletionManifestContract.WriteField(stream, scope.AccountingWindowId);
        UsageDispatchSourceCompletionManifestContract.WriteField(
            stream, receipts.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var receipt in receipts.OrderBy(
                     item => item.SourceReceiptId.ToString("D"), StringComparer.Ordinal))
        {
            UsageDispatchSourceCompletionManifestContract.WriteField(stream, receipt.SourceReceiptId.ToString("D"));
            UsageDispatchSourceCompletionManifestContract.WriteField(stream, receipt.EventId.ToString("D"));
            UsageDispatchSourceCompletionManifestContract.WriteField(stream, receipt.CanonicalPayloadHash);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}

/// <summary>Explicit project budget period. Usage belongs to the period that contains its occurrence time.</summary>
/// <param name="PeriodId">Stable period identity configured by the project.</param>
/// <param name="StartsAt">Inclusive period start.</param>
/// <param name="EndsAt">Exclusive period end.</param>
public sealed record UsageSpendPeriod(string PeriodId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>Requests represented spend for one (meter source, unit) bucket and a bounded dispatch set.</summary>
public sealed record UsageSpendEvidenceRequest(
    int ContractVersion,
    string MeterSource,
    string Unit,
    UsageSpendPeriod? ProjectPeriod,
    ImmutableArray<string> DispatchIds)
{
    /// <summary>Optional agent whose run total the evidence also reports.</summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentId { get; init; }
}

/// <summary>Priced amount and event counts for one spend bucket. Unpriced events never add to the amount.</summary>
public sealed record UsageSpendTotal(decimal PricedAmount, long PricedEvents, long UnpricedEvents);

/// <summary>
/// Events-owned represented-spend evidence. Every total and dispatch witness comes from one ledger
/// snapshot. <see cref="AccountingCursor"/> counts the append-only ledger events for the project and
/// meter source in that snapshot, so a later read always has an equal or higher cursor and represents
/// every charge an earlier read represented. Dispatch witnesses match any run in the project, because
/// project budgets hold exposure for dispatches from other runs.
/// </summary>
public sealed record UsageSpendEvidence(
    int ContractVersion,
    string TenantId,
    string ProjectId,
    string RunId,
    string MeterSource,
    string Unit,
    long AccountingCursor,
    UsageSpendTotal Run,
    UsageSpendTotal? ProjectPeriod,
    string? ProjectPeriodId,
    ImmutableArray<UsageDispatchAccountingWitness> Dispatches)
{
    /// <summary>Run total for the requested agent. Present only when the request names an agent.</summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public UsageSpendTotal? Agent { get; init; }
}

/// <summary>Validates represented-spend requests and the evidence returned for them.</summary>
public static class UsageSpendEvidenceContract
{
    /// <summary>Current contract version.</summary>
    public const int CurrentVersion = 1;
    /// <summary>Maximum number of dispatch witnesses returned by one read.</summary>
    public const int MaximumDispatches = 64;

    /// <summary>Requires an explicit bucket, a well-formed optional period, and unique canonical dispatch IDs.</summary>
    /// <exception cref="ArgumentException">The request is invalid.</exception>
    public static void ValidateRequest(UsageSpendEvidenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != CurrentVersion ||
            string.IsNullOrWhiteSpace(request.MeterSource) || request.MeterSource.Length > 256 ||
            string.IsNullOrWhiteSpace(request.Unit) || request.Unit.Length > 64 ||
            request.DispatchIds.IsDefault || request.DispatchIds.Length > MaximumDispatches ||
            request.AgentId is { } agentId && (string.IsNullOrWhiteSpace(agentId) || agentId.Length > 256) ||
            request.ProjectPeriod is { } period &&
                (string.IsNullOrWhiteSpace(period.PeriodId) || period.PeriodId.Length > 128 ||
                 period.EndsAt <= period.StartsAt))
            throw new ArgumentException("The spend evidence request is invalid.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dispatchId in request.DispatchIds)
            if (!Guid.TryParseExact(dispatchId, "D", out var parsed) || parsed == Guid.Empty ||
                parsed.ToString("D") != dispatchId || !seen.Add(dispatchId))
                throw new ArgumentException("The spend evidence dispatch set is invalid.");
    }

    /// <summary>Requires evidence that answers exactly the requested scope with consistent witnesses.</summary>
    /// <exception cref="ArgumentException">The evidence does not answer the request.</exception>
    public static void ValidateEvidence(
        UsageSpendEvidence evidence, UsageSpendEvidenceRequest request,
        string tenantId, string projectId, string runId)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateRequest(request);
        if (evidence.ContractVersion != CurrentVersion || evidence.TenantId != tenantId ||
            evidence.ProjectId != projectId || evidence.RunId != runId ||
            evidence.MeterSource != request.MeterSource || evidence.Unit != request.Unit ||
            evidence.AccountingCursor < 0 || !IsValid(evidence.Run) ||
            (request.ProjectPeriod is null
                ? evidence.ProjectPeriod is not null || evidence.ProjectPeriodId is not null
                : !IsValid(evidence.ProjectPeriod) || evidence.ProjectPeriodId != request.ProjectPeriod.PeriodId) ||
            (request.AgentId is null ? evidence.Agent is not null : !IsValid(evidence.Agent)) ||
            evidence.Dispatches.IsDefault || evidence.Dispatches.Length != request.DispatchIds.Length)
            throw new ArgumentException("The spend evidence does not match its request.");
        var scope = new UsageDispatchAccountingScope(
            tenantId, projectId, runId, request.MeterSource, request.Unit, request.ProjectPeriod?.PeriodId);
        for (var index = 0; index < request.DispatchIds.Length; index++)
        {
            var witness = evidence.Dispatches[index];
            if (witness is null || witness.DispatchId != request.DispatchIds[index] || witness.Scope != scope ||
                witness.AccountingWatermark != evidence.AccountingCursor ||
                witness.Receipts.IsDefault || witness.KnownPricedSubtotal < 0 ||
                witness.PricedReceiptCount < 0 || witness.UnpricedReceiptCount < 0 ||
                witness.PricedReceiptCount + witness.UnpricedReceiptCount != witness.Receipts.Length ||
                witness.Status == UsageDispatchAccountingStatus.SourceComplete ||
                witness.Status != (witness.Receipts.IsEmpty
                    ? UsageDispatchAccountingStatus.Unknown : UsageDispatchAccountingStatus.Partial) ||
                witness.ReceiptDigest != UsageDispatchAccountingWitnessContract.ComputeReceiptDigest(
                    witness.DispatchId, witness.Scope, witness.Receipts))
                throw new ArgumentException("The spend evidence dispatch witness is invalid.");
        }
    }

    private static bool IsValid(UsageSpendTotal? total) =>
        total is { PricedAmount: >= 0, PricedEvents: >= 0, UnpricedEvents: >= 0 };
}

/// <summary>Append result with duplicate status and a projection suitable for receipt delivery.</summary>
public sealed record UsageIngestionResult(UsageLedgerEntry Entry, bool IsDuplicate)
{
    /// <summary>Projects the ledger entry into its public accounting receipt form.</summary>
    public UsageAccountingReceipt Receipt => new(
        Entry.Usage.EventId,
        Entry.Usage.Attribution,
        Entry.CanonicalPayloadHash,
        Entry.Price.Disposition,
        Entry.Price.Amount,
        Entry.Price.Unit,
        Entry.Price.UnpricedReason,
        Entry.CostBinding?.RateCard.Id,
        Entry.CostBinding?.RateCard.Version,
        Entry.RecordedAt)
    {
        RateCardContentHash = Entry.CostBinding?.RateCard?.ContentHash,
        DispatchId = Entry.Usage.Attribution.DispatchId,
        AccountingCursor = Entry.AccountingRevision
    };
}

/// <summary>Total amount for one meter and unit, including priced and unpriced event counts.</summary>
public sealed record UsageAmountTotal(
    string MeterSource, string Unit, decimal Amount, long PricedEvents, long UnpricedEvents);

/// <summary>Usage and priced amounts aggregated for one agent within a run.</summary>
public sealed record UsageAgentTotals(
    string AgentId,
    long Events,
    long? RequestCount,
    long? InputTokens,
    long? OutputTokens,
    long? CachedTokens,
    long? ReasoningTokens,
    decimal? DurationMilliseconds,
    bool IsFullyPriced,
    ImmutableArray<UsageAmountTotal> Amounts)
{
    /// <summary>Aggregate cached tokens written by the agent's model calls.</summary>
    public long? CacheWriteTokens { get; init; }
}

/// <summary>Run-wide usage and cost totals, grouped by agent and meter/unit.</summary>
public sealed record UsageRunTotals(
    string TenantId,
    string ProjectId,
    string RunId,
    long Events,
    bool IsFullyPriced,
    ImmutableArray<UsageAgentTotals> Agents,
    ImmutableArray<UsageAmountTotal> Amounts);

/// <summary>Durable append and run-total query contract implemented by the usage ledger owner.</summary>
public interface IUsageLedger
{
    /// <summary>Appends an idempotent usage event with its selected cost binding and computed price.</summary>
    Task<UsageIngestionResult> AppendAsync(
        UsageSubmission submission, CostBinding? binding, CostPrice price,
        CancellationToken cancellationToken = default);

    /// <summary>Returns usage totals for the exact tenant, project, and run scope.</summary>
    Task<UsageRunTotals> GetRunTotalsAsync(
        string tenantId, string projectId, string runId,
        CancellationToken cancellationToken = default);
}
