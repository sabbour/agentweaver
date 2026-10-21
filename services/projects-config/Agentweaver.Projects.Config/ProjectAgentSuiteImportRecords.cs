using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Agentweaver.Projects.Config;

/// <summary>Request to validate a native AgentSuite archive and stage its agents as a casting proposal.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImportProjectAgentSuiteRequest
{
    public const string OciLayoutTarFormat = "oci-layout-tar";
    public const string ContentTarGzipFormat = "content-tar-gzip";

    public required long ExpectedConfigurationRevision { get; init; }
    /// <summary><c>oci-layout-tar</c> or <c>content-tar-gzip</c>.</summary>
    public required string Format { get; init; }
    /// <summary>Base64 archive bytes. The decoded archive is limited to 16 MiB.</summary>
    public required string Archive { get; init; }
    /// <summary>
    /// Binds remote MCP tool providers to Environment-owned Remote MCP connections (#1923). Each
    /// <see cref="ProjectAgentSuiteToolConnection.ToolProvider"/> is a catalog <c>id@version</c>. The package never
    /// carries endpoints or secret values; the Environment owner resolves them from the connection.
    /// </summary>
    public ImmutableArray<ProjectAgentSuiteToolConnection> ToolConnections { get; init; } = [];
    /// <summary>Optional blueprint projection. Null uses <see cref="ProjectAgentSuiteBlueprint"/> defaults.</summary>
    public ProjectAgentSuiteBlueprint? Blueprint { get; init; }
}

/// <summary>
/// Binds one suite tool provider to an Environment-owned Remote MCP connection. <see cref="ConnectionId"/> is a
/// project-scoped GUID in lowercase "D" format. Import checks only its shape; the Environment owner resolves it.
/// </summary>
public sealed record ProjectAgentSuiteToolConnection(string ToolProvider, string ConnectionId);

/// <summary>Skills a blueprint assigns to one suite agent, by native agent ID (the role).</summary>
public sealed record ProjectAgentSuiteSkillBinding(string Role, ImmutableArray<string> Skills);

/// <summary>
/// Blueprint data that travels with the import instead of in suite extensions. Import validates its shape and
/// keeps all of it on the record. It does not change project configuration; the blueprint catalog owner (#2184)
/// applies workflows, sandbox profile and skill bindings to the casting proposal.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectAgentSuiteBlueprint
{
    public const string DefaultId = "default";
    public const string RestrictedSandbox = "restricted";

    public string? SourceBlueprintId { get; init; }
    /// <summary>Defaults to the suite name.</summary>
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public ImmutableArray<string> Workflows { get; init; } = [DefaultId];
    public string DefaultWorkflow { get; init; } = DefaultId;
    public string ReviewPolicy { get; init; } = DefaultId;
    /// <summary><c>default</c> or <c>restricted</c>.</summary>
    public string SandboxProfile { get; init; } = DefaultId;
    public ImmutableArray<ProjectAgentSuiteSkillBinding> SkillBindings { get; init; } = [];
}

/// <summary>A retained original blob, stored by its native digest.</summary>
public sealed record ProjectAgentSuiteBlob(string Digest, string MediaType, long Size);

/// <summary>One reason an imported agent cannot run in Agentweaver. No fallback is applied.</summary>
public sealed record ProjectAgentSuiteBlocker(string Code, string Message);

/// <summary>A bounded, directional permission to invoke another suite agent. Review-only; it grants nothing.</summary>
public sealed record ProjectAgentSuiteInvoke(string Agent, long MaxConcurrent, long MaxDepth);

/// <summary>
/// One tool provider an agent requires. <see cref="Kind"/> is <c>remote-mcp</c> or <c>bundled</c>.
/// <see cref="ConnectionId"/> is the bound Environment Remote MCP connection, or null when unbound.
/// </summary>
public sealed record ProjectAgentSuiteToolDependency(
    string ToolProvider,
    string Kind,
    string? ConnectionId,
    ImmutableArray<string> Tools);

/// <summary>
/// Maps one native suite agent to its installation-scoped effective agent ID. <see cref="Supported"/> means the
/// agent has no blocker other than the missing runtime and unenforced policy; <see cref="Runnable"/> stays false
/// until an AgentSuite runtime exists. <see cref="PolicyDigest"/> binds the agent's authored policy, if any.
/// </summary>
public sealed record ProjectAgentSuiteAgentMapping(
    string NativeId,
    string ManifestDigest,
    string InstructionsDigest,
    string EffectiveAgentId,
    string Role,
    ImmutableArray<string> Platforms,
    ImmutableArray<ProjectAgentSuiteInvoke> Invokes,
    ImmutableArray<ProjectAgentSuiteToolDependency> ToolDependencies,
    string? PolicyDigest,
    bool Supported,
    bool Runnable,
    ImmutableArray<ProjectAgentSuiteBlocker> Blockers);

