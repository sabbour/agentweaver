using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Projects.Config;

internal sealed record ProjectGovernanceCheckpointV1(
    int SchemaVersion,
    Guid CheckpointId,
    string ScopeType,
    string ScopeId,
    string ResourceType,
    string ResourceId,
    long Sequence,
    string HeadHash,
    DateTimeOffset CreatedAt,
    string CheckpointHash);

internal sealed record ProjectGovernanceCheckpointContentV1(
    int SchemaVersion,
    Guid CheckpointId,
    string ScopeType,
    string ScopeId,
    string ResourceType,
    string ResourceId,
    long Sequence,
    string HeadHash,
    DateTimeOffset CreatedAt);

internal static class ProjectGovernanceCheckpointCanonicalJson
{
    internal const int MaximumCanonicalBytes = 8192;
    private static readonly byte[] HashDomain =
        Encoding.UTF8.GetBytes("agentweaver-governance-checkpoint-v1\0");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static ProjectGovernanceCheckpointV1 Create(
        Guid checkpointId,
        string scopeType,
        string scopeId,
        string resourceType,
        string resourceId,
        long sequence,
        string headHash,
        DateTimeOffset createdAt)
    {
        var checkpoint = new ProjectGovernanceCheckpointV1(
            1,
            checkpointId,
            scopeType,
            scopeId,
            resourceType,
            resourceId,
            sequence,
            headHash,
            createdAt.ToUniversalTime(),
            "");
        return checkpoint with { CheckpointHash = Hash(checkpoint) };
    }

    internal static byte[] Serialize(ProjectGovernanceCheckpointV1 checkpoint) =>
        JsonSerializer.SerializeToUtf8Bytes(checkpoint, Options);

    internal static bool TryDeserialize(
        ReadOnlySpan<byte> bytes,
        out ProjectGovernanceCheckpointV1? checkpoint)
    {
        if (bytes.Length is 0 or > MaximumCanonicalBytes)
        {
            checkpoint = null;
            return false;
        }

        try
        {
            checkpoint = JsonSerializer.Deserialize<ProjectGovernanceCheckpointV1>(bytes, Options);
            if (checkpoint is null ||
                checkpoint.SchemaVersion != 1 ||
                checkpoint.CheckpointId == Guid.Empty ||
                checkpoint.ScopeType is not ("Platform" or "Project") ||
                string.IsNullOrWhiteSpace(checkpoint.ScopeId) ||
                checkpoint.ScopeId.Length > 256 ||
                checkpoint.ScopeType == "Platform" && checkpoint.ScopeId != ProjectAuthorizationOwner.PlatformResourceId ||
                string.IsNullOrWhiteSpace(checkpoint.ResourceType) ||
                checkpoint.ResourceType.Length > 64 ||
                string.IsNullOrWhiteSpace(checkpoint.ResourceId) ||
                checkpoint.ResourceId.Length > 256 ||
                checkpoint.Sequence < 0 ||
                !ProjectGovernanceDecisionCanonicalJson.IsSha256(checkpoint.HeadHash) ||
                checkpoint.Sequence == 0 && checkpoint.HeadHash != new string('0', 64) ||
                checkpoint.CreatedAt.Offset != TimeSpan.Zero ||
                !ProjectGovernanceDecisionCanonicalJson.IsSha256(checkpoint.CheckpointHash) ||
                !Serialize(checkpoint).AsSpan().SequenceEqual(bytes) ||
                !string.Equals(checkpoint.CheckpointHash, Hash(checkpoint), StringComparison.Ordinal))
            {
                checkpoint = null;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            checkpoint = null;
            return false;
        }
    }

    private static string Hash(ProjectGovernanceCheckpointV1 checkpoint)
    {
        var body = new ProjectGovernanceCheckpointContentV1(
            checkpoint.SchemaVersion,
            checkpoint.CheckpointId,
            checkpoint.ScopeType,
            checkpoint.ScopeId,
            checkpoint.ResourceType,
            checkpoint.ResourceId,
            checkpoint.Sequence,
            checkpoint.HeadHash,
            checkpoint.CreatedAt);
        var canonicalBody = Serialize(body);
        var input = new byte[HashDomain.Length + canonicalBody.Length];
        HashDomain.CopyTo(input, 0);
        canonicalBody.CopyTo(input, HashDomain.Length);
        return ProjectGovernanceDecisionCanonicalJson.Digest(input);
    }

    private static byte[] Serialize(ProjectGovernanceCheckpointContentV1 checkpoint) =>
        JsonSerializer.SerializeToUtf8Bytes(checkpoint, Options);
}
