using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;

namespace Agentweaver.Environment.Tests;

public sealed class RemoteMcpConnectionReviewTests
{
    private static readonly RemoteMcpConnectionReference Connection = new("project-review", Guid.NewGuid());

    [Fact]
    public void UnchangedConnectionAllowsReviewedTool()
    {
        var snapshot = Snapshot(Configuration(), Catalog("forecast"));
        var reviewed = RemoteMcpConnectionReviewReference.FromSnapshot(snapshot);
        var tool = snapshot.Catalog!.Tools[0];

        var validity = RemoteMcpConnectionReview.Evaluate(reviewed, snapshot, tool.Name, tool.ToolRevision);

        Assert.True(validity.Allowed);
        Assert.Equal(RemoteMcpInvocationDenial.None, validity.Denial);
        Assert.False(validity.ReviewRequired);
        Assert.False(validity.CredentialValidityRequired);
        Assert.Equal(reviewed, validity.CurrentReference);
    }

    [Fact]
    public void ChangedEndpointRequiresReviewedRebind()
    {
        var reviewedSnapshot = Snapshot(Configuration(), Catalog("forecast"));
        var reviewed = RemoteMcpConnectionReviewReference.FromSnapshot(reviewedSnapshot);
        var changed = Snapshot(
            Configuration(revision: 2, endpoint: "https://other.example.com/mcp"),
            Catalog("forecast", configurationRevision: 2));
        var tool = reviewedSnapshot.Catalog!.Tools[0];

        var validity = RemoteMcpConnectionReview.Evaluate(reviewed, changed, tool.Name, tool.ToolRevision);

        Assert.False(validity.Allowed);
        Assert.Equal(RemoteMcpInvocationDenial.ConfigurationChanged, validity.Denial);
        Assert.True(validity.ReviewRequired);
        Assert.NotEqual(reviewed, validity.CurrentReference);
    }

    [Fact]
    public void ChangedSchemaWithholdsToolPendingReview()
    {
        var reviewedSnapshot = Snapshot(Configuration(), Catalog("forecast"));
        var reviewed = RemoteMcpConnectionReviewReference.FromSnapshot(reviewedSnapshot);
        var changed = Snapshot(Configuration(), Catalog("forecast", discoveryRevision: 2, changedSchema: true));
        var tool = reviewedSnapshot.Catalog!.Tools[0];

        var validity = RemoteMcpConnectionReview.Evaluate(reviewed, changed, tool.Name, tool.ToolRevision);

        Assert.False(validity.Allowed);
        Assert.Equal(RemoteMcpInvocationDenial.CatalogChanged, validity.Denial);
        Assert.True(validity.ReviewRequired);
    }

    [Fact]
    public void RemovedToolAndDisabledConnectionAreDenied()
    {
        var reviewedSnapshot = Snapshot(Configuration(), Catalog("forecast"));
        var reviewed = RemoteMcpConnectionReviewReference.FromSnapshot(reviewedSnapshot);
        var tool = reviewedSnapshot.Catalog!.Tools[0];

        var removedTool = RemoteMcpConnectionReview.Evaluate(
            reviewed,
            Snapshot(Configuration(), Catalog("alerts", discoveryRevision: 2)),
            tool.Name,
            tool.ToolRevision);
        var disabled = RemoteMcpConnectionReview.Evaluate(
            reviewed,
            Snapshot(Configuration(), Catalog("forecast"), RemoteMcpConnectionState.Disabled),
            tool.Name,
            tool.ToolRevision);
        var removed = RemoteMcpConnectionReview.Evaluate(
            reviewed,
            Snapshot(Configuration(), null, RemoteMcpConnectionState.Removed),
            tool.Name,
            tool.ToolRevision);
        var missing = RemoteMcpConnectionReview.Evaluate(reviewed, null, tool.Name, tool.ToolRevision);

        Assert.Equal(RemoteMcpInvocationDenial.ToolNotInCatalog, removedTool.Denial);
        Assert.Equal(RemoteMcpInvocationDenial.ConnectionNotEnabled, disabled.Denial);
        Assert.Null(disabled.CurrentReference);
        Assert.Equal(RemoteMcpInvocationDenial.ConnectionRemoved, removed.Denial);
        Assert.Equal(RemoteMcpInvocationDenial.ConnectionNotFound, missing.Denial);
        Assert.All(new[] { removedTool, disabled, removed, missing }, item => Assert.False(item.Allowed));
    }

