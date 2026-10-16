using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

public sealed record UsageAttribution(
    string TenantId, string ProjectId, string RunId, string SessionId, string AgentId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TurnId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DispatchId { get; init; }
}

public sealed record UsageModelBinding(
    string ModelReference, string ModelId, string MeterSource, string SelectionRevision);

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CacheWriteTokens { get; init; }
}

public sealed record UsageSubmission(
    Guid EventId,
    DateTimeOffset OccurredAt,
    UsageAttribution Attribution,
    UsageModelBinding ModelBinding,
    UsageMeasurement Measurement)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SdkSessionFacts? SdkSource { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SdkEventId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? A2AMessageId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SdkUsageAccountingObservation? SdkAccounting { get; init; }
}

public sealed record UsageLedgerEntry(
    UsageSubmission Usage, CostBinding? CostBinding, CostPrice Price,
    DateTimeOffset RecordedAt, string CanonicalPayloadHash)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AccountingRevision { get; init; }
}

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DispatchId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SourceReceiptId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AccountingCursor { get; init; }
}

public sealed record UsageDispatchSourceReceiptIdentity(
    Guid SourceReceiptId,
    Guid EventId,
    string CanonicalPayloadHash);

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

public static class UsageDispatchSourceCompletionManifestContract
{
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

    public static void Validate(UsageDispatchSourceCompletionManifest manifest)
    {
        ValidateShape(manifest);
        if (!IsHash(manifest.ReceiptDigest) ||
            !string.Equals(manifest.ReceiptDigest, ComputeDigest(manifest), StringComparison.Ordinal))
            throw new ArgumentException("The dispatch source-completion manifest digest is invalid.", nameof(manifest));
    }

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

    internal static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static void WriteField(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }
}

public sealed record UsageDispatchAccountingScope(
    string TenantId,
    string ProjectId,
    string RunId,
    string MeterSource,
    string Unit,
    string? AccountingWindowId);

public enum UsageDispatchAccountingStatus
{
    Unknown,
    Partial,
    SourceComplete
}

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UsageRunTotals? RunTotals { get; init; }
}

public static class UsageDispatchAccountingWitnessContract
{
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

public sealed record UsageIngestionResult(UsageLedgerEntry Entry, bool IsDuplicate)
{
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
        DispatchId = Entry.Usage.Attribution.DispatchId,
        AccountingCursor = Entry.AccountingRevision
    };
}

public sealed record UsageAmountTotal(
    string MeterSource, string Unit, decimal Amount, long PricedEvents, long UnpricedEvents);

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
    public long? CacheWriteTokens { get; init; }
}

public sealed record UsageRunTotals(
    string TenantId,
    string ProjectId,
    string RunId,
    long Events,
    bool IsFullyPriced,
    ImmutableArray<UsageAgentTotals> Agents,
    ImmutableArray<UsageAmountTotal> Amounts);

public interface IUsageLedger
{
    Task<UsageIngestionResult> AppendAsync(
        UsageSubmission submission, CostBinding? binding, CostPrice price,
        CancellationToken cancellationToken = default);

    Task<UsageRunTotals> GetRunTotalsAsync(
        string tenantId, string projectId, string runId,
        CancellationToken cancellationToken = default);
}
