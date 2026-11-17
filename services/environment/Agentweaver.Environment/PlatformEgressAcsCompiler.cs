using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Policy.Acs;

namespace Agentweaver.Environment;

/// <summary>Compiles the closed native finite-network profile without provider or store effects.</summary>
public static class PlatformEgressAcsCompiler
{
    public const string CompilerVersion = "finite-network-v1";

    public static PlatformEgressCompilationResult Compile(
        NativeAcsArtifact artifact,
        string sourceId,
        string sourceRevision,
        string admissionCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (string.IsNullOrWhiteSpace(sourceId) ||
            string.IsNullOrWhiteSpace(sourceRevision) ||
            string.IsNullOrWhiteSpace(admissionCheckpoint))
            return Failed("source_metadata_invalid");

        var candidateResult = CompileCandidate(artifact, sourceId, sourceRevision);
        if (!candidateResult.IsSuccess)
            return Failed(candidateResult.Failure!.Code);

        var candidate = candidateResult.Candidate!;
        var destinations = candidate.ApprovedDestinations
            .Concat(candidate.SystemDestinations)
            // Canonical ordering keeps the compiled snapshot stable when input order changes.
            .OrderBy(destination => destination.Host, StringComparer.Ordinal)
            .ThenBy(destination => destination.Port)
            .ToImmutableArray();
        var snapshot = new PlatformEgressPolicySnapshot(
            candidate.SourceId,
            candidate.SourceRevision,
            candidate.SourceHash,
            candidate.CompilerVersion,
            admissionCheckpoint,
            candidate.Mode,
            destinations);
        return snapshot.Validate() is null
            ? new PlatformEgressCompilationResult(snapshot, null)
            : Failed("compiled_snapshot_invalid");
    }

    internal static PlatformEgressCandidateCompilationResult CompileCandidate(
        NativeAcsArtifact artifact,
        string sourceId,
        string sourceRevision)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(sourceRevision))
            return CandidateFailed("source_metadata_invalid");

        var result = NativeAcsFiniteNetworkProfile.Validate(artifact);
        if (!result.IsValid || result.Policy is not { } policy)
            return CandidateFailed(result.Diagnostics.FirstOrDefault()?.Code ?? "network_profile_invalid");

        var candidate = CreateCandidate(
            artifact,
            sourceId,
            sourceRevision,
            policy.Mode,
            policy.ApprovedDestinations,
            policy.SystemDestinations);

        return new PlatformEgressCandidateCompilationResult(candidate, null);
    }

    internal static PlatformEgressCandidateCompilationResult CompileComposedCandidate(
        NativeAcsArtifact artifact,
        string sourceId,
        string sourceRevision)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(sourceRevision))
            return CandidateFailed("source_metadata_invalid");

        var inspection = NativeAcsComposableDomainsProfile.Inspect(artifact);
        if (!inspection.IsValid || inspection.NetworkPolicy is not { } policy)
            return CandidateFailed(inspection.Diagnostics.FirstOrDefault()?.Code ?? "composed_profile_invalid");

        var candidate = CreateCandidate(
            artifact,
            sourceId,
            sourceRevision,
            policy.Mode,
            policy.ApprovedDestinations,
            policy.SystemDestinations);
        return new PlatformEgressCandidateCompilationResult(candidate, null);
    }

    internal static PlatformEgressCandidateCompilationResult CompileSystemOnlyCandidate(
        NativeAcsArtifact artifact,
        string sourceId,
        string sourceRevision)
    {
        var result = CompileCandidate(artifact, sourceId, sourceRevision);
        if (!result.IsSuccess)
            return result;

        var candidate = result.Candidate!;
        return new PlatformEgressCandidateCompilationResult(
            candidate with
            {
                Mode = PlatformEgressMode.Allowlist,
                ApprovedDestinations = []
            },
            null);
    }

    internal static PlatformEgressCandidateCompilationResult CompileComposedSystemOnlyCandidate(
        NativeAcsArtifact artifact,
        string sourceId,
        string sourceRevision)
    {
        var result = CompileComposedCandidate(artifact, sourceId, sourceRevision);
        if (!result.IsSuccess)
            return result;

        var candidate = result.Candidate!;
        return new PlatformEgressCandidateCompilationResult(
            candidate with
            {
                Mode = PlatformEgressMode.Allowlist,
                ApprovedDestinations = []
            },
            null);
    }

    private static PlatformEgressCompilationCandidate CreateCandidate(
        NativeAcsArtifact artifact,
        string sourceId,
        string sourceRevision,
        PlatformEgressMode mode,
        ImmutableArray<PlatformEgressDestination> approvedDestinations,
        ImmutableArray<PlatformEgressDestination> systemDestinations) =>
        new(
            sourceId,
            sourceRevision,
            artifact.ContentSha256,
            CompilerVersion,
            mode,
            approvedDestinations,
            systemDestinations);

    private static PlatformEgressCompilationResult Failed(string code) =>
        new(null, new PlatformEgressCompilationFailure(code));

    private static PlatformEgressCandidateCompilationResult CandidateFailed(string code) =>
        new(null, new PlatformEgressCompilationFailure(code));
}

public sealed record PlatformEgressCompilationFailure(string Code);

internal sealed record PlatformEgressCompilationCandidate(
    string SourceId,
    string SourceRevision,
    string SourceHash,
    string CompilerVersion,
    PlatformEgressMode Mode,
    ImmutableArray<PlatformEgressDestination> ApprovedDestinations,
    ImmutableArray<PlatformEgressDestination> SystemDestinations);

internal sealed record PlatformEgressCandidateCompilationResult(
    PlatformEgressCompilationCandidate? Candidate,
    PlatformEgressCompilationFailure? Failure)
{
    public bool IsSuccess => Candidate is not null && Failure is null;
}

public sealed record PlatformEgressCompilationResult(
    PlatformEgressPolicySnapshot? Snapshot,
    PlatformEgressCompilationFailure? Failure)
{
    public bool IsSuccess => Snapshot is not null && Failure is null;
}
