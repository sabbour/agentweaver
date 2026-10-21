using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

/// <summary>Side effect that an AgentSuite ToolProvider declares for a remote tool.</summary>
public enum RemoteMcpToolEffect
{
    ReadOnly,
    Mutating
}

/// <summary>Approval mode that an AgentSuite ToolProvider declares for a remote tool.</summary>
public enum RemoteMcpToolApproval
{
    Required,
    PolicyControlled
}

/// <summary>One tool that an AgentSuite ToolProvider requires from a remote MCP server.</summary>
public sealed record RemoteMcpToolRequirement
{
    /// <summary>Creates a bounded tool requirement.</summary>
    /// <exception cref="ArgumentException">The name is blank, too long, or contains control characters.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The effect or approval is undefined.</exception>
    public RemoteMcpToolRequirement(
        string name,
        RemoteMcpToolEffect effect,
        RemoteMcpToolApproval approval,
        bool delegatedAuthorization)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 || name.Any(char.IsControl))
            throw new ArgumentException("MCP tool names must be bounded and contain no control characters.", nameof(name));
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect));
        if (!Enum.IsDefined(approval))
            throw new ArgumentOutOfRangeException(nameof(approval));

        Name = name;
        Effect = effect;
        Approval = approval;
        DelegatedAuthorization = delegatedAuthorization;
    }

    public string Name { get; }
    public RemoteMcpToolEffect Effect { get; }
    public RemoteMcpToolApproval Approval { get; }
    /// <summary>True when the tool needs a delegated user credential brokered by Identity.</summary>
    public bool DelegatedAuthorization { get; }
}

/// <summary>
/// Remote tool requirements from an AgentSuite ToolProvider. It carries no endpoint, header value,
/// secret, or credential. The project Remote MCP connection supplies the endpoint and authentication.
/// </summary>
public sealed record RemoteMcpToolProviderRequirement
{
    /// <summary>AgentSuite transport value for Streamable HTTP providers.</summary>
    public const string StreamableHttpTransport = "streamable-http";

    /// <summary>Creates a requirement for one versioned provider.</summary>
    /// <exception cref="ArgumentException">The provider, transport, or tool list is invalid.</exception>
    public RemoteMcpToolProviderRequirement(
        string toolProvider,
        string transport,
        bool headerSecretsDeclared,
        ImmutableArray<RemoteMcpToolRequirement> tools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolProvider);
        var separator = toolProvider.LastIndexOf('@');
        if (toolProvider.Length > 256 || toolProvider.Any(char.IsControl) ||
            separator < 1 || separator == toolProvider.Length - 1)
            throw new ArgumentException("Tool providers must use a bounded id@version reference.", nameof(toolProvider));
        if (!string.Equals(transport, StreamableHttpTransport, StringComparison.Ordinal))
            throw new ArgumentException("Remote tool providers must use the streamable-http transport.", nameof(transport));
        if (tools.IsDefault || tools.IsEmpty || tools.Length > 128 ||
            tools.Any(tool => tool is null) ||
            tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != tools.Length)
            throw new ArgumentException("A tool provider must list between 1 and 128 uniquely named tools.", nameof(tools));

        ToolProvider = toolProvider;
        Transport = transport;
        HeaderSecretsDeclared = headerSecretsDeclared;
        Tools = tools;
    }

    /// <summary>AgentSuite provider reference in id@version form.</summary>
    public string ToolProvider { get; }
    public string Transport { get; }
    /// <summary>True when the provider declares static header secret references.</summary>
    public bool HeaderSecretsDeclared { get; }
    public ImmutableArray<RemoteMcpToolRequirement> Tools { get; }
}

/// <summary>Reason a tool provider requirement cannot bind to a Remote MCP connection.</summary>
public enum RemoteMcpToolProviderBindingDenial
{
    None,
    ConnectionNotEnabled,
    CatalogMissing,
    CatalogStale,
    ToolMissing,
    DelegatedAuthorizationUnavailable,
    HeaderSecretsUnsupported
}

/// <summary>Exact discovered tool revision that satisfied one requirement.</summary>
public sealed record RemoteMcpBoundTool(string Name, string ToolRevision);

/// <summary>
/// Result of binding a tool provider requirement to the current connection snapshot. A bound result
/// pins the configuration, catalog, and tool revisions for reviewed tool snapshots (#1925).
/// </summary>
public sealed record RemoteMcpToolProviderBindingResult(
    bool Bound,
    RemoteMcpToolProviderBindingDenial Denial,
    Guid ConnectionId,
    long ConfigurationRevision,
    string ConfigurationSha256,
    long? DiscoveryRevision,
    string? CatalogSha256,
    ImmutableArray<string> MissingTools,
    ImmutableArray<RemoteMcpBoundTool> Tools);

/// <summary>Binds AgentSuite tool provider requirements to project Remote MCP connections.</summary>
public static class RemoteMcpToolProviderBinding
{
    /// <summary>
    /// Checks that the connection is enabled, its catalog belongs to the current configuration,
    /// every required tool exists, and delegated tools use delegated OAuth. It reads no credential.
    /// </summary>
    public static RemoteMcpToolProviderBindingResult Evaluate(
        RemoteMcpToolProviderRequirement requirement,
        RemoteMcpConnectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(snapshot);

        var configuration = snapshot.Configuration;
        var catalog = snapshot.Catalog;
        RemoteMcpToolProviderBindingResult Deny(
            RemoteMcpToolProviderBindingDenial denial,
            ImmutableArray<string>? missing = null) => new(
                false,
                denial,
                configuration.Connection.ConnectionId,
                configuration.ConfigurationRevision,
                configuration.ConfigurationSha256,
                catalog?.DiscoveryRevision,
                catalog?.CatalogSha256,
                missing ?? [],
                []);

        if (requirement.HeaderSecretsDeclared)
            return Deny(RemoteMcpToolProviderBindingDenial.HeaderSecretsUnsupported);
        if (snapshot.Head.State != RemoteMcpConnectionState.Enabled)
            return Deny(RemoteMcpToolProviderBindingDenial.ConnectionNotEnabled);
        if (catalog is null)
            return Deny(RemoteMcpToolProviderBindingDenial.CatalogMissing);
        if (catalog.ConfigurationRevision != configuration.ConfigurationRevision ||
            snapshot.Head.CurrentConfigurationRevision != configuration.ConfigurationRevision ||
            snapshot.Head.CurrentDiscoveryRevision != catalog.DiscoveryRevision)
            return Deny(RemoteMcpToolProviderBindingDenial.CatalogStale);

        var discovered = catalog.Tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var missingTools = requirement.Tools
            .Where(tool => !discovered.ContainsKey(tool.Name))
            .Select(tool => tool.Name)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        if (!missingTools.IsEmpty)
            return Deny(RemoteMcpToolProviderBindingDenial.ToolMissing, missingTools);
        if (requirement.Tools.Any(tool => tool.DelegatedAuthorization) &&
            configuration.AuthenticationMode != RemoteMcpAuthenticationMode.DelegatedOAuth)
            return Deny(RemoteMcpToolProviderBindingDenial.DelegatedAuthorizationUnavailable);

        return new(
            true,
            RemoteMcpToolProviderBindingDenial.None,
            configuration.Connection.ConnectionId,
            configuration.ConfigurationRevision,
            configuration.ConfigurationSha256,
            catalog.DiscoveryRevision,
            catalog.CatalogSha256,
            [],
            requirement.Tools
                .Select(tool => new RemoteMcpBoundTool(tool.Name, discovered[tool.Name].ToolRevision))
                .ToImmutableArray());
    }
}
