using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentweaver.Cli;

internal static class ApiAcceptanceNativeMaterial
{
    private static readonly Regex Sha256Pattern = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task RunAsync(
        IGatewayClient api,
        AcceptanceManifest manifest,
        AcceptanceFixture fixture,
        JsonObject initialRun,
        JsonObject initialSession,
        JsonObject initialDecisions,
        JsonObject sourceEvidence,
        JsonObject observations,
        List<JsonObject> steps,
        CancellationToken ct)
    {
        JsonObject evidence = CreateEvidence(manifest, fixture, sourceEvidence);
        observations["sessionMaterialAcceptance"] = evidence;
        try
        {
            string prompt = fixture.NativeMaterialPrompt
                ?? throw new AcceptanceBlockedException("The accepted run has no catalog-bound native material prompt.");
            JsonObject initialStartup = fixture.RuntimeStartup
                ?? throw new AcceptanceBlockedException("The accepted run has no current runtime-startup binding.");
            JsonObject approval = manifest.FixturePlan["nativeMaterialRecovery"] as JsonObject
                ?? throw new AcceptanceBlockedException("The explicit model and replacement approvals are missing.");
            string promptHash = Hash(Encoding.UTF8.GetBytes(prompt));
            string requestKey = ApiAcceptanceSupport.StableUuid(fixture.RootSessionId, "native-material-casting-request");
            JsonObject request = new()
            {
                ["expectedConfigurationRevision"] = Long(fixture.ProjectReceipt, "configurationRevision"),
                ["runId"] = fixture.RunId,
                ["sessionId"] = fixture.RootSessionId,
                ["idempotencyKey"] = requestKey,
                ["prompt"] = prompt,
                ["goal"] = ApiAcceptanceFixtureSetup.NativeMaterialGoal,
                ["teamSize"] = ApiAcceptanceFixtureSetup.NativeMaterialTeamSize,
                ["intent"] = "recast",
                ["preferredRoleIds"] = new JsonArray(),
            };
            JsonObject firstProposal = await ApiAcceptanceJourney.InvokeJsonAsync(api, "formulateCastingProposal",
                ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId), ("sessionId", fixture.RootSessionId)),
                request, null, [200], ct).ConfigureAwait(false);
            JsonObject firstGeneration = RequireGeneration(firstProposal, fixture.ProjectId, promptHash, expectReplay: false);
            JsonObject completion = firstGeneration["nativeCompletion"] as JsonObject
                ?? throw new AcceptanceBlockedException("The native turn has no bound completion receipt.");
            string outputHash = RequireCompletion(completion, initialStartup, approval, fixture.ExecutionFence, promptHash);
            string requestId = RequireGuid(completion, "requestId");
            JsonObject userMaterial = RequireMaterial(completion, "userMaterial", "turnContent");
            JsonObject assistantMaterial = RequireMaterial(completion, "assistantMaterial", "turnContent");
            JsonObject cacheMaterial = RequireMaterial(completion, "cacheMaterial", "sdkCache");
            if (Long(userMaterial, "position") >= Long(assistantMaterial, "position")
                || Long(assistantMaterial, "position") >= Long(cacheMaterial, "position"))
                throw new AcceptanceBlockedException("The native material receipts are not in user, assistant, then cache order.");

            JsonObject userReadback = await ReadMaterialAsync(api, fixture, userMaterial, ct).ConfigureAwait(false);
            JsonObject assistantReadback = await ReadMaterialAsync(api, fixture, assistantMaterial, ct).ConfigureAwait(false);
            RequireReadback(userReadback, fixture, prompt, userMaterial);
            string output = ApiAcceptanceSupport.GetString(assistantReadback, "text")
                ?? throw new AcceptanceBlockedException("The assistant turn material has no text.");
            RequireReadback(assistantReadback, fixture, output, assistantMaterial);
            if (Hash(Encoding.UTF8.GetBytes(output)) != outputHash)
                throw new AcceptanceBlockedException("The assistant turn readback does not match the completion receipt.");

