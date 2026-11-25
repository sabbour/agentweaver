using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

[JsonConverter(typeof(JsonStringEnumConverter<PromptContentScanInputKind>))]
public enum PromptContentScanInputKind
{
    ProjectCharter,
    SkillMarkdown,
    SkillInstructions,
    SkillResource
}

[JsonConverter(typeof(JsonStringEnumConverter<PromptContentScanOwnerKind>))]
public enum PromptContentScanOwnerKind
{
    ProjectConfiguration,
    SkillContent
}

[JsonConverter(typeof(JsonStringEnumConverter<PromptContentScanProvenance>))]
public enum PromptContentScanProvenance
{
    Authored,
    Git
}

[JsonConverter(typeof(JsonStringEnumConverter<PromptContentScanStatus>))]
public enum PromptContentScanStatus
{
    Completed,
    EvaluatorError
}

/// <summary>Owner revision and source location for one exact prompt or content input.</summary>
public sealed record PromptContentScanSourceV1(
    PromptContentScanOwnerKind OwnerKind,
    string OwnerId,
    long OwnerRevision,
    string RelativePath,
    PromptContentScanProvenance Provenance,
    string? SourceRevision,
    string? ResolvedCommitSha);

/// <summary>A missing defensive-language vector reported by the pinned evaluator.</summary>
public sealed record PromptContentScanFindingV1(string VectorId, string OwaspCategory);

/// <summary>Versioned, content-bound result from the local AGT .NET prompt-defense evaluator.</summary>
public sealed record PromptContentScanReportV1(
    int ContractVersion,
    PromptContentScanInputKind InputKind,
    PromptContentScanSourceV1 Source,
    string InputSha256,
    int InputByteCount,
    string ScannerPackage,
    string ScannerPackageVersion,
    string VectorSetRevision,
    string PolicyRevision,
    DateTimeOffset EvaluatedAt,
    PromptContentScanStatus Status,
    int? Score,
    string? Grade,
    int? CoveredVectorCount,
    int? VectorCount,
    bool? MeetsRecommendedThreshold,
    ImmutableArray<PromptContentScanFindingV1> Findings,
    string? ErrorCode);

/// <summary>Optional local charter scan outcome returned with a committed project configuration save.</summary>
public sealed record ProjectCharterScanResultV1(
    string AgentId,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    PromptContentScanReportV1? Report = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Code = null)
{
    public const string CompletedStatus = "completed";
    public const string UnavailableStatus = "unavailable";
    public const string UnavailableCode = "charter_scan_unavailable";

    public static ProjectCharterScanResultV1 Completed(string agentId, PromptContentScanReportV1 report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(report);
        return new(agentId, CompletedStatus, Report: report);
    }

    public static ProjectCharterScanResultV1 Unavailable(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return new(agentId, UnavailableStatus, Code: UnavailableCode);
    }
}

/// <summary>Validates report identity and its binding to the exact source bytes.</summary>
public static class PromptContentScanContract
{
    public const int CurrentVersion = 1;
    public const string ScannerPackage = "Microsoft.AgentGovernance";
    public const string ScannerPackageVersion = "4.0.0";
    public const string VectorSetRevision = "7bd5b44339db08e8b96bc2ecdb6fadf28f313450";
    public const string PolicyRevision = "agentweaver.prompt-defense.v1";
    public const int MaxProjectCharterCharacters = 20_000;
    public const int MaxProjectCharterBytes = MaxProjectCharterCharacters * 3;
    public const int MaxSkillScanSetBytes = SkillRuntimeContentContract.MaxProjectionBytes;
    public const int MaxSkillScanSetResources = SkillRuntimeContentContract.MaxResourceCount;
    public const int MaxSkillScanSetResourceBytes = SkillRuntimeContentContract.MaxTotalResourceBytes;
    public const int ExpectedVectorCount = 12;

