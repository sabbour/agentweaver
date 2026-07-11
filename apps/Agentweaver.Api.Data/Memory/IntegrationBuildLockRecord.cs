using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Memory;

/// <summary>
/// Cross-process advisory lock row that serializes integration-branch builds for a single project
/// repository. The physical repo at <c>/workspace/{projectId}/.git</c> is shared by every run in a
/// project (Azure Files SMB in the cloud), and <c>WorktreeManager.BuildIntegrationBranch</c> deletes
/// and recreates the integration ref on each rebuild. Two builds racing that same repo produce a
/// <c>LockedFileException</c> / null-ref, so the dispatch and assembly paths take this lock (keyed by
/// <see cref="ProjectId"/>) around the build.
///
/// One row per project, claimed via a conditional UPSERT and released by the holder in a finally. The
/// row carries a per-acquisition <see cref="OwnerToken"/> so a lock stolen after the stale TTL is
/// never released by the crashed original holder, and <see cref="AcquiredAt"/> so a crashed holder's
/// lock is reclaimable rather than deadlocking the project forever.
/// </summary>
public sealed class IntegrationBuildLockRecord
{
    /// <summary>The project whose shared repository this lock guards. Primary key (repo granularity).</summary>
    [Key] public required string ProjectId { get; set; }

    /// <summary>A unique token minted for each acquisition. Only the holder that minted it may release.</summary>
    public required string OwnerToken { get; set; }

    /// <summary>The pod/hostname that currently holds the lock. For diagnostics/log lines only.</summary>
    public required string OwnerPodId { get; set; }

    /// <summary>When the current holder acquired the lock. A lock older than the stale TTL is reclaimable.</summary>
    public DateTimeOffset AcquiredAt { get; set; }
}