            evidence["turn"] = new JsonObject
            {
                ["status"] = "passed",
                ["requestId"] = requestId,
                ["promptSha256"] = promptHash,
                ["outputSha256"] = outputHash,
                ["nativeEventCount"] = completion["nativeEventCount"]?.DeepClone(),
                ["usageEventCount"] = (completion["usageEventIds"] as JsonArray)?.Count,
                ["successfulToolCount"] = completion["successfulToolCount"]?.DeepClone(),
                ["failedToolCount"] = completion["failedToolCount"]?.DeepClone(),
                ["completionReceiptEventId"] = completion["nativeCompletionReceiptEventId"]?.DeepClone(),
            };
            evidence["materialReadback"] = new JsonObject
            {
                ["status"] = "passed",
                ["user"] = MaterialSummary(userMaterial),
                ["assistant"] = MaterialSummary(assistantMaterial),
                ["cache"] = MaterialSummary(cacheMaterial),
            };
            ApiAcceptanceSupport.AddStep(steps, "native-turn-and-material-readback", "passed",
                ("requestId", JsonValue.Create(requestId)),
                ("promptSha256", JsonValue.Create(promptHash)),
                ("outputSha256", JsonValue.Create(outputHash)));

            string operationId = ApiAcceptanceSupport.StableUuid(fixture.RootSessionId, "native-material-host-replacement");
            JsonObject replacementRequest = new()
            {
                ["contractVersion"] = 1,
                ["operationId"] = operationId,
                ["expectedRuntimeInstanceId"] = initialStartup["runtimeInstanceId"]?.DeepClone(),
                ["expectedRegistrationRevision"] = initialStartup["registrationRevision"]?.DeepClone(),
                ["expectedExecutionFence"] = fixture.ExecutionFence,
                ["expectedAcceptedSelectionHash"] = initialStartup["acceptedSelectionHash"]?.DeepClone(),
            };
            JsonObject acceptedReplacement = await ApiAcceptanceJourney.InvokeJsonAsync(api, "replaceRuntimeHost",
                ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId), ("sessionId", fixture.RootSessionId)),
                replacementRequest, null, [200], ct).ConfigureAwait(false);
            if (ApiAcceptanceSupport.GetString(acceptedReplacement, "operationId") != operationId)
                throw new AcceptanceBlockedException("The Host replacement receipt does not match the requested operation.");
            JsonObject replacement = await WaitForReplacementAsync(
                api, fixture, operationId, replacementRequest, ct).ConfigureAwait(false);
            RequireReplacement(replacement, completion, initialStartup, fixture.ExecutionFence);
            var (_, _, currentStartup) = await ApiAcceptanceFixtureSetup.WaitForNativeRuntimeReadyAsync(
                api, fixture.ProjectId, fixture.RunId, fixture.RootSessionId, ct).ConfigureAwait(false);
            ApiAcceptanceFixtureSetup.RequireNativeRuntimeStartup(
                currentStartup, manifest, fixture.P1SetupInvocationId!, fixture.RootSessionId, fixture.ExecutionFence);
            RequireReplacementStartup(replacement, initialStartup, currentStartup, fixture.ExecutionFence);

            evidence["recovery"] = new JsonObject
            {
                ["status"] = "passed",
                ["replacement"] = new JsonObject
                {
                    ["status"] = "passed",
                    ["operationId"] = operationId,
                    ["previousRuntimeInstanceId"] = replacement["previousRuntimeInstanceId"]?.DeepClone(),
                    ["previousRegistrationRevision"] = replacement["previousRegistrationRevision"]?.DeepClone(),
                    ["currentRuntimeInstanceId"] = replacement["currentRuntimeInstanceId"]?.DeepClone(),
                    ["currentRegistrationRevision"] = replacement["currentRegistrationRevision"]?.DeepClone(),
                    ["previousPodUid"] = replacement["previousPodUid"]?.DeepClone(),
                    ["currentPodUid"] = replacement["currentPodUid"]?.DeepClone(),
                    ["workspaceVolumeId"] = replacement["workspaceVolumeId"]?.DeepClone(),
                    ["workspaceResourceGeneration"] = replacement["workspaceResourceGeneration"]?.DeepClone(),
                    ["workspaceDataGeneration"] = replacement["workspaceDataGeneration"]?.DeepClone(),
                },
                ["cacheRestore"] = new JsonObject
                {
                    ["status"] = "passed",
                    ["material"] = MaterialSummary(replacement["recoveryCacheMaterial"] as JsonObject ?? []),
                },
                ["reconstructionInterval"] = replacement["reconstructionInterval"]?.DeepClone(),
                ["reconstructionCallSequence"] = replacement["reconstructionCallSequence"]?.DeepClone(),
                ["modelTurnDuringRebuild"] = new JsonObject { ["status"] = "passed", ["count"] = 0 },
            };
            ApiAcceptanceSupport.AddStep(steps, "ordinary-host-replacement-and-cache-restore", "passed",
                ("operationId", JsonValue.Create(operationId)),
                ("runtimeInstanceId", currentStartup["runtimeInstanceId"]?.DeepClone()),
                ("registrationRevision", currentStartup["registrationRevision"]?.DeepClone()));

            JsonObject replayProposal = await ApiAcceptanceJourney.InvokeJsonAsync(api, "formulateCastingProposal",
                ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId), ("sessionId", fixture.RootSessionId)),
                request, null, [200], ct).ConfigureAwait(false);
            JsonObject replayGeneration = RequireGeneration(replayProposal, fixture.ProjectId, promptHash, expectReplay: true);
            JsonObject replayCompletion = replayGeneration["nativeCompletion"] as JsonObject
                ?? throw new AcceptanceBlockedException("The exact retry has no original completion receipt.");
            JsonObject replay = replayGeneration["nativeReplay"] as JsonObject
                ?? throw new AcceptanceBlockedException("The exact retry has no replacement-bound replay receipt.");
            if (ApiAcceptanceSupport.GetString(replayProposal, "proposalId") != ApiAcceptanceSupport.GetString(firstProposal, "proposalId")
                || !JsonNode.DeepEquals(completion, replayCompletion))
                throw new AcceptanceBlockedException("The exact retry did not return the original proposal and completion receipt.");
            RequireReplay(replay, completion, currentStartup, fixture.ExecutionFence, promptHash, outputHash);
            (JsonObject currentRun, JsonObject currentSession, JsonObject currentDecisions) =
                await ReadOwnerSnapshotsAsync(api, fixture, ct).ConfigureAwait(false);
            RequireOwnerStateUnchanged(
                initialRun, initialSession, initialDecisions, currentRun, currentSession, currentDecisions, fixture);
            await RequireRootOnlyTreeAsync(api, fixture, ct).ConfigureAwait(false);

            evidence["replay"] = new JsonObject
            {
                ["status"] = "passed",
                ["requestId"] = replay["requestId"]?.DeepClone(),
                ["previousRuntimeInstanceId"] = replay["previousRuntimeInstanceId"]?.DeepClone(),
                ["previousRegistrationRevision"] = replay["previousRegistrationRevision"]?.DeepClone(),
                ["runtimeInstanceId"] = replay["runtimeInstanceId"]?.DeepClone(),
                ["registrationRevision"] = replay["registrationRevision"]?.DeepClone(),
                ["nativeInterval"] = replay["nativeInterval"]?.DeepClone(),
                ["nativeCallSequence"] = replay["nativeCallSequence"]?.DeepClone(),
            };
            evidence["ownerBoundaryChecks"] = new JsonArray
            {
                new JsonObject { ["name"] = "current-root-and-execution-fence", ["status"] = "passed" },
                new JsonObject { ["name"] = "exact-request-replay", ["status"] = "passed" },
                new JsonObject { ["name"] = "replacement-lineage", ["status"] = "passed", ["operationId"] = operationId },
                new JsonObject { ["name"] = "no-new-native-send-or-tool-effect", ["status"] = "passed" },
            };
            evidence["retainedGaps"] = new JsonArray(
                "This run proves one approved BYOK turn, retained material, Host replacement, cache restore, exact replay, and normal fixture cleanup.",
                "Authority revocation, stale fence or registration, changed-payload retry, optional-cache absence or expiry, and missing or expired required turn material were not tested by this journey.",
                "Other full-P1 checks remain outstanding.",
                "Archive and operating-system acceptance remain separate work under issue 2449.");
            evidence["status"] = "passed";
            ApiAcceptanceSupport.AddStep(steps, "exact-replay-without-new-native-effects", "passed",
                ("requestId", JsonValue.Create(requestId)));
        }
        catch (Exception error)
        {
            evidence["status"] = error is AcceptanceBlockedException ? "blocked" : "failed";
            evidence["failure"] = ApiAcceptanceSupport.RedactText(error.Message);
            throw;
        }
    }

    private static JsonObject CreateEvidence(
        AcceptanceManifest manifest,
        AcceptanceFixture fixture,
        JsonObject sourceEvidence) => new()
    {
        ["kind"] = "p1-session-material-acceptance",
        ["schemaVersion"] = 1,
        ["status"] = "blocked",
        ["evidenceClass"] = "deployed-runtime",
        ["sourceEvidence"] = sourceEvidence.DeepClone(),
        ["identity"] = new JsonObject
        {
            ["actorId"] = ApiAcceptanceSupport.GetString(manifest.Environment, "actorId"),
            ["tenantId"] = ApiAcceptanceSupport.GetString(manifest.Environment, "tenantId"),
            ["issuer"] = ApiAcceptanceSupport.GetString(manifest.Environment, "issuer"),
            ["audience"] = ApiAcceptanceSupport.GetString(manifest.Environment, "audience"),
            ["projectId"] = fixture.ProjectId,
            ["runId"] = fixture.RunId,
            ["rootSessionId"] = fixture.RootSessionId,
            ["sessionId"] = fixture.SessionId,
            ["executionFence"] = fixture.ExecutionFence,
            ["invocationId"] = fixture.P1SetupInvocationId,
            ["allowedJourneys"] = new JsonArray("native-material-recovery"),
            ["platformRuntimeRevision"] = manifest.FixturePlan["expectedPlatformRuntimeRevision"]?.DeepClone(),
            ["runtimeInstanceId"] = fixture.RuntimeStartup?["runtimeInstanceId"]?.DeepClone(),
            ["registrationRevision"] = fixture.RuntimeStartup?["registrationRevision"]?.DeepClone(),
            ["acceptedSelectionHash"] = fixture.RuntimeStartup?["acceptedSelectionHash"]?.DeepClone(),
        },
        ["turn"] = new JsonObject { ["status"] = "not-started" },
        ["materialReadback"] = new JsonObject { ["status"] = "not-started" },
        ["recovery"] = new JsonObject { ["status"] = "not-started" },
        ["replay"] = new JsonObject { ["status"] = "not-started" },
        ["ownerBoundaryChecks"] = new JsonArray(),
        ["cleanup"] = new JsonObject { ["status"] = "pending", ["target"] = "owned project and run fixture" },
        ["retainedGaps"] = new JsonArray(),
    };

    internal static JsonObject RequireGeneration(JsonObject proposal, string projectId, string promptHash, bool expectReplay)
    {
        JsonObject generation = proposal["generation"] as JsonObject
            ?? throw new AcceptanceBlockedException("The casting proposal has no generation result.");
        if (ApiAcceptanceSupport.GetString(proposal, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(generation, "state") != "completed"
            || ApiAcceptanceSupport.GetString(generation, "mode") != "formulate"
            || ApiAcceptanceSupport.GetString(generation, "failureCode") is not null
            || !ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(generation, "requestId"))
            || (generation["nativeReplay"] is JsonObject) != expectReplay)
            throw new AcceptanceBlockedException("The casting proposal is not the expected completed native generation.");
        JsonObject completion = generation["nativeCompletion"] as JsonObject
            ?? throw new AcceptanceBlockedException("The casting proposal has no native completion receipt.");
        if (ApiAcceptanceSupport.GetString(completion, "requestId") != ApiAcceptanceSupport.GetString(generation, "requestId")
            || ApiAcceptanceSupport.GetString(completion, "promptSha256") != promptHash)
            throw new AcceptanceBlockedException("The native completion receipt is bound to a different prompt.");
        return generation;
    }

    internal static string RequireCompletion(
        JsonObject completion,
        JsonObject startup,
        JsonObject approval,
        long executionFence,
        string promptHash)
    {
        string outputHash = RequireHash(completion, "outputSha256");
        JsonObject image = completion["image"] as JsonObject
            ?? throw new AcceptanceBlockedException("The native completion has no pinned image identity.");
        JsonArray usageEvents = completion["usageEventIds"] as JsonArray ?? [];
        if (Long(completion, "contractVersion") != 1
            || Long(completion, "nativeEventCount") is not > 0
            || usageEvents.Count == 0
            || Long(completion, "successfulToolCount") != 0
            || Long(completion, "failedToolCount") != 0
            || completion["cacheBaselineIsComplete"]?.GetValue<bool>() != true
            || Long(completion, "cacheBaselineEventCount") is null or < 0
            || RequireHash(completion, "promptSha256") != promptHash
            || RequireHash(completion, "acceptedSelectionHash") != ApiAcceptanceSupport.GetString(startup, "acceptedSelectionHash")
            || Long(completion, "executionFence") != executionFence
            || ApiAcceptanceSupport.GetString(completion, "runtimeInstanceId") != ApiAcceptanceSupport.GetString(startup, "runtimeInstanceId")
            || Long(completion, "registrationRevision") != Long(startup, "registrationRevision")
            || ApiAcceptanceSupport.GetString(completion, "modelSelectionReference") != ApiAcceptanceSupport.GetString(approval, "approvedModelSelectionReference")
            || ApiAcceptanceSupport.GetString(completion, "modelSourceMode") != "byok"
            || ApiAcceptanceSupport.GetString(completion, "reasoningEffort") != ApiAcceptanceSupport.GetString(approval, "approvedReasoningEffort")
            || Long(completion, "maxPromptTokens") != Long(approval, "approvedMaxPromptTokens")
            || ApiAcceptanceSupport.GetString(image, "digest") != ApiAcceptanceSupport.GetString(startup, "imageDigest")
            || ApiAcceptanceSupport.GetString(image, "digest") is not string imageDigest
            || !imageDigest.StartsWith("sha256:", StringComparison.Ordinal)
            || !HashesDiffer(completion, "nativeCursorBeforeSha256", "nativeCursorAfterSha256")
            || !ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(completion, "nativeMessageId"))
            || !ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(completion, "nativeTurnStartEventId"))
            || !ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(completion, "nativeTurnEndEventId"))
            || !ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(completion, "nativeCompletionReceiptEventId")))
            throw new AcceptanceBlockedException("The native completion receipt does not match the approved model, runtime binding, or completed turn.");
        foreach (string field in new[]
        {
            "registrationSha256", "sourceSha256", "sdkSessionSha256", "nativeEventIntervalSha256",
            "cacheBaselineCursorSha256", "cacheBaselineAfterCursorSha256",
        })
            _ = RequireHash(completion, field);
        if (!Sha256Pattern.IsMatch(imageDigest[7..]))
            throw new AcceptanceBlockedException("The native completion image digest is invalid.");
        return outputHash;
    }

    internal static JsonObject RequireMaterial(JsonObject receipt, string name, string expectedKind)
    {
        JsonObject material = receipt[name] as JsonObject
            ?? throw new AcceptanceBlockedException($"The native turn has no {name} acknowledgment.");
        if (!ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(material, "eventId"))
            || Long(material, "position") is not > 0
            || ApiAcceptanceSupport.GetString(material, "kind") != expectedKind)
            throw new AcceptanceBlockedException($"The native {name} acknowledgment is invalid.");
        _ = RequireHash(material, "contentSha256");
        _ = RequireHash(material, "bindingSha256");
        return material;
    }

    internal static async Task<JsonObject> ReadMaterialAsync(
        IGatewayClient api,
        AcceptanceFixture fixture,
        JsonObject material,
        CancellationToken ct) =>
        await ApiAcceptanceJourney.InvokeJsonAsync(api, "readSessionTurnContent",
            ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId),
                ("sessionId", fixture.SessionId), ("eventId", ApiAcceptanceSupport.GetString(material, "eventId")!)),
            null, null, [200], ct).ConfigureAwait(false);

    internal static void RequireReadback(
        JsonObject readback,
        AcceptanceFixture fixture,
        string expectedText,
        JsonObject material)
    {
        JsonObject identity = readback["identity"] as JsonObject ?? [];
        if (ApiAcceptanceSupport.GetString(readback, "eventId") != ApiAcceptanceSupport.GetString(material, "eventId")
            || ApiAcceptanceSupport.GetString(identity, "projectId") != fixture.ProjectId
            || ApiAcceptanceSupport.GetString(identity, "runId") != fixture.RunId
            || ApiAcceptanceSupport.GetString(identity, "sessionId") != fixture.SessionId
            || ApiAcceptanceSupport.GetString(readback, "text") != expectedText
            || Hash(Encoding.UTF8.GetBytes(expectedText)) != ApiAcceptanceSupport.GetString(material, "contentSha256"))
            throw new AcceptanceBlockedException("The public turn-material readback does not match its bound acknowledgment.");
    }

    internal static JsonObject MaterialSummary(JsonObject material) => new()
    {
        ["eventId"] = material["eventId"]?.DeepClone(),
        ["position"] = material["position"]?.DeepClone(),
        ["kind"] = material["kind"]?.DeepClone(),
        ["contentSha256"] = material["contentSha256"]?.DeepClone(),
        ["bindingSha256"] = material["bindingSha256"]?.DeepClone(),
    };

    private static async Task<JsonObject> WaitForReplacementAsync(
        IGatewayClient api,
        AcceptanceFixture fixture,
        string operationId,
        JsonObject replacementRequest,
        CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            JsonObject status = await ApiAcceptanceJourney.InvokeJsonAsync(api, "readRuntimeReplacement",
                ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId),
                    ("sessionId", fixture.RootSessionId), ("operationId", operationId)),
                null, null, [200], timeout.Token).ConfigureAwait(false);
            if (ApiAcceptanceSupport.GetString(status, "operationId") != operationId)
                throw new AcceptanceBlockedException("The Host replacement status belongs to a different operation.");
            string state = ApiAcceptanceSupport.GetString(status, "state") ?? "";
            if (state == "ready")
                return status;
            if (state is "blocked" or "failed" or "cleanup_pending" or "cleanup_complete")
                throw new AcceptanceBlockedException(
                    $"The approved Host replacement did not become ready ({ApiAcceptanceSupport.GetString(status, "failureCode") ?? state}).");
            JsonObject resumed = await ApiAcceptanceJourney.InvokeJsonAsync(api, "replaceRuntimeHost",
                ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId),
                    ("sessionId", fixture.RootSessionId)),
                replacementRequest.DeepClone() as JsonObject, null, [200], timeout.Token).ConfigureAwait(false);
            if (ApiAcceptanceSupport.GetString(resumed, "operationId") != operationId)
                throw new AcceptanceBlockedException("The Host replacement retry belongs to a different operation.");
            if (ApiAcceptanceSupport.GetString(resumed, "state") is "blocked" or "failed" or "cleanup_pending" or "cleanup_complete")
                throw new AcceptanceBlockedException(
                    $"The approved Host replacement did not become ready ({ApiAcceptanceSupport.GetString(resumed, "failureCode") ?? ApiAcceptanceSupport.GetString(resumed, "state")}).");
            if (ApiAcceptanceSupport.GetString(resumed, "state") == "ready")
                return resumed;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AcceptanceBlockedException("The approved Host replacement did not become ready before the bounded wait ended.");
            }
        }
    }

    internal static void RequireReplacement(
        JsonObject replacement,
        JsonObject completion,
        JsonObject startup,
        long executionFence)
    {
        JsonObject cache = replacement["recoveryCacheMaterial"] as JsonObject ?? [];
        JsonObject interval = replacement["reconstructionInterval"] as JsonObject ?? [];
        JsonObject calls = replacement["reconstructionCallSequence"] as JsonObject ?? [];
        if (Long(replacement, "contractVersion") != 1
            || !ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(replacement, "operationId"))
            || ApiAcceptanceSupport.GetString(replacement, "state") != "ready"
            || ApiAcceptanceSupport.GetString(replacement, "recoveryMode") != "nativeCache"
            || ApiAcceptanceSupport.GetString(replacement, "previousRuntimeInstanceId") != ApiAcceptanceSupport.GetString(startup, "runtimeInstanceId")
            || Long(replacement, "previousRegistrationRevision") != Long(startup, "registrationRevision")
            || ApiAcceptanceSupport.GetString(replacement, "currentRuntimeInstanceId") == ApiAcceptanceSupport.GetString(replacement, "previousRuntimeInstanceId")
            || Long(replacement, "currentRegistrationRevision") is not > 0
            || Long(replacement, "executionFence") != executionFence
            || ApiAcceptanceSupport.GetString(replacement, "acceptedSelectionHash") != ApiAcceptanceSupport.GetString(startup, "acceptedSelectionHash")
            || ApiAcceptanceSupport.GetString(cache, "kind") != "sdkCache"
            || !SameMaterial(cache, completion["cacheMaterial"] as JsonObject ?? [])
            || Long(interval, "eventCount") != 0
            || interval["isComplete"]?.GetValue<bool>() != true
            || ApiAcceptanceSupport.GetString(interval, "startCursorSha256") != ApiAcceptanceSupport.GetString(interval, "endCursorSha256")
            || Long(calls, "hostProcessId") is not > 0
            || !ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(calls, "hostProcessInstanceId"))
            || ApiAcceptanceSupport.GetString(calls, "runtimeInstanceId") != ApiAcceptanceSupport.GetString(replacement, "currentRuntimeInstanceId")
            || Long(calls, "registrationRevision") != Long(replacement, "currentRegistrationRevision")
            || Long(calls, "sendSequenceBefore") != 0
            || Long(calls, "sendSequenceAfter") != 0
            || Long(calls, "toolEffectSequenceBefore") != 0
            || Long(calls, "toolEffectSequenceAfter") != 0
            || string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(replacement, "previousPodUid"))
            || string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(replacement, "currentPodUid"))
            || ApiAcceptanceSupport.GetString(replacement, "previousPodUid") == ApiAcceptanceSupport.GetString(replacement, "currentPodUid")
            || string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(replacement, "workspaceVolumeId"))
            || Long(replacement, "workspaceResourceGeneration") is not > 0
            || Long(replacement, "workspaceDataGeneration") is null or < 0)
            throw new AcceptanceBlockedException("The replacement receipt does not prove a compatible cache restore with no native calls or tool effects.");
        _ = RequireHash(interval, "startCursorSha256");
        _ = RequireHash(interval, "endCursorSha256");
    }

    private static void RequireReplacementStartup(
        JsonObject replacement,
        JsonObject previousStartup,
        JsonObject currentStartup,
        long executionFence)
    {
        if (ApiAcceptanceSupport.GetString(currentStartup, "runtimeInstanceId") == ApiAcceptanceSupport.GetString(previousStartup, "runtimeInstanceId")
            || Long(currentStartup, "registrationRevision") is not > 0
            || ApiAcceptanceSupport.GetString(currentStartup, "runtimeInstanceId") != ApiAcceptanceSupport.GetString(replacement, "currentRuntimeInstanceId")
            || Long(currentStartup, "registrationRevision") != Long(replacement, "currentRegistrationRevision")
            || ApiAcceptanceSupport.GetString(currentStartup, "acceptedSelectionHash") != ApiAcceptanceSupport.GetString(previousStartup, "acceptedSelectionHash")
            || Long(currentStartup, "executionFence") != executionFence
            || ApiAcceptanceSupport.GetString(currentStartup, "podUid") != ApiAcceptanceSupport.GetString(replacement, "currentPodUid")
            || ApiAcceptanceSupport.GetString(currentStartup, "imageDigest") != ApiAcceptanceSupport.GetString(previousStartup, "imageDigest"))
            throw new AcceptanceBlockedException("The current runtime-startup projection does not match the ready replacement receipt.");
    }

    internal static void RequireReplay(
        JsonObject replay,
        JsonObject completion,
        JsonObject startup,
        long executionFence,
        string promptHash,
        string outputHash)
    {
        JsonObject interval = replay["nativeInterval"] as JsonObject ?? [];
        JsonObject calls = replay["nativeCallSequence"] as JsonObject ?? [];
        if (Long(replay, "contractVersion") != 1
            || ApiAcceptanceSupport.GetString(replay, "requestId") != ApiAcceptanceSupport.GetString(completion, "requestId")
            || ApiAcceptanceSupport.GetString(replay, "previousRuntimeInstanceId") != ApiAcceptanceSupport.GetString(completion, "runtimeInstanceId")
            || Long(replay, "previousRegistrationRevision") != Long(completion, "registrationRevision")
            || ApiAcceptanceSupport.GetString(replay, "previousRegistrationSha256") != ApiAcceptanceSupport.GetString(completion, "registrationSha256")
            || ApiAcceptanceSupport.GetString(replay, "runtimeInstanceId") != ApiAcceptanceSupport.GetString(startup, "runtimeInstanceId")
            || Long(replay, "registrationRevision") != Long(startup, "registrationRevision")
            || Long(replay, "executionFence") != executionFence
            || ApiAcceptanceSupport.GetString(replay, "acceptedSelectionHash") != ApiAcceptanceSupport.GetString(startup, "acceptedSelectionHash")
            || ApiAcceptanceSupport.GetString(replay, "promptSha256") != promptHash
            || ApiAcceptanceSupport.GetString(replay, "outputSha256") != outputHash
            || !SameMaterial(replay["userMaterial"] as JsonObject ?? [], completion["userMaterial"] as JsonObject ?? [])
            || !SameMaterial(replay["assistantMaterial"] as JsonObject ?? [], completion["assistantMaterial"] as JsonObject ?? [])
            || Long(interval, "eventCount") != 0
            || interval["isComplete"]?.GetValue<bool>() != true
            || ApiAcceptanceSupport.GetString(interval, "startCursorSha256") != ApiAcceptanceSupport.GetString(interval, "endCursorSha256")
            || Long(calls, "hostProcessId") is not > 0
            || !ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(calls, "hostProcessInstanceId"))
            || ApiAcceptanceSupport.GetString(calls, "runtimeInstanceId") != ApiAcceptanceSupport.GetString(startup, "runtimeInstanceId")
            || Long(calls, "registrationRevision") != Long(startup, "registrationRevision")
            || Long(calls, "sendSequenceBefore") != Long(calls, "sendSequenceAfter")
            || Long(calls, "toolEffectSequenceBefore") != Long(calls, "toolEffectSequenceAfter"))
            throw new AcceptanceBlockedException("The exact retry does not prove same-request material replay without a native send or tool effect.");
        _ = RequireHash(replay, "previousRegistrationSha256");
        _ = RequireHash(replay, "registrationSha256");
        _ = RequireHash(interval, "startCursorSha256");
        _ = RequireHash(interval, "endCursorSha256");
    }

    private static async Task<(JsonObject Run, JsonObject Session, JsonObject Decisions)> ReadOwnerSnapshotsAsync(
        IGatewayClient api,
        AcceptanceFixture fixture,
        CancellationToken ct)
    {
        Task<JsonObject> run = ApiAcceptanceJourney.InvokeJsonAsync(api, "readRunStatus",
            ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId)), null, null, [200], ct);
        Task<JsonObject> session = ApiAcceptanceJourney.InvokeJsonAsync(api, "readSessionStatus",
            ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId), ("sessionId", fixture.SessionId)),
            null, null, [200], ct);
        Task<JsonObject> decisions = ApiAcceptanceJourney.InvokeJsonAsync(api, "readDecisions",
            ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId), ("sessionId", fixture.SessionId)),
            null, null, [200], ct);
        await Task.WhenAll(run, session, decisions).ConfigureAwait(false);
        return (run.Result, session.Result, decisions.Result);
    }

    private static void RequireOwnerStateUnchanged(
        JsonObject initialRun,
        JsonObject initialSession,
        JsonObject initialDecisions,
        JsonObject run,
        JsonObject session,
        JsonObject decisions,
        AcceptanceFixture fixture)
    {
        if (Long(initialRun, "stateVersion") != Long(run, "stateVersion")
            || Long(initialSession, "stateVersion") != Long(session, "stateVersion")
            || Long(initialDecisions, "stateVersion") != Long(decisions, "stateVersion")
            || Long(run, "executionFence") != fixture.ExecutionFence
            || Long(session, "executionFence") != fixture.ExecutionFence
            || Long(decisions, "executionFence") != fixture.ExecutionFence)
            throw new AcceptanceBlockedException("The native material journey changed coordinator state or the accepted execution fence.");
    }

    internal static async Task RequireRootOnlyTreeAsync(
        IGatewayClient api,
        AcceptanceFixture fixture,
        CancellationToken ct)
    {
        JsonObject tree = await ApiAcceptanceJourney.InvokeJsonAsync(api, "readSessionTree",
            ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId)),
            null, null, [200], ct).ConfigureAwait(false);
        JsonArray nodes = tree["nodes"] as JsonArray ?? [];
        JsonObject? root = nodes.OfType<JsonObject>().SingleOrDefault();
        JsonObject identity = root?["identity"] as JsonObject ?? [];
        if (nodes.Count != 1
            || ApiAcceptanceSupport.GetString(tree, "rootSessionId") != fixture.RootSessionId
            || ApiAcceptanceSupport.GetString(identity, "projectId") != fixture.ProjectId
            || ApiAcceptanceSupport.GetString(identity, "runId") != fixture.RunId
            || ApiAcceptanceSupport.GetString(identity, "sessionId") != fixture.RootSessionId
            || root?["parentSessionId"] is not null
            || ApiAcceptanceSupport.GetString(root ?? [], "kind") != "coordinator"
            || ApiAcceptanceSupport.GetString(root ?? [], "lifecycle") != "active"
            || Long(root ?? [], "executionFence") != fixture.ExecutionFence)
            throw new AcceptanceBlockedException("The native material journey changed the accepted root or created an unexpected session.");
    }

    private static bool SameMaterial(JsonObject left, JsonObject right) =>
        ApiAcceptanceSupport.GetString(left, "eventId") == ApiAcceptanceSupport.GetString(right, "eventId")
        && Long(left, "position") == Long(right, "position")
        && ApiAcceptanceSupport.GetString(left, "kind") == ApiAcceptanceSupport.GetString(right, "kind")
        && ApiAcceptanceSupport.GetString(left, "contentSha256") == ApiAcceptanceSupport.GetString(right, "contentSha256")
        && ApiAcceptanceSupport.GetString(left, "bindingSha256") == ApiAcceptanceSupport.GetString(right, "bindingSha256");

    private static bool HashesDiffer(JsonObject value, string first, string second) =>
        RequireHash(value, first) != RequireHash(value, second);

    private static string RequireHash(JsonObject value, string name) =>
        ApiAcceptanceSupport.GetString(value, name) is string hash && Sha256Pattern.IsMatch(hash)
            ? hash
            : throw new AcceptanceBlockedException($"The native receipt has no valid {name} hash.");

    internal static string RequireGuid(JsonObject value, string name) =>
        ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(value, name))
            ? ApiAcceptanceSupport.GetString(value, name)!
            : throw new AcceptanceBlockedException($"The native receipt has no valid {name}.");

    internal static long? Long(JsonObject value, string name) =>
        ApiAcceptanceSupport.TryLong(value[name], out long result) ? result : null;

    internal static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