    private static readonly ImmutableDictionary<string, string> VectorCategories =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["role-escape"] = "LLM01",
            ["instruction-override"] = "LLM01",
            ["data-leakage"] = "LLM07",
            ["output-manipulation"] = "LLM02",
            ["multilang-bypass"] = "LLM01",
            ["unicode-attack"] = "LLM01",
            ["context-overflow"] = "LLM01",
            ["indirect-injection"] = "LLM01",
            ["social-engineering"] = "LLM01",
            ["output-weaponization"] = "LLM02",
            ["abuse-prevention"] = "LLM06",
            ["input-validation"] = "LLM01"
        }.ToImmutableDictionary(StringComparer.Ordinal);

    public static int MaximumInputBytes(PromptContentScanInputKind inputKind) => inputKind switch
    {
        PromptContentScanInputKind.ProjectCharter => MaxProjectCharterBytes,
        PromptContentScanInputKind.SkillMarkdown => SkillRuntimeContentContract.MaxSkillMarkdownBytes,
        PromptContentScanInputKind.SkillInstructions => SkillRuntimeContentContract.MaxInstructionsBytes,
        PromptContentScanInputKind.SkillResource => SkillRuntimeContentContract.MaxResourceBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(inputKind))
    };

    public static string ComputeInputSha256(ReadOnlySpan<byte> exactInput) =>
        Convert.ToHexString(SHA256.HashData(exactInput)).ToLowerInvariant();

    public static bool IsKnownVector(string? vectorId, string? owaspCategory) =>
        vectorId is not null &&
        owaspCategory is not null &&
        VectorCategories.TryGetValue(vectorId, out var category) &&
        category == owaspCategory;

    public static PromptContentScanSourceV1 ValidateSource(
        PromptContentScanInputKind inputKind,
        PromptContentScanSourceV1 source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var expectedOwner = inputKind == PromptContentScanInputKind.ProjectCharter
            ? PromptContentScanOwnerKind.ProjectConfiguration
            : PromptContentScanOwnerKind.SkillContent;
        if (!Enum.IsDefined(inputKind) ||
            !Enum.IsDefined(source.OwnerKind) ||
            source.OwnerKind != expectedOwner ||
            !Enum.IsDefined(source.Provenance) ||
            !IsSafeText(source.OwnerId, 128) ||
            source.OwnerRevision <= 0 ||
            !IsSafeText(source.RelativePath, 1024) ||
            !IsCanonicalPath(source.RelativePath))
            throw new ArgumentException("The prompt scan source is invalid.", nameof(source));

        if (source.Provenance == PromptContentScanProvenance.Authored)
        {
            if (source.SourceRevision is not null || source.ResolvedCommitSha is not null)
                throw new ArgumentException("Authored prompt scan sources cannot claim a Git revision.", nameof(source));
        }
        else if (!IsSafeText(source.SourceRevision, 256) || !IsCommitSha(source.ResolvedCommitSha))
        {
            throw new ArgumentException("Git prompt scan sources require a revision and resolved commit.", nameof(source));
        }

        return source;
    }

    public static PromptContentScanReportV1 ValidateReport(
        PromptContentScanReportV1 report,
        PromptContentScanInputKind inputKind,
        PromptContentScanSourceV1 expectedSource,
        ReadOnlySpan<byte> exactInput)
    {
        ArgumentNullException.ThrowIfNull(report);
        ValidateSource(inputKind, expectedSource);
        if (report.ContractVersion != CurrentVersion ||
            report.InputKind != inputKind ||
            report.Source != expectedSource ||
            report.InputByteCount != exactInput.Length ||
            report.InputByteCount > MaximumInputBytes(inputKind) ||
            !string.Equals(report.InputSha256, ComputeInputSha256(exactInput), StringComparison.Ordinal) ||
            report.ScannerPackage != ScannerPackage ||
            report.ScannerPackageVersion != ScannerPackageVersion ||
            report.VectorSetRevision != VectorSetRevision ||
            report.PolicyRevision != PolicyRevision ||
            report.EvaluatedAt.Offset != TimeSpan.Zero ||
            !Enum.IsDefined(report.Status) ||
            report.Findings.IsDefault)
            throw new ArgumentException("The prompt scan report does not match its contract or input.", nameof(report));

        if (report.Status == PromptContentScanStatus.Completed)
        {
            if (report.Score is null or < 0 or > 100 ||
                report.Grade is not ("A" or "B" or "C" or "D" or "F") ||
                report.CoveredVectorCount is < 0 or > ExpectedVectorCount ||
                report.VectorCount != ExpectedVectorCount ||
                report.Findings.Length != ExpectedVectorCount - report.CoveredVectorCount ||
                report.MeetsRecommendedThreshold != (report.Score >= 70) ||
                report.ErrorCode is not null ||
                !ValidGrade(report.Score.Value, report.Grade) ||
                report.Findings.Any(finding =>
                    finding is null ||
                    !IsKnownVector(finding.VectorId, finding.OwaspCategory)) ||
                report.Findings.Select(finding => finding.VectorId).Distinct(StringComparer.Ordinal).Count() !=
                    report.Findings.Length)
                throw new ArgumentException("The completed prompt scan report is invalid.", nameof(report));
        }
        else if (report.Score is not null ||
            report.Grade is not null ||
            report.CoveredVectorCount is not null ||
            report.VectorCount is not null ||
            report.MeetsRecommendedThreshold is not null ||
            !report.Findings.IsEmpty ||
            report.ErrorCode is not ("evaluator_timeout" or "input_hash_mismatch" or "unsupported_vector_set"))
        {
            throw new ArgumentException("The failed prompt scan report is invalid.", nameof(report));
        }

        return report;
    }

    private static bool IsCanonicalPath(string path)
    {
        try
        {
            return SkillRuntimeContentContract.NormalizeResourcePath(path) == path;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static bool IsCommitSha(string? value) =>
        value is { Length: 40 or 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidGrade(int score, string grade) => grade == (score switch
    {
        >= 90 => "A",
        >= 70 => "B",
        >= 50 => "C",
        >= 30 => "D",
        _ => "F"
    });
}