/// <summary>One exact IPv4/TCP egress tuple from a policy. An empty list denies all egress.</summary>
public sealed record ProjectAgentSuiteEgressRule(string Address, string Protocol, int Port);

/// <summary>
/// An authored agent policy, retained as original bytes. Import records it and binds it to its owning agent;
/// it does not enforce it. Fields are parsed only for policy v2. <see cref="EgressAllow"/> is null when the
/// policy is v1 or has no network section.
/// </summary>
public sealed record ProjectAgentSuitePolicy(
    string Digest,
    string MediaType,
    string? Version,
    string OwningAgent,
    ImmutableArray<ProjectAgentSuiteEgressRule>? EgressAllow,
    ImmutableArray<string> RequiredCapabilities);

/// <summary>A remote MCP header whose value comes only from a Secret key reference. It never holds a value.</summary>
public sealed record ProjectAgentSuiteRemoteHeader(string Name, string SecretName, string SecretKey);

/// <summary>
/// The unresolved Streamable HTTP declaration of a remote tool provider. <see cref="EndpointRef"/>, header secret
/// references and <see cref="NetworkDestinationRefs"/> are binding slots, not URLs or credentials.
/// </summary>
public sealed record ProjectAgentSuiteRemoteMcp(
    string Transport,
    string EndpointRef,
    ImmutableArray<ProjectAgentSuiteRemoteHeader> Headers,
    ImmutableArray<string> NetworkDestinationRefs,
    long ConnectMilliseconds,
    long RequestMilliseconds);

/// <summary>A delegated-user authorization requirement. <see cref="BindingRef"/> is an unresolved runtime slot.</summary>
public sealed record ProjectAgentSuiteDelegatedAuthorization(string Service, string Scope, string BindingRef);

/// <summary>One callable tool as declared. Null <see cref="Effect"/> makes no read-only claim.</summary>
public sealed record ProjectAgentSuiteTool(
    string Name,
    string? Effect,
    string? Approval,
    ProjectAgentSuiteDelegatedAuthorization? DelegatedAuthorization);

/// <summary>
/// A validated tool provider. <see cref="Support"/> is <c>remote-mcp</c> when it declares a remote endpoint without
/// header secrets, which an Environment connection can bind. Otherwise it is <c>unsupported</c>: bundled-only
/// providers do not run, and Remote MCP connections do not support header secrets. Nothing is contacted.
/// </summary>
public sealed record ProjectAgentSuiteToolProvider(
    string Id,
    string Version,
    string ManifestDigest,
    bool BundledVariants,
    ProjectAgentSuiteRemoteMcp? Remote,
    ImmutableArray<ProjectAgentSuiteTool> Tools,
    string Support);

/// <summary>
/// Immutable import record for one native source identity in one project. It stores native digests and IDs
/// separately from effective agent IDs and tool connection bindings, and is never overwritten.
/// </summary>
public sealed record ProjectAgentSuiteImport(
    int RecordVersion,
    string ProjectId,
    string SourceIdentity,
    string SourceFormat,
    string? ManifestDigest,
    string ContentDigest,
    string SuiteName,
    string SuiteManifestDigest,
    string SpecVersion,
    string SchemaCommit,
    ImmutableArray<ProjectAgentSuiteAgentMapping> Agents,
    ImmutableArray<ProjectAgentSuiteToolProvider> ToolProviders,
    ImmutableArray<ProjectAgentSuitePolicy> Policies,
    ImmutableArray<ProjectAgentSuiteToolConnection> ToolConnections,
    ProjectAgentSuiteBlueprint Blueprint,
    ImmutableArray<string> RuntimeProfiles,
    ImmutableArray<ProjectAgentSuiteBlob> Blobs);

/// <summary>
/// The policy bound to one imported effective agent ID, or a null <see cref="Policy"/> when none is bound.
/// Read-only context for policy consumers; it does not mean the policy is enforced.
/// </summary>
public sealed record ProjectAgentSuiteAgentPolicy(
    string EffectiveAgentId,
    string SourceIdentity,
    ProjectAgentSuitePolicy? Policy);

/// <summary>
/// Import outcome. <see cref="Proposal"/> is null when every mapped agent already exists unchanged in the
/// current configuration and the blueprint changes nothing, so there is nothing to review.
/// </summary>
public sealed record ProjectAgentSuiteImportResult(ProjectAgentSuiteImport Import, ProjectCastingProposal? Proposal)
{
    /// <summary>Blueprint skill bindings that don't take effect in the proposal, each with a stable reason code.</summary>
    public ImmutableArray<BlueprintUnresolvedSkillBinding> UnresolvedSkillBindings { get; init; } = [];
}

/// <summary>Thrown when the archive fails native or Agentweaver validation. Carries every error line.</summary>
public sealed class AgentSuiteImportRejectedException(
    string code,
    string message,
    ImmutableArray<string> errors) : Exception(message)
{
    public string Code { get; } = code;
    public ImmutableArray<string> Errors { get; } = errors;
}
