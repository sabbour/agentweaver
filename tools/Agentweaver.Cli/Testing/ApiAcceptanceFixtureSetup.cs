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
    internal static readonly TimeSpan DefaultRuntimeCleanupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RuntimeCleanupPollInterval = TimeSpan.FromSeconds(1);

    public static async Task<(AcceptanceFixture Fixture, IGatewayClient RunApi, IGatewayClient CancellationApi, IGatewayClient? DeniedApi, AcceptanceFixtureState State)> CreateAsync(
        AcceptanceManifest manifest,
        string invocationId,
        IGatewayClient setupApi,
        IGatewayClientFactory clients,
        ICliAuthentication authentication,
        InteractionPolicy interaction,
        CancellationToken ct)
    {
        AcceptanceFixtureState state = new(invocationId);
        state.Journey = manifest.Journey;
        state.VerifyCleanupReadback = true;
        JsonObject environment = manifest.Environment;
        try
        {
            JsonObject openApi = await setupApi.DiscoverAsync(ct).ConfigureAwait(false);
            EnsureSetupCapabilities(openApi, manifest);
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

            JsonObject assignments = await MutatingSetupStepAsync(state, "read-owner-assignment",
                setupApi, "listProjectRoleAssignments",
                new GatewayInvocation(Path(("projectId", projectId))), [200], ct).ConfigureAwait(false);
            JsonObject owner = RequireOwnerAssignment(assignments, projectId, environment);
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

            long projectRevision = GetLong(created, "revision")
                ?? throw new InvalidOperationException("The current project revision is invalid.");
            bool serverOwnedSelection = RequiresServerOwnedSelection(manifest);
            long expectedPlatformRevision = GetLong(manifest.FixturePlan, "expectedPlatformRuntimeRevision")
                ?? throw new InvalidDataException("A positive expected platform runtime revision is required in the runner fixture plan.");
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
            string runId;
            string rootSessionId;
            JsonObject observedSelection;
            if (serverOwnedSelection)
            {
                JsonObject createdRun = await MutatingSetupStepAsync(state, "create-interactive-run", selectionApi,
                    "createProjectRun",
                    new GatewayInvocation(
                        Path(("projectId", projectId)),
                        Body: new JsonObject
                        {
                            ["firstMessage"] = "Acceptance fixture request; no work is dispatched.",
                            ["expectedProjectConfigRevision"] = configurationRevision,
                        },
                        Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Idempotency-Key"] = ApiAcceptanceSupport.StableUuid(invocationId,
                                manifest.Journey == "session-history-metric" ? "metric-create-run" : "journal-create-run"),
                        }), [201], ct).ConfigureAwait(false);
                try
                {
                    (runId, rootSessionId) = RequireInteractiveRun(createdRun, projectId);
                }
                catch
                {
                    state.SetupIndeterminate = true;
                    throw;
                }
                state.RunId = runId;
                state.RootSessionId = rootSessionId;
                state.SelectionKnown = true;
                state.Setup["runId"] = runId;
                state.Setup["rootSessionId"] = rootSessionId;
                try
                {
                    observedSelection = await CallJsonAsync(selectionApi, "getRunSelection",
                        new GatewayInvocation(Path(("projectId", projectId), ("runId", runId))), [200], ct)
                        .ConfigureAwait(false);
                    RequireSelection(observedSelection, projectId, runId, configurationRevision,
                        expectedPlatformRevision, "interactive-v1", projectRevision);
                }
                catch
                {
                    state.SetupIndeterminate = true;
                    throw;
                }
            }
            else
            {
                runId = ApiAcceptanceSupport.StableUuid(invocationId, "run");
                rootSessionId = ApiAcceptanceSupport.StableUuid(invocationId, "root-session");
                state.RunId = runId;
                state.RootSessionId = rootSessionId;
                state.Setup["runId"] = runId;
                state.Setup["rootSessionId"] = rootSessionId;
                JsonObject selectionContext = (JsonObject)manifest.FixturePlan["selectionContext"]!;
                JsonObject selectionRequest = new()
                {
                    ["expectedProjectConfigRevision"] = configurationRevision,
                    ["expectedPlatformRuntimeRevision"] = expectedPlatformRevision,
                    ["context"] = selectionContext.DeepClone(),
                };
                JsonObject acceptedSelection = await MutatingSetupStepAsync(state, "accept-run-selection", selectionApi,
                    "acceptRunSelection",
                    new GatewayInvocation(
                        Path(("projectId", projectId), ("runId", runId)),
                        Body: selectionRequest), [200], ct).ConfigureAwait(false);
                state.SelectionKnown = true;
                try
                {
                        observedSelection = await CallJsonAsync(selectionApi, "getRunSelection",
                            new GatewayInvocation(Path(("projectId", projectId), ("runId", runId))), [200], ct)
                            .ConfigureAwait(false);
                    RequireSelection(acceptedSelection, projectId, runId, configurationRevision,
                        expectedPlatformRevision, ApiAcceptanceSupport.GetString(selectionContext, "revision")!,
                        projectRevision);
                    RequireSelection(observedSelection, projectId, runId, configurationRevision,
                        expectedPlatformRevision, ApiAcceptanceSupport.GetString(selectionContext, "revision")!,
                        projectRevision);
                }
                catch
                {
                    state.SetupIndeterminate = true;
                    throw;
                }
            }
            state.Selection = observedSelection.DeepClone() as JsonObject;

            string? childSessionId = manifest.Journey == "message"
                ? ApiAcceptanceSupport.StableUuid(invocationId, "child-session")
                : null;

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

            string rootId = rootSessionId;
            state.RootAcceptanceAttempted = true;
            JsonObject rootReceipt = await MutatingSetupStepAsync(state, "accept-run-root", runApi, "acceptRunRoot",
                new GatewayInvocation(
                    Path(("projectId", projectId), ("runId", runId)),
                    Body: new JsonObject { ["sessionId"] = rootId }), [201], ct).ConfigureAwait(false);
            state.RootExecutionFence = GetLong(rootReceipt, "executionFence");
            state.RootStateVersion = GetLong(rootReceipt, "stateVersion");
            state.Setup["rootExecutionFence"] = state.RootExecutionFence;
            state.Setup["rootStateVersion"] = state.RootStateVersion;
            try
            {
                RequireRoot(rootReceipt, projectId, runId, rootId);
            }
            catch
            {
                state.SetupIndeterminate = true;
                throw;
            }
            state.RootAccepted = true;
            long fence = GetLong(rootReceipt, "executionFence")!.Value;
            long stateVersion = GetLong(rootReceipt, "stateVersion")!.Value;
            try
            {
                JsonObject rootStatus = await CallJsonAsync(runApi, "readRunStatus",
                    new GatewayInvocation(Path(("projectId", projectId), ("runId", runId))), [200], ct).ConfigureAwait(false);
                JsonObject sessionStatus = await CallJsonAsync(runApi, "readSessionStatus",
                    new GatewayInvocation(Path(("projectId", projectId), ("runId", runId), ("sessionId", rootId))), [200], ct).ConfigureAwait(false);
                RequireRootReadback(rootStatus, sessionStatus, projectId, runId, rootId, fence);
            }
            catch
            {
                if (state.VerifyCleanupReadback)
                    state.SetupIndeterminate = true;
                throw;
            }

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
                try
                {
                    RequireJournalMessageReceipt(seedReceipt, projectId, runId, rootId, environment);
                }
                catch
                {
                    state.SetupIndeterminate = true;
                    throw;
                }
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
                observedSelection, gateReceipt);
            return (fixture, runApi, selectionApi, await CreateDeniedClientAsync(
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
        IGatewayClient? cancellationApi,
        IGatewayClient? runApi,
        AcceptanceFixtureState state,
        string actorId,
        CancellationToken ct,
        TimeSpan? runtimeCleanupTimeout = null)
    {
        if (!state.SetupAttempted)
        {
            return state.Cleanup = new JsonObject
            {
                ["invocationId"] = state.InvocationId,
                ["state"] = "not-created",
            };
        }
        if (!state.SetupComplete || state.Setup is null)
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
        if (state.VerifyCleanupReadback)
            state.Cleanup["steps"] = new JsonArray();

        JsonObject setup = state.Setup;
        string projectId = ApiAcceptanceSupport.GetString(setup, "projectId")!;
        if (state.VerifyCleanupReadback)
        {
            if (!state.SelectionKnown || state.RunId is null || state.RootSessionId is null)
            {
                state.Cleanup["reason"] = "The fixture run selection or server identity is not acknowledged; reconcile before removing project authority.";
                return state.Cleanup;
            }
            if (cancellationApi is null)
            {
                state.Cleanup["reason"] = "The fixture lacks its unbound cancellation client.";
                return state.Cleanup;
            }
            // Cancellation can precede runtime teardown; verify cleanup before revoking roles or archiving the project.
            await VerifyAcceptedRunCleanupAsync(
                cancellationApi, runApi, state, projectId, ct, runtimeCleanupTimeout).ConfigureAwait(false);
            if (state.SetupIndeterminate)
            {
                state.Cleanup["reason"] =
                    "The accepted run and runtime cleanup were verified, but setup remains uncertain; retain project authority for reconciliation.";
                return state.Cleanup;
            }
        }

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

        JsonObject cleanup = state.Cleanup;
        cleanup["state"] = state.VerifyCleanupReadback || state.SetupIndeterminate ? "indeterminate" : "cleaned";
        cleanup["tenantId"] = setup["tenantId"]?.DeepClone();
        cleanup["projectId"] = projectId;
        cleanup["projectRevision"] = GetLong(archived, "revision");
        cleanup["projectState"] = "archived";
        cleanup["projectConfigurationRevision"] = setup["projectConfigurationRevision"]?.DeepClone();
        cleanup["assignmentId"] = revokeOrchestrator["assignmentId"]?.DeepClone();
        cleanup["assignmentRevision"] = GetLong(revokeOrchestrator, "revision");
        cleanup["ownerAssignmentId"] = revokeOwner["assignmentId"]?.DeepClone();
        cleanup["ownerAssignmentRevision"] = GetLong(revokeOwner, "revision");
        if (state.SetupIndeterminate)
            cleanup["reason"] = "Acknowledged project state was contained, but an earlier setup write remains ambiguous.";
        if (state.VerifyCleanupReadback)
        {
            JsonObject project = await CleanupStepAsync(state, "read-archived-project",
                setupApi, "getProject",
                new GatewayInvocation(Path(("projectId", projectId))), ct).ConfigureAwait(false);
            if (ApiAcceptanceSupport.GetString(project, "projectId") != projectId
                || ApiAcceptanceSupport.GetString(project, "name") != state.ProjectName
                || ApiAcceptanceSupport.GetString(project, "state") != "archived"
                || GetLong(project, "revision") != GetLong(archived, "revision")
                || GetLong(project, "configurationRevision") != GetLong(archived, "configurationRevision"))
            {
                throw new InvalidOperationException("The independent Projects readback did not confirm the archived P1 fixture.");
            }

            JsonObject assignments = await CleanupStepAsync(state, "read-project-assignments",
                setupApi, "listProjectRoleAssignments",
                new GatewayInvocation(Path(("projectId", projectId))), ct).ConfigureAwait(false);
            if (ApiAcceptanceSupport.GetString(assignments, "projectId") != projectId
                || assignments["assignments"] is not JsonArray activeAssignments
                || activeAssignments.Count != 0)
            {
                throw new InvalidOperationException("The independent Projects readback did not confirm that all P1 fixture assignments were revoked.");
            }
            cleanup["projectReadback"] = "archived";
            cleanup["assignmentsReadback"] = "empty";
            cleanup["state"] = state.SetupIndeterminate ? "indeterminate" : "cleaned";
        }

        return state.Cleanup = cleanup;
    }

    public static async Task<(IGatewayClient? CancellationApi, IGatewayClient? RunApi)> CreateCleanupClientsAsync(
        AcceptanceManifest manifest,
        AcceptanceFixtureState state,
        IGatewayClientFactory clients,
        ICliAuthentication authentication,
        InteractionPolicy interaction,
        CancellationToken ct)
    {
        if (!state.VerifyCleanupReadback || !state.SelectionKnown || state.RunId is null || state.Setup is null)
            return (null, null);

        CliTarget target = new(
            new Uri(ApiAcceptanceSupport.GetString(manifest.Environment, "baseUrl")!, UriKind.Absolute),
            new Uri(ApiAcceptanceSupport.GetString(manifest.Environment, "issuer")!, UriKind.Absolute),
            "agentweaver-cli",
            ApiAcceptanceSupport.GetString(manifest.Environment, "audience")!,
            ApiAcceptanceSupport.GetString(manifest.Environment, "tenantId")!);
        CliIdentity cancellationIdentity = new(target,
            ["openid", "api.read", "offline_access", "projects.orchestrator"]);
        CliIdentity runIdentity = new(target,
            ["openid", "api.read", "offline_access", "projects.orchestrator"],
            ApiAcceptanceSupport.GetString(state.Setup, "projectId"),
            state.RunId);
        _ = await authentication.GetBearerAsync(cancellationIdentity, interaction, ct).ConfigureAwait(false);
        IGatewayClient cancellationApi = clients.Create(cancellationIdentity);
        JsonObject cancellationContext = await ApiAcceptanceJourney.ValidateAuthContextAsync(cancellationApi, ct)
            .ConfigureAwait(false);
        ApiAcceptanceJourney.ValidateAuthContext(cancellationContext, manifest);
        try
        {
            _ = await authentication.GetBearerAsync(runIdentity, interaction, ct).ConfigureAwait(false);
            IGatewayClient runApi = clients.Create(runIdentity);
            JsonObject runContext = await ApiAcceptanceJourney.ValidateAuthContextAsync(runApi, ct).ConfigureAwait(false);
            ApiAcceptanceJourney.ValidateAuthContext(runContext, manifest,
                ApiAcceptanceSupport.GetString(state.Setup, "projectId"), state.RunId);
            return (cancellationApi, runApi);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (cancellationApi, null);
        }
    }

    private static async Task VerifyAcceptedRunCleanupAsync(
        IGatewayClient cancellationApi,
        IGatewayClient? runApi,
        AcceptanceFixtureState state,
        string projectId,
        CancellationToken ct,
        TimeSpan? runtimeCleanupTimeout)
    {
        string runId = state.RunId!;
        string rootSessionId = state.RootSessionId!;
        string cancellationKey = ApiAcceptanceSupport.StableUuid(state.InvocationId, state.Journey switch
        {
            "journal" => "journal-cancel-run",
            "session-history-metric" => "metric-cancel-run",
            _ => "fixture-cancel-run",
        });
        JsonObject cancellation = await CleanupStepAsync(state, "cancel-accepted-run", cancellationApi,
            "cancelProjectRun",
            new GatewayInvocation(
                Path(("projectId", projectId), ("runId", runId)),
                Body: new JsonObject { ["reason"] = "Acceptance fixture cleanup." },
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Idempotency-Key"] = cancellationKey,
                }), ct).ConfigureAwait(false);
        if (ApiAcceptanceSupport.GetString(cancellation, "runId") != runId
            || ApiAcceptanceSupport.GetString(cancellation, "state") != "cancelled"
            || cancellation["alreadyTerminal"]?.GetValue<bool>() != false
            || !DateTimeOffset.TryParse(ApiAcceptanceSupport.GetString(cancellation, "cancelledAt"),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTimeOffset cancelledAt)
            || cancelledAt == default)
        {
            throw new InvalidOperationException("The Orchestrator did not return the exact non-terminal fixture run cancellation receipt.");
        }

        if (!state.RootAccepted)
        {
            state.Cleanup!["reason"] = state.RootAcceptanceAttempted
                ? "The known run was cancelled, but root acceptance is uncertain and cannot be verified safely."
                : "The known pending run was cancelled without an accepted root; no root readback was inferred.";
            throw new InvalidOperationException("The fixture run has no acknowledged accepted root for terminal readback.");
        }

        if (runApi is null)
            throw new InvalidOperationException("The fixture run was cancelled, but run-bound authorization is unavailable for independent terminal readback.");

        long expectedFence = (state.RootExecutionFence
            ?? throw new InvalidOperationException("The fixture root admission did not acknowledge its execution fence.")) + 1;
        long expectedStateVersion = (state.RootStateVersion
            ?? throw new InvalidOperationException("The fixture root admission did not acknowledge its state version.")) + 1;
        JsonObject runStatus = await CleanupStepAsync(state, "read-cancelled-run",
            runApi, "readRunStatus",
            new GatewayInvocation(Path(("projectId", projectId), ("runId", runId))), ct).ConfigureAwait(false);
        if (ApiAcceptanceSupport.GetString(runStatus, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(runStatus, "runId") != runId
            || ApiAcceptanceSupport.GetString(runStatus, "rootSessionId") != rootSessionId
            || ApiAcceptanceSupport.GetString(runStatus, "executionState") != "cancelled"
            || GetLong(runStatus, "executionFence") != expectedFence
            || GetLong(runStatus, "stateVersion") != expectedStateVersion)
        {
            throw new InvalidOperationException("The independent accepted-run readback did not confirm cancellation at the advanced execution fence and state version.");
        }

        JsonObject rootStatus = await CleanupStepAsync(state, "read-cancelled-root-session",
            runApi, "readSessionStatus",
            new GatewayInvocation(Path(("projectId", projectId), ("runId", runId), ("sessionId", rootSessionId))),
            ct).ConfigureAwait(false);
        JsonObject rootIdentity = rootStatus["identity"] as JsonObject ?? [];
        if (rootIdentity.Count != 3
            || ApiAcceptanceSupport.GetString(rootIdentity, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(rootIdentity, "runId") != runId
            || ApiAcceptanceSupport.GetString(rootIdentity, "sessionId") != rootSessionId
            || ApiAcceptanceSupport.GetString(rootStatus, "rootSessionId") != rootSessionId
            || rootStatus["detached"]?.GetValue<bool>() != false
            || ApiAcceptanceSupport.GetString(rootStatus, "lifecycle") != "cancelled"
            || GetLong(rootStatus, "executionFence") != expectedFence
            || GetLong(rootStatus, "stateVersion") != expectedStateVersion)
        {
            throw new InvalidOperationException("The independent root-session readback did not confirm cancellation at the advanced execution fence and state version.");
        }

        JsonObject tree = await CleanupStepAsync(state, "read-cancelled-session-tree",
            runApi, "readSessionTree",
            new GatewayInvocation(Path(("projectId", projectId), ("runId", runId), ("sessionId", rootSessionId))),
            ct).ConfigureAwait(false);
        JsonArray nodes = tree["nodes"] as JsonArray ?? [];
        JsonObject[] nodeObjects = nodes.OfType<JsonObject>().ToArray();
        JsonObject[] rootNodes = nodes.OfType<JsonObject>().Where(node =>
            ApiAcceptanceSupport.GetString(node["identity"] as JsonObject ?? [], "sessionId") == rootSessionId).ToArray();
        if (ApiAcceptanceSupport.GetString(tree, "rootSessionId") != rootSessionId
            || nodes.Count != nodeObjects.Length
            || rootNodes.Length != 1
            || (state.Journey == "journal" && nodeObjects.Length != 1)
            || nodeObjects.Any(node =>
                (node["identity"] as JsonObject ?? []).Count != 3
                || ApiAcceptanceSupport.GetString(node["identity"] as JsonObject ?? [], "projectId") != projectId
                || ApiAcceptanceSupport.GetString(node["identity"] as JsonObject ?? [], "runId") != runId
                || ApiAcceptanceSupport.GetString(node, "rootSessionId") != rootSessionId
                || node["detached"]?.GetValue<bool>() != false
                || GetLong(node, "executionFence") != expectedFence)
            || ApiAcceptanceSupport.GetString(rootNodes[0], "lifecycle") != "cancelled")
        {
            throw new InvalidOperationException("The retained session tree did not confirm the exact cancelled root and advanced run fence.");
        }

        JsonObject runtimeCleanup = await WaitForRuntimeCleanupCompleteAsync(
            runApi, state, projectId, runId, rootSessionId, ct, runtimeCleanupTimeout).ConfigureAwait(false);
        JsonObject runTerminalization = new()
        {
            ["runId"] = runId,
            ["state"] = "cancelled",
            ["cancelledAt"] = cancelledAt,
            ["alreadyTerminal"] = false,
            ["executionFence"] = expectedFence,
            ["stateVersion"] = expectedStateVersion,
            ["acceptedRunReadback"] = "verified",
            ["rootSessionReadback"] = "verified",
            ["attachedHistoryReadback"] = "verified",
            ["runtimeCleanup"] = runtimeCleanup.DeepClone(),
        };
        if (state.Journey == "session-history-metric")
        {
            JsonObject metricReadback = await VerifyMetricRunHistoryCleanupAsync(
                runApi, state, projectId, runId, rootSessionId, tree, expectedFence, ct).ConfigureAwait(false);
            foreach ((string name, JsonNode? value) in metricReadback)
                runTerminalization[name] = value?.DeepClone();
        }

        state.Cleanup!["runTerminalization"] = runTerminalization;
    }

    private static async Task<JsonObject> WaitForRuntimeCleanupCompleteAsync(
        IGatewayClient runApi,
        AcceptanceFixtureState state,
        string projectId,
        string runId,
        string rootSessionId,
        CancellationToken ct,
        TimeSpan? timeout)
    {
        TimeSpan waitTimeout = timeout ?? DefaultRuntimeCleanupTimeout;
        if (waitTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using CancellationTokenSource deadline = new(waitTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        string? lastState = null;
        string? lastStep = null;
        string? lastFailureCode = null;
        int pollCount = 0;
        long expectedFence = (state.RootExecutionFence
            ?? throw new InvalidOperationException("The fixture root admission did not acknowledge its execution fence.")) + 1;

        void RecordTimeout(Exception? error = null)
        {
            string reason = $"Runtime cleanup did not report cleanup_complete before the {waitTimeout.TotalSeconds:0}-second deadline.";
            state.Cleanup!["runtimeCleanup"] = new JsonObject
            {
                ["state"] = "timed-out",
                ["pollCount"] = pollCount,
                ["lastState"] = lastState,
                ["lastStep"] = lastStep,
                ["failureCode"] = lastFailureCode,
                ["reason"] = error is null ? reason : $"{reason} {ApiAcceptanceSupport.RedactText(error.Message)}",
            };
            state.Cleanup["reason"] = reason;
        }

        while (true)
        {
            JsonObject status;
            try
            {
                status = await CleanupStepAsync(state, "wait-runtime-cleanup", runApi, "readSessionStatus",
                    new GatewayInvocation(Path(("projectId", projectId), ("runId", runId), ("sessionId", rootSessionId))),
                    linked.Token).ConfigureAwait(false);
                JsonObject identity = status["identity"] as JsonObject ?? [];
                // Reject stale or cross-run status; only the cancelled root at the advanced fence can prove this fixture is safe to tear down.
                if (identity.Count != 3
                    || ApiAcceptanceSupport.GetString(identity, "projectId") != projectId
                    || ApiAcceptanceSupport.GetString(identity, "runId") != runId
                    || ApiAcceptanceSupport.GetString(identity, "sessionId") != rootSessionId
                    || ApiAcceptanceSupport.GetString(status, "rootSessionId") != rootSessionId
                    || status["detached"]?.GetValue<bool>() != false
                    || ApiAcceptanceSupport.GetString(status, "lifecycle") != "cancelled"
                    || GetLong(status, "executionFence") != expectedFence)
                {
                    throw new InvalidOperationException(
                        "The runtime cleanup status readback did not match the cancelled fixture root.");
                }
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                RecordTimeout(error);
                throw new TimeoutException(
                    $"Runtime cleanup did not report cleanup_complete before the {waitTimeout.TotalSeconds:0}-second deadline.",
                    error);
            }
            catch (Exception error)
            {
                state.Cleanup!["runtimeCleanup"] = new JsonObject
                {
                    ["state"] = "failed",
                    ["pollCount"] = pollCount,
                    ["lastState"] = lastState,
                    ["lastStep"] = lastStep,
                    ["failureCode"] = lastFailureCode,
                    ["reason"] = ApiAcceptanceSupport.RedactText(error.Message),
                };
                state.Cleanup["reason"] = "Runtime cleanup status could not be verified.";
                throw;
            }

            pollCount++;
            JsonObject startup = status["runtimeStartup"] as JsonObject ?? [];
            lastState = ApiAcceptanceSupport.GetString(startup, "state");
            lastStep = ApiAcceptanceSupport.GetString(startup, "step");
            lastFailureCode = ApiAcceptanceSupport.GetString(startup, "failureCode");
            if (lastState is null || lastStep is null)
            {
                state.Cleanup!["runtimeCleanup"] = new JsonObject
                {
                    ["state"] = "unavailable",
                    ["pollCount"] = pollCount,
                    ["lastState"] = lastState,
                    ["lastStep"] = lastStep,
                    ["reason"] = "The session status did not include a complete runtime startup summary.",
                };
                state.Cleanup["reason"] = "Runtime cleanup status could not be verified.";
                throw new InvalidOperationException(
                    "The session status did not include a complete runtime startup summary.");
            }
            if (deadline.IsCancellationRequested)
            {
                RecordTimeout();
                throw new TimeoutException(
                    $"Runtime cleanup did not report cleanup_complete before the {waitTimeout.TotalSeconds:0}-second deadline.");
            }

            JsonObject evidence = new()
            {
                ["state"] = lastState,
                ["step"] = lastStep,
                ["pollCount"] = pollCount,
                ["failureCode"] = lastFailureCode,
            };
            state.Cleanup!["runtimeCleanup"] = evidence.DeepClone();
            if (lastState == "cleanup_complete")
                return evidence;

            try
            {
                await Task.Delay(RuntimeCleanupPollInterval, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                RecordTimeout(error);
                throw new TimeoutException(
                    $"Runtime cleanup did not report cleanup_complete before the {waitTimeout.TotalSeconds:0}-second deadline.",
                    error);
            }
        }
    }

    private static async Task<JsonObject> VerifyMetricRunHistoryCleanupAsync(
        IGatewayClient runApi,
        AcceptanceFixtureState state,
        string projectId,
        string runId,
        string rootSessionId,
        JsonObject tree,
        long expectedFence,
        CancellationToken ct)
    {
        string archivedChildId = ApiAcceptanceSupport.StableUuid(runId, "metric-archived-child");
        string detachedChildId = ApiAcceptanceSupport.StableUuid(runId, "metric-detached-child");
        JsonArray nodes = tree["nodes"] as JsonArray ?? [];
        JsonObject[] rootNodes = nodes.OfType<JsonObject>().Where(node =>
            ApiAcceptanceSupport.GetString(node["identity"] as JsonObject ?? [], "sessionId") == rootSessionId).ToArray();
        JsonObject[] archivedNodes = nodes.OfType<JsonObject>().Where(node =>
            ApiAcceptanceSupport.GetString(node["identity"] as JsonObject ?? [], "sessionId") == archivedChildId).ToArray();
        if (nodes.Count != 2
            || rootNodes.Length != 1
            || archivedNodes.Length != 1
            || rootNodes[0]["detached"]?.GetValue<bool>() != false
            || ApiAcceptanceSupport.GetString(archivedNodes[0], "lifecycle") != "archived"
            || nodes.OfType<JsonObject>().Any(node =>
                ApiAcceptanceSupport.GetString(node["identity"] as JsonObject ?? [], "sessionId") == detachedChildId))
        {
            throw new InvalidOperationException("The retained metric session tree did not confirm the archived child and detached-child exclusion.");
        }

        JsonObject detachedStatus = await CleanupStepAsync(state, "read-detached-session",
            runApi, "readSessionStatus",
            new GatewayInvocation(Path(("projectId", projectId), ("runId", runId), ("sessionId", detachedChildId))),
            ct).ConfigureAwait(false);
        JsonObject detachedIdentity = detachedStatus["identity"] as JsonObject ?? [];
        if (ApiAcceptanceSupport.GetString(detachedIdentity, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(detachedIdentity, "runId") != runId
            || ApiAcceptanceSupport.GetString(detachedIdentity, "sessionId") != detachedChildId
            || ApiAcceptanceSupport.GetString(detachedStatus, "rootSessionId") != detachedChildId
            || detachedStatus["detached"]?.GetValue<bool>() != true
            || ApiAcceptanceSupport.GetString(detachedStatus, "lifecycle") != "cancelled"
            || GetLong(detachedStatus, "executionFence") != expectedFence)
        {
            throw new InvalidOperationException("The independent detached-session readback did not confirm retained cancellation at the new run fence.");
        }

        return new JsonObject
        {
            ["detachedSessionReadback"] = "verified",
            ["detachedSessionId"] = detachedChildId,
            ["detachedSessionRootId"] = detachedChildId,
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

    private static void EnsureSetupCapabilities(JsonObject openApi, AcceptanceManifest manifest)
    {
        string journey = manifest.Journey;
        bool serverOwnedSelection = RequiresServerOwnedSelection(manifest);
        IReadOnlySet<string> operations = ApiAcceptanceJourney.GetOperationIds(openApi);
        List<string> required =
        [
            "getAuthorizationContext", "createProject", "assignProjectRole", "listProjectRoleAssignments", "getProjectConfiguration",
            "getRunSelection", "acceptRunRoot", "cancelProjectRun", "readRunStatus", "readSessionStatus",
            "readSessionTree", "readDecisions", "revokeProjectRoleAssignment", "updateProject",
        ];
        if (!serverOwnedSelection)
            required.Add("acceptRunSelection");
        if (journey == "journal")
        {
            required.AddRange([
                "sendProjectRunMessage", "replayRunEvents", "getProject",
                "listProjectRoleAssignments",
            ]);
            if (serverOwnedSelection)
                required.Add("createProjectRun");
        }
        if (journey == "message") required.AddRange(["registerChild", "sendMessage"]);
        if (journey == "session-history-metric")
        {
            required.AddRange([
                "registerChild", "advanceTurnBoundary", "completeTurn", "archiveChild",
                "detachSession", "readProjectMetrics", "getProject", "listProjectRoleAssignments",
                "cancelProjectRun",
            ]);
            if (serverOwnedSelection)
                required.Add("createProjectRun");
        }
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
        if (serverOwnedSelection)
            ValidateProjectRunOperation(openApi, "createProjectRun");
        ValidateProjectRunOperation(openApi, "cancelProjectRun");
        ValidateRuntimeCleanupStatusContract(openApi);
    }

    private static void ValidateRuntimeCleanupStatusContract(JsonObject openApi)
    {
        JsonObject schemas = openApi["components"]?["schemas"] as JsonObject ?? [];
        JsonObject statusProperties = schemas["SessionStatusSnapshot"]?["properties"] as JsonObject ?? [];
        JsonArray states = schemas["RuntimeStartupSummary"]?["properties"]?["state"]?["enum"] as JsonArray ?? [];
        if (statusProperties["runtimeStartup"] is null
            || !states.OfType<JsonValue>().Any(state => state.GetValue<string>() == "cleanup_complete"))
        {
            throw new AcceptanceBlockedException(
                "The selected Gateway does not advertise runtime startup status that can confirm cleanup completion.");
        }
    }

    private static bool RequiresServerOwnedSelection(AcceptanceManifest manifest) =>
        (manifest.Journey is "journal" or "session-history-metric")
        && !manifest.FixturePlan.ContainsKey("selectionContext");

    private static void ValidateProjectRunOperation(JsonObject openApi, string operationId)
    {
        string expectedPath = operationId switch
        {
            "createProjectRun" => "/api/v1/projects/{projectId}/runs",
            "cancelProjectRun" => "/api/v1/projects/{projectId}/runs/{runId}/cancel",
            _ => throw new ArgumentOutOfRangeException(nameof(operationId)),
        };
        JsonObject paths = openApi["paths"] as JsonObject ?? [];
        JsonObject? match = paths.SelectMany(path => (path.Value as JsonObject ?? [])
                .Select(operation => (Path: path.Key, Method: operation.Key, Value: operation.Value as JsonObject)))
            .Where(item => ApiAcceptanceSupport.GetString(item.Value ?? [], "operationId") == operationId)
            .Select(item => new JsonObject
            {
                ["path"] = item.Path,
                ["method"] = item.Method,
                ["operation"] = item.Value?.DeepClone(),
            }).FirstOrDefault();
        JsonObject? operationObject = match?["operation"] as JsonObject;
        string? actualPath = ApiAcceptanceSupport.GetString(match ?? [], "path");
        JsonArray pathParameters = actualPath is null
            ? []
            : (paths[actualPath] as JsonObject)?["parameters"] as JsonArray ?? [];
        JsonArray operationParameters = operationObject?["parameters"] as JsonArray ?? [];
        bool idempotencyRequired = pathParameters.OfType<JsonObject>()
            .Concat(operationParameters.OfType<JsonObject>())
            .Any(parameter =>
                ApiAcceptanceSupport.GetString(parameter, "in") == "header"
                && ApiAcceptanceSupport.GetString(parameter, "name")?.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase) == true
                && parameter["required"]?.GetValue<bool>() == true);
        if (ApiAcceptanceSupport.GetString(match ?? [], "path") != expectedPath
            || ApiAcceptanceSupport.GetString(match ?? [], "method") != "post"
            || ApiAcceptanceSupport.GetString(operationObject ?? [], "x-agentweaver-owner") != "Orchestrator"
            || operationObject?["requestBody"]?["required"]?.GetValue<bool>() != true
            || !idempotencyRequired)
        {
            throw new AcceptanceBlockedException($"The discovered Gateway {operationId} operation does not expose its required idempotent Orchestrator contract.");
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

    private static JsonObject RequireOwnerAssignment(JsonObject assignments, string projectId, JsonObject environment)
    {
        JsonObject[] owners = (assignments["assignments"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(item => ApiAcceptanceSupport.GetString(item, "principalId") == ApiAcceptanceSupport.GetString(environment, "actorId")
                && ApiAcceptanceSupport.GetString(item, "role") == "owner")
            .ToArray();
        if (ApiAcceptanceSupport.GetString(assignments, "projectId") != projectId
            || owners.Length != 1
            || !ApiAcceptanceSupport.IsUuid(ApiAcceptanceSupport.GetString(owners[0], "assignmentId"))
            || GetLong(owners[0], "revision") != 1
            || ApiAcceptanceSupport.GetString(owners[0], "createdBy") != ApiAcceptanceSupport.GetString(environment, "actorId"))
        {
            throw new InvalidOperationException("Projects Config did not return the exact creator Owner assignment receipt.");
        }
        return owners[0];
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

    private static (string RunId, string RootSessionId) RequireInteractiveRun(JsonObject run, string projectId)
    {
        string? runId = ApiAcceptanceSupport.GetString(run, "runId");
        string? rootSessionId = ApiAcceptanceSupport.GetString(run, "rootSessionId");
        JsonObject binding = run["binding"] as JsonObject ?? [];
        if (!IsRunIdentity(runId)
            || !IsRunIdentity(rootSessionId)
            || ApiAcceptanceSupport.GetString(run, "state") != "awaiting_binding"
            || ApiAcceptanceSupport.GetString(binding, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(binding, "runId") != runId)
        {
            throw new InvalidOperationException("Projects did not return the exact pending interactive run identity and binding.");
        }
        return (runId!, rootSessionId!);
    }

    private static bool IsRunIdentity(string? value) =>
        ApiAcceptanceSupport.IsUuid(value) || Guid.TryParseExact(value, "N", out _);

    private static void RequireSelection(
        JsonObject selection,
        string projectId,
        string runId,
        long configurationRevision,
        long platformRevision,
        string contextRevision,
        long? expectedProjectRevision = null)
    {
        if (ApiAcceptanceSupport.GetString(selection, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(selection, "runId") != runId
            || GetLong(selection, "projectConfigurationRevision") != configurationRevision
            || GetLong(selection, "platformRuntimeRevision") != platformRevision
            || ApiAcceptanceSupport.GetString(selection, "contextRevision") != contextRevision
            || expectedProjectRevision is not null && GetLong(selection, "projectRevision") != expectedProjectRevision)
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
            JsonObject receipt = response.Body as JsonObject
                ?? throw new InvalidOperationException($"The live {operation} operation returned no JSON object.");
            RecordCleanupStep(state, step, operation, "passed", receipt);
            return receipt;
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
            RecordCleanupStep(state, step, operation, "failed");
            throw;
        }
    }

    private static void RecordCleanupStep(
        AcceptanceFixtureState state,
        string step,
        string operation,
        string status,
        JsonObject? receipt = null)
    {
        if (!state.VerifyCleanupReadback || state.Cleanup is null)
            return;
        JsonObject? summary = receipt is null ? null : operation switch
        {
            "cancelProjectRun" => new JsonObject
            {
                ["runId"] = receipt["runId"]?.DeepClone(),
                ["state"] = receipt["state"]?.DeepClone(),
                ["cancelledAt"] = receipt["cancelledAt"]?.DeepClone(),
                ["alreadyTerminal"] = receipt["alreadyTerminal"]?.DeepClone(),
            },
            "readRunStatus" => new JsonObject
            {
                ["projectId"] = receipt["projectId"]?.DeepClone(),
                ["runId"] = receipt["runId"]?.DeepClone(),
                ["rootSessionId"] = receipt["rootSessionId"]?.DeepClone(),
                ["executionState"] = receipt["executionState"]?.DeepClone(),
                ["executionFence"] = receipt["executionFence"]?.DeepClone(),
                ["stateVersion"] = receipt["stateVersion"]?.DeepClone(),
            },
            "readSessionTree" => new JsonObject
            {
                ["rootSessionId"] = receipt["rootSessionId"]?.DeepClone(),
                ["nodeCount"] = (receipt["nodes"] as JsonArray)?.Count,
            },
            "readSessionStatus" => new JsonObject
            {
                ["identity"] = receipt["identity"]?.DeepClone(),
                ["rootSessionId"] = receipt["rootSessionId"]?.DeepClone(),
                ["detached"] = receipt["detached"]?.DeepClone(),
                ["lifecycle"] = receipt["lifecycle"]?.DeepClone(),
                ["executionFence"] = receipt["executionFence"]?.DeepClone(),
                ["runtimeStartup"] = receipt["runtimeStartup"]?.DeepClone(),
            },
            "getProject" => new JsonObject
            {
                ["projectId"] = receipt["projectId"]?.DeepClone(),
                ["state"] = receipt["state"]?.DeepClone(),
                ["revision"] = receipt["revision"]?.DeepClone(),
            },
            "listProjectRoleAssignments" => new JsonObject
            {
                ["projectId"] = receipt["projectId"]?.DeepClone(),
                ["activeAssignmentCount"] = (receipt["assignments"] as JsonArray)?.Count,
            },
            _ => new JsonObject
            {
                ["projectId"] = receipt["projectId"]?.DeepClone(),
                ["assignmentId"] = receipt["assignmentId"]?.DeepClone(),
                ["state"] = receipt["state"]?.DeepClone(),
                ["revision"] = receipt["revision"]?.DeepClone(),
            },
        };
        JsonArray steps = state.Cleanup["steps"] as JsonArray ?? [];
        steps.Add(new JsonObject
        {
            ["step"] = step,
            ["operationId"] = operation,
            ["status"] = status,
            ["receipt"] = summary,
        });
        state.Cleanup["steps"] = steps;
    }

    private sealed class AcceptanceHttpException(int status, string? problemCode, string message) : Exception(message)
    {
        public int Status { get; } = status;
        public string? ProblemCode { get; } = problemCode;
    }
}
