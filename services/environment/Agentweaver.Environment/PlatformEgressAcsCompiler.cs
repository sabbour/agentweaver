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

        var result = NativeAcsFiniteNetworkProfile.Validate(artifact);
        if (!result.IsValid || result.Policy is not { } policy)
            return Failed(result.Diagnostics.FirstOrDefault()?.Code ?? "network_profile_invalid");

        var destinations = policy.ApprovedDestinations
            .Concat(policy.SystemDestinations)
            .OrderBy(destination => destination.Host, StringComparer.Ordinal)
            .ThenBy(destination => destination.Port)
            .ToImmutableArray();
        var snapshot = new PlatformEgressPolicySnapshot(
            sourceId,
            sourceRevision,
            artifact.ContentSha256,
            CompilerVersion,
            admissionCheckpoint,
            policy.Mode,
            destinations);

        return snapshot.Validate() is null
            ? new PlatformEgressCompilationResult(snapshot, null)
            : Failed("compiled_snapshot_invalid");
    }

    private static PlatformEgressCompilationResult Failed(string code) =>
        new(null, new PlatformEgressCompilationFailure(code));
}

public sealed record PlatformEgressCompilationFailure(string Code);

public sealed record PlatformEgressCompilationResult(
    PlatformEgressPolicySnapshot? Snapshot,
    PlatformEgressCompilationFailure? Failure)
{
    public bool IsSuccess => Snapshot is not null && Failure is null;
}
