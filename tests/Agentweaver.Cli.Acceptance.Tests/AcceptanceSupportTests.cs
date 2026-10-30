using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Xunit;

namespace Agentweaver.Cli.Acceptance.Tests;

public sealed class AcceptanceSupportTests
{
    [Fact]
    public void CanonicalJsonMatchesSharedJavaScriptMigrationVectors()
    {
        JsonArray vectors = JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "p1-json-migration-vectors.json")))!.AsArray();

        foreach (JsonObject vector in vectors.OfType<JsonObject>())
        {
            JsonNode value = vector["value"]!.DeepClone();
            string expected = vector["json"]!.GetValue<string>();
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

            Assert.Equal(expected, ApiAcceptanceSupport.CanonicalJson(value));
            Assert.Equal(expectedHash, ApiAcceptanceSupport.Sha256Hex(
                Encoding.UTF8.GetBytes(ApiAcceptanceSupport.CanonicalJson(value))));
        }
    }

    [Fact]
    public void RedactedEvidenceDoesNotExposeCamelCaseDescriptorOrUrlQuerySecrets()
    {
        JsonObject receipt = new()
        {
            ["refreshToken"] = "refresh-token-canary",
            ["header"] = new JsonObject { ["name"] = "secret", ["value"] = "descriptor-secret-canary" },
            ["details"] = "See https://example.test/callback?secret=query-secret-canary",
        };

        string serialized = ApiAcceptanceSupport.EvidenceJson(ApiAcceptanceSupport.Redact(receipt));

        Assert.DoesNotContain("refresh-token-canary", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("descriptor-secret-canary", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret-canary", serialized, StringComparison.Ordinal);
        Assert.Contains("https://example.test/callback", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceSerializationPreservesReceiptPropertyOrder()
    {
        JsonObject receipt = new()
        {
            ["z"] = 1,
            ["a"] = 2,
        };

        string serialized = ApiAcceptanceSupport.EvidenceJson(receipt);
        Assert.Equal("{\"z\":1,\"a\":2}", serialized);
        Assert.Equal("{\"a\":2,\"z\":1}", ApiAcceptanceSupport.CanonicalJson(receipt));
        Assert.Equal(
            AcceptanceDeploymentEvidenceProvider.HashJson(receipt),
            AcceptanceDeploymentEvidenceProvider.HashJson(JsonNode.Parse(serialized)!));
    }
}
