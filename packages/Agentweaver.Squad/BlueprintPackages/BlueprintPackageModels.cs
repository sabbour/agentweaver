using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Squad.BlueprintPackages;

/// <summary>Immutable input to the definitions-only Blueprint package contract.</summary>
public sealed class BlueprintPackageSource
{
    private readonly ImmutableDictionary<string, ImmutableArray<byte>> _payloads;
    private readonly ImmutableArray<byte> _rawManifest;

    public BlueprintPackageSource(
        ReadOnlySpan<byte> rawManifest,
        IEnumerable<KeyValuePair<string, byte[]>> payloads,
        string? containerSha256 = null)
    {
        _rawManifest = ImmutableArray.CreateRange(rawManifest.ToArray());
        _payloads = payloads.ToImmutableDictionary(
            pair => pair.Key,
            pair => ImmutableArray.CreateRange(pair.Value.ToArray()),
            StringComparer.Ordinal);
        ContainerSha256 = containerSha256;
    }

    /// <summary>Manifest bytes exactly as supplied. They are never parsed and re-serialized for hashing.</summary>
    public ImmutableArray<byte> RawManifest => _rawManifest;
    /// <summary>Payload files, keyed by their package-relative path.</summary>
    public IReadOnlyDictionary<string, ImmutableArray<byte>> Payloads => _payloads;
    /// <summary>Optional digest calculated by a container transport; no container format is defined here.</summary>
    public string? ContainerSha256 { get; }
}

/// <summary>A validated package. This layer deliberately does not read or write archives or persistence stores.</summary>
public sealed record BlueprintPackage(
    BlueprintPackageManifest Manifest,
    ImmutableArray<byte> RawManifest,
    BlueprintPackageDigests Digests);

/// <summary>Version-one manifest model.</summary>
public sealed record BlueprintPackageManifest(
    string SchemaVersion,
    string PackageId,
    SemanticVersion Version,
    ImmutableArray<BlueprintPackageDefinition> Definitions,
    BlueprintPackageCompatibility? Compatibility,
    BlueprintPackageProvenance? Provenance,
    string? ContainerSha256);

/// <summary>One payload file covered by the manifest inventory.</summary>
public sealed record BlueprintPackageDefinition(
    BlueprintPackageDefinitionKind Kind,
    string Id,
    string Path,
    long Size,
    string Sha256);

public enum BlueprintPackageDefinitionKind
{
    Blueprint,
    Role,
    Workflow,
    Skill,
}

/// <summary>Inclusive Agentweaver semantic-version compatibility range.</summary>
public sealed record BlueprintPackageCompatibility(
    SemanticVersion MinimumAgentweaverVersion,
    SemanticVersion? MaximumAgentweaverVersion);

/// <summary>Bounded, structured origin information. It is descriptive and excluded from the semantic digest.</summary>
public sealed record BlueprintPackageProvenance(
    string Source,
    string? Producer,
    string? Repository,
    string? Revision,
    string? CreatedAt);

/// <summary>All package identities calculated without reformatting raw manifest bytes.</summary>
public sealed record BlueprintPackageDigests(
    string SemanticSha256,
    string PayloadSetSha256,
    string RawManifestSha256,
    string? ContainerSha256);

/// <summary>Validation outcome; diagnostics are stable, bounded strings suitable for callers to surface.</summary>
public sealed class BlueprintPackageValidationResult
{
    private BlueprintPackageValidationResult(BlueprintPackage? package, ImmutableArray<string> errors)
    {
        Package = package;
        Errors = errors;
    }

    public BlueprintPackage? Package { get; }
    public ImmutableArray<string> Errors { get; }
    public bool IsValid => Package is not null && Errors.Length == 0;

    internal static BlueprintPackageValidationResult Success(BlueprintPackage package) => new(package, []);
    internal static BlueprintPackageValidationResult Failure(IEnumerable<string> errors) =>
        new(null, errors.Take(BlueprintPackageLimits.MaximumErrors).ToImmutableArray());
}

/// <summary>Hard limits shared by the parser, validation rules, and JSON schema.</summary>
public static class BlueprintPackageLimits
{
    public const int MaximumManifestBytes = 1_048_576;
    public const int MaximumDefinitions = 256;
    public const int MaximumPayloadBytes = 1_048_576;
    public const int MaximumTotalPayloadBytes = 16_777_216;
    public const int MaximumPathLength = 240;
    public const int MaximumIdentifierLength = 64;
    /// <summary>Maximum lexical length of a JSON number before exact canonicalization.</summary>
    public const int MaximumCanonicalNumberTokenLength = 4_096;
    public const int MaximumErrors = 32;
}

/// <summary>SHA-256 utilities used by the package contract.</summary>
public static class BlueprintPackageHash
{
    public static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string Sha256Utf8(string value) => Sha256(Encoding.UTF8.GetBytes(value));
}
