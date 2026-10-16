namespace Agentweaver.Abstractions;

/// <summary>
/// Identifies a reviewed remote-tool snapshot for one project, agent, and workflow node.
/// The digest pins the snapshot contents rather than trusting a mutable lookup alone.
/// </summary>
public sealed class ReviewedRemoteToolSnapshotReference
{
    /// <summary>Creates a reference after checking bounded identifiers and a lowercase SHA-256 digest.</summary>
    /// <exception cref="ArgumentException">A required identifier, snapshot ID, or digest is invalid.</exception>
    public ReviewedRemoteToolSnapshotReference(
        string projectId,
        Guid snapshotId,
        string snapshotDigest,
        string agentId,
        string nodeId)
    {
        ProjectId = RequireIdentifier(projectId, nameof(projectId));
        SnapshotId = snapshotId != Guid.Empty
            ? snapshotId
            : throw new ArgumentException("A reviewed snapshot ID is required.", nameof(snapshotId));
        SnapshotDigest = RequireDigest(snapshotDigest, nameof(snapshotDigest));
        AgentId = RequireIdentifier(agentId, nameof(agentId));
        NodeId = RequireIdentifier(nodeId, nameof(nodeId));
    }

    /// <summary>Project whose owner issued the reviewed snapshot.</summary>
    public string ProjectId { get; }
    /// <summary>Stable identity of the reviewed snapshot.</summary>
    public Guid SnapshotId { get; }
    /// <summary>Lowercase SHA-256 digest of the reviewed snapshot content.</summary>
    public string SnapshotDigest { get; }
    /// <summary>Agent whose tool set was reviewed.</summary>
    public string AgentId { get; }
    /// <summary>Workflow node to which this reviewed snapshot applies.</summary>
    public string NodeId { get; }

    /// <summary>Checks that an identifier is nonempty, bounded, and has no control characters.</summary>
    private static string RequireIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("A valid value is required.", parameterName);
        return value;
    }

    /// <summary>Checks the canonical lowercase hexadecimal representation of a SHA-256 digest.</summary>
    private static string RequireDigest(string value, string parameterName)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A lowercase SHA-256 digest is required.", parameterName);
        return value;
    }
}
