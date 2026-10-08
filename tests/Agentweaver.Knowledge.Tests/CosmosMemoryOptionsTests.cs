using Agentweaver.Knowledge;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.Knowledge.Tests;

public sealed class CosmosMemoryOptionsTests
{
    [Fact]
    public void ReadOptionalReturnsNullWhenNoCosmosConfigurationExists()
    {
        var options = CosmosMemoryOptions.ReadOptional(new ConfigurationBuilder().Build());

        Assert.Null(options);
    }

    [Fact]
    public void ReadOptionalLoadsVersionedOptionsWithoutCredentials()
    {
        var configuration = Configuration(
            ("Knowledge:CosmosProvider:Endpoint", "https://memory.documents.azure.com/"),
            ("Knowledge:CosmosProvider:DatabaseId", "agentweaver"),
            ("Knowledge:CosmosProvider:ContainerId", "knowledge"),
            ("Knowledge:CosmosProvider:ResourceId", "cosmos-memory"),
            ("Knowledge:CosmosProvider:ResourceGeneration", "3"),
            ("Knowledge:CosmosProvider:OptionsRevision", "cosmos-memory-v1"));

        var options = CosmosMemoryOptions.ReadOptional(configuration);

        Assert.NotNull(options);
        Assert.Equal(new Uri("https://memory.documents.azure.com/"), options.Endpoint);
        Assert.Equal("agentweaver", options.DatabaseId);
        Assert.Equal("knowledge", options.ContainerId);
        Assert.Equal("cosmos-memory", options.ResourceId);
        Assert.Equal(3, options.ResourceGeneration);
        Assert.Equal("cosmos-memory-v1", options.OptionsRevision);
        Assert.Equal(1, options.OptionsSchemaVersion);
        Assert.Equal("/projectId", CosmosMemoryOptions.PartitionKeyPath);
    }

    [Theory]
    [InlineData("http://memory.documents.azure.com/")]
    [InlineData("https://user@memory.documents.azure.com/")]
    [InlineData("https://memory.documents.azure.com/path")]
    [InlineData("https://memory.documents.azure.com/?key=value")]
    public void ValidateRejectsNonResourceCosmosEndpoints(string endpoint)
    {
        var options = ValidOptions() with { Endpoint = new Uri(endpoint) };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void ValidateRejectsInvalidResourceIdentityAndVersions()
    {
        Assert.Throws<ArgumentException>(() =>
            (ValidOptions() with { DatabaseId = "bad/name" }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (ValidOptions() with { ResourceGeneration = 0 }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (ValidOptions() with { OptionsRevision = "revision\ninjection" }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (ValidOptions() with { OptionsSchemaVersion = 2 }).Validate());
    }

    [Fact]
    public void ReadOptionalRejectsPartiallyConfiguredProvider()
    {
        var configuration = Configuration(
            ("Knowledge:CosmosProvider:Endpoint", "https://memory.documents.azure.com/"));

        Assert.Throws<InvalidOperationException>(() =>
            CosmosMemoryOptions.ReadOptional(configuration));
    }

    private static CosmosMemoryOptions ValidOptions() =>
        new(
            new Uri("https://memory.documents.azure.com/"),
            "agentweaver",
            "knowledge",
            "cosmos-memory",
            1,
            "cosmos-memory-v1",
            CosmosMemoryOptions.CurrentOptionsSchemaVersion);

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(item => item.Key, item => (string?)item.Value))
            .Build();
}
