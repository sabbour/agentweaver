using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentweaver.Cli;

internal sealed class AcceptanceBlockedException(string message) : Exception(message);

internal sealed record AcceptanceFixture(
    string ProjectId,
    string RunId,
    string RootSessionId,
    string SessionId,
    long ExecutionFence,
    long StateVersion,
    string? GateRequestId,
    string? ChildSessionId,
    JsonObject RootReceipt,
    JsonObject ProjectReceipt,
    JsonObject OwnerAssignment,
    JsonObject OrchestratorAssignment,
    JsonObject Selection,
    JsonObject? GateReceipt);

internal static class ApiAcceptanceJourney
{
    private static readonly Regex EventIdPattern = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ProblemCodePattern = new(
        "^[a-z][a-z0-9_]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] SnapshotOperations =
        ["readRunStatus", "readSessionStatus", "readDecisions"];

    public static async Task<(string Status, string? Failure)> RunAsync(
        AcceptanceManifest manifest,
        AcceptanceFixture fixture,
        IGatewayClient api,
        IGatewayClient? deniedApi,
        JsonObject sourceEvidence,
        List<JsonObject> steps,
        JsonObject observations,
        Func<string, string> idempotencyKey,
        CancellationToken ct)
    {
        string? failure = null;
        try
        {
            IReadOnlySet<string> operations = GetOperationIds(await api.DiscoverAsync(ct).ConfigureAwait(false));
            RequireCapabilities(operations, manifest.Journey);
            ApiAcceptanceSupport.AddStep(steps, "live-capabilities", "passed",
                ("requiredApiOperations", JsonValue.Create(RequiredOperations(manifest.Journey, fixture).Count)));

            JsonObject? denied = await CheckDeniedActorAsync(manifest, fixture, deniedApi, steps, ct).ConfigureAwait(false);
            OwnerSnapshot snapshot = await ReadOwnerSnapshotsAsync(api, fixture, ct).ConfigureAwait(false);
            ApiAcceptanceSupport.AddStep(steps, "current-owner-fences", "passed",
                ("operationId", JsonValue.Create("readRunStatus/readSessionStatus/readDecisions")));

            if (manifest.Journey.StartsWith("gate-", StringComparison.Ordinal))
            {
                ValidateActorAnswer(snapshot, manifest, fixture);
            }
            if (manifest.Journey == "message")
            {
                await ReadMessageTreeAsync(api, fixture, manifest, ct).ConfigureAwait(false);
            }

            switch (manifest.Journey)
            {
                case "journal":
                    JsonObject journal = await ReplayJournalAsync(api, fixture, ct).ConfigureAwait(false);
                    observations["journal"] = new JsonObject
                    {
                        ["eventCount"] = journal["eventCount"]?.DeepClone(),
                        ["pageCount"] = journal["pageCount"]?.DeepClone(),
                    };
                    ApiAcceptanceSupport.AddStep(steps, "committed-run-journal", "passed",
                        ("operationId", JsonValue.Create("replayRunEvents")),
                        ("correlationId", journal["correlationId"]?.DeepClone()));
                    ApiAcceptanceSupport.AddStep(steps, "journal", "passed",
                        ("operationId", JsonValue.Create("replayRunEvents")));
                    break;
                case "gate-answer":
                case "gate-approve":
                case "gate-reject":
                    await DispatchGateAsync(api, manifest, fixture, snapshot, steps, observations, idempotencyKey, ct)
                        .ConfigureAwait(false);
                    break;
                case "message":
                    await DispatchMessageAsync(api, manifest, fixture, steps, observations, idempotencyKey, ct)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new AcceptanceBlockedException("The selected P1 API journey is unsupported.");
            }

            bool missingDeniedActor = denied is null;
            return (missingDeniedActor ? "blocked" : "passed", null);
        }
        catch (AcceptanceBlockedException error)
        {
            // Missing prerequisites or unverified owner receipts block the proof; an observed contract violation fails it.
            failure = ApiAcceptanceSupport.RedactText(error.Message);
            return ("blocked", failure);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            failure = ApiAcceptanceSupport.RedactText(error.Message);
            return ("failed", failure);
        }
    }

    private static async Task<JsonObject?> CheckDeniedActorAsync(
        AcceptanceManifest manifest,
        AcceptanceFixture fixture,
        IGatewayClient? deniedApi,
        List<JsonObject> steps,
        CancellationToken ct)
    {
        JsonObject? deniedActor = manifest.FixturePlan["deniedActor"] as JsonObject;
        if (deniedActor is null)
        {
            ApiAcceptanceSupport.AddStep(steps, "valid-denied-actor-403", "blocked",
                ("reason", JsonValue.Create("Distinct authorized-but-denied actor unavailable: fixturePlan.deniedActor not provided (no second Broker account available)")));
            return null;
        }

        if (deniedApi is null)
        {
            ApiAcceptanceSupport.AddStep(steps, "valid-denied-actor-403", "blocked",
                ("reason", JsonValue.Create("The distinct denied-actor credential is unavailable; no second account was enrolled.")));
            return null;
        }

        JsonObject context = await ValidateAuthContextAsync(deniedApi, ct).ConfigureAwait(false);
        if (ApiAcceptanceSupport.GetString(context, "actorId") != ApiAcceptanceSupport.GetString(deniedActor, "actorId")
            || ApiAcceptanceSupport.GetString(context, "tenantId") != ApiAcceptanceSupport.GetString(deniedActor, "tenantId")
            || ApiAcceptanceSupport.GetString(context, "issuer") != ApiAcceptanceSupport.GetString(manifest.Environment, "issuer"))
        {
            throw new AcceptanceBlockedException("The denied-actor identity does not match the explicitly planned actor and tenant.");
        }

        GatewayResponse call = await InvokeAsync(deniedApi, "readRunStatus",
            Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId)), null, null, ct).ConfigureAwait(false);
        if (call.Status != 403)
        {
            throw new InvalidOperationException(call.Status == 401
                ? "The denied-actor token was rejected as unauthenticated; HTTP 403 was required."
                : $"The denied-actor read returned HTTP {call.Status}; HTTP 403 was required.");
        }

