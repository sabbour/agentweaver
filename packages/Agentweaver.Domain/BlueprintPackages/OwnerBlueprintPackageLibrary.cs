using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Agentweaver.Domain.BlueprintPackages;

/// <summary>Authenticated server identity used to scope every package-library operation.</summary>
public interface IAuthenticatedOwnerContext
{
    string OwnerId { get; }
}

/// <summary>Structured, descriptive acquisition information. It is not a trust assertion.</summary>
public sealed record BlueprintPackageAcquisition(
    string Source,
    string? Producer = null,
    string? Repository = null,
    string? Revision = null,
    DateTimeOffset? AcquiredAt = null,
    string? RequestedRef = null);

/// <summary>Raw bytes of one validated package payload.</summary>
public sealed record BlueprintPackagePayload(string Path, byte[] Bytes);

/// <summary>Canonical payload-set hashing shared by package validation and owner-library persistence.</summary>
public static class BlueprintPackagePayloadSetDigest
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Prefix = "blueprint-package-payload-set-v1\0"u8.ToArray();

    /// <summary>Hashes exact payload bytes under ordinal-sorted canonical paths using the package v1 framing.</summary>
    public static string Calculate(IEnumerable<BlueprintPackagePayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        var canonicalPayloads = payloads.ToArray();
        foreach (var payload in canonicalPayloads)
        {
            ArgumentNullException.ThrowIfNull(payload);
            ArgumentNullException.ThrowIfNull(payload.Path);
            ArgumentNullException.ThrowIfNull(payload.Bytes);
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Prefix);
        foreach (var payload in canonicalPayloads.OrderBy(x => x.Path, StringComparer.Ordinal))
        {
            AppendLengthPrefixed(hash, EncodePathUtf8(payload.Path));
            AppendLengthPrefixed(hash, payload.Bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendLengthPrefixed(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static byte[] EncodePathUtf8(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return StrictUtf8.GetBytes(path);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Payload paths must be valid Unicode.", nameof(path), exception);
        }
    }
}

/// <summary>
/// Validated package material supplied by an acquisition boundary. Owner identity is deliberately
/// absent: it is always taken from <see cref="IAuthenticatedOwnerContext"/>.
/// </summary>
public sealed record BlueprintPackageWrite(
    string PackageId,
    string CanonicalVersion,
    byte[] RawManifest,
    IReadOnlyList<BlueprintPackagePayload> Payloads,
    string ContentDigest,
    string PayloadSetDigest,
    string RawManifestSha256,
    string? ContainerSha256,
    IReadOnlyList<BlueprintPackageAcquisition> Acquisitions);

/// <summary>An immutable persisted package version, including exact acquired bytes.</summary>
public sealed record OwnerBlueprintPackageVersion(
    string PackageId,
    string CanonicalVersion,
    byte[] RawManifest,
    IReadOnlyList<BlueprintPackagePayload> Payloads,
    string ContentDigest,
    string PayloadSetDigest,
    string RawManifestSha256,
    string? ContainerSha256,
    IReadOnlyList<BlueprintPackageAcquisition> Acquisitions,
    DateTimeOffset CreatedAt);

/// <summary>Owner-private package-library entry with its immutable versions.</summary>
public sealed record OwnerBlueprintPackageEntry(
    string PackageId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OwnerBlueprintPackageVersion> Versions);

public enum BlueprintPackagePersistDisposition
{
    Created,
    Idempotent,
    ImmutableConflict,
}

public sealed record BlueprintPackagePersistResult(
    BlueprintPackagePersistDisposition Disposition,
    OwnerBlueprintPackageVersion? Version = null);

/// <summary>Owner-scoped persistence API for the package library.</summary>
public interface IOwnerBlueprintPackageLibrary
{
    Task<BlueprintPackagePersistResult> PersistAsync(BlueprintPackageWrite package, CancellationToken ct = default);
    Task<OwnerBlueprintPackageVersion?> GetVersionAsync(string packageId, string canonicalVersion, CancellationToken ct = default);
    Task<IReadOnlyList<OwnerBlueprintPackageEntry>> ListAsync(CancellationToken ct = default);
    Task<bool> DeletePackageAsync(string packageId, CancellationToken ct = default);
}

/// <summary>Fail-closed structural and byte bounds used before persistence opens a database connection.</summary>
public static class BlueprintPackageLibraryLimits
{
    public const int MaximumPackageIdLength = 128;
    public const int MaximumVersionLength = 4_096;
    public const int MaximumProvenanceRecords = 16;
    public const int MaximumProvenanceFieldLength = 2_048;
    public const int MaximumStoredPayloadCount = 256;
    public const int MaximumStoredManifestBytes = 1_048_576;
    public const int MaximumStoredPayloadBytes = 1_048_576;
    public const int MaximumStoredPayloadSetBytes = 16_777_216;

    private static readonly Regex Digest = new(@"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Sensitive = new(@"(?i)(token|secret|password|credential|authorization\s*:|://[^/\s:@]+:[^/\s@]+@)", RegexOptions.CultureInvariant);

    public static void Validate(BlueprintPackageWrite package)
    {
        ArgumentNullException.ThrowIfNull(package);
        Require(package.PackageId, MaximumPackageIdLength, nameof(package.PackageId));
        Require(package.CanonicalVersion, MaximumVersionLength, nameof(package.CanonicalVersion));
        if (package.RawManifest is null || package.RawManifest.Length > MaximumStoredManifestBytes)
            throw new ArgumentOutOfRangeException(nameof(package.RawManifest), "Raw manifest exceeds the storage limit.");
        if (package.Payloads is null || package.Payloads.Count > MaximumStoredPayloadCount)
            throw new ArgumentOutOfRangeException(nameof(package.Payloads), "Payload count exceeds the storage limit.");
        if (package.Acquisitions is null || package.Acquisitions.Count > MaximumProvenanceRecords)
            throw new ArgumentOutOfRangeException(nameof(package.Acquisitions), "Acquisition count exceeds the storage limit.");
        ValidateDigest(package.ContentDigest, nameof(package.ContentDigest));
        ValidateDigest(package.PayloadSetDigest, nameof(package.PayloadSetDigest));
        ValidateDigest(package.RawManifestSha256, nameof(package.RawManifestSha256));
        if (package.ContainerSha256 is not null) ValidateDigest(package.ContainerSha256, nameof(package.ContainerSha256));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(package.RawManifestSha256),
                SHA256.HashData(package.RawManifest)))
            throw new ArgumentException("Raw manifest digest does not match the supplied bytes.", nameof(package.RawManifestSha256));

        long total = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var payload in package.Payloads)
        {
            if (payload is null || payload.Bytes is null || string.IsNullOrWhiteSpace(payload.Path) ||
                !paths.Add(payload.Path) || payload.Bytes.Length > MaximumStoredPayloadBytes)
                throw new ArgumentException("Payloads are invalid or exceed storage limits.", nameof(package.Payloads));
            total += payload.Bytes.Length;
            if (total > MaximumStoredPayloadSetBytes)
                throw new ArgumentOutOfRangeException(nameof(package.Payloads), "Payload set exceeds the storage limit.");
        }
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(package.PayloadSetDigest),
                Convert.FromHexString(BlueprintPackagePayloadSetDigest.Calculate(package.Payloads))))
            throw new ArgumentException("Payload set digest does not match the supplied bytes.", nameof(package.PayloadSetDigest));
        foreach (var acquisition in package.Acquisitions)
        {
            if (acquisition is null) throw new ArgumentException("Acquisition records cannot be null.", nameof(package.Acquisitions));
            Require(acquisition.Source, MaximumProvenanceFieldLength, nameof(acquisition.Source));
            ValidateProvenance(acquisition.Source);
            ValidateProvenance(acquisition.Producer);
            ValidateProvenance(acquisition.Repository);
            ValidateProvenance(acquisition.Revision);
            ValidateProvenance(acquisition.RequestedRef);
        }
    }

    /// <summary>SemVer ordering that compares arbitrary-length numeric identifiers without truncation.</summary>
    public static class CanonicalSemanticVersion
    {
        private static readonly Regex Version = new(
            @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
            RegexOptions.CultureInvariant);

        public static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value) || !Version.IsMatch(value))
                throw new ArgumentException("Version must be canonical SemVer 2.0.0.", nameof(value));
            var prerelease = value.Split('+', 2)[0].Split('-', 2);
            foreach (var identifier in prerelease.Length == 2 ? prerelease[1].Split('.') : [])
                if (identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit))
                    throw new ArgumentException("Numeric prerelease identifiers cannot contain leading zeroes.", nameof(value));
            return value;
        }

        public static int Compare(string left, string right)
        {
            var l = Parse(Normalize(left));
            var r = Parse(Normalize(right));
            for (var i = 0; i < 3; i++)
            {
                var result = CompareNumber(l.Core[i], r.Core[i]);
                if (result != 0) return result;
            }
            if (l.PreRelease.Length == 0 || r.PreRelease.Length == 0)
                return l.PreRelease.Length == r.PreRelease.Length ? 0 : l.PreRelease.Length == 0 ? 1 : -1;
            for (var i = 0; i < Math.Min(l.PreRelease.Length, r.PreRelease.Length); i++)
            {
                var ln = l.PreRelease[i].All(char.IsAsciiDigit);
                var rn = r.PreRelease[i].All(char.IsAsciiDigit);
                var result = ln && rn ? CompareNumber(l.PreRelease[i], r.PreRelease[i])
                    : ln != rn ? (ln ? -1 : 1) : string.CompareOrdinal(l.PreRelease[i], r.PreRelease[i]);
                if (result != 0) return result;
            }
            return l.PreRelease.Length.CompareTo(r.PreRelease.Length);
        }

        private static (string[] Core, string[] PreRelease) Parse(string value)
        {
            var noBuild = value.Split('+', 2)[0];
            var pieces = noBuild.Split('-', 2);
            return (pieces[0].Split('.'), pieces.Length == 1 ? [] : pieces[1].Split('.'));
        }

        private static int CompareNumber(string left, string right) =>
            left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
    }

    private static void Require(string? value, int limit, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit)
            throw new ArgumentException("Value is required and exceeds its storage limit.", name);
    }

    private static void ValidateDigest(string value, string name)
    {
        if (value is null || !Digest.IsMatch(value))
            throw new ArgumentException("Digest must be a lowercase SHA-256 hex value.", name);
    }

    private static void ValidateProvenance(string? value)
    {
        if (value is not null && (value.Length > MaximumProvenanceFieldLength || Sensitive.IsMatch(value)))
            throw new ArgumentException("Provenance is oversized or contains credential-like content.");
    }
}
