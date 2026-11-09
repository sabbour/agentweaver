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
    public async Task BoardDeleteRequiresAnExactOrchestratorSourceReceipt()
    {
        JsonObject root = BoardManifest();
        root["pins"]!.AsObject().Remove("orchestratorSourceReceipt");
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
    public async Task BoardDeleteRejectsAnIncompleteOrchestratorSourceReceipt()
    {
        JsonObject root = BoardManifest();
        root["pins"]!["orchestratorSourceReceipt"]!["sourceTree"] = "not-a-full-git-tree";
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

    [Fact]
    public async Task JournalAllowsAnAbsentSelectionContext()
    {
        JsonObject root = JournalManifest();
        root["fixturePlan"]!.AsObject().Remove("selectionContext");
        string path = WriteManifest(root);
        try
        {
            AcceptanceManifest manifest = await ApiAcceptanceSupport.ReadManifestAsync(path, CancellationToken.None);

            Assert.Equal("journal", manifest.Journey);
            Assert.False(manifest.FixturePlan.ContainsKey("selectionContext"));
            Assert.Equal(1L, manifest.FixturePlan["expectedPlatformRuntimeRevision"]?.GetValue<long>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task JournalStillRejectsExplicitNullOrMalformedSelectionContext()
    {
        foreach (JsonObject? context in new JsonObject?[]
        {
            null,
            new JsonObject
            {
                ["revision"] = "selection-1",
                ["availableModelSelectionReferences"] = new JsonArray(),
                ["providerRequirements"] = new JsonArray(),
                ["requiredEgress"] = new JsonArray(),
                ["unexpected"] = true,
            },
        })
        {
            JsonObject root = JournalManifest();
            root["fixturePlan"]!["selectionContext"] = context?.DeepClone();
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
    }

    [Fact]
    public async Task JournalRetainsExplicitRawSelectionContext()
    {
        string path = WriteManifest(JournalManifest());
        try
        {
            AcceptanceManifest manifest = await ApiAcceptanceSupport.ReadManifestAsync(path, CancellationToken.None);

            Assert.Equal("selection-1", ApiAcceptanceSupport.GetString(
                manifest.FixturePlan["selectionContext"]!.AsObject(), "revision"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SessionHistoryMetricAllowsAnAbsentSelectionContext()
    {
        JsonObject root = SessionHistoryMetricManifest();
        root["fixturePlan"]!.AsObject().Remove("selectionContext");
        string path = WriteManifest(root);
        try
        {
            AcceptanceManifest manifest = await ApiAcceptanceSupport.ReadManifestAsync(path, CancellationToken.None);

            Assert.Equal("session-history-metric", manifest.Journey);
            Assert.False(manifest.FixturePlan.ContainsKey("selectionContext"));
            Assert.Equal(1L, manifest.FixturePlan["expectedPlatformRuntimeRevision"]?.GetValue<long>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SessionHistoryMetricStillRejectsExplicitNullOrMalformedSelectionContext()
    {
        foreach (JsonObject? context in new JsonObject?[]
        {
            null,
            new JsonObject
            {
                ["revision"] = "selection-1",
                ["availableModelSelectionReferences"] = new JsonArray(),
                ["providerRequirements"] = new JsonArray(),
                ["requiredEgress"] = new JsonArray(),
                ["unexpected"] = true,
            },
        })
        {
            JsonObject root = SessionHistoryMetricManifest();
            root["fixturePlan"]!["selectionContext"] = context?.DeepClone();
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
    }

    [Fact]
    public async Task SessionHistoryMetricRetainsExplicitRawSelectionContext()
    {
        JsonObject root = SessionHistoryMetricManifest();
        string path = WriteManifest(root);
        try
        {
            AcceptanceManifest manifest = await ApiAcceptanceSupport.ReadManifestAsync(path, CancellationToken.None);

            Assert.Equal("selection-1", ApiAcceptanceSupport.GetString(
                manifest.FixturePlan["selectionContext"]!.AsObject(), "revision"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WorkspaceReadAcceptsOnlyTheExactRepositoryReadAssertions()
    {
        string path = WriteManifest(WorkspaceManifest());
        try
        {
            AcceptanceManifest manifest = await ApiAcceptanceSupport.ReadManifestAsync(path, CancellationToken.None);

            Assert.Equal("workspace-repository-read", manifest.Journey);
            Assert.Equal("branch", ApiAcceptanceSupport.GetString(
                manifest.FixturePlan["workspace"]!.AsObject(), "refKind"));
            Assert.Equal("docs/readme.txt", ApiAcceptanceSupport.GetString(
                manifest.FixturePlan["workspace"]!.AsObject(), "filePath"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WorkspaceReadRejectsSecretsAndInvalidBoundsOrReferences()
    {
        Action<JsonObject>[] mutations =
        [
            root => root["fixturePlan"]!["workspace"]!["selectionCode"] = "secret-selection",
            root => root["fixturePlan"]!["workspace"]!["refKind"] = "commit",
            root => root["fixturePlan"]!["workspace"]!["filePath"] = "../outside.txt",
            root => root["fixturePlan"]!["workspace"]!["expectedByteLength"] = 1024L * 1024 + 1,
            root => root["fixturePlan"]!["workspace"]!["connectionRevision"] = 0,
            root => root["fixturePlan"]!["workspace"]!["contentSha256"] = new string('A', 64),
        ];

        foreach (Action<JsonObject> mutate in mutations)
        {
            JsonObject root = WorkspaceManifest();
            mutate(root);
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
    }

    [Fact]
    public async Task WorkspaceReadRejectsRefComponentsEndingInLock()
    {
        JsonObject root = WorkspaceManifest();
        root["fixturePlan"]!["workspace"]!["refName"] = "feature.lock/next";
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
    public async Task WorkspaceReadAcceptsRefComponentsEndingInUppercaseLock()
    {
        JsonObject root = WorkspaceManifest();
        root["fixturePlan"]!["workspace"]!["refName"] = "feature.LOCK/next";
        string path = WriteManifest(root);
        try
        {
            AcceptanceManifest manifest = await ApiAcceptanceSupport.ReadManifestAsync(path, CancellationToken.None);

            Assert.Equal(
                "feature.LOCK/next",
                ApiAcceptanceSupport.GetString(
                    (JsonObject)manifest.FixturePlan["workspace"]!,
                    "refName"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static JsonObject WorkspaceManifest()
    {
        JsonObject root = P1Manifest();
        root["journey"] = "workspace-repository-read";
        root["environment"]!["actorId"] = "11111111-1111-4111-8111-111111111111";
        root["environment"]!["tenantId"] = "22222222-2222-4222-8222-222222222222";
        root["fixturePlan"] = new JsonObject
        {
            ["workspace"] = new JsonObject
            {
                ["projectId"] = "33333333-3333-4333-8333-333333333333",
                ["expectedProjectRevision"] = 4,
                ["connectionId"] = "connection_1",
                ["connectionRevision"] = 3,
                ["installationId"] = 10,
                ["repositoryId"] = 20,
                ["repositoryFullName"] = "octo/repo",
                ["refKind"] = "branch",
                ["refName"] = "main",
                ["commitSha"] = new string('a', 40),
                ["filePath"] = "docs/readme.txt",
                ["expectedByteLength"] = 1,
                ["contentSha256"] = new string('b', 64),
            },
        };
        return root;
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
            ["orchestratorSourceReceipt"] = SourceReceipt(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
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

    [Fact]
    public async Task NativeMaterialRecoveryRequiresExplicitOneRequestAndReplacementApproval()
    {
        JsonObject root = NativeMaterialManifest();
        string path = WriteManifest(root);
        try
        {
            AcceptanceManifest manifest = await ApiAcceptanceSupport.ReadManifestAsync(path, CancellationToken.None);
            Assert.Equal("native-material-recovery", manifest.Journey);
            Assert.Equal("byok", ApiAcceptanceSupport.GetString(
                (JsonObject)manifest.FixturePlan["nativeMaterialRecovery"]!, "approvedModelSourceMode"));
        }
        finally
        {
            File.Delete(path);
        }

        JsonObject invalid = NativeMaterialManifest();
        invalid["fixturePlan"]!["nativeMaterialRecovery"]!["approvedNativeRequestCount"] = 2;
        path = WriteManifest(invalid);
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

    private static JsonObject NativeMaterialManifest()
    {
        JsonObject root = P1Manifest();
        root["journey"] = "native-material-recovery";
        JsonObject plan = root["fixturePlan"]!.AsObject();
        plan.Remove("selectionContext");
        plan["gate"] = null;
        plan["message"] = null;
        plan["nativeMaterialRecovery"] = new JsonObject
        {
            ["approvedModelSelectionReference"] = "approved/byok",
            ["approvedModelSourceMode"] = "byok",
            ["approvedReasoningEffort"] = "medium",
            ["approvedMaxPromptTokens"] = 4096,
            ["approvedNativeRequestCount"] = 1,
            ["allowOrdinaryHostReplacement"] = true,
        };
        return root;
    }

    private static JsonObject SessionHistoryMetricManifest()
    {
        JsonObject root = P1Manifest();
        root["journey"] = "session-history-metric";
        JsonObject plan = root["fixturePlan"]!.AsObject();
        plan["gate"] = null;
        plan["message"] = null;
        plan.Remove("deniedActor");
        return root;
    }

    private static JsonObject JournalManifest()
    {
        JsonObject root = P1Manifest();
        root["journey"] = "journal";
        root["fixturePlan"]!["gate"] = null;
        root["fixturePlan"]!["message"] = null;
        return root;
    }

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
