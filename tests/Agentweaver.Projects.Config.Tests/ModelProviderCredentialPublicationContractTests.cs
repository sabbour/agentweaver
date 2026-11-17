using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ModelProviderCredentialPublicationContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ContractUsesNamedScopesAndRejectsUnknownFields()
    {
        var request = Request();
        var json = JsonSerializer.Serialize(request, JsonOptions);

        Assert.Contains("\"scope\":\"project\"", json);
        Assert.Equal(
            request,
            JsonSerializer.Deserialize<ModelProviderCredentialPublicationRequest>(json, JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ModelProviderCredentialPublicationRequest>(
            json.Replace("\"scope\":\"project\"", "\"scope\":1", StringComparison.Ordinal), JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ModelProviderCredentialPublicationRequest>(
            json[..^1] + ",\"secret\":\"not-allowed\"}", JsonOptions));
    }

    [Fact]
    public void RequestBindingRequiresTheExactTypedTokenAndBody()
    {
        var request = Request();
        var principal = Principal(request);

        Assert.True(ModelProviderCredentialPublicationEndpoints.TryReadBoundRequest(principal, out var actorId));
        Assert.Equal("actor-1", actorId);
        Assert.True(ModelProviderCredentialPublicationEndpoints.MatchesTokenBinding(principal, request));
        Assert.False(ModelProviderCredentialPublicationEndpoints.MatchesTokenBinding(
            principal, request with { CredentialVersion = "fedcba9876543210fedcba9876543210" }));

        var wrongPurpose = Principal(request, purpose: "run-binding-query");
        Assert.False(ModelProviderCredentialPublicationEndpoints.TryReadBoundRequest(wrongPurpose, out _));
    }

    [Theory]
    [InlineData("", "0123456789abcdef0123456789abcdef")]
    [InlineData("project-1", "not-a-version")]
    public void InvalidPublicationRequestsAreRejected(string? projectId, string credentialVersion)
    {
        var request = Request() with { ProjectId = projectId, CredentialVersion = credentialVersion };

        Assert.False(request.IsValid);
    }

    private static ModelProviderCredentialPublicationRequest Request() =>
        new(
            Guid.Parse("ad451e41-9b48-412f-99dc-ad6a6cd4080c"),
            7,
            ModelProviderBindingScope.Project,
            "project-1",
            "provider-1",
            "0123456789abcdef0123456789abcdef");

    private static ClaimsPrincipal Principal(
        ModelProviderCredentialPublicationRequest request,
        string purpose = "credential-publication") =>
        new(new ClaimsIdentity(
        [
            new Claim("sub", "identity-broker"),
            new Claim("purpose", purpose),
            new Claim("actor_id", "actor-1"),
            new Claim("operation_id", request.OperationId.ToString("D")),
            new Claim("expected_provider_revision", request.ExpectedProviderRevision.ToString()),
            new Claim("scope", "project"),
            new Claim("project_id", request.ProjectId ?? string.Empty),
            new Claim("provider_id", request.ProviderId),
            new Claim("credential_version", request.CredentialVersion),
        ], "test"));
}
