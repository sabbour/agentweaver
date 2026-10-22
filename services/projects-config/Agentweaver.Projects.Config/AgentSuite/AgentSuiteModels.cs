using System.Collections.Immutable;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>Accepted archive forms. Both are parsed offline from bytes the caller supplies.</summary>
internal enum AgentSuiteArchiveFormat
{
    /// <summary>A POSIX tar of an OCI image layout directory (oci-layout, index.json, blobs/sha256/*).</summary>
    OciLayoutTar,
    /// <summary>The raw application/vnd.agentsuite.content.v1.tar+gzip content layer.</summary>
    ContentTarGzip,
}

/// <summary>One retained original blob. Its bytes are never rewritten.</summary>
internal sealed record AgentSuiteBlob(string Digest, string MediaType, ReadOnlyMemory<byte> Data);

/// <summary>A literal-model or capability-model requirement, as the agent declares it.</summary>
internal sealed record AgentSuiteModelRequirement(
    string Protocol,
    string? Model,
    bool CapabilityBased,
    string? EndpointEnv,
    ImmutableArray<string> SecretRefs);

/// <summary>A bounded, directional permission to invoke another agent in the same suite.</summary>
internal sealed record AgentSuiteInvoke(string Agent, long MaxConcurrent, long MaxDepth);

/// <summary>An exact tool provider requirement of an agent.</summary>
internal sealed record AgentSuiteToolProviderRequirement(string Id, string Version, string ExecutionMode);

/// <summary>A validated agent with its original manifest digest and instruction bytes.</summary>
internal sealed record AgentSuiteAgent(
    string Id,
    string? Description,
    string ManifestDigest,
    string InstructionsPath,
    string InstructionsDigest,
    ReadOnlyMemory<byte> InstructionsBytes,
    AgentSuiteModelRequirement Model,
    ImmutableArray<AgentSuiteToolProviderRequirement> ToolProviders,
    ImmutableArray<AgentSuiteInvoke> Invokes,
    bool HasPolicy,
    bool HasEvaluations,
    ImmutableArray<string> Extensions);

/// <summary>A validated tool provider. Remote MCP endpoints are review-only and never contacted.</summary>
internal sealed record AgentSuiteToolProvider(
    string Id,
    string Version,
    string ManifestDigest,
    bool HasBundledVariants,
    bool HasRemoteMcp,
    ImmutableArray<string> Tools);

/// <summary>A validated platform composition.</summary>
internal sealed record AgentSuiteComposition(
    string Agent,
    string Platform,
    string BuildProfile,
    string? RuntimeProfile,
    string Digest);

/// <summary>A validated build profile with digest-pinned images.</summary>
internal sealed record AgentSuiteBuildProfile(
    string Id,
    string Digest,
    long SourceEpoch,
    bool HasExecutionContract,
    ImmutableArray<string> ImageReferences);

/// <summary>The validated suite graph. It is produced only when every native rule passes.</summary>
internal sealed record AgentSuiteGraph(
    string Name,
    string SuiteManifestDigest,
    ImmutableArray<AgentSuiteAgent> Agents,
    ImmutableArray<AgentSuiteToolProvider> ToolProviders,
    ImmutableArray<AgentSuiteComposition> Compositions,
    ImmutableArray<AgentSuiteBuildProfile> BuildProfiles,
    ImmutableArray<string> RuntimeProfiles,
    ImmutableArray<string> Capabilities);

/// <summary>A stricter Agentweaver limit, refused explicitly with a stable code.</summary>
internal sealed record AgentSuiteProfileError(string Code, string Message);

/// <summary>
/// Validation outcome. <see cref="NativeErrors"/> holds the native validator messages, one per
/// line, sorted ordinally. <see cref="ProfileErrors"/> holds stricter Agentweaver refusals.
/// </summary>
internal sealed record AgentSuiteValidationResult(
    AgentSuiteArchiveFormat Format,
    string SourceDigest,
    string? ManifestDigest,
    string? ContentDigest,
    ImmutableArray<string> NativeErrors,
    ImmutableArray<AgentSuiteProfileError> ProfileErrors,
    AgentSuiteGraph? Graph,
    ImmutableArray<AgentSuiteBlob> Blobs)
{
    // Accepted needs a clean native pass, no Agentweaver refusals, and a built graph; any one missing means not accepted.
    public bool Accepted => NativeErrors.IsEmpty && ProfileErrors.IsEmpty && Graph is not null;
}
