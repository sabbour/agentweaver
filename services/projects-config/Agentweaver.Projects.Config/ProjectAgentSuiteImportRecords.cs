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
    /// Binds remote MCP tool providers to Environment-owned Remote MCP connections (#1923). Each key is a
    /// tool provider <c>id@version</c> from the suite catalog. The package itself never carries endpoints or secrets.
    /// </summary>
    public ImmutableArray<ProjectAgentSuiteToolConnection> ToolConnections { get; init; } = [];
}

/// <summary>Binds one suite tool provider to an Environment-owned Remote MCP connection ID.</summary>
public sealed record ProjectAgentSuiteToolConnection(string ToolProvider, string ConnectionId);

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
/// agent has no blocker other than the missing runtime; <see cref="Runnable"/> stays false until an AgentSuite
/// runtime exists.
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
    bool Supported,
    bool Runnable,
    ImmutableArray<ProjectAgentSuiteBlocker> Blockers);

/// <summary>A validated tool provider kept for review. Remote MCP endpoints are never contacted.</summary>
public sealed record ProjectAgentSuiteToolProvider(
    string Id,
    string Version,
    string ManifestDigest,
    bool BundledVariants,
    bool RemoteMcp,
    ImmutableArray<string> Tools,
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
    ImmutableArray<string> RuntimeProfiles,
    ImmutableArray<ProjectAgentSuiteBlob> Blobs);

/// <summary>
/// Import outcome. <see cref="Proposal"/> is null when every mapped agent already exists unchanged in the
/// current configuration, so there is nothing to review.
/// </summary>
public sealed record ProjectAgentSuiteImportResult(ProjectAgentSuiteImport Import, ProjectCastingProposal? Proposal);

/// <summary>Thrown when the archive fails native or Agentweaver validation. Carries every error line.</summary>
public sealed class AgentSuiteImportRejectedException(
    string code,
    string message,
    ImmutableArray<string> errors) : Exception(message)
{
    public string Code { get; } = code;
    public ImmutableArray<string> Errors { get; } = errors;
}
