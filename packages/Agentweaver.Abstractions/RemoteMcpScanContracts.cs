using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>Execution state of one owner-issued Remote MCP catalog scan.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RemoteMcpScanState>))]
public enum RemoteMcpScanState
{
    Completed,
    Error
}

/// <summary>Result of one bounded Remote MCP catalog check.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RemoteMcpScanOutcome>))]
public enum RemoteMcpScanOutcome
{
    Pass,
    Unknown,
    Fail,
    Error
}

/// <summary>Evidence for one named Remote MCP catalog check.</summary>
public sealed record RemoteMcpScanCheckEvidence(
    string CheckId,
    RemoteMcpScanOutcome Outcome,
    int? RiskScore,
    string? ReasonCode);

/// <summary>Owner-issued, exact-catalog scan evidence retained with an immutable discovery catalog.</summary>
public sealed record RemoteMcpCatalogScanEvidence
{
    public const int CurrentVersion = 1;
    public const string CurrentScannerId = "agentweaver.environment.remote-mcp-catalog";
    public const string CurrentScannerVersion = "1.0.0";
    public const string PoisonedDescriptionCheck = "mcp.poisoned-description";
    public const string HiddenUnicodeCheck = "mcp.hidden-instructions-unicode";
    public const string LookalikeNameCheck = "mcp.lookalike-name";
    public const string CrossServerCollisionCheck = "mcp.cross-server-collision";
    public const string BaselineDriftCheck = "mcp.accepted-baseline-drift";

    public static readonly ImmutableArray<string> CheckIds =
        [
            PoisonedDescriptionCheck,
            HiddenUnicodeCheck,
            LookalikeNameCheck,
            CrossServerCollisionCheck,
            BaselineDriftCheck
        ];

    public RemoteMcpCatalogScanEvidence(
        int contractVersion,
        string scannerId,
        string scannerVersion,
        long configurationRevision,
        long discoveryRevision,
        string catalogSha256,
        long? baselineDiscoveryRevision,
        string? baselineCatalogSha256,
        DateTimeOffset evaluatedAt,
        RemoteMcpScanState state,
        ImmutableArray<RemoteMcpScanCheckEvidence> checks,
        string? errorCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scannerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scannerVersion);
        if (contractVersion < 1 ||
            scannerId.Length > 128 || scannerId.Any(char.IsControl) ||
            scannerVersion.Length > 64 || scannerVersion.Any(char.IsControl) ||
            configurationRevision < 1 ||
            discoveryRevision < 1 ||
            evaluatedAt.Offset != TimeSpan.Zero ||
            !Enum.IsDefined(state) ||
            checks.IsDefault ||
            checks.Length > 16 ||
            checks.Any(check => check is null || !CheckIds.Contains(check.CheckId) ||
                !Enum.IsDefined(check.Outcome) ||
                check.RiskScore is < 0 or > 100 ||
                check.Outcome is RemoteMcpScanOutcome.Pass or RemoteMcpScanOutcome.Unknown &&
                    check.RiskScore is not null ||
                check.Outcome is RemoteMcpScanOutcome.Fail or RemoteMcpScanOutcome.Error &&
                    check.RiskScore is null) ||
            checks.Select(check => check.CheckId).Distinct(StringComparer.Ordinal).Count() != checks.Length ||
            (baselineDiscoveryRevision is null) != (baselineCatalogSha256 is null) ||
            baselineDiscoveryRevision is < 1 ||
            state == RemoteMcpScanState.Completed && errorCode is not null ||
            state == RemoteMcpScanState.Error && errorCode is null)
            throw new ArgumentException("The Remote MCP scan evidence is invalid.", nameof(checks));

