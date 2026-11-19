using System.Text;
using System.Text.Json;

namespace Agentweaver.Environment;

internal static class PlatformPolicyProofSnapshot
{
    internal const int MaxBytes = 1_048_576;

    internal static string Serialize(KubernetesPlatformPolicyInventory inventory)
    {
        var json = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                inventory.SnapshotId,
                inventory.NamespaceUid,
                listResourceVersions = new
                {
                    networkPolicies = inventory.NetworkPolicyListResourceVersion,
                    ciliumNetworkPolicies = inventory.CiliumPolicyListResourceVersion,
                    ciliumClusterwideNetworkPolicies = inventory.CiliumClusterwidePolicyListResourceVersion,
                },
                policies = inventory.Policies.Select(policy => new
                {
                    policy.ApiVersion,
                    policy.Kind,
                    policy.Namespace,
                    policy.Name,
                    policy.Uid,
                    policy.ResourceVersion,
                    policy.CanonicalContent,
                }),
            });
        ValidateSize(json);
        return json;
    }

    internal static void ValidateSize(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
            throw new PlatformPolicyProofException(
                "policy_semantics_unsupported", "The retained policy snapshot exceeds the supported bound.");
    }
}
