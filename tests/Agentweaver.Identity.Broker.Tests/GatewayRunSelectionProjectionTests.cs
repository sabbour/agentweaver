extern alias GatewayHost;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayRunSelectionProjectionTests
{
    private const string SecretIdSentinel = "SENTINEL_BYOK_SECRET_ID_DO_NOT_LEAK";
    private const string SecretVersionSentinel = "SENTINEL_BYOK_SECRET_VERSION_DO_NOT_LEAK";
    private const string CredentialVersionSentinel = "SENTINEL_CREDENTIAL_VERSION_DO_NOT_LEAK";

    [Theory]
    [InlineData("PUT", false)]
    [InlineData("GET", false)]
    [InlineData("PUT", true)]
    [InlineData("GET", true)]
    public async Task PublicRunSelectionRoutesProjectCredentialsAndPreserveSafeMetadata(
        string method,
        bool oddCasingAndDuplicates)
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var ownerPayload = BuildFullByokPayload(oddCasingAndDuplicates);
        var ownerCalls = 0;

        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new ProjectsStubHandler((req, ct) =>
            {
                ownerCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ownerPayload, Encoding.UTF8, "application/json"),
                });
            }),
            (owner, req, ct) => throw new InvalidOperationException($"Unexpected owner {owner}."));

        // Verify direct private owner payload still retains pins and secret references.
        using (var directDoc = JsonDocument.Parse(ownerPayload))
        {
            var root = directDoc.RootElement;
            Assert.Contains(SecretIdSentinel, ownerPayload, StringComparison.Ordinal);
            Assert.Contains(CredentialVersionSentinel, ownerPayload, StringComparison.Ordinal);
            Assert.Contains(root.EnumerateObject(), p =>
                string.Equals(p.Name, "modelSelection", StringComparison.OrdinalIgnoreCase));
        }

        var bearer = CreateToken(signingKey);
        var requestBody = """{"expectedProjectConfigRevision":2,"expectedPlatformRuntimeRevision":1,"context":{"revision":"ctx-1"}}""";

        for (var attempt = 0; attempt < (method == "PUT" ? 2 : 1); attempt++)
        {
            using var request = new HttpRequestMessage(
                new HttpMethod(method),
                "/api/v1/projects/proj-1/runs/run-1/selection");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
            if (method == "PUT")
                request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            using var response = await gateway.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

            Assert.DoesNotContain(SecretIdSentinel, body, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretVersionSentinel, body, StringComparison.Ordinal);
            Assert.DoesNotContain(CredentialVersionSentinel, body, StringComparison.Ordinal);
            Assert.DoesNotContain("credentialReference", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credentialVersion", body, StringComparison.OrdinalIgnoreCase);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal("proj-1", root.GetProperty("projectId").GetString());
            Assert.Equal("run-1", root.GetProperty("runId").GetString());
            var modelSelectionProp = oddCasingAndDuplicates ? "ModelSelection" : "modelSelection";
            var bindingProp = oddCasingAndDuplicates ? "ProviderBindingRevision" : "providerBindingRevision";
            Assert.Equal("rc-1", root.GetProperty(modelSelectionProp)
                .GetProperty(bindingProp).GetProperty("rateCardId").GetString());
            var provenance = root.GetProperty(modelSelectionProp)
                .GetProperty(bindingProp).GetProperty("rateCardPin").GetProperty("provenance");
            Assert.Equal("manual", provenance.GetProperty("source").GetString());
            Assert.Equal("https://pricing.example.test/manual", provenance.GetProperty("sourceUrl").GetString());
            Assert.Equal("2026-11-26", provenance.GetProperty("retrievedOn").GetString());
            Assert.Equal("slot-1", root.GetProperty(modelSelectionProp)
                .GetProperty("credentialIdentity").GetProperty("credentialSlotId").GetString());
            var projectConfigProp = oddCasingAndDuplicates ? "ProjectConfiguration" : "projectConfiguration";
            Assert.Equal("gh-api-ref", root.GetProperty(projectConfigProp)
                .GetProperty("sourceControl").GetProperty("apiSecretReference").GetProperty("id").GetString());
            Assert.Equal("gh-checkout-ref", root.GetProperty(projectConfigProp)
                .GetProperty("sourceControl").GetProperty("checkoutSecretReference").GetProperty("id").GetString());
            Assert.Equal("gh-webhook-ref", root.GetProperty(projectConfigProp)
                .GetProperty("sourceControl").GetProperty("webhookSecretReference").GetProperty("id").GetString());
        }

        Assert.Equal(method == "PUT" ? 2 : 1, ownerCalls);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task PublicRunSelectionPreservesCompatibleNonByokPayload(string method)
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        const string hostedPayload = """
            {
              "projectId": "proj-1",
              "runId": "run-1",
              "projectRevision": 1,
              "projectConfigurationRevision": 1,
              "platformRuntimeRevision": 1,
              "contextRevision": "ctx-1",
              "modelSelection": {
                "reference": "hosted-gpt",
                "sourceMode": "hostedCopilot",
                "connectionId": "11111111-1111-1111-1111-111111111111",
                "reasoningEffort": "high"
              },
              "providers": [],
              "egressAllowlist": [],
              "runLimits": { "maxModelTurns": 10, "maxToolCalls": 20, "maxChildren": 2, "maxConcurrentChildren": 1, "maxWallTimeSeconds": 60 },
              "projectConfiguration": {
                "modelSelection": null,
                "modelTaskOverrides": [],
                "modelRoleOverrides": [],
                "modelAgentOverrides": []
              },
              "egressBaseline": [],
              "requiredEgress": [],
              "modelSelections": [],
              "modelTaskSelections": []
            }
            """;

        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new ProjectsStubHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(hostedPayload, Encoding.UTF8, "application/json"),
            })),
            (owner, req, ct) => throw new InvalidOperationException($"Unexpected owner {owner}."));

        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            "/api/v1/projects/proj-1/runs/run-1/selection");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(signingKey));
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
        if (method == "PUT")
            request.Content = new StringContent(
                """{"expectedProjectConfigRevision":1,"expectedPlatformRuntimeRevision":1,"context":{"revision":"ctx-1"}}""",
                Encoding.UTF8,
                "application/json");

        using var response = await gateway.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("hosted-gpt", doc.RootElement.GetProperty("modelSelection").GetProperty("reference").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("projectConfiguration").GetProperty("modelSelection").ValueKind);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("invalid-surrogate")]
    [InlineData("array-root")]
    [InlineData("missing-model-selection")]
    [InlineData("null-model-selection")]
    [InlineData("non-object-model-selection")]
    [InlineData("missing-model-selections")]
    [InlineData("null-model-selections")]
    [InlineData("non-array-model-selections")]
    [InlineData("non-object-model-selections-item")]
    [InlineData("missing-model-selections-selection")]
    [InlineData("null-model-selections-selection")]
    [InlineData("non-object-model-selections-selection")]
    [InlineData("missing-project-configuration")]
    [InlineData("null-project-configuration")]
    [InlineData("non-object-project-configuration")]
    [InlineData("non-array-task-overrides")]
    [InlineData("non-object-role-override-selection")]
    [InlineData("non-object-agent-override-selection")]
    [InlineData("non-object-provider-binding-revision")]
    [InlineData("non-200-status")]
    [InlineData("non-json-content-type")]
    public async Task MalformedOwnerResponseFailsClosedWith502AndNoLeak(string fault)
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);

        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new ProjectsStubHandler((req, ct) =>
            {
                var status = fault == "non-200-status" ? HttpStatusCode.Created : HttpStatusCode.OK;
                var mediaType = fault == "non-json-content-type" ? "text/plain" : "application/json";
                var payload = fault switch
                {
                    "invalid-json" => $$"""{"modelSelection":{"credentialReference":{"id":"{{SecretIdSentinel}}"}""",
                    "invalid-surrogate" => """{"modelSelection":{"reference":"m"},"\uD800":1}""",
                    "array-root" => $$"""[{"id":"{{SecretIdSentinel}}"}]""",
                    "missing-model-selection" => """{"projectId":"proj-1","runId":"run-1","projectRevision":1,"projectConfigurationRevision":1,"platformRuntimeRevision":1,"contextRevision":"ctx-1","providers":[],"egressAllowlist":[],"runLimits":{"maxModelTurns":10,"maxToolCalls":20,"maxChildren":2,"maxConcurrentChildren":1,"maxWallTimeSeconds":60},"egressBaseline":[],"requiredEgress":[],"modelSelections":[],"modelTaskSelections":[],"projectConfiguration":{"modelSelection":null,"modelTaskOverrides":[],"modelRoleOverrides":[],"modelAgentOverrides":[]}}""",
                    "null-model-selection" => """{"modelSelection":null}""",
                    "non-object-model-selection" => """{"modelSelection":"bad","modelSelections":[],"projectConfiguration":{}}""",
                    "missing-model-selections" => """{"modelSelection":{"reference":"m"},"projectConfiguration":{}}""",
                    "null-model-selections" => """{"modelSelection":{"reference":"m"},"modelSelections":null,"projectConfiguration":{}}""",
                    "non-array-model-selections" => """{"modelSelection":{"reference":"m"},"modelSelections":{},"projectConfiguration":{}}""",
                    "non-object-model-selections-item" => """{"modelSelection":{"reference":"m"},"modelSelections":["bad"],"projectConfiguration":{}}""",
                    "missing-model-selections-selection" => """{"modelSelection":{"reference":"m"},"modelSelections":[{"connectionScope":"project"}],"projectConfiguration":{}}""",
                    "null-model-selections-selection" => """{"modelSelection":{"reference":"m"},"modelSelections":[{"selection":null}],"projectConfiguration":{}}""",
                    "non-object-model-selections-selection" => """{"modelSelection":{"reference":"m"},"modelSelections":[{"selection":123}],"projectConfiguration":{}}""",
                    "missing-project-configuration" => """{"modelSelection":{"reference":"m"},"modelSelections":[]}""",
                    "null-project-configuration" => """{"modelSelection":{"reference":"m"},"modelSelections":[],"projectConfiguration":null}""",
                    "non-object-project-configuration" => """{"modelSelection":{"reference":"m"},"modelSelections":[],"projectConfiguration":[]}""",
                    "non-array-task-overrides" => """{"modelSelection":{"reference":"m"},"modelSelections":[],"projectConfiguration":{"modelTaskOverrides":{}}}""",
                    "non-object-role-override-selection" => """{"modelSelection":{"reference":"m"},"modelSelections":[],"projectConfiguration":{"modelRoleOverrides":[{"selection":"bad"}]}}""",
                    "non-object-agent-override-selection" => """{"modelSelection":{"reference":"m"},"modelSelections":[],"projectConfiguration":{"modelAgentOverrides":[{"selection":42}]}}""",
                    "non-object-provider-binding-revision" => """{"modelSelection":{"reference":"m","providerBindingRevision":"bad"},"modelSelections":[],"projectConfiguration":{}}""",
                    _ => $"{{\"modelSelection\":{{\"reference\":\"m\",\"credentialReference\":{{\"id\":\"{SecretIdSentinel}\",\"version\":\"{SecretVersionSentinel}\"}}}}}}",
                };
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(payload, Encoding.UTF8, mediaType),
                });
            }),
            (owner, req, ct) => throw new InvalidOperationException($"Unexpected owner {owner}."));

        foreach (var method in new[] { "GET", "PUT" })
        {
            using var request = new HttpRequestMessage(
                new HttpMethod(method),
                "/api/v1/projects/proj-1/runs/run-1/selection");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(signingKey));
            request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
            if (method == "PUT")
                request.Content = new StringContent(
                    """{"expectedProjectConfigRevision":1,"expectedPlatformRuntimeRevision":1,"context":{"revision":"ctx-1"}}""",
                    Encoding.UTF8,
                    "application/json");

            using var response = await gateway.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.DoesNotContain(SecretIdSentinel, body, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretVersionSentinel, body, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("run_selection_owner_contract_invalid", doc.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task OversizedOwnerResponseReturns502BeforeWritingBody()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var oversized = $"{{\"modelSelection\":{{\"reference\":\"{new string('a', (8 * 1024 * 1024) + 128)}\"}}}}";

        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new ProjectsStubHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(oversized, Encoding.UTF8, "application/json"),
            })),
            (owner, req, ct) => throw new InvalidOperationException($"Unexpected owner {owner}."));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/projects/proj-1/runs/run-1/selection");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(signingKey));
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("owner_response_too_large", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OpenApiEffectiveRunSelectionOmitsCredentialReferenceAndVersion()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            (owner, req, ct) => throw new InvalidOperationException("OpenAPI should not call owner."));

        using var resp = await gateway.Client.GetAsync("/openapi/v1.json");
        resp.EnsureSuccessStatusCode();
        using var openApi = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var schemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");

        var effective = schemas.GetProperty("EffectiveRunSelection").GetProperty("properties");
        Assert.Equal("#/components/schemas/PublicModelSelectionSettings",
            effective.GetProperty("modelSelection").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/PublicProjectConfiguration",
            effective.GetProperty("projectConfiguration").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/PublicEffectiveModelSelection",
            effective.GetProperty("modelSelections").GetProperty("items").GetProperty("$ref").GetString());

        var publicSelProps = schemas.GetProperty("PublicModelSelectionSettings").GetProperty("properties");
        Assert.False(publicSelProps.TryGetProperty("credentialReference", out _));
        Assert.True(publicSelProps.TryGetProperty("credentialIdentity", out _));
        var publicBindingProps = schemas.GetProperty("PublicModelProviderBindingRevision").GetProperty("properties");
        Assert.False(publicBindingProps.TryGetProperty("credentialReference", out _));
        Assert.False(publicBindingProps.TryGetProperty("credentialVersion", out _));
        Assert.True(publicBindingProps.TryGetProperty("credentialIdentity", out _));
        Assert.True(publicBindingProps.TryGetProperty("rateCardPin", out _));
        var rateCardPin = schemas.GetProperty("RuntimeRateCardPin");
        Assert.Contains("provenance", rateCardPin.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        Assert.True(rateCardPin.GetProperty("properties").TryGetProperty("provenance", out _));

        // Shared ModelSelectionSettings remains intact for non-run-selection routes.
        Assert.True(schemas.GetProperty("ModelSelectionSettings").GetProperty("properties")
            .TryGetProperty("credentialReference", out _));
    }

    private static string BuildFullByokPayload(bool oddCasingAndDuplicates)
    {
        var credKey = oddCasingAndDuplicates ? "CredentialReference" : "credentialReference";
        var credKeyDup = oddCasingAndDuplicates ? "CREDENTIALREFERENCE" : "credentialReference";
        var verKey = oddCasingAndDuplicates ? "CredentialVersion" : "credentialVersion";
        var verKeyDup = oddCasingAndDuplicates ? "CREDENTIALVERSION" : "credentialVersion";
        var bindingKey = oddCasingAndDuplicates ? "ProviderBindingRevision" : "providerBindingRevision";

        return $$"""
        {
          "projectId": "proj-1",
          "runId": "run-1",
          "projectRevision": 3,
          "projectConfigurationRevision": 2,
          "platformRuntimeRevision": 1,
          "contextRevision": "ctx-1",
          "{{(oddCasingAndDuplicates ? "ModelSelection" : "modelSelection")}}": {
            "reference": "prov-byok",
            "sourceMode": "byok",
            "{{credKey}}": { "id": "{{SecretIdSentinel}}", "version": "{{SecretVersionSentinel}}" },
            "{{credKeyDup}}": null,
            "credentialIdentity": { "credentialSlotId": "slot-1", "scope": "project", "projectId": "proj-1", "providerId": "prov-byok" },
            "{{bindingKey}}": {
              "contractVersion": 4,
              "providerId": "prov-byok",
              "scope": "project",
              "projectId": "proj-1",
              "displayName": "BYOK",
              "providerType": "openai",
              "baseUrl": "https://api.example.test/v1",
              "deployment": "gpt-test",
              "reasoningEfforts": ["high"],
              "providerRevision": 7,
              "{{verKey}}": "{{CredentialVersionSentinel}}",
              "{{verKeyDup}}": null,
              "{{credKey}}": { "id": "{{SecretIdSentinel}}", "version": "{{SecretVersionSentinel}}" },
              "rateCardId": "rc-1",
              "rateCardVersion": "v1",
              "rateCardPin": {
                "rateCardId": "rc-1",
                "rateCardVersion": "v1",
                "contentHash": "pin-hash-1",
                "provenance": { "source": "manual", "sourceUrl": "https://pricing.example.test/manual", "retrievedOn": "2026-11-26" }
              },
              "configurationHash": "hash-1"
            }
          },
          "{{(oddCasingAndDuplicates ? "ModelSelections" : "modelSelections")}}": [
            {
              "connectionScope": "project",
              "{{(oddCasingAndDuplicates ? "Selection" : "selection")}}": {
                "reference": "prov-byok",
                "{{credKey}}": { "id": "{{SecretIdSentinel}}", "version": "{{SecretVersionSentinel}}" },
                "{{bindingKey}}": {
                  "contractVersion": 4,
                  "providerId": "prov-byok",
                  "scope": "project",
                  "displayName": "BYOK",
                  "providerType": "openai",
                  "baseUrl": "https://api.example.test/v1",
                  "deployment": "gpt-test",
                  "reasoningEfforts": ["high"],
                  "providerRevision": 7,
                  "{{verKey}}": "{{CredentialVersionSentinel}}",
                  "{{credKey}}": { "id": "{{SecretIdSentinel}}", "version": "{{SecretVersionSentinel}}" },
                  "configurationHash": "hash-1"
                }
              }
            }
          ],
          "{{(oddCasingAndDuplicates ? "ProjectConfiguration" : "projectConfiguration")}}": {
            "{{(oddCasingAndDuplicates ? "ModelSelection" : "modelSelection")}}": {
              "reference": "prov-byok",
              "{{credKey}}": { "id": "{{SecretIdSentinel}}", "version": "{{SecretVersionSentinel}}" },
              "{{bindingKey}}": {
                "contractVersion": 4,
                "providerId": "prov-byok",
                "scope": "project",
                "displayName": "BYOK",
                "providerType": "openai",
                "baseUrl": "https://api.example.test/v1",
                "deployment": "gpt-test",
                "reasoningEfforts": ["high"],
                "providerRevision": 7,
                "{{verKey}}": "{{CredentialVersionSentinel}}",
                "configurationHash": "hash-1"
              }
            },
            "{{(oddCasingAndDuplicates ? "ModelTaskOverrides" : "modelTaskOverrides")}}": [
              {
                "taskKind": "planning",
                "selection": {
                  "reference": "prov-byok",
                  "{{credKey}}": { "id": "{{SecretIdSentinel}}", "version": "{{SecretVersionSentinel}}" },
                  "{{bindingKey}}": {
                    "{{verKey}}": "{{CredentialVersionSentinel}}",
                    "{{credKey}}": { "id": "{{SecretIdSentinel}}", "version": "{{SecretVersionSentinel}}" }
                  }
                }
              }
            ],
            "{{(oddCasingAndDuplicates ? "ModelRoleOverrides" : "modelRoleOverrides")}}": [
              {
                "roleId": "architect",
                "selection": {
                  "reference": "prov-byok",
                  "{{credKey}}": { "id": "{{SecretIdSentinel}}", "version": "{{SecretVersionSentinel}}" }
                }
              }
            ],
            "{{(oddCasingAndDuplicates ? "ModelAgentOverrides" : "modelAgentOverrides")}}": [
              {
                "agentId": "agent-1",
                "selection": {
                  "reference": "prov-byok",
                  "{{credKey}}": { "id": "{{SecretIdSentinel}}", "version": "{{SecretVersionSentinel}}" }
                }
              }
            ],
            "sourceControl": {
              "authMode": "secret",
              "repository": { "owner": "sabbour", "name": "agentweaver" },
              "apiSecretReference": { "id": "gh-api-ref", "version": "1" },
              "checkoutSecretReference": { "id": "gh-checkout-ref", "version": "1" },
              "webhookSecretReference": { "id": "gh-webhook-ref", "version": "1" }
            }
          },
          "providers": [],
          "egressAllowlist": [],
          "runLimits": { "maxModelTurns": 10, "maxToolCalls": 20, "maxChildren": 2, "maxConcurrentChildren": 1, "maxWallTimeSeconds": 60 },
          "egressBaseline": [],
          "requiredEgress": [],
          "modelTaskSelections": []
        }
        """;
    }

    private static string CreateToken(SecurityKey signingKey)
    {
        var claims = new List<Claim>
        {
            new("sub", "user-1"),
            new("scope", "api.read projects.orchestrator"),
        };
        var token = new JwtSecurityToken(
            issuer: new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            audience: "https://api.test",
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class ProjectsStubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
