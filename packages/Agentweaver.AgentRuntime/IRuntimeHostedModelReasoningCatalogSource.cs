using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

/// <summary>Reads current hosted reasoning levels for the enabled model references.</summary>
public interface IRuntimeHostedModelReasoningCatalogSource
{
    ImmutableArray<string> EnabledHostedModelReferences { get; }

    Task<ImmutableDictionary<string, HostedModelReasoningModelV1>> ReadAsync(
        SecretCredential credential,
        CancellationToken cancellationToken);
}
