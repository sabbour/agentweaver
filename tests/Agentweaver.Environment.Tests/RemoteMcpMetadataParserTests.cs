using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Environment;

namespace Agentweaver.Environment.Tests;

public sealed class RemoteMcpMetadataParserTests
{
    [Fact]
    public void ParsesRegistryPageAndPinsExactVersionAndStaticEndpoint()
    {
        const string pageJson = """
            {"servers":[{"server":{"name":"example/weather","version":"1.2.3","title":"Weather","description":"Weather tools"}}],"metadata":{"nextCursor":"next"}}
            """;
        var pageBytes = Encoding.UTF8.GetBytes(pageJson);
        var page = RemoteMcpMetadataParser.ParseRegistryServerPage(pageBytes);

        Assert.Equal("next", page.NextCursor);
        Assert.Equal(RemoteMcpDigest.Sha256(pageBytes), page.ResponseSha256);
        Assert.Equal("example/weather", Assert.Single(page.Servers).Name);

        const string versionJson = """
            {"server":{"name":"example/weather","version":"1.2.3","remotes":[{"type":"streamable-http","url":"https://MCP.Example.com"}]}}
            """;
        var selection = RemoteMcpMetadataParser.ParseRegistryServerVersion(
            Encoding.UTF8.GetBytes(versionJson),
            "example/weather",
            "1.2.3");

        Assert.Equal("1.2.3", selection.Server.ExactVersion);
        Assert.Equal("https://mcp.example.com/", selection.SelectedEndpointUri);
        Assert.Equal(RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(versionJson)), selection.Server.MetadataSha256);
    }

    [Fact]
    public void ParsesToolsAndProducesStableSortedCatalogPins()
    {
        const string response = """
            {"result":{"tools":[{"name":"zeta","inputSchema":{"type":"object"}},{"name":"alpha","description":"Alpha tool","inputSchema":{"type":"object","properties":{"value":{"type":"string"}}}}]}}
            """;

        var catalog = RemoteMcpMetadataParser.ParseToolsList(Encoding.UTF8.GetBytes(response));

        Assert.Equal(["alpha", "zeta"], catalog.Tools.Select(tool => tool.Name));
        Assert.All(catalog.Tools, tool =>
        {
            Assert.Equal(RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(tool.SchemaJson)), tool.SchemaSha256);
            Assert.Equal(
                RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(
                    $"{tool.Name}\0{tool.Description ?? string.Empty}\0{tool.SchemaSha256}")),
                tool.ToolRevision);
        });
        Assert.Equal(
            RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(string.Join(
                '\n',
                catalog.Tools.Select(tool => $"{tool.Name}\0{tool.ToolRevision}")))),
            catalog.CatalogSha256);
    }

    [Theory]
    [InlineData("""{"result":{"tools":[{"name":"same","inputSchema":{"type":"object"}},{"name":"same","inputSchema":{"type":"object"}}]}}""")]
    [InlineData("""{"result":{"tools":[{"name":"bad","inputSchema":{"type":"array"}}]}}""")]
    [InlineData("""{"result":{"tools":[{"name":"bad","inputSchema":{"type":"object","type":"object"}}]}}""")]
    public void RejectsDuplicateNamesInvalidSchemasAndDuplicateJsonProperties(string json)
    {
        Assert.Throws<InvalidDataException>(() =>
            RemoteMcpMetadataParser.ParseToolsList(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void RejectsDocumentsAndSchemasOverTheirByteLimits()
    {
        Assert.Throws<InvalidDataException>(() =>
            RemoteMcpMetadataParser.ParseToolsList(new byte[RemoteMcpMetadataParser.MaximumDocumentBytes + 1]));

        var oversizedSchema =
            "{\"result\":{\"tools\":[{\"name\":\"large\",\"inputSchema\":{\"type\":\"object\",\"x\":\"" +
            new string('a', RemoteMcpMetadataParser.MaximumSchemaBytes) +
            "\"}}]}}";
        Assert.Throws<InvalidDataException>(() =>
            RemoteMcpMetadataParser.ParseToolsList(Encoding.UTF8.GetBytes(oversizedSchema)));
    }
}
