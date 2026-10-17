using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Abstractions;

/// <summary>Version and size limits for a Source Control-produced run capture package.</summary>
public static class ProducedRunCaptureLimits
{
    /// <summary>Wire and package format version currently accepted by the owner.</summary>
    public const int ContractVersion = 2;
    public const int MaximumFiles = 10_000;
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    public const int MaximumTotalFileBytes = 64 * 1024 * 1024;
    public const int MaximumManifestBytes = 32 * 1024 * 1024;
    public const int MaximumDiffBytes = 8 * 1024 * 1024;
    public const int PackageHeaderBytes = 12;
    public const int PackageFileLengthBytes = 8;
    public const int MaximumPackageBytes =
        MaximumTotalFileBytes + MaximumFiles * PackageFileLengthBytes + PackageHeaderBytes;
    public const int DocumentHeaderBytes = 24;
    public const int MaximumDocumentBytes =
        DocumentHeaderBytes + MaximumManifestBytes + MaximumDiffBytes + MaximumPackageBytes;
}

/// <summary>Stable capture key paired with the session journal event that records it.</summary>
public sealed record ProducedRunCaptureIdentity(string CaptureId, Guid EventId);

/// <summary>
/// Source Control proof that one accepted run produced a specific repository output.
/// Hashes, workspace incarnation, resource generation, and selection pin bind the bytes
/// to the exact run and prevent reuse after workspace replacement.
/// </summary>
public sealed record ProducedRunCaptureProof(
    int ContractVersion,
    SessionIdentity Identity,
    string CaptureId,
    Guid EventId,
    string ActorIssuer,
    string ActorSubject,
    string TenantId,
    string SourceControlPinId,
    string AcceptedSelectionHash,
    string WorkspaceId,
    Guid WorkspaceIncarnationId,
    string BranchName,
    string RepositoryId,
    long ResourceGeneration,
    string BaseSha,
    string OutputTreeSha,
    string ManifestSha256,
    long ManifestByteLength,
    string PatchSha256,
    long PatchByteLength,
    string PackageSha256,
    long PackageByteLength,
    DateTimeOffset CapturedAt);

/// <summary>Session event payload that points to the immutable captured package object.</summary>
public sealed record ProducedRunCaptureSessionPayload(
    ProducedRunCaptureProof Capture,
    SessionObjectReference Package) : SessionEventPayload;

/// <summary>Journal position and proof for a capture admitted by the session owner.</summary>
public sealed record ProducedRunCaptureJournalEntry(
    ProducedRunCaptureProof Capture,
    long Position);

/// <summary>Append acknowledgment indicating whether the capture was already recorded.</summary>
public sealed record ProducedRunCaptureAcknowledgment(
    ProducedRunCaptureJournalEntry Entry,
    bool IsDuplicate);

/// <summary>Capture proof plus package bytes returned by the trusted content reader.</summary>
public sealed record ProducedRunCaptureContentResult(
    ProducedRunCaptureJournalEntry Entry,
    byte[] PackageBytes)
{
    /// <summary>Prevents captured source content from appearing in default log formatting.</summary>
    public override string ToString() => nameof(ProducedRunCaptureContentResult) + " [REDACTED]";
}

/// <summary>Validates capture proof, deterministic identity, and the owner-held package reference.</summary>
public static class ProducedRunCaptureContractValidation
{
    /// <summary>Purpose label used when storing produced output packages.</summary>
    public const string ObjectPurpose = "source-control-produced-output";
    private const string CaptureIdPrefix = "sc-output-";