    [Fact]
    public void WrongToolRevisionAndWrongProjectAreDenied()
    {
        var snapshot = Snapshot(Configuration(), Catalog("forecast"));
        var reviewed = RemoteMcpConnectionReviewReference.FromSnapshot(snapshot);
        var otherProject = new RemoteMcpConnectionReviewReference(
            new RemoteMcpConnectionReference("project-other", Connection.ConnectionId),
            reviewed.ConfigurationRevision,
            reviewed.ConfigurationSha256,
            reviewed.DiscoveryRevision,
            reviewed.CatalogSha256);

        var wrongRevision = RemoteMcpConnectionReview.Evaluate(reviewed, snapshot, "forecast", new string('b', 64));
        var wrongProject = RemoteMcpConnectionReview.Evaluate(
            otherProject, snapshot, "forecast", snapshot.Catalog!.Tools[0].ToolRevision);

        Assert.Equal(RemoteMcpInvocationDenial.ToolChanged, wrongRevision.Denial);
        Assert.True(wrongRevision.ReviewRequired);
        Assert.Equal(RemoteMcpInvocationDenial.ConnectionNotFound, wrongProject.Denial);
        Assert.False(wrongProject.Allowed);
    }

    [Fact]
    public void DelegatedOAuthRequiresIdentityBindingAndCredentialValidity()
    {
        var unbound = Snapshot(Configuration(oauth: true), Catalog("forecast"));
        var bound = Snapshot(Configuration(oauth: true, binding: "binding-1"), Catalog("forecast"));
        var tool = unbound.Catalog!.Tools[0];

        var unboundValidity = RemoteMcpConnectionReview.Evaluate(
            RemoteMcpConnectionReviewReference.FromSnapshot(unbound), unbound, tool.Name, tool.ToolRevision);
        var boundValidity = RemoteMcpConnectionReview.Evaluate(
            RemoteMcpConnectionReviewReference.FromSnapshot(bound), bound, tool.Name, tool.ToolRevision);

        Assert.Equal(RemoteMcpInvocationDenial.IdentityBindingMissing, unboundValidity.Denial);
        Assert.True(boundValidity.Allowed);
        Assert.True(boundValidity.CredentialValidityRequired);
        Assert.Equal("binding-1", boundValidity.IdentityBindingReference);
    }

    [Fact]
    public void ReviewReferenceRequiresEnabledConnectionWithCatalog()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RemoteMcpConnectionReviewReference.FromSnapshot(Snapshot(Configuration(), null)));
        Assert.Throws<InvalidOperationException>(() =>
            RemoteMcpConnectionReviewReference.FromSnapshot(
                Snapshot(Configuration(), Catalog("forecast"), RemoteMcpConnectionState.Disabled)));
    }

    [Fact]
    public void DirectoryEntryNeverCarriesBindingReferenceOrSchemas()
    {
        var snapshot = Snapshot(Configuration(oauth: true, binding: "binding-secret-reference"), Catalog("forecast"));

        var entry = RemoteMcpConnectionDirectoryEntry.FromSnapshot(snapshot);
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(entry.IdentityBound);
        Assert.Equal(snapshot.Configuration.ConfigurationSha256, entry.ConfigurationSha256);
        Assert.Equal(1, entry.ToolCount);
        Assert.DoesNotContain("binding-secret-reference", json, StringComparison.Ordinal);
        Assert.DoesNotContain("inputSchema", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schemaJson", json, StringComparison.OrdinalIgnoreCase);
    }

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

    private static RemoteMcpConnectionConfiguration Configuration(
        long revision = 1,
        string endpoint = "https://mcp.example.com/mcp",
        bool oauth = false,
        string? binding = null) =>
        new(
            Connection,
            revision,
            "Weather",
            endpoint,
            oauth ? "https://mcp.example.com" : null,
            oauth ? RemoteMcpAuthenticationMode.DelegatedOAuth : RemoteMcpAuthenticationMode.None,
            binding,
            RemoteMcpTransportProfile.StreamableHttp20250618);

    private static RemoteMcpDiscoveryCatalogPin Catalog(
        string toolName,
        long configurationRevision = 1,
        long discoveryRevision = 1,
        bool changedSchema = false)
    {
        var schema = changedSchema
            ? """{"type":"object","properties":{"city":{"type":"string"}}}"""
            : """{"type":"object"}""";
        var parsed = RemoteMcpMetadataParser.ParseToolsList(Encoding.UTF8.GetBytes(
            $$$"""{"result":{"tools":[{"name":"{{{toolName}}}","inputSchema":{{{schema}}}}]}}"""));
        return new(Connection, configurationRevision, discoveryRevision, parsed.CatalogSha256, null, parsed.Tools);
    }
}
