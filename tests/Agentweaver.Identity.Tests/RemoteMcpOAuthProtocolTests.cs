using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class RemoteMcpOAuthProtocolTests
{
    private static readonly Uri Resource = new("https://mcp.example.test/resource");
    private static readonly Uri Issuer = new("https://issuer.example.test/");
    private static readonly Uri AuthorizationEndpoint = new("https://issuer.example.test/authorize");
    private static readonly Uri TokenEndpoint = new("https://issuer.example.test/token");

    [Fact]
    public void MetadataRequiresExactResourceIssuerApprovedEndpointsAndS256CodeFlow()
    {
        var binding = Binding();
        var metadata = Validate(binding);

        Assert.Equal(binding.BindingHash, metadata.BindingHash);
        Assert.Equal(Resource, metadata.Resource);
        Assert.Equal(Issuer, metadata.Issuer);
        Assert.Equal(AuthorizationEndpoint, metadata.AuthorizationEndpoint);
        Assert.Equal(TokenEndpoint, metadata.TokenEndpoint);
        Assert.Equal(["tools.list", "tools.read"], metadata.ScopesSupported);

        Assert.Equal("remote_mcp_metadata_binding_mismatch", Assert.Throws<RemoteMcpOAuthProtocolException>(
            () => Validate(binding, resource: "https://mcp.example.test/other")).Code);
        Assert.Equal("remote_mcp_metadata_binding_mismatch", Assert.Throws<RemoteMcpOAuthProtocolException>(
            () => Validate(binding, issuer: "https://issuer.example.test/other")).Code);
        Assert.Equal("remote_mcp_metadata_endpoint_unapproved",
            Assert.Throws<RemoteMcpOAuthProtocolException>(
                () => Validate(binding, authorizationEndpoint: "https://other.example.test/authorize")).Code);
        Assert.Equal("remote_mcp_metadata_profile_unsupported",
            Assert.Throws<RemoteMcpOAuthProtocolException>(
                () => Validate(binding, challengeMethods: ["plain"])).Code);
        Assert.Equal("remote_mcp_metadata_profile_unsupported",
            Assert.Throws<RemoteMcpOAuthProtocolException>(
                () => Validate(binding, responseTypes: ["token"])).Code);
        Assert.Equal("remote_mcp_metadata_profile_unsupported",
            Assert.Throws<RemoteMcpOAuthProtocolException>(
                () => Validate(binding, serverScopes: ["tools.read"])).Code);
    }

    [Fact]
    public void MetadataRejectsAmbiguousMalformedAndUncanonicalValues()
    {
        var binding = Binding();
        var duplicate = Encoding.UTF8.GetBytes(
            $$"""{"resource":"{{Resource.AbsoluteUri}}","resource":"{{Resource.AbsoluteUri}}","authorization_servers":["{{Issuer.AbsoluteUri}}"]}""");

        Assert.Equal("remote_mcp_metadata_invalid",
            Assert.Throws<RemoteMcpOAuthProtocolException>(
                () => RemoteMcpOAuthProtocol.ValidateMetadata(
                    binding, duplicate, AuthorizationServerJson(), [AuthorizationEndpoint, TokenEndpoint])).Code);
        Assert.Equal("remote_mcp_metadata_invalid",
            Assert.Throws<RemoteMcpOAuthProtocolException>(
                () => RemoteMcpOAuthProtocol.ValidateMetadata(
                    binding, "{broken"u8.ToArray(), AuthorizationServerJson(),
                    [AuthorizationEndpoint, TokenEndpoint])).Code);
        Assert.Equal("remote_mcp_metadata_invalid",
            Assert.Throws<RemoteMcpOAuthProtocolException>(
                () => Validate(binding, authorizationEndpoint: "https://ISSUER.example.test/authorize")).Code);
        Assert.Equal("remote_mcp_metadata_endpoint_unapproved",
            Assert.Throws<RemoteMcpOAuthProtocolException>(
                () => RemoteMcpOAuthProtocol.ValidateMetadata(
                    binding, ResourceJson(), AuthorizationServerJson(),
                    [new Uri("https://ISSUER.example.test/authorize"), TokenEndpoint])).Code);
    }

    [Fact]
    public void AuthorizationRequestUsesOnlyBoundValuesAndNeverIncludesVerifier()
    {
        var binding = Binding();
        var metadata = Validate(binding);
        var material = RemoteMcpOAuthConsentMaterial.Create(
            binding, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5));
        var state = material.State.GetValue();
        var verifier = material.PkceVerifier.GetValue();

        var uri = RemoteMcpOAuthProtocol.BuildAuthorizationRequestUri(
            binding, metadata, "registered-client", material);
        var query = ParseQuery(uri);

        Assert.Equal("code", query["response_type"]);
        Assert.Equal("registered-client", query["client_id"]);
        Assert.Equal(binding.RedirectUri.AbsoluteUri, query["redirect_uri"]);
        Assert.Equal("tools.list tools.read", query["scope"]);
        Assert.Equal(state, query["state"]);
        Assert.Equal(material.PkceChallenge, query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(Resource.AbsoluteUri, query["resource"]);
        Assert.DoesNotContain(verifier, uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorizationRequestRejectsStaleBindingAndParameterCollision()
    {
        var binding = Binding();
        var material = RemoteMcpOAuthConsentMaterial.Create(
            binding, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5));
        var metadata = Validate(binding);

        Assert.Equal("remote_mcp_authorization_request_invalid",
            Assert.Throws<RemoteMcpOAuthProtocolException>(() =>
                RemoteMcpOAuthProtocol.BuildAuthorizationRequestUri(
                    Binding(projectId: "other-project"), metadata, "registered-client", material)).Code);
        Assert.Equal("remote_mcp_authorization_request_invalid",
            Assert.Throws<RemoteMcpOAuthProtocolException>(() =>
                RemoteMcpOAuthProtocol.BuildAuthorizationRequestUri(
                    binding,
                    Validate(
                        binding,
                        authorizationEndpoint: "https://issuer.example.test/authorize?client_id=wrong",
                        approvedOAuthEndpoints:
                        [
                            "https://issuer.example.test/authorize?client_id=wrong",
                            TokenEndpoint.AbsoluteUri
                        ]),
                    "registered-client", material)).Code);
    }

    private static RemoteMcpOAuthServerMetadata Validate(
        RemoteMcpOAuthConnectionBinding binding,
        string? resource = null,
        string? issuer = null,
        string? authorizationEndpoint = null,
        string[]? responseTypes = null,
        string[]? challengeMethods = null,
        string[]? serverScopes = null,
        string[]? approvedOAuthEndpoints = null) =>
        RemoteMcpOAuthProtocol.ValidateMetadata(
            binding,
            ResourceJson(resource),
            AuthorizationServerJson(
                issuer, authorizationEndpoint, responseTypes, challengeMethods, serverScopes),
            (approvedOAuthEndpoints ?? [AuthorizationEndpoint.AbsoluteUri, TokenEndpoint.AbsoluteUri])
                .Select(value => new Uri(value)));

    private static byte[] ResourceJson(string? resource = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            resource = resource ?? Resource.AbsoluteUri,
            authorization_servers = new[] { Issuer.AbsoluteUri },
            scopes_supported = new[] { "tools.list", "tools.read" }
        });

    private static byte[] AuthorizationServerJson(
        string? issuer = null,
        string? authorizationEndpoint = null,
        string[]? responseTypes = null,
        string[]? challengeMethods = null,
        string[]? scopesSupported = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["issuer"] = issuer ?? Issuer.AbsoluteUri,
            ["authorization_endpoint"] = authorizationEndpoint ?? AuthorizationEndpoint.AbsoluteUri,
            ["token_endpoint"] = TokenEndpoint.AbsoluteUri,
            ["response_types_supported"] = responseTypes ?? ["code"],
            ["code_challenge_methods_supported"] = challengeMethods ?? ["S256"],
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" }
        };
        if (scopesSupported is not null)
            metadata["scopes_supported"] = scopesSupported;
        return JsonSerializer.SerializeToUtf8Bytes(metadata);
    }

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
                parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')),
                StringComparer.Ordinal);

    private static RemoteMcpOAuthConnectionBinding Binding(string projectId = "project-1") =>
        new("human-1", "tenant-1", projectId, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            7, new string('a', 64), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new Uri("https://mcp.example.test/"), Resource, Issuer,
            new Uri("https://identity.example.test/oauth/callback"),
            RemoteMcpOAuthConnectionBinding.SupportedTransportProfile,
            ["tools.read", "tools.list"]);
}