        ApiAcceptanceSupport.AddStep(steps, "valid-denied-actor-403", "passed",
            ("operationId", JsonValue.Create("readRunStatus")),
            ("httpStatus", JsonValue.Create(call.Status)));
        return deniedActor;
    }

    private static async Task<OwnerSnapshot> ReadOwnerSnapshotsAsync(IGatewayClient api, AcceptanceFixture fixture, CancellationToken ct)
    {
        Task<JsonObject> runTask = InvokeJsonAsync(api, "readRunStatus",
            Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId)), null, null, [200], ct);
        Task<JsonObject> sessionTask = InvokeJsonAsync(api, "readSessionStatus",
            Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId), ("sessionId", fixture.SessionId)), null, null, [200], ct);
        Task<JsonObject> decisionsTask = InvokeJsonAsync(api, "readDecisions",
            Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId), ("sessionId", fixture.SessionId)), null, null, [200], ct);
        await Task.WhenAll(runTask, sessionTask, decisionsTask).ConfigureAwait(false);
        JsonObject run = runTask.Result;
        JsonObject session = sessionTask.Result;
        JsonObject decisions = decisionsTask.Result;

        if (ApiAcceptanceSupport.GetString(run, "projectId") != fixture.ProjectId
            || ApiAcceptanceSupport.GetString(run, "runId") != fixture.RunId
            || ApiAcceptanceSupport.GetString(run, "rootSessionId") != fixture.RootSessionId
            || GetLong(run, "executionFence") != fixture.ExecutionFence
            || GetLong(run, "stateVersion") is null
            || ApiAcceptanceSupport.GetString(session["identity"] as JsonObject ?? [], "projectId") != fixture.ProjectId
            || ApiAcceptanceSupport.GetString(session["identity"] as JsonObject ?? [], "runId") != fixture.RunId
            || ApiAcceptanceSupport.GetString(session["identity"] as JsonObject ?? [], "sessionId") != fixture.SessionId
            || ApiAcceptanceSupport.GetString(session, "rootSessionId") != fixture.RootSessionId
            || GetLong(session, "executionFence") != fixture.ExecutionFence
            || GetLong(decisions, "executionFence") != fixture.ExecutionFence
            || GetLong(decisions, "stateVersion") is null)
        {
            throw new AcceptanceBlockedException("Current owner snapshots do not match the pinned project, run, session, and execution fence.");
        }

        return new OwnerSnapshot(run, session, decisions);
    }

    private static void ValidateActorAnswer(OwnerSnapshot snapshot, AcceptanceManifest manifest, AcceptanceFixture fixture)
    {
        JsonObject? gate = snapshot.Decisions["pendingGate"] as JsonObject;
        JsonArray blockers = snapshot.Session["blockers"] as JsonArray ?? [];
        JsonObject? blocker = blockers.OfType<JsonObject>()
            .FirstOrDefault(item => ApiAcceptanceSupport.GetString(item, "requestId") == fixture.GateRequestId);
        if (fixture.GateRequestId is null
            || ApiAcceptanceSupport.GetString(gate ?? [], "requestId") != fixture.GateRequestId
            || ApiAcceptanceSupport.GetString(gate ?? [], "authorizedActorId") != ApiAcceptanceSupport.GetString(manifest.Environment, "actorId")
            || GetLong(gate ?? [], "fence") != fixture.ExecutionFence
            || gate?["allowedChoices"] is not JsonArray
            || gate?["allowsFreeform"] is not JsonValue freeform || !freeform.TryGetValue<bool>(out _)
            || blocker is null)
        {
            throw new AcceptanceBlockedException("The current owner snapshot does not expose the exact fixture-owned gate for this actor and fence.");
        }

        string expectedKind = manifest.Journey == "gate-answer" ? "question" : "approval";
        string[] blockerKinds = manifest.Journey == "gate-answer" ? ["awaitingInput"] : ["awaitingApproval"];
        if (ApiAcceptanceSupport.GetString(gate, "kind") != expectedKind
            || !blockerKinds.Contains(ApiAcceptanceSupport.GetString(blocker, "kind"), StringComparer.Ordinal))
        {
            throw new AcceptanceBlockedException("The exact fixture-owned gate is missing or its owner blocker kind does not match.");
        }

        if (manifest.Journey == "gate-answer")
        {
            JsonObject planned = (JsonObject)manifest.FixturePlan["gate"]!;
            string answer = ApiAcceptanceSupport.GetString(planned, "answer")!;
            JsonArray choices = blocker["choices"] as JsonArray ?? [];
            JsonArray allowed = gate["allowedChoices"] as JsonArray ?? [];
            bool listed = choices.Any(value => value?.GetValue<string>() == answer)
                && allowed.Any(value => value?.GetValue<string>() == answer);
            bool freeformAllowed = blocker["allowsFreeform"]?.GetValue<bool>() == true
                && gate["allowsFreeform"]?.GetValue<bool>() == true
                && !choices.Any(value => value?.GetValue<string>() == answer);
            if (!listed && !freeformAllowed)
            {
                throw new AcceptanceBlockedException("The exact fixture-owned gate does not allow its configured answer.");
            }
        }
    }

    private static async Task DispatchGateAsync(
        IGatewayClient api,
        AcceptanceManifest manifest,
        AcceptanceFixture fixture,
        OwnerSnapshot snapshot,
        List<JsonObject> steps,
        JsonObject observations,
        Func<string, string> idempotencyKey,
        CancellationToken ct)
    {
        string operation = manifest.Journey switch
        {
            "gate-answer" => "answerGate",
            "gate-approve" => "approveGate",
            _ => "rejectGate",
        };
        JsonObject body = new()
        {
            ["expectedStateVersion"] = GetLong(snapshot.Decisions, "stateVersion"),
            ["idempotencyKey"] = idempotencyKey(operation),
        };
        if (manifest.Journey == "gate-answer")
        {
            string answer = ApiAcceptanceSupport.GetString((JsonObject)manifest.FixturePlan["gate"]!, "answer")!;
            JsonArray choices = (JsonArray)snapshot.Decisions["pendingGate"]!["allowedChoices"]!;
            JsonArray blockers = snapshot.Session["blockers"] as JsonArray ?? [];
            JsonObject blocker = blockers.OfType<JsonObject>().First(item =>
                ApiAcceptanceSupport.GetString(item, "requestId") == fixture.GateRequestId);
            bool choice = choices.Any(value => value?.GetValue<string>() == answer)
                && (blocker["choices"] as JsonArray ?? []).Any(value => value?.GetValue<string>() == answer);
            body["choiceId"] = choice ? answer : null;
            body["freeformAnswer"] = choice ? null : answer;
        }

        GatewayResponse response = await InvokeAsync(api, operation,
            Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId),
                ("sessionId", fixture.SessionId), ("requestId", fixture.GateRequestId!)),
            null, body, ct).ConfigureAwait(false);
        JsonObject receipt = RequireStatusBody(response, [200], operation);
        RequireDecisionReceipt(receipt, GetLong(snapshot.Decisions, "stateVersion")!.Value, fixture.ExecutionFence);
        AddActionReceipt(observations, operation, response.Status, receipt, response);
        await WaitForGateResolutionAsync(api, fixture, GetLong(receipt, "stateVersion")!.Value, ct).ConfigureAwait(false);
        ApiAcceptanceSupport.AddStep(steps, manifest.Journey, "passed",
            ("operationId", JsonValue.Create(operation)),
            ("httpStatus", JsonValue.Create(response.Status)));
    }

    private static async Task DispatchMessageAsync(
        IGatewayClient api,
        AcceptanceManifest manifest,
        AcceptanceFixture fixture,
        List<JsonObject> steps,
        JsonObject observations,
        Func<string, string> idempotencyKey,
        CancellationToken ct)
    {
        JsonObject messagePlan = (JsonObject)manifest.FixturePlan["message"]!;
        JsonObject tree = await InvokeJsonAsync(api, "readSessionTree",
            Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId), ("sessionId", fixture.RootSessionId)),
            null, null, [200], ct).ConfigureAwait(false);
        if (ApiAcceptanceSupport.GetString(tree, "rootSessionId") != fixture.RootSessionId
            || tree["nodes"] is not JsonArray nodes
            || nodes.OfType<JsonObject>().Any(node =>
                ApiAcceptanceSupport.GetString(node["identity"] as JsonObject ?? [], "projectId") != fixture.ProjectId
                || ApiAcceptanceSupport.GetString(node["identity"] as JsonObject ?? [], "runId") != fixture.RunId))
        {
            throw new AcceptanceBlockedException("The live owner session tree does not match the pinned fixture scope.");
        }

        string sender = fixture.RootSessionId;
        string recipient = fixture.ChildSessionId ?? "";
        foreach (string sessionId in new[] { sender, recipient })
        {
            int matches = nodes.OfType<JsonObject>().Count(node =>
                ApiAcceptanceSupport.GetString(node["identity"] as JsonObject ?? [], "sessionId") == sessionId
                && ApiAcceptanceSupport.GetString(node, "lifecycle") == "active");
            if (string.IsNullOrWhiteSpace(sessionId) || matches != 1)
            {
                throw new AcceptanceBlockedException("Both addressed message sessions must already be active in the runner-owned session tree.");
            }
        }

        string text = ApiAcceptanceSupport.GetString(messagePlan, "text")!.Trim();
        if (sender == recipient || string.IsNullOrWhiteSpace(text))
        {
            throw new AcceptanceBlockedException("The fixture scope has no exact addressed-message pair and content.");
        }

        JsonObject body = new()
        {
            ["recipientSessionId"] = recipient,
            ["idempotencyKey"] = idempotencyKey("sendMessage"),
            ["deliveryMode"] = "immediate",
            ["purpose"] = "progress",
            ["kind"] = "text",
            ["payload"] = new JsonObject { ["text"] = text },
        };
        GatewayResponse response = await InvokeAsync(api, "sendMessage",
            Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId), ("sessionId", sender)),
            null, body, ct).ConfigureAwait(false);
        JsonObject receipt = RequireStatusBody(response, [200, 202], "sendMessage");
        if (string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(receipt, "ownerMessageId"))
            || ApiAcceptanceSupport.GetString(receipt, "recipientSessionId") != recipient
            || ApiAcceptanceSupport.GetString(receipt, "status") is not ("accepted" or "admitted"))
        {
            throw new InvalidOperationException("The message owner did not return its actual accepted-message receipt.");
        }

        AddActionReceipt(observations, "sendMessage", response.Status, receipt, response);
        ApiAcceptanceSupport.AddStep(steps, "message", "passed",
            ("operationId", JsonValue.Create("sendMessage")),
            ("httpStatus", JsonValue.Create(response.Status)),
            ("acceptedNotDelivered", JsonValue.Create(true)));
    }

    private static void AddActionReceipt(
        JsonObject observations,
        string operation,
        int status,
        JsonObject receipt,
        GatewayResponse response)
    {
        JsonArray receipts = observations["actionReceipts"] as JsonArray ?? [];
        receipts.Add(new JsonObject
        {
            ["operationId"] = operation,
            ["httpStatus"] = status,
            ["receipt"] = ApiAcceptanceSupport.Redact(receipt),
            ["correlationId"] = CorrelationId(response),
        });
        observations["actionReceipts"] = receipts;
    }

    private static async Task<JsonObject> ReplayJournalAsync(IGatewayClient api, AcceptanceFixture fixture, CancellationToken ct)
    {
        HashSet<string> eventIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<long> positions = [];
        HashSet<string> cursors = new(StringComparer.Ordinal);
        string? cursor = null;
        string? correlationId = null;
        int pageCount = 0;
        long lastPosition = 0;

        for (int pageNumber = 0; pageNumber < 20; pageNumber++)
        {
            Dictionary<string, string> query = new(StringComparer.Ordinal)
            {
                ["limit"] = "100",
            };
            if (cursor is not null) query["cursor"] = cursor;
            GatewayResponse response = await InvokeAsync(api, "replayRunEvents",
                Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId)), query, null, ct).ConfigureAwait(false);
            JsonObject page = RequireStatusBody(response, [200], "replayRunEvents");
            if (page["events"] is not JsonArray events || events.Count is < 1 or > 100
                || page["hasMore"] is not JsonValue hasMoreValue
                || !hasMoreValue.TryGetValue<bool>(out bool hasMore)
                || !page.ContainsKey("nextCursor")
                || page["nextCursor"] is not null
                    && (page["nextCursor"] is not JsonValue cursorValue
                        || !cursorValue.TryGetValue<string>(out _))
                || events.Any(item => item is not JsonObject))
            {
                throw new InvalidOperationException("The actual owner replay page is malformed, empty, or exceeds the requested event limit.");
            }
            string? nextCursor = ApiAcceptanceSupport.GetString(page, "nextCursor");
            if (nextCursor is { } boundedCursor
                && (boundedCursor.Length == 0 || boundedCursor.Length > 8192
                    || boundedCursor.Any(character => character is '\r' or '\n' or '\0')))
            {
                throw new InvalidOperationException("The actual owner replay cursor does not match the bounded cursor contract.");
            }
            foreach (JsonObject? item in events.OfType<JsonObject>())
            {
                JsonObject identity = item["identity"] as JsonObject ?? [];
                string? eventId = ApiAcceptanceSupport.GetString(item, "eventId");
                string? sessionId = ApiAcceptanceSupport.GetString(identity, "sessionId");
                long? position = GetLong(item, "position");
                if (!EventIdPattern.IsMatch(eventId ?? "")
                    || identity.Count != 3
                    || ApiAcceptanceSupport.GetString(identity, "projectId") != fixture.ProjectId
                    || ApiAcceptanceSupport.GetString(identity, "runId") != fixture.RunId
                    || string.IsNullOrWhiteSpace(sessionId)
                    || position is null || position < 1 || position > 9_007_199_254_740_991L
                    || !eventIds.Add(eventId!) || !positions.Add(position.Value) || position.Value <= lastPosition)
                {
                    throw new InvalidOperationException("The actual owner replay contains a foreign, repeated, or reordered event identity.");
                }
                lastPosition = position.Value;
            }

            if (string.IsNullOrEmpty(nextCursor) || nextCursor == cursor || !cursors.Add(nextCursor))
            {
                throw new InvalidOperationException("The actual API replay cursor did not advance.");
            }
            pageCount++;
            correlationId = CorrelationId(response) ?? correlationId;
            if (!hasMore) return new JsonObject
            {
                ["eventCount"] = eventIds.Count,
                ["pageCount"] = pageCount,
                ["correlationId"] = correlationId,
            };
            cursor = nextCursor;
        }
        throw new InvalidOperationException("The actual owner replay exceeded the 20-page bound.");
    }

    private static async Task ReadMessageTreeAsync(IGatewayClient api, AcceptanceFixture fixture, AcceptanceManifest manifest, CancellationToken ct)
    {
        if (fixture.ChildSessionId is null || manifest.FixturePlan["message"] is not JsonObject)
        {
            throw new AcceptanceBlockedException("The fixture scope has no exact addressed-message pair and content.");
        }
        JsonObject tree = await InvokeJsonAsync(api, "readSessionTree",
            Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId), ("sessionId", fixture.RootSessionId)),
            null, null, [200], ct).ConfigureAwait(false);
        if (ApiAcceptanceSupport.GetString(tree, "rootSessionId") != fixture.RootSessionId
            || tree["nodes"] is not JsonArray nodes
            || nodes.OfType<JsonObject>().Any(node =>
                ApiAcceptanceSupport.GetString(node["identity"] as JsonObject ?? [], "projectId") != fixture.ProjectId
                || ApiAcceptanceSupport.GetString(node["identity"] as JsonObject ?? [], "runId") != fixture.RunId))
        {
            throw new AcceptanceBlockedException("The live owner session tree does not match the pinned fixture scope.");
        }
    }

    private static async Task WaitForGateResolutionAsync(IGatewayClient api, AcceptanceFixture fixture, long expectedVersion, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow <= deadline)
        {
            JsonObject decisions = await InvokeJsonAsync(api, "readDecisions",
                Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId), ("sessionId", fixture.SessionId)),
                null, null, [200], ct).ConfigureAwait(false);
            if (GetLong(decisions, "executionFence") != fixture.ExecutionFence
                || GetLong(decisions, "stateVersion") is not long version
                || version < expectedVersion)
            {
                throw new AcceptanceBlockedException("The gate resolution changed or lost its pinned owner fence.");
            }
            if (ApiAcceptanceSupport.GetString(decisions["pendingGate"] as JsonObject ?? [], "requestId") != fixture.GateRequestId)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }
        throw new TimeoutException("The owner did not confirm the fixture-owned gate resolution before the bounded wait expired.");
    }

    private static void RequireDecisionReceipt(JsonObject receipt, long stateVersion, long fence)
    {
        if (receipt["accepted"] is not JsonValue accepted || !accepted.TryGetValue<bool>(out bool value) || !value
            || string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(receipt, "decisionId"))
            || GetLong(receipt, "stateVersion") != stateVersion + 1
            || GetLong(receipt, "executionFence") != fence
            || receipt["issues"] is not JsonArray issues
            || issues.OfType<JsonObject>().Any(issue =>
                ApiAcceptanceSupport.GetString(issue, "code") is null
                || ApiAcceptanceSupport.GetString(issue, "path") is null
                || ApiAcceptanceSupport.GetString(issue, "message") is null))
        {
            throw new InvalidOperationException("The owner action did not return its typed decision receipt for the next state version and current fence.");
        }
    }

    private static void RequireCapabilities(IReadOnlySet<string> operations, string journey)
    {
        string[] missing = RequiredOperations(journey, null)
            .Where(operation => !operations.Contains(operation)).ToArray();
        if (missing.Length > 0)
        {
            throw new AcceptanceBlockedException($"Required P1 API operations are missing: {string.Join(", ", missing)}.");
        }
    }

    private static HashSet<string> RequiredOperations(string journey, AcceptanceFixture? fixture)
    {
        HashSet<string> required = ["readRunStatus", "readSessionStatus", "readDecisions"];
        if (journey.StartsWith("gate-", StringComparison.Ordinal))
        {
            required.Add(journey switch
            {
                "gate-answer" => "answerGate",
                "gate-approve" => "approveGate",
                _ => "rejectGate",
            });
        }
        else if (journey == "message")
        {
            required.Add("readSessionTree");
            required.Add("sendMessage");
        }
        else
        {
            required.Add("replayRunEvents");
        }
        return required;
    }

    public static IReadOnlySet<string> GetOperationIds(JsonObject openApi)
    {
        JsonObject paths = openApi["paths"] as JsonObject
            ?? throw new AcceptanceBlockedException("The live Gateway OpenAPI document has no path map.");
        HashSet<string> result = new(StringComparer.Ordinal);
        foreach (JsonObject? item in paths.Select(pair => pair.Value).OfType<JsonObject>())
        {
            foreach (JsonObject operation in item.Select(pair => pair.Value).OfType<JsonObject>())
            {
                string? id = ApiAcceptanceSupport.GetString(operation, "operationId");
                if (id is null) continue;
                if (!result.Add(id))
                {
                    throw new AcceptanceBlockedException("The live Gateway OpenAPI document contains a duplicate operation identity.");
                }
            }
        }
        return result;
    }

    public static async Task<JsonObject> ValidateAuthContextAsync(IGatewayClient api, CancellationToken ct)
    {
        JsonObject context;
        try
        {
            context = await api.ValidateAuthorizationContextAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new AcceptanceBlockedException("The current Broker identity could not establish its live Gateway authorization context.");
        }
        if (GetLong(context, "contractVersion") != 1
            || context["effectiveAuthority"] is not JsonArray)
        {
            throw new AcceptanceBlockedException("The live Gateway authorization context is incomplete.");
        }
        return context;
    }

    public static void ValidateAuthContext(
        JsonObject context,
        AcceptanceManifest manifest,
        string? projectId = null,
        string? runId = null)
    {
        if (ApiAcceptanceSupport.GetString(context, "issuer") != ApiAcceptanceSupport.GetString(manifest.Environment, "issuer")
            || ApiAcceptanceSupport.GetString(context, "actorId") != ApiAcceptanceSupport.GetString(manifest.Environment, "actorId")
            || ApiAcceptanceSupport.GetString(context, "tenantId") != ApiAcceptanceSupport.GetString(manifest.Environment, "tenantId")
            || ApiAcceptanceSupport.GetString(context, "boundProjectId") != projectId
            || ApiAcceptanceSupport.GetString(context, "boundRunId") != runId)
        {
            throw new AcceptanceBlockedException("The live Gateway authorization context does not match the exact selected actor and run.");
        }
        if (projectId is not null)
        {
            JsonArray authorities = context["effectiveAuthority"] as JsonArray ?? [];
            JsonObject? project = authorities.OfType<JsonObject>().FirstOrDefault(authority =>
                ApiAcceptanceSupport.GetString(authority, "resourceType") == "project"
                && ApiAcceptanceSupport.GetString(authority, "resourceId") == projectId);
            JsonArray permissions = project?["permissions"] as JsonArray ?? [];
            if (!permissions.OfType<JsonObject>().Any(permission =>
                ApiAcceptanceSupport.GetString(permission, "permission") == "accessPrivateKnowledge"))
            {
                throw new AcceptanceBlockedException("The run-bound actor has no current Owner and Orchestrator authority for this project.");
            }
        }
    }

    public static async Task<JsonObject> InvokeJsonAsync(
        IGatewayClient api,
        string operation,
        IReadOnlyDictionary<string, string>? path,
        JsonObject? body,
        IReadOnlyDictionary<string, string>? headers,
        IReadOnlyCollection<int> expected,
        CancellationToken ct)
    {
        GatewayResponse response = await InvokeAsync(api, operation, path, null, body, ct, headers).ConfigureAwait(false);
        return RequireStatusBody(response, expected, operation);
    }

    public static async Task<GatewayResponse> InvokeAsync(
        IGatewayClient api,
        string operation,
        IReadOnlyDictionary<string, string>? path,
        IReadOnlyDictionary<string, string>? query,
        JsonObject? body,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        return await api.InvokeAsync(operation,
            new GatewayInvocation(path, query, body?.DeepClone(), headers), ct).ConfigureAwait(false);
    }

    public static JsonObject RequireStatusBody(GatewayResponse response, IReadOnlyCollection<int> expected, string operation)
    {
        if (!expected.Contains(response.Status))
        {
            string? code = ApiAcceptanceSupport.GetString(response.Body as JsonObject ?? [], "code");
            if (!ProblemCodePattern.IsMatch(code ?? "")) code = null;
            throw new InvalidOperationException(
                $"The live {operation} operation returned HTTP {response.Status}{(code is null ? "" : $" ({code})")}; expected {string.Join(" or ", expected)}.");
        }
        return response.Body as JsonObject
            ?? throw new InvalidOperationException($"The live {operation} operation returned no JSON object.");
    }

    public static Dictionary<string, string> Path(params (string Name, string Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);

    private static long? GetLong(JsonObject value, string name) =>
        ApiAcceptanceSupport.TryLong(value[name], out long result) ? result : null;

    private static string? CorrelationId(GatewayResponse response)
    {
        foreach (string header in new[] { "x-correlation-id", "x-request-id", "request-id", "traceparent" })
        {
            if (response.Headers.TryGetValue(header, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    private sealed record OwnerSnapshot(JsonObject Run, JsonObject Session, JsonObject Decisions);
}
