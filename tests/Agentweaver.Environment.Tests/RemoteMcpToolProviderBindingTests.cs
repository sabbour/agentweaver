using System.Collections.Immutable;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Environment;

namespace Agentweaver.Environment.Tests;

public sealed class RemoteMcpToolProviderBindingTests
{
    private static readonly RemoteMcpConnectionReference Connection = new("project-binding", Guid.NewGuid());

    [Fact]
    public void BindsRequiredToolsToExactCatalogRevisions()
    {
        var snapshot = Snapshot(Configuration(), Catalog("alerts", "forecast"));

        var result = RemoteMcpToolProviderBinding.Evaluate(
            Requirement(Tool("forecast"), Tool("alerts", effect: RemoteMcpToolEffect.Mutating)), snapshot);

        Assert.True(result.Bound);
        Assert.Equal(RemoteMcpToolProviderBindingDenial.None, result.Denial);
        Assert.Equal(snapshot.Configuration.ConfigurationSha256, result.ConfigurationSha256);
        Assert.Equal(snapshot.Catalog!.CatalogSha256, result.CatalogSha256);
        Assert.Equal(1, result.DiscoveryRevision);
        Assert.Equal(
            snapshot.Catalog.Tools.Single(tool => tool.Name == "forecast").ToolRevision,
            result.Tools.Single(tool => tool.Name == "forecast").ToolRevision);
        Assert.Empty(result.MissingTools);
    }

    [Fact]
    public void MissingToolsAreListedAndDenied()
    {
        var result = RemoteMcpToolProviderBinding.Evaluate(
            Requirement(Tool("forecast"), Tool("radar"), Tool("alerts")),
            Snapshot(Configuration(), Catalog("forecast")));

        Assert.False(result.Bound);
        Assert.Equal(RemoteMcpToolProviderBindingDenial.ToolMissing, result.Denial);
        Assert.Equal(["alerts", "radar"], result.MissingTools.ToArray());
        Assert.Empty(result.Tools);
    }

    [Fact]
    public void DisabledMissingAndStaleCatalogsAreDenied()
    {
        var requirement = Requirement(Tool("forecast"));

        var disabled = RemoteMcpToolProviderBinding.Evaluate(
            requirement, Snapshot(Configuration(), Catalog("forecast"), RemoteMcpConnectionState.Disabled));
        var missing = RemoteMcpToolProviderBinding.Evaluate(requirement, Snapshot(Configuration(), null));
        var stale = RemoteMcpToolProviderBinding.Evaluate(
            requirement, Snapshot(Configuration(revision: 2), Catalog("forecast")));

        Assert.Equal(RemoteMcpToolProviderBindingDenial.ConnectionNotEnabled, disabled.Denial);
        Assert.Equal(RemoteMcpToolProviderBindingDenial.CatalogMissing, missing.Denial);
        Assert.Equal(RemoteMcpToolProviderBindingDenial.CatalogStale, stale.Denial);
        Assert.All(new[] { disabled, missing, stale }, item => Assert.False(item.Bound));
    }

    [Fact]
    public void DelegatedToolsRequireDelegatedOAuthConnection()
    {
        var requirement = Requirement(Tool("forecast", delegated: true));

        var anonymous = RemoteMcpToolProviderBinding.Evaluate(
            requirement, Snapshot(Configuration(), Catalog("forecast")));
        var delegated = RemoteMcpToolProviderBinding.Evaluate(
            requirement, Snapshot(Configuration(oauth: true), Catalog("forecast")));

        Assert.Equal(RemoteMcpToolProviderBindingDenial.DelegatedAuthorizationUnavailable, anonymous.Denial);
        Assert.True(delegated.Bound);
    }

    [Fact]
    public void HeaderSecretsAreUnsupported()
    {
        var requirement = new RemoteMcpToolProviderRequirement(
            "weather@1.0.0",
            RemoteMcpToolProviderRequirement.StreamableHttpTransport,
            headerSecretsDeclared: true,
            [Tool("forecast")]);

        var result = RemoteMcpToolProviderBinding.Evaluate(
            requirement, Snapshot(Configuration(), Catalog("forecast")));

        Assert.False(result.Bound);
        Assert.Equal(RemoteMcpToolProviderBindingDenial.HeaderSecretsUnsupported, result.Denial);
    }

    [Theory]
    [InlineData("weather", "streamable-http")]
    [InlineData("@1.0.0", "streamable-http")]
    [InlineData("weather@", "streamable-http")]
    [InlineData("weather@1.0.0", "stdio")]
    public void RequirementRejectsInvalidProviderOrTransport(string provider, string transport) =>
        Assert.Throws<ArgumentException>(() =>
            new RemoteMcpToolProviderRequirement(provider, transport, false, [Tool("forecast")]));

    [Fact]
    public void RequirementRejectsEmptyOrDuplicateTools()
    {
        Assert.Throws<ArgumentException>(() => new RemoteMcpToolProviderRequirement(
            "weather@1.0.0", RemoteMcpToolProviderRequirement.StreamableHttpTransport, false, []));
        Assert.Throws<ArgumentException>(() => new RemoteMcpToolProviderRequirement(
            "weather@1.0.0",
            RemoteMcpToolProviderRequirement.StreamableHttpTransport,
            false,
            [Tool("forecast"), Tool("forecast")]));
    }

    private static RemoteMcpToolRequirement Tool(
        string name,
        RemoteMcpToolEffect effect = RemoteMcpToolEffect.ReadOnly,
        bool delegated = false) =>
        new(name, effect, RemoteMcpToolApproval.Required, delegated);

    private static RemoteMcpToolProviderRequirement Requirement(params RemoteMcpToolRequirement[] tools) =>
        new("weather@1.0.0", RemoteMcpToolProviderRequirement.StreamableHttpTransport, false, [.. tools]);

    private static RemoteMcpConnectionSnapshot Snapshot(
        RemoteMcpConnectionConfiguration configuration,
        RemoteMcpDiscoveryCatalogPin? catalog,
        RemoteMcpConnectionState state = RemoteMcpConnectionState.Enabled) =>
        new(
            new RemoteMcpConnectionHead(
                Connection,
                rowRevision: 3,
                configuration.ConfigurationRevision,
                state,
                catalog?.DiscoveryRevision),
            configuration,
            catalog);

    private static RemoteMcpConnectionConfiguration Configuration(long revision = 1, bool oauth = false) =>
        new(
            Connection,
            revision,
            "Weather",
            "https://mcp.example.com/mcp",
            oauth ? "https://mcp.example.com" : null,
            oauth ? RemoteMcpAuthenticationMode.DelegatedOAuth : RemoteMcpAuthenticationMode.None,
            oauth ? "binding-1" : null,
            RemoteMcpTransportProfile.StreamableHttp20250618);

    private static RemoteMcpDiscoveryCatalogPin Catalog(params string[] toolNames)
    {
        var tools = string.Join(',', toolNames.Select(name => $$$"""{"name":"{{{name}}}","inputSchema":{"type":"object"}}"""));
        var parsed = RemoteMcpMetadataParser.ParseToolsList(Encoding.UTF8.GetBytes(
            $$$"""{"result":{"tools":[{{{tools}}}]}}"""));
        return new(Connection, 1, 1, parsed.CatalogSha256, null, parsed.Tools);
    }
}
