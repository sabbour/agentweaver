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
        var snapshot = new PlatformEgressPolicySnapshot(
            candidate.SourceId,
            candidate.SourceRevision,
            candidate.SourceHash,
            candidate.CompilerVersion,
            admissionCheckpoint,
            candidate.Mode,
            candidate.Destinations);
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

        var destinations = policy.ApprovedDestinations
            .Concat(policy.SystemDestinations)
            // Canonical ordering keeps the compiled snapshot stable when input order changes.
            .OrderBy(destination => destination.Host, StringComparer.Ordinal)
            .ThenBy(destination => destination.Port)
            .ToImmutableArray();
        var candidate = new PlatformEgressCompilationCandidate(
            sourceId,
            sourceRevision,
            artifact.ContentSha256,
            CompilerVersion,
            policy.Mode,
            destinations);

        return new PlatformEgressCandidateCompilationResult(candidate, null);
    }

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
    ImmutableArray<PlatformEgressDestination> Destinations);

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
