using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Abstractions;

/// <summary>One relative file that belongs to a complete native ACS artifact.</summary>
public sealed record NativeAcsArtifactFile(string RelativePath, ImmutableArray<byte> Bytes);

/// <summary>The exact manifest and every relative module or data file, bound by one content hash.</summary>
public sealed record NativeAcsArtifact(
    ImmutableArray<byte> ManifestBytes,
    ImmutableArray<NativeAcsArtifactFile> Files,
    string ContentSha256);

/// <summary>Computes the canonical content hash for one complete native ACS artifact.</summary>
public static class NativeAcsArtifactHash
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Compute(NativeAcsArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("agentweaver-native-acs-artifact-v1\0"u8);
        AppendField(hash, artifact.ManifestBytes.IsDefault ? [] : artifact.ManifestBytes.AsSpan());

        var files = artifact.Files.IsDefault
            ? Enumerable.Empty<NativeAcsArtifactFile>()
            : artifact.Files.OrderBy(file => file?.RelativePath, StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file is null || string.IsNullOrEmpty(file.RelativePath))
                throw new ArgumentException("Every ACS artifact file needs a relative path.", nameof(artifact));
            AppendField(hash, StrictUtf8.GetBytes(file.RelativePath));
            AppendField(hash, file.Bytes.IsDefault ? [] : file.Bytes.AsSpan());
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendField(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

/// <summary>A verified Entra human identity. Callers must not infer it from another claim.</summary>
public sealed record NativeAcsVerifiedHumanIdentity(string TenantId, string ObjectId);

/// <summary>One server-owned, typed destination fact for bounded network evaluation.</summary>
public sealed record NativeAcsNetworkDestinationFacts(string Kind, string Value);

/// <summary>Bounded facts for one network permission check.</summary>
public sealed record NativeAcsNetworkEvaluationFacts(
    string CheckKind,
    string Protocol,
    int Port,
    NativeAcsNetworkDestinationFacts Destination);

/// <summary>
/// Bounded runtime facts for native ACS evaluation. A null human identity means that verified
/// human facts are unavailable; callers must not infer them from Broker subject, actor ID, or UPN.
/// </summary>
public sealed record NativeAcsEvaluationSnapshot(
    SessionIdentity Identity,
    string TenantId,
    string AgentId,
    string ActionId,
    NativeAcsVerifiedHumanIdentity? VerifiedHumanIdentity = null,
    NativeAcsNetworkEvaluationFacts? Network = null);

/// <summary>A bounded validation diagnostic. It does not include source snippets.</summary>
public sealed record NativeAcsDiagnostic(
    string Code,
    string Message,
    string? RelativePath = null,
    int? Line = null,
    int? Column = null);

/// <summary>Validation result for one complete, content-pinned ACS artifact.</summary>
public sealed record NativeAcsValidationResult(
    bool IsValid,
    ImmutableArray<NativeAcsDiagnostic> Diagnostics);

/// <summary>Native ACS decision bound to the exact artifact hash that was evaluated.</summary>
public sealed record NativeAcsEvaluationResult(
    PolicyEvaluationOutcome Outcome,
    PolicyEvaluationReasonCode ReasonCode,
    string ArtifactSha256,
    string? DiagnosticCode = null);
