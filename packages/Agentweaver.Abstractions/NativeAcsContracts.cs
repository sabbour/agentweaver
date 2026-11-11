using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

/// <summary>One relative file that belongs to a complete native ACS artifact.</summary>
public sealed record NativeAcsArtifactFile(string RelativePath, ImmutableArray<byte> Bytes);

/// <summary>The exact manifest and every relative module or data file, bound by one content hash.</summary>
public sealed record NativeAcsArtifact(
    ImmutableArray<byte> ManifestBytes,
    ImmutableArray<NativeAcsArtifactFile> Files,
    string ContentSha256);

/// <summary>A verified Entra human identity. Callers must not infer it from another claim.</summary>
public sealed record NativeAcsVerifiedHumanIdentity(string TenantId, string ObjectId);

/// <summary>
/// Bounded runtime facts for native ACS evaluation. A null human identity means that verified
/// human facts are unavailable; callers must not infer them from Broker subject, actor ID, or UPN.
/// </summary>
public sealed record NativeAcsEvaluationSnapshot(
    SessionIdentity Identity,
    string TenantId,
    string AgentId,
    string ActionId,
    NativeAcsVerifiedHumanIdentity? VerifiedHumanIdentity = null);

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
