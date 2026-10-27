using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Root-only read joining retained creation provenance to current Host readiness and mount observation.
/// This does not attest child execution, network enforcement, storage byte isolation, or no-send.
/// </summary>
public sealed record RuntimeHostP1EvidenceReceipt(
    int ContractVersion,
    DateTimeOffset ObservedAt,
    EnvironmentP1CreationBinding Creation,
    RuntimeHostReadinessEvidenceReceipt Host);