        ContractVersion = contractVersion;
        ScannerId = scannerId;
        ScannerVersion = scannerVersion;
        ConfigurationRevision = configurationRevision;
        DiscoveryRevision = discoveryRevision;
        CatalogSha256 = RemoteMcpDigest.ValidateSha256(catalogSha256, nameof(catalogSha256));
        BaselineDiscoveryRevision = baselineDiscoveryRevision;
        BaselineCatalogSha256 = baselineCatalogSha256 is null
            ? null
            : RemoteMcpDigest.ValidateSha256(baselineCatalogSha256, nameof(baselineCatalogSha256));
        EvaluatedAt = evaluatedAt;
        State = state;
        Checks = checks;
        ErrorCode = errorCode;
    }

    public int ContractVersion { get; }
    public string ScannerId { get; }
    public string ScannerVersion { get; }
    public long ConfigurationRevision { get; }
    public long DiscoveryRevision { get; }
    public string CatalogSha256 { get; }
    public long? BaselineDiscoveryRevision { get; }
    public string? BaselineCatalogSha256 { get; }
    public DateTimeOffset EvaluatedAt { get; }
    public RemoteMcpScanState State { get; }
    public ImmutableArray<RemoteMcpScanCheckEvidence> Checks { get; }
    public string? ErrorCode { get; }

    public bool Equals(RemoteMcpCatalogScanEvidence? other) =>
        other is not null &&
        ContractVersion == other.ContractVersion &&
        ScannerId == other.ScannerId &&
        ScannerVersion == other.ScannerVersion &&
        ConfigurationRevision == other.ConfigurationRevision &&
        DiscoveryRevision == other.DiscoveryRevision &&
        CatalogSha256 == other.CatalogSha256 &&
        BaselineDiscoveryRevision == other.BaselineDiscoveryRevision &&
        BaselineCatalogSha256 == other.BaselineCatalogSha256 &&
        EvaluatedAt == other.EvaluatedAt &&
        State == other.State &&
        Checks.SequenceEqual(other.Checks) &&
        ErrorCode == other.ErrorCode;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ContractVersion);
        hash.Add(ScannerId, StringComparer.Ordinal);
        hash.Add(ScannerVersion, StringComparer.Ordinal);
        hash.Add(ConfigurationRevision);
        hash.Add(DiscoveryRevision);
        hash.Add(CatalogSha256, StringComparer.Ordinal);
        hash.Add(BaselineDiscoveryRevision);
        hash.Add(BaselineCatalogSha256, StringComparer.Ordinal);
        hash.Add(EvaluatedAt);
        hash.Add(State);
        foreach (var check in Checks)
            hash.Add(check);
        hash.Add(ErrorCode, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>One exact check requirement supplied by the current ToolPermissions policy.</summary>
public sealed record RemoteMcpScanRequirement(
    string CheckId,
    bool Required,
    int MaximumRiskScore);

/// <summary>Result of checking owner-issued scan evidence against one current Policy requirement.</summary>
public sealed record RemoteMcpScanAdmissionResult(
    bool Allowed,
    string? ReasonCode,
    ImmutableArray<RemoteMcpScanCheckEvidence> Checks);

/// <summary>Rules used to admit scan evidence at the owner review boundary.</summary>
public static class RemoteMcpScanAdmission
{
    public static RemoteMcpScanAdmissionResult Evaluate(
        RemoteMcpCatalogScanEvidence? evidence,
        RemoteMcpDiscoveryCatalogPin catalog,
        ImmutableArray<RemoteMcpScanRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (requirements.IsDefaultOrEmpty || requirements.Length > 16 ||
            requirements.Any(requirement => requirement is null ||
                !RemoteMcpCatalogScanEvidence.CheckIds.Contains(requirement.CheckId) ||
                requirement.MaximumRiskScore is < 0 or > 100) ||
            requirements.Select(requirement => requirement.CheckId).Distinct(StringComparer.Ordinal).Count() !=
                requirements.Length)
            throw new ArgumentException("The Remote MCP scan requirements are invalid.", nameof(requirements));
        if (evidence is null ||
            evidence.State != RemoteMcpScanState.Completed ||
            evidence.ContractVersion != RemoteMcpCatalogScanEvidence.CurrentVersion ||
            evidence.ScannerId != RemoteMcpCatalogScanEvidence.CurrentScannerId ||
            evidence.ScannerVersion != RemoteMcpCatalogScanEvidence.CurrentScannerVersion ||
            evidence.ConfigurationRevision != catalog.ConfigurationRevision ||
            evidence.DiscoveryRevision != catalog.DiscoveryRevision ||
            evidence.CatalogSha256 != catalog.CatalogSha256)
            return new(false, "remote_mcp_scan_evidence_stale", evidence?.Checks ?? []);

        foreach (var requirement in requirements.Where(item => item.Required))
        {
            var check = evidence.Checks.FirstOrDefault(item => item.CheckId == requirement.CheckId);
            if (check is null)
                return new(false, "remote_mcp_scan_required_check_missing", evidence.Checks);
            if (check.Outcome == RemoteMcpScanOutcome.Error)
                return new(false, "remote_mcp_scan_required_check_error", evidence.Checks);
            if (check.Outcome == RemoteMcpScanOutcome.Unknown)
                return new(false, "remote_mcp_scan_required_check_unknown", evidence.Checks);
            if (check.Outcome == RemoteMcpScanOutcome.Fail &&
                check.RiskScore > requirement.MaximumRiskScore)
                return new(false, "remote_mcp_scan_threshold_exceeded", evidence.Checks);
        }

        return new(true, null, evidence.Checks);
    }
}
