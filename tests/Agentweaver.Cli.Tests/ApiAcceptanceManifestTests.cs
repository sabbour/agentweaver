using System.Text.Json.Nodes;
using Agentweaver.Cli;

namespace Agentweaver.Cli.Tests;

public sealed class ApiAcceptanceManifestTests
{
    [Fact]
    public async Task BoardDeleteAcceptsOnlyTheBoundedActorOptionalManifest()
    {
        JsonObject root = BoardManifest();
        string path = WriteManifest(root);
        try
        {
            AcceptanceManifest manifest = await ApiAcceptanceSupport.ReadManifestAsync(path, CancellationToken.None);

            Assert.Equal("board-delete", manifest.Journey);
            Assert.Null(ApiAcceptanceSupport.GetString(manifest.Environment, "actorId"));
            Assert.Equal("owned-disposable-board", ApiAcceptanceSupport.GetString(manifest.FixturePlan, "kind"));
            Assert.Equal("https://api.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io",
                new Uri(ApiAcceptanceSupport.GetString(manifest.Environment, "baseUrl")!).GetLeftPart(UriPartial.Authority));
            Assert.Equal("https://agentweaver.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io/",
                ApiAcceptanceSupport.GetString(manifest.Environment, "issuer"));
            Assert.Equal(ApiAcceptanceSupport.GetString(manifest.Environment, "issuer"),
                ApiAcceptanceSupport.GetString(manifest.Environment, "audience"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BoardDeleteRejectsGatewayOriginAsBrokerIssuer()
    {
        JsonObject root = BoardManifest();
        root["environment"]!["issuer"] = "https://api.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io/";
        string path = WriteManifest(root);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => ApiAcceptanceSupport.ReadManifestAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BoardDeleteRejectsCallerSuppliedResourceIdentifiers()
    {
        JsonObject root = BoardManifest();
        root["projectId"] = "caller-project";
        string path = WriteManifest(root);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => ApiAcceptanceSupport.ReadManifestAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExistingP1AdmissionStillRequiresActorAndAcceptsItsCurrentPlan()
    {
        JsonObject root = P1Manifest();
        string path = WriteManifest(root);
        try
        {
            AcceptanceManifest manifest = await ApiAcceptanceSupport.ReadManifestAsync(path, CancellationToken.None);
            Assert.Equal("gate-answer", manifest.Journey);
            Assert.Equal("actor-1", ApiAcceptanceSupport.GetString(manifest.Environment, "actorId"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static JsonObject BoardManifest() => new()
    {
        ["executionIntent"] = "execute",
        ["journey"] = "board-delete",
        ["environment"] = new JsonObject
        {
            ["baseUrl"] = "https://api.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io",
            ["issuer"] = "https://agentweaver.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io/",
            ["audience"] = "https://agentweaver.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io/",
            ["tenantId"] = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        },
        ["pins"] = new JsonObject
        {
            ["context"] = "agentweaver-aks",
            ["namespace"] = "agentweaver",
            ["sourceReceipt"] = SourceReceipt("fa4c10d40b10b5f06c6333ecfb6e6d86a6a7a8fe", "e235a0f576a26924920130c0c4bb4dbd01d4d567"),
            ["authoritySourceReceipt"] = SourceReceipt("1471e0b6ee4ab2a859231582ed1efa8e86693002", "5846e8524d8d684f78d24518453a4e1348fa7bf4"),
        },
        ["deploymentEvidence"] = new JsonObject
        {
            ["manifestPath"] = "source.json",
            ["imageManifestPath"] = "images.json",
        },
        ["fixturePlan"] = new JsonObject { ["kind"] = "owned-disposable-board" },
    };

    private static JsonObject P1Manifest() => new()
    {
        ["executionIntent"] = "execute",
        ["journey"] = "gate-answer",
        ["environment"] = new JsonObject
        {
            ["baseUrl"] = "https://gateway.example",
            ["issuer"] = "https://issuer.example/",
            ["audience"] = "https://gateway.example",
            ["actorId"] = "actor-1",
            ["tenantId"] = "tenant-1",
        },
        ["pins"] = new JsonObject
        {
            ["context"] = "test-context",
            ["namespace"] = "agentweaver",
            ["providerReceipt"] = new JsonObject
            {
                ["configMapName"] = "provider-catalog",
                ["sourceCommit"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                ["uid"] = "uid-1",
                ["resourceVersion"] = "1",
            },
            ["runtimeReceipt"] = new JsonObject { ["revision"] = 1 },
            ["storageReceipt"] = new JsonObject { ["shareId"] = "storage-test" },
            ["sourceReceipt"] = SourceReceipt("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        },
        ["deploymentEvidence"] = new JsonObject
        {
            ["manifestPath"] = "source.json",
            ["imageManifestPath"] = "images.json",
        },
        ["fixturePlan"] = new JsonObject
        {
            ["expectedPlatformRuntimeRevision"] = 1,
            ["selectionContext"] = new JsonObject
            {
                ["revision"] = "selection-1",
                ["availableModelSelectionReferences"] = new JsonArray(),
                ["providerRequirements"] = new JsonArray(),
                ["requiredEgress"] = new JsonArray(),
            },
            ["gate"] = new JsonObject
            {
                ["prompt"] = "Continue?",
                ["allowedChoices"] = new JsonArray("yes"),
                ["allowsFreeform"] = false,
                ["answer"] = "yes",
            },
            ["message"] = null,
        },
    };

    private static JsonObject SourceReceipt(string sourceSha, string sourceTree) => new()
    {
        ["sourceSha"] = sourceSha,
        ["sourceTree"] = sourceTree,
    };

    private static string WriteManifest(JsonObject manifest)
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"acceptance-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, manifest.ToJsonString());
        return path;
    }
}
