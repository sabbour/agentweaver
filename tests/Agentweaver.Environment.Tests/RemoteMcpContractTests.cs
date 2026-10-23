using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment.Tests;

public sealed class RemoteMcpContractTests
{
    [Fact]
    public void OriginalNineParameterConstructorRemainsAvailable()
    {
        var constructor = typeof(RemoteMcpConnectionConfiguration).GetConstructor(
        [
            typeof(RemoteMcpConnectionReference),
            typeof(long),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(RemoteMcpAuthenticationMode),
            typeof(string),
            typeof(RemoteMcpTransportProfile),
            typeof(RemoteMcpRegistryServerPin),
        ]);

        Assert.NotNull(constructor);
        var configuration = new RemoteMcpConnectionConfiguration(
            new("project-a", Guid.NewGuid()),
            1,
            "Weather",
            "https://mcp.example.com",
            "https://mcp.example.com",
            RemoteMcpAuthenticationMode.DelegatedOAuth,
            null,
            RemoteMcpTransportProfile.StreamableHttp20250618,
            null);

        Assert.Equal(
            RemoteMcpTransportAdapterOptions.ForProfile(RemoteMcpTransportProfile.StreamableHttp20250618),
            configuration.TransportAdapterOptions);
    }

    [Fact]
    public void DelegatedOAuthConfigurationCanTruthfullyRemainUnlinked()
    {
        var configuration = new RemoteMcpConnectionConfiguration(
            new("project-a", Guid.NewGuid()),
            configurationRevision: 1,
            displayName: "Weather",
            endpointUri: "https://mcp.example.com",
            resourceUri: "https://mcp.example.com",
            authenticationMode: RemoteMcpAuthenticationMode.DelegatedOAuth,
            identityBindingReference: null,
            RemoteMcpTransportProfile.StreamableHttp20250618);

        Assert.Null(configuration.IdentityBindingReference);
        Assert.Equal("https://mcp.example.com/", configuration.EndpointUri);
        Assert.Equal("https://mcp.example.com/", configuration.ResourceUri);
        Assert.Equal(64, configuration.ConfigurationSha256.Length);
        Assert.Equal(
            RemoteMcpTransportAdapterOptions.ForProfile(RemoteMcpTransportProfile.StreamableHttp20250618),
            configuration.TransportAdapterOptions);
    }

    [Fact]
    public void AdapterOptionsArePinnedAndUnsupportedValuesFailClosed()
    {
        var options = RemoteMcpTransportAdapterOptions.ForProfile(
            RemoteMcpTransportProfile.StreamableHttp20250618);

        Assert.Equal("streamable-http", options.AdapterId);
        Assert.Equal("1.0.0", options.AdapterVersion);
        Assert.Equal(1, options.OptionsSchemaVersion);
        Assert.Equal("streamable-http-2025-06-18-v1", options.OptionsRevision);
        Assert.Equal("2025-06-18", options.ProtocolVersion);
        Assert.Equal(30, options.RequestTimeoutSeconds);
        Assert.Equal(1024 * 1024, options.MaximumResponseBytes);
        Assert.Equal(16, options.MaximumToolsListPages);
        Assert.Equal("d01cdc1f6ba701256d992bdc1ecc76f6ff3445ccb0c957a285c71c6a5c03fe93", options.OptionsSha256);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RemoteMcpTransportAdapterOptions.ForProfile((RemoteMcpTransportProfile)999));
        Assert.Throws<ArgumentException>(() => new RemoteMcpTransportAdapterOptions(
            "other",
            options.AdapterVersion,
            options.OptionsSchemaVersion,
            options.OptionsRevision,
            options.ProtocolVersion,
            options.RequestTimeoutSeconds,
            options.MaximumResponseBytes,
            options.MaximumToolsListPages));
    }

    [Fact]
    public void AdapterPinDoesNotChangeTheEstablishedConfigurationDigest()
    {
        var connection = new RemoteMcpConnectionReference(
            "project-a",
            Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var configuration = new RemoteMcpConnectionConfiguration(
            connection,
            configurationRevision: 1,
            displayName: "Weather",
            endpointUri: "https://mcp.example.com",
            resourceUri: "https://mcp.example.com",
            authenticationMode: RemoteMcpAuthenticationMode.DelegatedOAuth,
            identityBindingReference: null,
            RemoteMcpTransportProfile.StreamableHttp20250618);
        var priorDigestMaterial = string.Join(
            '\0',
            "project-a",
            "00000000-0000-0000-0000-000000000001",
            "1",
            "Weather",
            "https://mcp.example.com/",
            "https://mcp.example.com/",
            "DelegatedOAuth",
            string.Empty,
            "StreamableHttp20250618",
            string.Empty,
            string.Empty,
            string.Empty);
        var priorDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(priorDigestMaterial)));

        Assert.Equal(priorDigest, configuration.ConfigurationSha256);
    }
}
