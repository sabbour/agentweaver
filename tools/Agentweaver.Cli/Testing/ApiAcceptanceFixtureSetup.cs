using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentweaver.Cli;

internal sealed class AcceptanceFixtureSetupException(Exception inner, AcceptanceFixtureState state)
    : Exception(inner.Message, inner)
{
    public AcceptanceFixtureState State { get; } = state;
}

internal static class ApiAcceptanceFixtureSetup
{
    private static readonly Regex ProblemCode = new("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task<(AcceptanceFixture Fixture, IGatewayClient RunApi, IGatewayClient? DeniedApi, AcceptanceFixtureState State)> CreateAsync(
        AcceptanceManifest manifest,
        string invocationId,
        IGatewayClient setupApi,
        IGatewayClientFactory clients,
        ICliAuthentication authentication,
        InteractionPolicy interaction,
        CancellationToken ct)
    {
        AcceptanceFixtureState state = new(invocationId);
        JsonObject environment = manifest.Environment;
        try
        {
            JsonObject openApi = await setupApi.DiscoverAsync(ct).ConfigureAwait(false);
            EnsureSetupCapabilities(openApi, manifest.Journey);
            JsonObject setupContext = await ApiAcceptanceJourney.ValidateAuthContextAsync(setupApi, ct).ConfigureAwait(false);
            ApiAcceptanceJourney.ValidateAuthContext(setupContext, manifest);

            JsonObject created = await MutatingSetupStepAsync(state, "create-project", setupApi, "createProject",
                new GatewayInvocation(Body: new JsonObject { ["name"] = state.ProjectName }), [201], ct).ConfigureAwait(false);
            string projectId = RequireProject(created, state, "active");
            state.Setup = new JsonObject
            {
                ["invocationId"] = invocationId,
                ["tenantId"] = ApiAcceptanceSupport.GetString(environment, "tenantId"),
                ["projectId"] = projectId,
                ["projectRevision"] = GetLong(created, "revision"),
                ["projectConfigurationRevision"] = GetLong(created, "configurationRevision"),
                ["ownerAssignmentId"] = null,
                ["ownerAssignmentRevision"] = null,
                ["assignmentId"] = null,
                ["assignmentRevision"] = null,
                ["complete"] = false,
            };

            JsonObject owner = await MutatingSetupStepAsync(state, "assign-owner", setupApi, "assignProjectRole",
                new GatewayInvocation(
                    Path(("projectId", projectId)),
                    Body: new JsonObject
                    {
                        ["principalId"] = ApiAcceptanceSupport.GetString(environment, "actorId"),
                        ["role"] = "owner",
                    }), [201], ct).ConfigureAwait(false);
            RequireAssignment(owner, projectId, environment, "owner", "active", 1);
            state.Setup["ownerAssignmentId"] = ApiAcceptanceSupport.GetString(owner, "assignmentId");
            state.Setup["ownerAssignmentRevision"] = GetLong(owner, "revision");

            JsonObject orchestrator = await MutatingSetupStepAsync(state, "assign-orchestrator", setupApi, "assignProjectRole",
                new GatewayInvocation(
                    Path(("projectId", projectId)),
                    Body: new JsonObject
                    {
                        ["principalId"] = ApiAcceptanceSupport.GetString(environment, "actorId"),
                        ["role"] = "orchestrator",
                    }), [201], ct).ConfigureAwait(false);
            RequireAssignment(orchestrator, projectId, environment, "orchestrator", "active", 1);
            state.Setup["assignmentId"] = ApiAcceptanceSupport.GetString(orchestrator, "assignmentId");
            state.Setup["assignmentRevision"] = GetLong(orchestrator, "revision");
            state.Setup["complete"] = true;
            state.SetupComplete = true;

            JsonObject configuration = await CallJsonAsync(setupApi, "getProjectConfiguration",
                new GatewayInvocation(Path(("projectId", projectId))), [200], ct).ConfigureAwait(false);
            long configurationRevision = GetLong(configuration, "revision")
                ?? throw new InvalidOperationException("The current project configuration revision is invalid.");
            if (ApiAcceptanceSupport.GetString(configuration, "projectId") != projectId
                || configurationRevision != GetLong(created, "configurationRevision"))
            {
                throw new InvalidOperationException("The current project configuration revision does not match setup.");
            }

            string runId = ApiAcceptanceSupport.StableUuid(invocationId, "run");
            string rootSessionId = ApiAcceptanceSupport.StableUuid(invocationId, "root-session");
            string? childSessionId = manifest.Journey == "message"
                ? ApiAcceptanceSupport.StableUuid(invocationId, "child-session")
                : null;
            long expectedPlatformRevision = GetLong(manifest.FixturePlan, "expectedPlatformRuntimeRevision")
                ?? throw new InvalidDataException("A positive expected platform runtime revision is required in the runner fixture plan.");
            JsonObject selectionContext = (JsonObject)manifest.FixturePlan["selectionContext"]!;
            JsonObject selectionRequest = new()
            {
                ["expectedProjectConfigRevision"] = configurationRevision,
                ["expectedPlatformRuntimeRevision"] = expectedPlatformRevision,
                ["context"] = selectionContext.DeepClone(),
            };
            CliTarget target = new(
                new Uri(ApiAcceptanceSupport.GetString(environment, "baseUrl")!, UriKind.Absolute),
                new Uri(ApiAcceptanceSupport.GetString(environment, "issuer")!, UriKind.Absolute),
                "agentweaver-cli",
                ApiAcceptanceSupport.GetString(environment, "audience")!,
                ApiAcceptanceSupport.GetString(environment, "tenantId")!);
            CliIdentity selectionIdentity = new(target,
                ["openid", "api.read", "offline_access", "projects.orchestrator"]);
            try
            {
                _ = await authentication.GetBearerAsync(selectionIdentity, interaction, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw new AcceptanceBlockedException("Unbound Orchestrator authorization for run selection is unavailable.");
            }

            IGatewayClient selectionApi = clients.Create(selectionIdentity);
            JsonObject acceptedSelection = await CallJsonAsync(selectionApi, "acceptRunSelection",
                new GatewayInvocation(
                    Path(("projectId", projectId), ("runId", runId)),
                    Body: selectionRequest), [200], ct).ConfigureAwait(false);
            state.Selection = acceptedSelection.DeepClone() as JsonObject;
            JsonObject observedSelection = await CallJsonAsync(selectionApi, "getRunSelection",
                new GatewayInvocation(Path(("projectId", projectId), ("runId", runId))), [200], ct).ConfigureAwait(false);
            RequireSelection(acceptedSelection, projectId, runId, configurationRevision, expectedPlatformRevision,
                ApiAcceptanceSupport.GetString(selectionContext, "revision")!);
            RequireSelection(observedSelection, projectId, runId, configurationRevision, expectedPlatformRevision,
                ApiAcceptanceSupport.GetString(selectionContext, "revision")!);

            CliIdentity runIdentity = new(target,
                ["openid", "api.read", "offline_access", "projects.orchestrator"],
                projectId,
                runId);
            try
            {
                _ = await authentication.GetBearerAsync(runIdentity, interaction, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw new AcceptanceBlockedException("Run-bound Broker authorization is unavailable; the owned fixture will be cleaned up.");
            }

            IGatewayClient runApi = clients.Create(runIdentity);
            JsonObject runContext = await ApiAcceptanceJourney.ValidateAuthContextAsync(runApi, ct).ConfigureAwait(false);
            if (ApiAcceptanceSupport.GetString(runContext, "issuer") != ApiAcceptanceSupport.GetString(setupContext, "issuer")
                || ApiAcceptanceSupport.GetString(runContext, "actorId") != ApiAcceptanceSupport.GetString(setupContext, "actorId")
                || ApiAcceptanceSupport.GetString(runContext, "tenantId") != ApiAcceptanceSupport.GetString(setupContext, "tenantId"))
            {
                throw new AcceptanceBlockedException("The setup and run-bound contexts do not belong to the same authenticated actor and tenant.");
            }
            ApiAcceptanceJourney.ValidateAuthContext(runContext, manifest, projectId, runId);

            if (manifest.Journey == "journal")
            {
                ValidateJournalMessageOperation(openApi);
            }

            string rootId = ApiAcceptanceSupport.StableUuid(invocationId, "root-session");
            JsonObject rootReceipt = await CallJsonAsync(runApi, "acceptRunRoot",
                new GatewayInvocation(
                    Path(("projectId", projectId), ("runId", runId)),
                    Body: new JsonObject { ["sessionId"] = rootId }), [201], ct).ConfigureAwait(false);
            RequireRoot(rootReceipt, projectId, runId, rootId);
            long fence = GetLong(rootReceipt, "executionFence")!.Value;
            long stateVersion = GetLong(rootReceipt, "stateVersion")!.Value;
            JsonObject rootStatus = await CallJsonAsync(runApi, "readRunStatus",
                new GatewayInvocation(Path(("projectId", projectId), ("runId", runId))), [200], ct).ConfigureAwait(false);
            JsonObject sessionStatus = await CallJsonAsync(runApi, "readSessionStatus",
                new GatewayInvocation(Path(("projectId", projectId), ("runId", runId), ("sessionId", rootId))), [200], ct).ConfigureAwait(false);
            RequireRootReadback(rootStatus, sessionStatus, projectId, runId, rootId, fence);

            if (manifest.Journey == "journal")
            {
                string seedKey = ApiAcceptanceSupport.StableUuid(invocationId, "journal-message-idempotency");
                JsonObject seedReceipt = await MutatingSetupStepAsync(state, "seed-journal-message", runApi,
                    "sendProjectRunMessage",
                    new GatewayInvocation(
                        Path(("projectId", projectId), ("runId", runId)),
                        Body: new JsonObject { ["text"] = "Controlled P1 journal fixture event." },
                        Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Idempotency-Key"] = seedKey,
                        }), [200], ct).ConfigureAwait(false);
                RequireJournalMessageReceipt(seedReceipt, projectId, runId, rootId, environment);
            }

            if (childSessionId is not null)
            {
                JsonObject child = await CallJsonAsync(runApi, "registerChild",
                    new GatewayInvocation(
                        Path(("projectId", projectId), ("runId", runId), ("parentSessionId", rootId)),
                        Body: new JsonObject { ["sessionId"] = childSessionId }), [201], ct).ConfigureAwait(false);
                JsonObject childIdentity = child["identity"] as JsonObject ?? [];
                if (ApiAcceptanceSupport.GetString(childIdentity, "projectId") != projectId
                    || ApiAcceptanceSupport.GetString(childIdentity, "runId") != runId
                    || ApiAcceptanceSupport.GetString(childIdentity, "sessionId") != childSessionId)
                {
                    throw new InvalidOperationException("The owner did not return the exact accepted child session.");
                }
                await ReadTreeAsync(runApi, projectId, runId, rootId, [rootId, childSessionId], ct).ConfigureAwait(false);
            }

            string? gateRequestId = null;
            JsonObject? gateReceipt = null;
            long currentStateVersion = stateVersion;
            if (manifest.Journey.StartsWith("gate-", StringComparison.Ordinal))
            {
                (gateRequestId, gateReceipt, currentStateVersion) = await CreateGateAsync(
                    runApi, manifest, projectId, runId, rootId, fence, stateVersion, environment, invocationId, ct)
                    .ConfigureAwait(false);
            }

            await ReadTreeAsync(runApi, projectId, runId, rootId,
                childSessionId is null ? [rootId] : [rootId, childSessionId], ct).ConfigureAwait(false);

            JsonObject projectReceipt = created.DeepClone() as JsonObject ?? [];
            JsonObject ownerAssignment = owner.DeepClone() as JsonObject ?? [];
            JsonObject orchestratorAssignment = orchestrator.DeepClone() as JsonObject ?? [];
            AcceptanceFixture fixture = new(
                projectId, runId, rootId, rootId, fence, currentStateVersion,
                gateRequestId, childSessionId, rootReceipt, projectReceipt,
                ownerAssignment, orchestratorAssignment,
                acceptedSelection, gateReceipt);
            return (fixture, runApi, await CreateDeniedClientAsync(
                manifest, target, clients, authentication, interaction, projectId, runId, ct).ConfigureAwait(false), state);
        }
        catch (OperationCanceledException error) when (ct.IsCancellationRequested)
        {
            throw new AcceptanceFixtureSetupException(error, state);
        }
        catch (Exception error)
        {
            throw new AcceptanceFixtureSetupException(error, state);
        }
    }

    private static async Task<IGatewayClient?> CreateDeniedClientAsync(
        AcceptanceManifest manifest,
        CliTarget target,
        IGatewayClientFactory clients,
        ICliAuthentication authentication,
        InteractionPolicy interaction,
        string projectId,
        string runId,
        CancellationToken ct)
    {
        if (manifest.FixturePlan["deniedActor"] is not JsonObject denied)
        {
            return null;
        }

        CliTarget deniedTarget = target with { TenantId = ApiAcceptanceSupport.GetString(denied, "tenantId")! };
        CliIdentity identity = new(deniedTarget,
            ["openid", "api.read", "offline_access", "projects.orchestrator"], projectId, runId);
        try
        {
            _ = await authentication.GetBearerAsync(identity, interaction, ct).ConfigureAwait(false);
            return clients.Create(identity);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<JsonObject> CleanupAsync(
        IGatewayClient setupApi,
        AcceptanceFixtureState state,
        string actorId,
        CancellationToken ct)
    {
        if (!state.SetupAttempted)
        {
            return state.Cleanup = new JsonObject
            {
                ["invocationId"] = state.InvocationId,
                ["state"] = "not-created",
            };
        }
        if (state.SetupIndeterminate || !state.SetupComplete || state.Setup is null)
        {
            return state.Cleanup = new JsonObject
            {
                ["invocationId"] = state.InvocationId,
                ["state"] = "indeterminate",
                ["reason"] = "Setup did not return every expected receipt, so cleanup was not attempted.",
            };
        }
        if (state.CleanupAttempted)
        {
            throw new InvalidOperationException("P1 fixture cleanup was already attempted for this invocation and is never retried.");
        }
        state.CleanupAttempted = true;
        state.Cleanup = new JsonObject
        {
            ["invocationId"] = state.InvocationId,
            ["state"] = "indeterminate",
            ["reason"] = "Cleanup was attempted, but not every cleanup receipt was verified.",
        };

        JsonObject setup = state.Setup;
        string projectId = ApiAcceptanceSupport.GetString(setup, "projectId")!;
        JsonObject revokeOrchestrator = await CleanupStepAsync(state, "revoke-orchestrator",
            setupApi, "revokeProjectRoleAssignment",
            new GatewayInvocation(
                Path(("projectId", projectId), ("assignmentId", ApiAcceptanceSupport.GetString(setup, "assignmentId")!)),
                Query: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["expectedRevision"] = GetLong(setup, "assignmentRevision")!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }), ct).ConfigureAwait(false);
        RequireRevokedAssignment(revokeOrchestrator, projectId,
            ApiAcceptanceSupport.GetString(setup, "assignmentId")!,
            actorId, "orchestrator", GetLong(setup, "assignmentRevision")!.Value);

        JsonObject archived = await CleanupStepAsync(state, "archive-project",
            setupApi, "updateProject",
            new GatewayInvocation(
                Path(("projectId", projectId)),
                Body: new JsonObject
                {
                    ["expectedRevision"] = GetLong(setup, "projectRevision"),
                    ["name"] = state.ProjectName,
                    ["state"] = "archived",
                }), ct).ConfigureAwait(false);
        RequireArchivedProject(archived, projectId, state.ProjectName, GetLong(setup, "projectRevision")!.Value);

        JsonObject revokeOwner = await CleanupStepAsync(state, "revoke-owner",
            setupApi, "revokeProjectRoleAssignment",
            new GatewayInvocation(
                Path(("projectId", projectId), ("assignmentId", ApiAcceptanceSupport.GetString(setup, "ownerAssignmentId")!)),
                Query: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["expectedRevision"] = GetLong(setup, "ownerAssignmentRevision")!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }), ct).ConfigureAwait(false);
        RequireRevokedAssignment(revokeOwner, projectId,
            ApiAcceptanceSupport.GetString(setup, "ownerAssignmentId")!,
            actorId, "owner", GetLong(setup, "ownerAssignmentRevision")!.Value);

        return state.Cleanup = new JsonObject
        {
            ["invocationId"] = state.InvocationId,
            ["state"] = "cleaned",
            ["tenantId"] = setup["tenantId"]?.DeepClone(),
            ["projectId"] = projectId,
            ["projectRevision"] = GetLong(archived, "revision"),
            ["projectState"] = "archived",
            ["projectConfigurationRevision"] = setup["projectConfigurationRevision"]?.DeepClone(),
            ["assignmentId"] = revokeOrchestrator["assignmentId"]?.DeepClone(),
            ["assignmentRevision"] = GetLong(revokeOrchestrator, "revision"),
            ["ownerAssignmentId"] = revokeOwner["assignmentId"]?.DeepClone(),
            ["ownerAssignmentRevision"] = GetLong(revokeOwner, "revision"),
        };
    }

    private static async Task<(string RequestId, JsonObject Receipt, long StateVersion)> CreateGateAsync(
        IGatewayClient api,
        AcceptanceManifest manifest,
        string projectId,
        string runId,
        string rootSessionId,
        long fence,
        long stateVersion,
        JsonObject environment,
        string invocationId,
        CancellationToken ct)
    {
        JsonObject current = await CallJsonAsync(api, "readDecisions",
            new GatewayInvocation(Path(("projectId", projectId), ("runId", runId), ("sessionId", rootSessionId))),
            [200], ct).ConfigureAwait(false);
        long version = GetLong(current, "stateVersion")
            ?? throw new InvalidOperationException("The owner did not return current decision state for gate setup.");
        if (GetLong(current, "executionFence") != fence)
        {
            throw new InvalidOperationException("The owner did not return current decision state for gate setup.");
        }

        JsonObject gatePlan = (JsonObject)manifest.FixturePlan["gate"]!;
        string requestId = ApiAcceptanceSupport.StableUuid(invocationId, "gate-request");
        string operation = manifest.Journey == "gate-answer" ? "askQuestion" : "requestApproval";
        string subjectId = ApiAcceptanceSupport.StableUuid(invocationId,
            manifest.Journey == "gate-answer" ? "gate-question" : "gate-subject");
        JsonObject request = new()
        {
            ["expectedStateVersion"] = version,
            ["idempotencyKey"] = ApiAcceptanceSupport.StableUuid(invocationId, "gate-idempotency"),
            ["requestId"] = requestId,
        };
        if (manifest.Journey == "gate-answer")
        {
            request["questionId"] = subjectId;
            request["prompt"] = ApiAcceptanceSupport.GetString(gatePlan, "prompt");
            request["allowedChoices"] = gatePlan["allowedChoices"]?.DeepClone();
            request["allowsFreeform"] = gatePlan["allowsFreeform"]?.DeepClone();
        }
        else
        {
            request["subjectId"] = subjectId;
            request["prompt"] = ApiAcceptanceSupport.GetString(gatePlan, "prompt");
        }

        JsonObject? currentGate = current["pendingGate"] as JsonObject;
        JsonObject gate;
        long expectedReadbackVersion;
        if (ApiAcceptanceSupport.GetString(currentGate ?? [], "requestId") == requestId)
        {
            gate = currentGate!.DeepClone() as JsonObject ?? [];
            if (!MatchesGate(gate, request, manifest.Journey, environment, fence))
            {
                throw new InvalidOperationException("The existing owner gate does not match the exact fixture actor, fence, kind, and planned payload.");
            }
            expectedReadbackVersion = version;
        }
        else if (currentGate is not null)
        {
            throw new InvalidOperationException("A different decision gate already exists for this fixture run.");
        }
        else
        {
            JsonObject receipt = await CallJsonAsync(api, operation,
                new GatewayInvocation(
                    Path(("projectId", projectId), ("runId", runId), ("sessionId", rootSessionId)),
                    Body: request), [200], ct).ConfigureAwait(false);
            gate = receipt["pendingGate"] as JsonObject
                ?? throw new InvalidOperationException("The owner did not accept the exact fixture-owned gate request.");
            expectedReadbackVersion = GetLong(receipt, "stateVersion")
                ?? throw new InvalidOperationException("The owner did not accept the exact fixture-owned gate request.");
            if (receipt["accepted"]?.GetValue<bool>() != true
                || GetLong(receipt, "executionFence") != fence
                || expectedReadbackVersion != version + 1
                || !MatchesGate(gate, request, manifest.Journey, environment, fence))
            {
                throw new InvalidOperationException("The owner did not accept the exact fixture-owned gate request.");
            }
        }

        JsonObject decisions = await CallJsonAsync(api, "readDecisions",
            new GatewayInvocation(Path(("projectId", projectId), ("runId", runId), ("sessionId", rootSessionId))),
            [200], ct).ConfigureAwait(false);
        JsonObject session = await CallJsonAsync(api, "readSessionStatus",
            new GatewayInvocation(Path(("projectId", projectId), ("runId", runId), ("sessionId", rootSessionId))),
            [200], ct).ConfigureAwait(false);
        JsonObject? blocker = (session["blockers"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(item => ApiAcceptanceSupport.GetString(item, "requestId") == requestId);
        string expectedBlocker = manifest.Journey == "gate-answer" ? "awaitingInput" : "awaitingApproval";
        JsonArray allowed = gate["allowedChoices"] as JsonArray ?? [];
        JsonArray blockerChoices = blocker?["choices"] as JsonArray ?? [];
        if (GetLong(decisions, "executionFence") != fence
            || GetLong(decisions, "stateVersion") != expectedReadbackVersion
            || !MatchesGate(decisions["pendingGate"] as JsonObject ?? [], request, manifest.Journey, environment, fence)
            || ApiAcceptanceSupport.GetString(blocker ?? [], "kind") != expectedBlocker
            || blockerChoices.Count != allowed.Count
            || blockerChoices.Where((choice, index) => choice?.GetValue<string>() != allowed[index]?.GetValue<string>()).Any()
            || blocker?["allowsFreeform"]?.GetValue<bool>() != (request["allowsFreeform"]?.GetValue<bool>() ?? false))
        {
            throw new InvalidOperationException("The owner gate readback does not match the created fixture request.");
        }

        return (requestId, new JsonObject
        {
            ["requestId"] = requestId,
            ["request"] = gate.DeepClone(),
            ["readback"] = new JsonObject
            {
                ["decisions"] = decisions,
                ["blocker"] = blocker?.DeepClone(),
            },
        }, expectedReadbackVersion);
    }

    private static bool MatchesGate(JsonObject gate, JsonObject request, string journey, JsonObject environment, long fence)
    {
        JsonArray expectedChoices = journey == "gate-answer"
            ? request["allowedChoices"] as JsonArray ?? []
            : new JsonArray("approve", "reject");
        JsonArray actualChoices = gate["allowedChoices"] as JsonArray ?? [];
        return ApiAcceptanceSupport.GetString(gate, "requestId") == ApiAcceptanceSupport.GetString(request, "requestId")
            && ApiAcceptanceSupport.GetString(gate, "kind") == (journey == "gate-answer" ? "question" : "approval")
            && ApiAcceptanceSupport.GetString(gate, "subjectId") == ApiAcceptanceSupport.GetString(request,
                journey == "gate-answer" ? "questionId" : "subjectId")
            && ApiAcceptanceSupport.GetString(gate, "authorizedActorId") == ApiAcceptanceSupport.GetString(environment, "actorId")
            && GetLong(gate, "fence") == fence
            && actualChoices.Count == expectedChoices.Count
            && !actualChoices.Where((choice, index) => choice?.GetValue<string>() != expectedChoices[index]?.GetValue<string>()).Any()
            && gate["allowsFreeform"]?.GetValue<bool>() == (request["allowsFreeform"]?.GetValue<bool>() ?? false)
            && ApiAcceptanceSupport.GetString(gate, "prompt") == ApiAcceptanceSupport.GetString(request, "prompt")
            && gate["resolvesOutcomeClarification"]?.GetValue<bool>() == false;
    }

    private static void EnsureSetupCapabilities(JsonObject openApi, string journey)
    {
        IReadOnlySet<string> operations = ApiAcceptanceJourney.GetOperationIds(openApi);
        List<string> required =
        [
            "getAuthorizationContext", "createProject", "assignProjectRole", "getProjectConfiguration",
            "acceptRunSelection", "getRunSelection", "acceptRunRoot", "readRunStatus", "readSessionStatus",
            "readSessionTree", "readDecisions", "revokeProjectRoleAssignment", "updateProject",
        ];
        if (journey == "journal") required.AddRange(["sendProjectRunMessage", "replayRunEvents"]);
        if (journey == "message") required.AddRange(["registerChild", "sendMessage"]);
        if (journey.StartsWith("gate-", StringComparison.Ordinal))
        {
            required.Add(journey == "gate-answer" ? "askQuestion" : "requestApproval");
            required.Add(journey == "gate-answer" ? "answerGate" : journey == "gate-approve" ? "approveGate" : "rejectGate");
        }
        string[] missing = required.Distinct(StringComparer.Ordinal).Where(name => !operations.Contains(name)).ToArray();
        if (missing.Length > 0)
        {
            throw new AcceptanceBlockedException($"Required P1 API operations are missing: {string.Join(", ", missing)}.");
        }
        if (journey == "journal")
        {
            ValidateJournalMessageOperation(openApi);
        }
    }

    internal static void ValidateJournalMessageOperation(JsonObject openApi)
    {
        JsonObject paths = openApi["paths"] as JsonObject ?? [];
        JsonObject? send = paths.SelectMany(path => (path.Value as JsonObject ?? []).Select(operation => (Path: path.Key, Method: operation.Key, Value: operation.Value as JsonObject)))
            .Where(item => ApiAcceptanceSupport.GetString(item.Value ?? [], "operationId") == "sendProjectRunMessage")
            .Select(item => new JsonObject
            {
                ["path"] = item.Path,
                ["method"] = item.Method,
                ["operation"] = item.Value?.DeepClone(),
            }).FirstOrDefault();
        JsonObject? operationObject = send?["operation"] as JsonObject;
        JsonArray pathParameters = (paths[ApiAcceptanceSupport.GetString(send ?? [], "path")!] as JsonObject)?["parameters"] as JsonArray ?? [];
        JsonArray operationParameters = operationObject?["parameters"] as JsonArray ?? [];
        JsonArray parametersFound = new(pathParameters.Select(item => item?.DeepClone())
            .Concat(operationParameters.Select(item => item?.DeepClone())).ToArray());
        bool idempotencyRequired = parametersFound.OfType<JsonObject>().Any(parameter =>
            ApiAcceptanceSupport.GetString(parameter, "in") == "header"
            && ApiAcceptanceSupport.GetString(parameter, "name")?.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase) == true
            && parameter["required"]?.GetValue<bool>() == true);
        if (send is null || ApiAcceptanceSupport.GetString(send, "path") != "/api/v1/projects/{projectId}/runs/{runId}/messages"
            || ApiAcceptanceSupport.GetString(send, "method") != "post"
            || ApiAcceptanceSupport.GetString(operationObject ?? [], "x-agentweaver-owner") != "Orchestrator"
            || operationObject?["requestBody"]?["required"]?.GetValue<bool>() != true
            || !idempotencyRequired)
        {
            throw new AcceptanceBlockedException("The discovered Gateway sendProjectRunMessage operation does not expose its required owner contract.");
        }
    }

    private static string RequireProject(JsonObject project, AcceptanceFixtureState state, string expectedState)
    {
        string? projectId = ApiAcceptanceSupport.GetString(project, "projectId");
        if (string.IsNullOrWhiteSpace(projectId)
            || ApiAcceptanceSupport.GetString(project, "name") != state.ProjectName
            || ApiAcceptanceSupport.GetString(project, "state") != expectedState
            || GetLong(project, "revision") is not > 0
            || GetLong(project, "configurationRevision") is not > 0)
        {
            throw new InvalidOperationException($"Projects Config did not return the exact {expectedState} fixture project.");
        }
        return projectId;
    }

    private static void RequireAssignment(JsonObject assignment, string projectId, JsonObject environment, string role, string state, long revision)
    {
        if (!ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(assignment, "assignmentId"))
            || ApiAcceptanceSupport.GetString(assignment, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(assignment, "principalId") != ApiAcceptanceSupport.GetString(environment, "actorId")
            || ApiAcceptanceSupport.GetString(assignment, "role") != role
            || ApiAcceptanceSupport.GetString(assignment, "state") != state
            || GetLong(assignment, "revision") != revision
            || ApiAcceptanceSupport.GetString(assignment, "grantedBy") != ApiAcceptanceSupport.GetString(environment, "actorId")
            || state == "revoked" && ApiAcceptanceSupport.GetString(assignment, "revokedBy") != ApiAcceptanceSupport.GetString(environment, "actorId"))
        {
            throw new InvalidOperationException($"Projects Config did not return the exact {state} {role} assignment receipt.");
        }
    }

    private static void RequireRevokedAssignment(JsonObject value, string projectId, string assignmentId, string actorId, string role, long expectedRevision)
    {
        if (!ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(value, "assignmentId"))
            || !string.Equals(ApiAcceptanceSupport.GetString(value, "assignmentId"), assignmentId, StringComparison.OrdinalIgnoreCase)
            || ApiAcceptanceSupport.GetString(value, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(value, "principalId") != actorId
            || ApiAcceptanceSupport.GetString(value, "role") != role
            || ApiAcceptanceSupport.GetString(value, "state") != "revoked"
            || GetLong(value, "revision") != expectedRevision + 1
            || ApiAcceptanceSupport.GetString(value, "grantedBy") != actorId
            || ApiAcceptanceSupport.GetString(value, "revokedBy") != actorId)
        {
            throw new InvalidOperationException($"Projects Config did not return the exact revoked {role} assignment receipt.");
        }
    }

    private static void RequireArchivedProject(JsonObject value, string projectId, string projectName, long expectedRevision)
    {
        if (ApiAcceptanceSupport.GetString(value, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(value, "name") != projectName
            || ApiAcceptanceSupport.GetString(value, "state") != "archived"
            || GetLong(value, "revision") != expectedRevision + 1
            || GetLong(value, "configurationRevision") is not > 0)
        {
            throw new InvalidOperationException("Projects Config did not return the exact archived project receipt.");
        }
    }

    private static void RequireSelection(JsonObject selection, string projectId, string runId, long projectRevision, long platformRevision, string contextRevision)
    {
        if (ApiAcceptanceSupport.GetString(selection, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(selection, "runId") != runId
            || GetLong(selection, "projectConfigurationRevision") != projectRevision
            || GetLong(selection, "platformRuntimeRevision") != platformRevision
            || ApiAcceptanceSupport.GetString(selection, "contextRevision") != contextRevision)
        {
            throw new InvalidOperationException("The owner selection readback does not match the exact fixture project, run, and revisions.");
        }
    }

    private static void RequireRoot(JsonObject root, string projectId, string runId, string rootSessionId)
    {
        if (ApiAcceptanceSupport.GetString(root, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(root, "runId") != runId
            || ApiAcceptanceSupport.GetString(root, "rootSessionId") != rootSessionId
            || GetLong(root, "executionFence") is not > 0
            || GetLong(root, "stateVersion") is null)
        {
            throw new InvalidOperationException("The owner did not return the exact accepted root and execution fence.");
        }
    }

    private static void RequireRootReadback(JsonObject run, JsonObject session, string projectId, string runId, string rootSessionId, long executionFence)
    {
        JsonObject identity = session["identity"] as JsonObject ?? [];
        if (ApiAcceptanceSupport.GetString(run, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(run, "runId") != runId
            || ApiAcceptanceSupport.GetString(run, "rootSessionId") != rootSessionId
            || GetLong(run, "executionFence") != executionFence
            || ApiAcceptanceSupport.GetString(identity, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(identity, "runId") != runId
            || ApiAcceptanceSupport.GetString(identity, "sessionId") != rootSessionId
            || ApiAcceptanceSupport.GetString(session, "rootSessionId") != rootSessionId
            || GetLong(session, "executionFence") != executionFence)
        {
            throw new InvalidOperationException("The owner root readback does not match the exact newly accepted session.");
        }
    }

    private static async Task ReadTreeAsync(IGatewayClient api, string projectId, string runId, string rootSessionId, IReadOnlyCollection<string> required, CancellationToken ct)
    {
        JsonObject tree = await CallJsonAsync(api, "readSessionTree",
            new GatewayInvocation(Path(("projectId", projectId), ("runId", runId), ("sessionId", rootSessionId))),
            [200], ct).ConfigureAwait(false);
        JsonArray nodes = tree["nodes"] as JsonArray ?? [];
        if (ApiAcceptanceSupport.GetString(tree, "rootSessionId") != rootSessionId
            || nodes.OfType<JsonObject>().Any(node =>
                ApiAcceptanceSupport.GetString(node["identity"] as JsonObject ?? [], "projectId") != projectId
                || ApiAcceptanceSupport.GetString(node["identity"] as JsonObject ?? [], "runId") != runId)
            || required.Any(sessionId => nodes.OfType<JsonObject>().Count(node =>
                ApiAcceptanceSupport.GetString(node["identity"] as JsonObject ?? [], "sessionId") == sessionId
                && ApiAcceptanceSupport.GetString(node, "lifecycle") == "active") != 1))
        {
            throw new InvalidOperationException("The live owner session tree does not contain the exact active fixture sessions.");
        }
    }

    private static void RequireJournalMessageReceipt(JsonObject receipt, string projectId, string runId, string rootSessionId, JsonObject environment)
    {
        string[] expected = ["messageId", "ownerMessageId", "projectId", "recipientSessionId", "rootSessionId", "runId", "sender", "status", "threadId", "threadSequence"];
        if (receipt.Count != expected.Length || receipt.Any(pair => !expected.Contains(pair.Key, StringComparer.Ordinal))
            || expected.Any(name => !receipt.ContainsKey(name))
            || !ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(receipt, "ownerMessageId"))
            || !ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(receipt, "messageId"))
            || !ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(receipt, "threadId"))
            || GetLong(receipt, "threadSequence") is not > 0
            || ApiAcceptanceSupport.GetString(receipt, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(receipt, "runId") != runId
            || ApiAcceptanceSupport.GetString(receipt, "rootSessionId") != rootSessionId
            || ApiAcceptanceSupport.GetString(receipt, "recipientSessionId") != rootSessionId
            || ApiAcceptanceSupport.GetString(receipt, "status") != "accepted")
        {
            throw new InvalidOperationException("The owner did not return the exact accepted journal message receipt.");
        }
        JsonObject sender = receipt["sender"] as JsonObject ?? [];
        if (sender.Count != 2 || ApiAcceptanceSupport.GetString(sender, "issuer") != ApiAcceptanceSupport.GetString(environment, "issuer")
            || ApiAcceptanceSupport.GetString(sender, "subject") != ApiAcceptanceSupport.GetString(environment, "actorId"))
        {
            throw new InvalidOperationException("The owner did not return the exact accepted journal message receipt.");
        }
    }

    private static async Task<JsonObject> CallJsonAsync(
        IGatewayClient api,
        string operation,
        GatewayInvocation invocation,
        IReadOnlyCollection<int> expected,
        CancellationToken ct)
    {
        GatewayResponse response = await api.InvokeAsync(operation, invocation, ct).ConfigureAwait(false);
        return ApiAcceptanceJourney.RequireStatusBody(response, expected, operation);
    }

    private static long? GetLong(JsonObject value, string name) =>
        ApiAcceptanceSupport.TryLong(value[name], out long result) ? result : null;

    private static IReadOnlyDictionary<string, string> Path(params (string Name, string Value)[] values) =>
        ApiAcceptanceJourney.Path(values);

    private static async Task<JsonObject> CallJsonAsync(
        IGatewayClient api,
        string operation,
        GatewayInvocation invocation,
        int[] expected,
        CancellationToken ct) =>
        await CallJsonAsync(api, operation, invocation, (IReadOnlyCollection<int>)expected, ct).ConfigureAwait(false);

    private static AcceptanceHttpException HttpFailure(string step, string operation, GatewayResponse response)
    {
        string? code = ApiAcceptanceSupport.GetString(response.Body as JsonObject ?? [], "code");
        return new AcceptanceHttpException(response.Status,
            ProblemCode.IsMatch(code ?? "") ? code : null,
            $"The P1 owner {operation} call returned an unexpected HTTP result during {step}.");
    }

    private static async Task<JsonObject> MutatingSetupStepAsync(
        AcceptanceFixtureState state,
        string step,
        IGatewayClient api,
        string operation,
        GatewayInvocation invocation,
        int[] expected,
        CancellationToken ct)
    {
        state.SetupAttempted = true;
        GatewayResponse? response = null;
        try
        {
            response = await api.InvokeAsync(operation, invocation, ct).ConfigureAwait(false);
            if (!expected.Contains(response.Status))
            {
                throw HttpFailure(step, operation, response);
            }
            return response.Body as JsonObject
                ?? throw new InvalidOperationException($"The live {operation} operation returned no JSON object.");
        }
        catch (Exception error)
        {
            state.SetupIndeterminate = true;
            if (error is AcceptanceHttpException http)
            {
                state.RequestFailures.Add(new JsonObject
                {
                    ["step"] = step,
                    ["operationId"] = operation,
                    ["status"] = http.Status,
                    ["problemCode"] = http.ProblemCode,
                });
            }
            else
            {
                state.RequestFailures.Add(new JsonObject
                {
                    ["step"] = step,
                    ["operationId"] = operation,
                    ["status"] = response?.Status,
                    ["problemCode"] = null,
                });
            }
            throw;
        }
    }

    private static async Task<JsonObject> CleanupStepAsync(
        AcceptanceFixtureState state,
        string step,
        IGatewayClient api,
        string operation,
        GatewayInvocation invocation,
        CancellationToken ct)
    {
        GatewayResponse? response = null;
        try
        {
            response = await api.InvokeAsync(operation, invocation, ct).ConfigureAwait(false);
            if (response.Status != 200)
            {
                throw HttpFailure(step, operation, response);
            }
            return response.Body as JsonObject
                ?? throw new InvalidOperationException($"The live {operation} operation returned no JSON object.");
        }
        catch (Exception error)
        {
            if (error is AcceptanceHttpException http)
            {
                state.RequestFailures.Add(new JsonObject
                {
                    ["step"] = step,
                    ["operationId"] = operation,
                    ["status"] = http.Status,
                    ["problemCode"] = http.ProblemCode,
                });
            }
            else
            {
                state.RequestFailures.Add(new JsonObject
                {
                    ["step"] = step,
                    ["operationId"] = operation,
                    ["status"] = response?.Status,
                    ["problemCode"] = null,
                });
            }
            throw;
        }
    }

    private sealed class AcceptanceHttpException(int status, string? problemCode, string message) : Exception(message)
    {
        public int Status { get; } = status;
        public string? ProblemCode { get; } = problemCode;
    }
}
