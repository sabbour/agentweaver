using Agentweaver.Abstractions;

namespace Agentweaver.Environment.Tests;

public sealed class RemoteMcpContractTests
{
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
    }
}
