using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Agentweaver.Cli.Auth;
using Xunit;

namespace Agentweaver.Cli.Acceptance.Tests;

public sealed class ApiAcceptanceNativeMaterialTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void SessionMaterialBlockerIsRemovedOnlyAfterJourneyAndCleanupPass()
    {
        AcceptanceManifest manifest = Manifest();
        JsonObject observations = new()
        {
            ["sessionMaterialAcceptance"] = new JsonObject
            {
                ["status"] = "passed",
                ["cleanup"] = new JsonObject { ["status"] = "passed" },
            },
        };

        JsonObject result = ApiAcceptanceSupport.Result("passed", new JsonObject(), [],
            observations, null, [], manifest);

        Assert.Equal("passed", result["observations"]?["sessionMaterialAcceptance"]?["status"]?.GetValue<string>());
        Assert.Null(result["observations"]?["sessionMaterialAcceptance"]?["blockers"]);
        Assert.Equal("blocked", result["fullP1Status"]?.GetValue<string>());
    }

    [Fact]
    public void FailedCleanupKeepsNativeMaterialAcceptanceBlocked()
    {
        AcceptanceManifest manifest = Manifest();
        JsonObject observations = new()
        {
            ["sessionMaterialAcceptance"] = new JsonObject
            {
                ["status"] = "passed",
                ["cleanup"] = new JsonObject { ["status"] = "blocked" },
            },
        };

        JsonObject result = ApiAcceptanceSupport.Result("failed", new JsonObject(), [],
            observations, "cleanup failed", [], manifest);

        Assert.Equal("blocked", result["observations"]?["sessionMaterialAcceptance"]?["status"]?.GetValue<string>());
        JsonArray blockers = result["observations"]?["sessionMaterialAcceptance"]?["blockers"] as JsonArray ?? [];
        Assert.NotEmpty(blockers);
        Assert.Equal("failed", result["fullP1Status"]?.GetValue<string>());
    }

    [Fact]
    public void MissingOrWrongTurnMaterialAcknowledgmentsDoNotValidate()
    {
        Assert.Throws<AcceptanceBlockedException>(() =>
            ApiAcceptanceNativeMaterial.RequireMaterial(new JsonObject(), "userMaterial", "turnContent"));

        JsonObject wrongKind = Material("sdkCache");
        Assert.Throws<AcceptanceBlockedException>(() =>
            ApiAcceptanceNativeMaterial.RequireMaterial(
                new JsonObject { ["userMaterial"] = wrongKind }, "userMaterial", "turnContent"));

        JsonObject missingBinding = (JsonObject)wrongKind.DeepClone();
        missingBinding["kind"] = "turnContent";
        missingBinding["bindingSha256"] = null;
        Assert.Throws<AcceptanceBlockedException>(() =>
            ApiAcceptanceNativeMaterial.RequireMaterial(
                new JsonObject { ["userMaterial"] = missingBinding }, "userMaterial", "turnContent"));
    }

    [Fact]
    public void StaleCompletionBindingDoesNotValidate()
    {
        JsonObject completion = Completion();
        JsonObject startup = Startup("runtime-old", 4);
        JsonObject approval = Approval();
        completion["runtimeInstanceId"] = "runtime-stale";

        Assert.Throws<AcceptanceBlockedException>(() =>
            ApiAcceptanceNativeMaterial.RequireCompletion(completion, startup, approval, 7, Hash));
    }

    [Fact]
    public void MissingOrStaleReplacementCacheReceiptDoesNotValidate()
    {
        JsonObject completion = Completion();
        JsonObject startup = Startup("runtime-old", 4);
        JsonObject replacement = Replacement(completion);

        ApiAcceptanceNativeMaterial.RequireReplacement(replacement, completion, startup, 7);
        replacement["recoveryCacheMaterial"] = null;
        Assert.Throws<AcceptanceBlockedException>(() =>
            ApiAcceptanceNativeMaterial.RequireReplacement(replacement, completion, startup, 7));

        replacement = Replacement(completion);
        replacement["previousRuntimeInstanceId"] = "runtime-stale";
        Assert.Throws<AcceptanceBlockedException>(() =>
            ApiAcceptanceNativeMaterial.RequireReplacement(replacement, completion, startup, 7));
    }

    [Fact]
    public void MismatchedOrMissingReplayReceiptDoesNotValidate()
    {
        JsonObject completion = Completion();
        JsonObject startup = Startup("runtime-new", 1);
        JsonObject replay = Replay(completion, startup);

        ApiAcceptanceNativeMaterial.RequireReplay(replay, completion, startup, 7, Hash, Hash);
        replay["registrationRevision"] = 6;
        Assert.Throws<AcceptanceBlockedException>(() =>
            ApiAcceptanceNativeMaterial.RequireReplay(replay, completion, startup, 7, Hash, Hash));

        replay = Replay(completion, startup);
        replay["nativeCallSequence"] = null;
        Assert.Throws<AcceptanceBlockedException>(() =>
            ApiAcceptanceNativeMaterial.RequireReplay(replay, completion, startup, 7, Hash, Hash));
    }

    [Fact]
    public async Task NativeMaterialJourneyReusesTheExactRequestAfterVerifiedReplacement()
    {
        string projectId = "project-1";
        string runId = "run-1";
        string sessionId = "session-1";
        string setupInvocationId = Guid.NewGuid().ToString("D");
        string coreInvocationId = Guid.NewGuid().ToString("D");
        string oldRuntimeId = Guid.NewGuid().ToString("D");
        string newRuntimeId = Guid.NewGuid().ToString("D");
        string acceptedHash = Hash;
        string prompt = "Read-only acceptance prompt";
        string output = "Two-role proposal";
        string promptHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
        string outputHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(output))).ToLowerInvariant();
        JsonObject completion = Completion();
        completion["runtimeInstanceId"] = oldRuntimeId;
        completion["registrationRevision"] = 4L;
        completion["acceptedSelectionHash"] = acceptedHash;
        completion["promptSha256"] = promptHash;
        completion["outputSha256"] = outputHash;
        completion["image"] = new JsonObject { ["digest"] = $"sha256:{Hash}" };
        completion["userMaterial"]!["contentSha256"] = promptHash;
        completion["assistantMaterial"]!["contentSha256"] = outputHash;
        JsonObject oldStartup = RuntimeStartup(setupInvocationId, coreInvocationId, oldRuntimeId, 4, acceptedHash, "pod-old");
        JsonObject newStartup = RuntimeStartup(setupInvocationId, coreInvocationId, newRuntimeId, 1, acceptedHash, "pod-new");
        JsonObject replacement = Replacement(completion);
        replacement["operationId"] = ApiAcceptanceSupport.StableUuid(sessionId, "native-material-host-replacement");
        replacement["previousRuntimeInstanceId"] = oldRuntimeId;
        replacement["currentRuntimeInstanceId"] = newRuntimeId;
        replacement["previousRegistrationRevision"] = 4L;
        replacement["currentRegistrationRevision"] = 1L;
        replacement["acceptedSelectionHash"] = acceptedHash;
        replacement["recoveryCacheMaterial"] = completion["cacheMaterial"]?.DeepClone();
        replacement["previousPodUid"] = "pod-old";
        replacement["currentPodUid"] = "pod-new";
        replacement["reconstructionCallSequence"]!["runtimeInstanceId"] = newRuntimeId;
        replacement["reconstructionCallSequence"]!["registrationRevision"] = 1L;
        JsonObject replay = Replay(completion, newStartup);
        replay["previousRegistrationSha256"] = completion["registrationSha256"]?.DeepClone();
        replay["promptSha256"] = promptHash;
        replay["outputSha256"] = outputHash;
        FakeNativeMaterialGateway api = new(projectId, runId, sessionId, completion, replacement, replay,
            newStartup, prompt, output);
        AcceptanceFixture fixture = new(
            projectId, runId, sessionId, sessionId, 7, 1, null, null,
            new JsonObject(), new JsonObject { ["configurationRevision"] = 1L },
            new JsonObject(), new JsonObject(), new JsonObject(), null)
        {
            P1SetupInvocationId = setupInvocationId,
            NativeMaterialPrompt = prompt,
            RuntimeStartup = oldStartup,
        };
        AcceptanceManifest manifest = new(
            new JsonObject(),
            new JsonObject { ["actorId"] = "actor-1", ["tenantId"] = "tenant-1", ["issuer"] = "https://issuer.example/", ["audience"] = "gateway" },
            new JsonObject(), new JsonObject
            {
                ["expectedPlatformRuntimeRevision"] = 1L,
                ["nativeMaterialRecovery"] = Approval(),
            },
            "native-material-recovery", "input.json", ".");
        JsonObject observations = new();

        await ApiAcceptanceNativeMaterial.RunAsync(
            api, manifest, fixture,
            OwnerRun(projectId, runId, sessionId), OwnerSession(projectId, runId, sessionId, oldStartup),
            OwnerDecisions(), new JsonObject(), observations, [], CancellationToken.None);

        JsonObject evidence = observations["sessionMaterialAcceptance"]!.AsObject();
        Assert.Equal("passed", evidence["status"]?.GetValue<string>());
        Assert.Equal("passed", evidence["recovery"]?["status"]?.GetValue<string>());
        Assert.Equal("passed", evidence["replay"]?["status"]?.GetValue<string>());
        string[] retainedGaps = evidence["retainedGaps"]!.AsArray()
            .Select(value => value!.GetValue<string>())
            .ToArray();
        Assert.Contains(retainedGaps, gap => gap.Contains("Authority revocation", StringComparison.Ordinal));
        Assert.Contains(retainedGaps, gap => gap.Contains("denied-actor", StringComparison.Ordinal));
        Assert.Equal(2, api.CastingRequests.Count);
        Assert.True(JsonNode.DeepEquals(api.CastingRequests[0], api.CastingRequests[1]));
        Assert.Equal(2, api.ReplacementRequests.Count);
        Assert.True(JsonNode.DeepEquals(api.ReplacementRequests[0], api.ReplacementRequests[1]));
    }

    private static AcceptanceManifest Manifest() =>
        new(new JsonObject(), new JsonObject(), new JsonObject(), new JsonObject(),
            "native-material-recovery", "input.json", ".");

    private static JsonObject Completion() => new()
    {
        ["requestId"] = Guid.NewGuid().ToString("D"),
        ["runtimeInstanceId"] = "runtime-old",
        ["registrationRevision"] = 4L,
        ["acceptedSelectionHash"] = Hash,
        ["executionFence"] = 7L,
        ["promptSha256"] = Hash,
        ["outputSha256"] = Hash,
        ["contractVersion"] = 1L,
        ["nativeEventCount"] = 1L,
        ["usageEventIds"] = new JsonArray(Guid.NewGuid().ToString("D")),
        ["successfulToolCount"] = 0L,
        ["failedToolCount"] = 0L,
        ["cacheBaselineIsComplete"] = true,
        ["cacheBaselineEventCount"] = 0L,
        ["modelSelectionReference"] = "approved/byok",
        ["modelSourceMode"] = "byok",
        ["reasoningEffort"] = "medium",
        ["maxPromptTokens"] = 4096L,
        ["image"] = new JsonObject { ["digest"] = $"sha256:{Hash}" },
        ["nativeCursorBeforeSha256"] = Hash,
        ["nativeCursorAfterSha256"] = new string('b', 64),
        ["nativeMessageId"] = Guid.NewGuid().ToString("D"),
        ["nativeTurnStartEventId"] = Guid.NewGuid().ToString("D"),
        ["nativeTurnEndEventId"] = Guid.NewGuid().ToString("D"),
        ["nativeCompletionReceiptEventId"] = Guid.NewGuid().ToString("D"),
        ["registrationSha256"] = Hash,
        ["sourceSha256"] = Hash,
        ["sdkSessionSha256"] = Hash,
        ["nativeEventIntervalSha256"] = Hash,
        ["cacheBaselineCursorSha256"] = Hash,
        ["cacheBaselineAfterCursorSha256"] = Hash,
        ["userMaterial"] = Material("turnContent", 1),
        ["assistantMaterial"] = Material("turnContent", 2),
        ["cacheMaterial"] = Material("sdkCache", 3),
    };

    private static JsonObject Startup(string runtime, long revision) => new()
    {
        ["runtimeInstanceId"] = runtime,
        ["registrationRevision"] = revision,
        ["acceptedSelectionHash"] = Hash,
        ["imageDigest"] = $"sha256:{Hash}",
    };

    private static JsonObject Approval() => new()
    {
        ["approvedModelSelectionReference"] = "approved/byok",
        ["approvedReasoningEffort"] = "medium",
        ["approvedMaxPromptTokens"] = 4096L,
    };

    private static JsonObject Material(string kind, long position = 1) => new()
    {
        ["eventId"] = Guid.NewGuid().ToString("D"),
        ["position"] = position,
        ["kind"] = kind,
        ["contentSha256"] = Hash,
        ["bindingSha256"] = Hash,
    };

    private static JsonObject Replacement(JsonObject completion)
    {
        JsonObject cache = (JsonObject)completion["cacheMaterial"]!.DeepClone();
        return new JsonObject
        {
            ["contractVersion"] = 1L,
            ["operationId"] = Guid.NewGuid().ToString("D"),
            ["state"] = "ready",
            ["recoveryMode"] = "nativeCache",
            ["previousRuntimeInstanceId"] = "runtime-old",
            ["previousRegistrationRevision"] = 4L,
            ["currentRuntimeInstanceId"] = "runtime-new",
            ["currentRegistrationRevision"] = 1L,
            ["executionFence"] = 7L,
            ["acceptedSelectionHash"] = Hash,
            ["recoveryCacheMaterial"] = cache,
            ["reconstructionInterval"] = new JsonObject
            {
                ["startCursorSha256"] = Hash,
                ["endCursorSha256"] = Hash,
                ["eventCount"] = 0L,
                ["isComplete"] = true,
            },
            ["reconstructionCallSequence"] = new JsonObject
            {
                ["hostProcessId"] = 1L,
                ["hostProcessInstanceId"] = Guid.NewGuid().ToString("D"),
                ["runtimeInstanceId"] = "runtime-new",
                ["registrationRevision"] = 1L,
                ["sendSequenceBefore"] = 0L,
                ["sendSequenceAfter"] = 0L,
                ["toolEffectSequenceBefore"] = 0L,
                ["toolEffectSequenceAfter"] = 0L,
            },
            ["previousPodUid"] = "pod-old",
            ["currentPodUid"] = "pod-new",
            ["workspaceVolumeId"] = "workspace",
            ["workspaceResourceGeneration"] = 2L,
            ["workspaceDataGeneration"] = 3L,
        };
    }

    private static JsonObject Replay(JsonObject completion, JsonObject startup) => new()
    {
        ["contractVersion"] = 1L,
        ["requestId"] = completion["requestId"]?.DeepClone(),
        ["previousRuntimeInstanceId"] = completion["runtimeInstanceId"]?.DeepClone(),
        ["previousRegistrationRevision"] = completion["registrationRevision"]?.DeepClone(),
        ["previousRegistrationSha256"] = Hash,
        ["runtimeInstanceId"] = startup["runtimeInstanceId"]?.DeepClone(),
        ["registrationRevision"] = startup["registrationRevision"]?.DeepClone(),
        ["registrationSha256"] = Hash,
        ["executionFence"] = 7L,
        ["acceptedSelectionHash"] = Hash,
        ["promptSha256"] = Hash,
        ["outputSha256"] = Hash,
        ["userMaterial"] = completion["userMaterial"]?.DeepClone(),
        ["assistantMaterial"] = completion["assistantMaterial"]?.DeepClone(),
        ["nativeInterval"] = new JsonObject
        {
            ["startCursorSha256"] = Hash,
            ["endCursorSha256"] = Hash,
            ["eventCount"] = 0L,
            ["isComplete"] = true,
        },
        ["nativeCallSequence"] = new JsonObject
        {
            ["hostProcessId"] = 1L,
            ["hostProcessInstanceId"] = Guid.NewGuid().ToString("D"),
            ["runtimeInstanceId"] = startup["runtimeInstanceId"]?.DeepClone(),
            ["registrationRevision"] = startup["registrationRevision"]?.DeepClone(),
            ["sendSequenceBefore"] = 0L,
            ["sendSequenceAfter"] = 0L,
            ["toolEffectSequenceBefore"] = 0L,
            ["toolEffectSequenceAfter"] = 0L,
        },
    };

    private static JsonObject RuntimeStartup(
        string setupInvocation,
        string coreInvocation,
        string runtime,
        long revision,
        string acceptedSelectionHash,
        string podUid) => new()
    {
        ["state"] = "ready",
        ["p1SetupInvocationId"] = setupInvocation,
        ["p1CoreInvocationId"] = coreInvocation,
        ["runtimeInstanceId"] = runtime,
        ["registrationRevision"] = revision,
        ["acceptedSelectionHash"] = acceptedSelectionHash,
        ["executionFence"] = 7L,
        ["modelSelectionReference"] = "approved/byok",
        ["modelSourceMode"] = "byok",
        ["reasoningEffort"] = "medium",
        ["maxPromptTokens"] = 4096L,
        ["imageDigest"] = $"sha256:{Hash}",
        ["podUid"] = podUid,
    };

    private static JsonObject OwnerRun(string projectId, string runId, string sessionId) => new()
    {
        ["projectId"] = projectId,
        ["runId"] = runId,
        ["rootSessionId"] = sessionId,
        ["executionFence"] = 7L,
        ["stateVersion"] = 1L,
    };

    private static JsonObject OwnerSession(
        string projectId,
        string runId,
        string sessionId,
        JsonObject startup) => new()
    {
        ["identity"] = new JsonObject { ["projectId"] = projectId, ["runId"] = runId, ["sessionId"] = sessionId },
        ["rootSessionId"] = sessionId,
        ["executionFence"] = 7L,
        ["stateVersion"] = 1L,
        ["runtimeStartup"] = startup.DeepClone(),
    };

    private static JsonObject OwnerDecisions() => new()
    {
        ["executionFence"] = 7L,
        ["stateVersion"] = 1L,
    };

    private sealed class FakeNativeMaterialGateway(
        string projectId,
        string runId,
        string sessionId,
        JsonObject completion,
        JsonObject replacement,
        JsonObject replay,
        JsonObject newStartup,
        string prompt,
        string output) : IGatewayClient
    {
        private int _castingCalls;
        private int _replacementStatusCalls;
        public List<JsonObject> CastingRequests { get; } = [];
        public List<JsonObject> ReplacementRequests { get; } = [];
        public CliIdentity Identity => null!;
        public IReadOnlyList<JsonObject> Calls => [];
        public Task<JsonObject> DiscoverAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject> ValidateAuthorizationContextAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<GatewayResponse> InvokeAsync(string operationId, GatewayInvocation invocation, CancellationToken ct)
        {
            JsonNode? body = operationId switch
            {
                "formulateCastingProposal" => Proposal(invocation),
                "readSessionTurnContent" => TurnContent(invocation),
                "replaceRuntimeHost" => ReplacementReceipt(invocation),
                "readRuntimeReplacement" => ReplacementStatus(),
                "readRunStatus" => OwnerRun(projectId, runId, sessionId),
                "readSessionStatus" => RuntimeOrOwnerSession(invocation),
                "readDecisions" => OwnerDecisions(),
                "readSessionTree" => RootTree(),
                _ => throw new InvalidOperationException($"Unexpected operation {operationId}."),
            };
            return Task.FromResult(new GatewayResponse(200, body, new Dictionary<string, string>(), new JsonObject()));
        }

        public Task<GatewayResponse> InvokeRawAsync(
            string method, string url, byte[]? body, IReadOnlyDictionary<string, string>? headers,
            bool outputRequested, Func<string, ValueTask>? onEventChunk, CancellationToken ct) =>
            throw new NotSupportedException();

        private JsonObject Proposal(GatewayInvocation invocation)
        {
            CastingRequests.Add(invocation.Body!.DeepClone().AsObject());
            bool first = _castingCalls++ == 0;
            JsonObject generation = new()
            {
                ["requestId"] = completion["requestId"]?.DeepClone(),
                ["mode"] = "formulate",
                ["state"] = "completed",
                ["failureCode"] = null,
                ["nativeCompletion"] = completion.DeepClone(),
            };
            if (!first)
                generation["nativeReplay"] = replay.DeepClone();
            return new JsonObject
            {
                ["proposalId"] = "proposal-1",
                ["projectId"] = projectId,
                ["generation"] = generation,
            };
        }

        private JsonObject TurnContent(GatewayInvocation invocation)
        {
            string eventId = invocation.PathParameters!["eventId"];
            bool user = eventId == ApiAcceptanceSupport.GetString(completion["userMaterial"]!.AsObject(), "eventId");
            return new JsonObject
            {
                ["eventId"] = eventId,
                ["identity"] = new JsonObject { ["projectId"] = projectId, ["runId"] = runId, ["sessionId"] = sessionId },
                ["text"] = user ? prompt : output,
            };
        }

        private JsonObject ReplacementReceipt(GatewayInvocation invocation)
        {
            Assert.Equal(ApiAcceptanceSupport.GetString(replacement, "operationId"),
                ApiAcceptanceSupport.GetString(invocation.Body!.AsObject(), "operationId"));
            ReplacementRequests.Add(invocation.Body.AsObject().DeepClone().AsObject());
            return replacement.DeepClone().AsObject();
        }

        private JsonObject ReplacementStatus() =>
            _replacementStatusCalls++ == 0
                ? new JsonObject
                {
                    ["operationId"] = replacement["operationId"]?.DeepClone(),
                    ["state"] = "pending",
                }
                : replacement.DeepClone().AsObject();

        private JsonObject RuntimeOrOwnerSession(GatewayInvocation invocation)
        {
            return OwnerSession(projectId, runId, sessionId, newStartup);
        }

        private JsonObject RootTree() => new()
        {
            ["rootSessionId"] = sessionId,
            ["nodes"] = new JsonArray(new JsonObject
            {
                ["identity"] = new JsonObject { ["projectId"] = projectId, ["runId"] = runId, ["sessionId"] = sessionId },
                ["rootSessionId"] = sessionId,
                ["parentSessionId"] = null,
                ["kind"] = "coordinator",
                ["lifecycle"] = "active",
                ["executionFence"] = 7L,
            }),
        };
    }
}