    /// <summary>Rejects captures that are malformed, oversized, unpinned, or outside the contract version.</summary>
    /// <exception cref="ArgumentException">A capture field violates its format, size, identity, or generation invariant.</exception>
    public static void Validate(ProducedRunCaptureProof capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.ContractVersion != ProducedRunCaptureLimits.ContractVersion ||
            !IsCaptureId(capture.CaptureId) ||
            capture.EventId == Guid.Empty ||
            capture.EventId != EventIdFromCaptureId(capture.CaptureId) ||
            !IsBoundedText(capture.ActorIssuer, 512) ||
            !IsBoundedText(capture.ActorSubject, 256) ||
            !IsBoundedText(capture.TenantId, 256) ||
            !IsIdentifier(capture.SourceControlPinId, 128) ||
            !IsHash(capture.AcceptedSelectionHash, allowUppercase: true) ||
            !IsWorkspaceId(capture.WorkspaceId) ||
            capture.WorkspaceIncarnationId == Guid.Empty ||
            !IsBoundedText(capture.BranchName, 255) ||
            !IsIdentifier(capture.RepositoryId, 256) ||
            capture.ResourceGeneration < 1 ||
            !IsGitObjectId(capture.BaseSha) ||
            !IsGitObjectId(capture.OutputTreeSha) ||
            !IsHash(capture.ManifestSha256) ||
            capture.ManifestByteLength is <= 0 or > ProducedRunCaptureLimits.MaximumManifestBytes ||
            !IsHash(capture.PatchSha256) ||
            capture.PatchByteLength is < 0 or > ProducedRunCaptureLimits.MaximumDiffBytes ||
            !IsHash(capture.PackageSha256) ||
            capture.PackageByteLength is < ProducedRunCaptureLimits.PackageHeaderBytes or
                > ProducedRunCaptureLimits.MaximumPackageBytes ||
            capture.CapturedAt == default)
            throw new ArgumentException("The produced-run capture proof is invalid.", nameof(capture));
    }

    /// <summary>Validates the proof and requires the referenced package to be its exact expected object.</summary>
    /// <exception cref="ArgumentException">The payload or package reference does not match the proof.</exception>
    public static void Validate(ProducedRunCaptureSessionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Validate(payload.Capture);
        ValidatePackageReference(payload.Package, payload.Capture);
    }

    /// <summary>Checks the deterministic key, purpose, and byte length for a capture package.</summary>
    /// <exception cref="ArgumentException">The reference does not identify the exact package for this capture.</exception>
    public static void ValidatePackageReference(
        SessionObjectReference package,
        ProducedRunCaptureProof capture)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(capture);
        _ = new ObjectKey(package.Key.Value);
        var expected = CreatePackageReference(capture);
        if (package.Material is not null ||
            package.Key != expected.Key ||
            package.Purpose != ObjectPurpose ||
            package.ByteLength != capture.PackageByteLength)
            throw new ArgumentException("The produced-run capture object reference is invalid.", nameof(package));
    }

    /// <summary>Builds the tenant- and run-scoped object reference for a validated capture proof.</summary>
    public static SessionObjectReference CreatePackageReference(ProducedRunCaptureProof capture)
    {
        Validate(capture);
        var scopeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{capture.TenantId}\0{capture.Identity.ProjectId}\0{capture.Identity.RunId}\0{capture.Identity.SessionId}")));
        return new SessionObjectReference(
            new ObjectKey(
                $"source-control-produced-output/v2/{scopeHash}/{capture.CaptureId}/{capture.PackageSha256}"),
            ObjectPurpose,
            capture.PackageByteLength);
    }

    /// <summary>Derives a stable capture and event identity from the run, workspace, and output pins.</summary>
    public static ProducedRunCaptureIdentity CreateIdentity(
        SessionIdentity identity,
        string sourceControlPinId,
        string acceptedSelectionHash,
        string workspaceId,
        Guid workspaceIncarnationId,
        string branchName,
        string repositoryId,
        long resourceGeneration,
        string baseSha,
        string outputTreeSha,
        string manifestSha256)
    {
        var material = string.Join('\0',
            identity.ProjectId,
            identity.RunId,
            identity.SessionId,
            sourceControlPinId,
            acceptedSelectionHash,
            workspaceId,
            workspaceIncarnationId.ToString("N"),
            branchName,
            repositoryId,
            resourceGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            baseSha.ToLowerInvariant(),
            outputTreeSha.ToLowerInvariant(),
            manifestSha256);
        var suffix = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)).AsSpan(0, 16));
        var captureId = CaptureIdPrefix + suffix;
        return new(captureId, Guid.ParseExact(suffix, "N"));
    }

    /// <summary>Reads the canonical event UUID encoded in a capture ID, or an empty UUID if invalid.</summary>
    private static Guid EventIdFromCaptureId(string captureId) =>
        Guid.TryParseExact(captureId[CaptureIdPrefix.Length..], "N", out var eventId)
            ? eventId
            : Guid.Empty;

    private static bool IsCaptureId(string? value) =>
        value is { Length: 42 } &&
        value.StartsWith(CaptureIdPrefix, StringComparison.Ordinal) &&
        value[CaptureIdPrefix.Length..].All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsWorkspaceId(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsIdentifier(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':' or '/');

    private static bool IsBoundedText(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static bool IsGitObjectId(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    /// <summary>Checks canonical SHA-256 hex, optionally accepting uppercase for legacy selection hashes.</summary>
    private static bool IsHash(string? value, bool allowUppercase = false) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' ||
            allowUppercase && character is >= 'A' and <= 'F');
}
