namespace Agentweaver.Abstractions;

public sealed class ReviewedRemoteToolSnapshotReference
{
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

    public string ProjectId { get; }
    public Guid SnapshotId { get; }
    public string SnapshotDigest { get; }
    public string AgentId { get; }
    public string NodeId { get; }

    private static string RequireIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("A valid value is required.", parameterName);
        return value;
    }

    private static string RequireDigest(string value, string parameterName)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A lowercase SHA-256 digest is required.", parameterName);
        return value;
    }
}
