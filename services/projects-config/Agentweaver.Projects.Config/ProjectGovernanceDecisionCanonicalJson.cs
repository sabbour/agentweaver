using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Projects.Config;

internal static class ProjectGovernanceDecisionCanonicalJson
{
    internal const int MaximumCanonicalBytes = 65_536;
    private static readonly byte[] HashDomain = Encoding.UTF8.GetBytes("agentweaver-governance-decision-v1\0");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static ProjectGovernanceDecisionInput NormalizeAndValidate(ProjectGovernanceDecisionInput decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (string.IsNullOrWhiteSpace(decision.DecisionType) || decision.DecisionType.Length > 64 ||
            decision.DecisionType != decision.DecisionType.Trim().ToLowerInvariant() ||
            !decision.DecisionType.All(character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.'))
            throw new ArgumentException("Decision type must be a normalized lowercase token.", nameof(decision));
        if (!Enum.IsDefined(decision.Outcome) || !Enum.IsDefined(decision.ApprovalState))
            throw new ArgumentException("Decision outcome or approval state is invalid.", nameof(decision));
        if (decision.PolicyRevision is <= 0 || decision.OwnerReceiptPosition is <= 0)
            throw new ArgumentException("Decision revisions and receipt positions must be positive.", nameof(decision));
        if (decision.PolicyArtifactSha256 is not null &&
            !IsSha256(decision.PolicyArtifactSha256))
            throw new ArgumentException("Policy artifact hash must be lowercase SHA-256 hex.", nameof(decision));
        if (decision.HumanEntraOid.HasValue != decision.TenantId.HasValue)
            throw new ArgumentException("A verified Entra oid and tenant must be recorded together.", nameof(decision));
        if ((decision.BrokerIssuer is null) != (decision.BrokerSubject is null))
            throw new ArgumentException("Broker issuer and subject must be recorded together.", nameof(decision));
        if (string.IsNullOrWhiteSpace(decision.CorrelationId) || decision.CorrelationId.Length > 128)
            throw new ArgumentException("Correlation id is required and must be at most 128 characters.", nameof(decision));

        var roles = decision.EffectiveRoleRevisions.IsDefault
            ? []
            : decision.EffectiveRoleRevisions
                .OrderBy(item => item.AssignmentId.ToString("D"), StringComparer.Ordinal)
                .ToArray();
        if (roles.Any(item => item is null || item.Revision <= 0) ||
            roles.Select(item => item.AssignmentId).Distinct().Count() != roles.Length)
            throw new ArgumentException("Effective role revisions must be positive and unique.", nameof(decision));

        var normalized = decision with
        {
            EffectiveRoleRevisions = [.. roles],
            DecidedAt = decision.DecidedAt.ToUniversalTime(),
        };
        if (Serialize(normalized).Length > MaximumCanonicalBytes)
            throw new ArgumentException("Canonical decision record exceeds 65536 bytes.", nameof(decision));
        return normalized;
    }

    internal static byte[] Serialize(ProjectGovernanceDecisionInput decision) =>
        JsonSerializer.SerializeToUtf8Bytes(decision, Options);

    internal static byte[] Serialize(ProjectGovernanceDecisionV1 decision) =>
        JsonSerializer.SerializeToUtf8Bytes(decision, Options);

    internal static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string ChainHash(string previousHash, ReadOnlySpan<byte> canonicalRecord)
    {
        if (!IsSha256(previousHash))
            throw new ArgumentException("Previous hash must be lowercase SHA-256 hex.", nameof(previousHash));

        var previousBytes = Convert.FromHexString(previousHash);
        var input = new byte[HashDomain.Length + previousBytes.Length + canonicalRecord.Length];
        HashDomain.CopyTo(input, 0);
        previousBytes.CopyTo(input, HashDomain.Length);
        canonicalRecord.CopyTo(input.AsSpan(HashDomain.Length + previousBytes.Length));
        return Digest(input);
    }

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
