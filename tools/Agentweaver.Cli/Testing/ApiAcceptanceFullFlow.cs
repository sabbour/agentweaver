using System.Text;
using System.Text.Json.Nodes;

namespace Agentweaver.Cli;

/// <summary>
/// Runs the full-flow critical-path journey: one real BYOK casting turn, casting confirmation,
/// backlog task capture and archive, and priced run usage. The checks use outcomes and hashes,
/// not exact model text.
/// </summary>
internal static class ApiAcceptanceFullFlow
{
    internal static readonly IReadOnlyList<string> RequiredOperations =
    [
        "formulateCastingProposal",
        "readSessionTurnContent",
        "readSessionTree",
        "listProjectCastingRoles",
        "confirmProjectCastingProposal",
        "getProjectConfiguration",
        "captureBacklogTask",
        "readProjectBacklog",
        "archiveProjectBacklogTask",
        "readRunUsage",
    ];

    internal static readonly string[] ReservedRoleIds = ["scribe", "work-monitor", "coordinator", "rai"];

    internal static TimeSpan UsageWaitLimit { get; set; } = TimeSpan.FromMinutes(3);

    internal static TimeSpan UsagePollInterval { get; set; } = TimeSpan.FromSeconds(3);

    // Workflow generation and task dispatch have no public routes yet, so this journey cannot cover them.
    internal static JsonArray CriticalPathGaps() =>
    [
        new JsonObject
        {
            ["stage"] = "workflow-generation",
            ["missingRoutePrefix"] = "/workflows",
        },
        new JsonObject
        {
            ["stage"] = "task-model-completion",
            ["missingRoutePrefix"] = "/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/actions/dispatch",
        },
    ];

    public static async Task RunAsync(
        IGatewayClient runApi,
        IGatewayClient ownerApi,
        AcceptanceManifest manifest,
        AcceptanceFixture fixture,
        JsonObject observations,
        List<JsonObject> steps,
        CancellationToken ct)
    {
        JsonObject approval = ApiAcceptanceSupport.ModelApproval(manifest)
            ?? throw new AcceptanceBlockedException("The full-flow model approval and run limits are missing.");
        JsonObject evidence = new()
        {
            ["status"] = "blocked",
            ["projectId"] = fixture.ProjectId,
            ["runId"] = fixture.RunId,
            ["rootSessionId"] = fixture.RootSessionId,
            ["limits"] = new JsonObject
            {
                ["maxRunUsd"] = approval["maxRunUsd"]?.DeepClone(),
                ["maxDurationMinutes"] = approval["maxDurationMinutes"]?.DeepClone(),
            },
            ["stages"] = new JsonObject
            {
                ["casting"] = new JsonObject { ["status"] = "not-started" },
                ["castingConfirmation"] = new JsonObject { ["status"] = "not-started" },
                ["backlogTask"] = new JsonObject { ["status"] = "not-started" },
                ["usage"] = new JsonObject { ["status"] = "not-started" },
            },
            ["criticalPathCoverage"] = new JsonObject
            {
                ["complete"] = false,
                ["gaps"] = CriticalPathGaps(),
            },
        };
        observations["fullFlow"] = evidence;
        JsonObject stages = (JsonObject)evidence["stages"]!;
        string current = "casting";
        try
        {
            JsonObject proposal = await RunCastingAsync(runApi, ownerApi, fixture, approval, stages, steps, ct)
                .ConfigureAwait(false);
            current = "castingConfirmation";
            await ConfirmCastingAsync(ownerApi, fixture, proposal, stages, steps, ct).ConfigureAwait(false);
            current = "backlogTask";
            await RunBacklogTaskAsync(ownerApi, fixture, stages, steps, ct).ConfigureAwait(false);
            current = "usage";
            await RequirePricedUsageAsync(runApi, fixture, approval, stages, steps, ct).ConfigureAwait(false);
            evidence["status"] = "passed";
        }
        catch (Exception error)
        {
            string status = error is AcceptanceBlockedException ? "blocked" : "failed";
            evidence["status"] = status;
            evidence["failedStage"] = current;
            ((JsonObject)stages[current]!)["status"] = status;
            evidence["failure"] = ApiAcceptanceSupport.RedactText(error.Message);
            throw;
        }
    }

    private static async Task<JsonObject> RunCastingAsync(
        IGatewayClient runApi,
        IGatewayClient ownerApi,
        AcceptanceFixture fixture,
        JsonObject approval,
        JsonObject stages,
        List<JsonObject> steps,
        CancellationToken ct)
    {
        string prompt = fixture.NativeMaterialPrompt
            ?? throw new AcceptanceBlockedException("The accepted run has no catalog-bound casting prompt.");
        JsonObject startup = fixture.RuntimeStartup
            ?? throw new AcceptanceBlockedException("The accepted run has no current runtime-startup binding.");
        string promptHash = ApiAcceptanceNativeMaterial.Hash(Encoding.UTF8.GetBytes(prompt));
        JsonObject request = new()
        {
            ["expectedConfigurationRevision"] = ApiAcceptanceNativeMaterial.Long(fixture.ProjectReceipt, "configurationRevision"),
            ["runId"] = fixture.RunId,
            ["sessionId"] = fixture.RootSessionId,
            ["idempotencyKey"] = ApiAcceptanceSupport.StableUuid(fixture.RootSessionId, "full-flow-casting-request"),
            ["prompt"] = prompt,
            ["goal"] = ApiAcceptanceFixtureSetup.NativeMaterialGoal,
            ["teamSize"] = ApiAcceptanceFixtureSetup.NativeMaterialTeamSize,
            ["intent"] = "recast",
            ["preferredRoleIds"] = new JsonArray(),
        };
        JsonObject proposal = await ApiAcceptanceJourney.InvokeJsonAsync(runApi, "formulateCastingProposal",
            ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId), ("sessionId", fixture.RootSessionId)),
            request, null, [200], ct).ConfigureAwait(false);
        JsonObject generation = ApiAcceptanceNativeMaterial.RequireGeneration(proposal, fixture.ProjectId, promptHash, expectReplay: false);
        JsonObject completion = (JsonObject)generation["nativeCompletion"]!;
        string outputHash = ApiAcceptanceNativeMaterial.RequireCompletion(
            completion, startup, approval, fixture.ExecutionFence, promptHash);
        JsonObject userMaterial = ApiAcceptanceNativeMaterial.RequireMaterial(completion, "userMaterial", "turnContent");
        JsonObject assistantMaterial = ApiAcceptanceNativeMaterial.RequireMaterial(completion, "assistantMaterial", "turnContent");
        JsonObject userReadback = await ApiAcceptanceNativeMaterial.ReadMaterialAsync(runApi, fixture, userMaterial, ct)
            .ConfigureAwait(false);
        ApiAcceptanceNativeMaterial.RequireReadback(userReadback, fixture, prompt, userMaterial);
        JsonObject assistantReadback = await ApiAcceptanceNativeMaterial.ReadMaterialAsync(runApi, fixture, assistantMaterial, ct)
            .ConfigureAwait(false);
        string output = ApiAcceptanceSupport.GetString(assistantReadback, "text")
            ?? throw new AcceptanceBlockedException("The assistant turn material has no text.");
        ApiAcceptanceNativeMaterial.RequireReadback(assistantReadback, fixture, output, assistantMaterial);
        if (ApiAcceptanceNativeMaterial.Hash(Encoding.UTF8.GetBytes(output)) != outputHash)
            throw new InvalidOperationException("The assistant turn readback does not match the completion receipt.");
        await ApiAcceptanceNativeMaterial.RequireRootOnlyTreeAsync(runApi, fixture, ct).ConfigureAwait(false);

        IReadOnlySet<string> catalogRoles = await ReadCatalogRolesAsync(ownerApi, ct).ConfigureAwait(false);
        JsonArray cast = RequireDraftCasting(proposal, catalogRoles);
        if (ApiAcceptanceSupport.GetString(proposal, "state") != "pending")
            throw new InvalidOperationException("The formulated casting proposal is not pending review.");

        stages["casting"] = new JsonObject
        {
            ["status"] = "passed",
            ["proposalId"] = proposal["proposalId"]?.DeepClone(),
            ["draftRevision"] = proposal["draftRevision"]?.DeepClone(),
            ["requestId"] = completion["requestId"]?.DeepClone(),
            ["promptSha256"] = promptHash,
            ["outputSha256"] = outputHash,
            ["usageEventCount"] = (completion["usageEventIds"] as JsonArray)?.Count,
            ["agentCount"] = cast.Count,
            ["user"] = ApiAcceptanceNativeMaterial.MaterialSummary(userMaterial),
            ["assistant"] = ApiAcceptanceNativeMaterial.MaterialSummary(assistantMaterial),
        };
        ApiAcceptanceSupport.AddStep(steps, "full-flow-casting-model-turn", "passed",
            ("proposalId", proposal["proposalId"]?.DeepClone()),
            ("outputSha256", JsonValue.Create(outputHash)),
            ("agentCount", JsonValue.Create(cast.Count)));
        return proposal;
    }

    private static async Task<IReadOnlySet<string>> ReadCatalogRolesAsync(IGatewayClient api, CancellationToken ct)
    {
        GatewayResponse response = await ApiAcceptanceJourney.InvokeAsync(
            api, "listProjectCastingRoles", null, null, null, ct).ConfigureAwait(false);
        if (response.Status != 200 || response.Body is not JsonArray roles)
            throw new AcceptanceBlockedException("The current casting role catalog is unavailable.");
        return roles.OfType<JsonObject>()
            .Select(role => ApiAcceptanceSupport.GetString(role, "id"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    // The model chooses the roles, so the oracle checks the shape and catalog membership, not the exact roles.
    internal static JsonArray RequireDraftCasting(JsonObject proposal, IReadOnlySet<string> catalogRoles)
    {
        JsonArray cast = proposal["draft"]?["casting"] as JsonArray
            ?? throw new InvalidOperationException("The casting proposal has no draft casting.");
        List<JsonObject> agents = cast.OfType<JsonObject>().ToList();
        HashSet<string> agentIds = new(StringComparer.Ordinal);
        HashSet<string> roles = new(StringComparer.Ordinal);
        foreach (JsonObject agent in agents)
        {
            string? agentId = ApiAcceptanceSupport.GetString(agent, "agentId");
            string? role = ApiAcceptanceSupport.GetString(agent, "role");
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(role)
                || !agentIds.Add(agentId) || !roles.Add(role)
                || !catalogRoles.Contains(role)
                || ReservedRoleIds.Contains(role, StringComparer.OrdinalIgnoreCase)
                || ApiAcceptanceNativeMaterial.Long(agent, "order") is null)
                throw new InvalidOperationException(
                    "The draft casting has a missing, duplicate, reserved, or unknown agent or role.");
        }
        if (agents.Count != cast.Count || agents.Count != ApiAcceptanceFixtureSetup.NativeMaterialTeamSize)
            throw new InvalidOperationException(
                $"The draft casting has {cast.Count} agents; the journey asked for {ApiAcceptanceFixtureSetup.NativeMaterialTeamSize}.");
        return cast;
    }

    private static async Task ConfirmCastingAsync(
        IGatewayClient ownerApi,
        AcceptanceFixture fixture,
        JsonObject proposal,
        JsonObject stages,
        List<JsonObject> steps,
        CancellationToken ct)
    {
        string proposalId = ApiAcceptanceSupport.GetString(proposal, "proposalId")
            ?? throw new InvalidOperationException("The casting proposal has no ID.");
        long baseRevision = ApiAcceptanceNativeMaterial.Long(proposal, "baseConfigurationRevision")
            ?? throw new InvalidOperationException("The casting proposal has no base configuration revision.");
        JsonObject confirmed = await ApiAcceptanceJourney.InvokeJsonAsync(ownerApi, "confirmProjectCastingProposal",
            ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId), ("proposalId", proposalId)),
            new JsonObject { ["expectedDraftRevision"] = proposal["draftRevision"]?.DeepClone() },
            null, [200], ct).ConfigureAwait(false);
        long? confirmedRevision = ApiAcceptanceNativeMaterial.Long(confirmed, "confirmedConfigurationRevision");
        if (ApiAcceptanceSupport.GetString(confirmed, "proposalId") != proposalId
            || ApiAcceptanceSupport.GetString(confirmed, "state") != "confirmed"
            || confirmedRevision is null || confirmedRevision <= baseRevision)
            throw new InvalidOperationException("The casting confirmation did not create a new configuration revision.");

        JsonObject configuration = await ApiAcceptanceJourney.InvokeJsonAsync(ownerApi, "getProjectConfiguration",
            ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId)), null, null, [200], ct).ConfigureAwait(false);
        if (ApiAcceptanceSupport.GetString(configuration, "projectId") != fixture.ProjectId
            || ApiAcceptanceNativeMaterial.Long(configuration, "revision") != confirmedRevision
            || !SameCasting(configuration["configuration"]?["casting"] as JsonArray, proposal["draft"]?["casting"] as JsonArray))
            throw new InvalidOperationException("The project configuration readback does not show the confirmed casting.");

        stages["castingConfirmation"] = new JsonObject
        {
            ["status"] = "passed",
            ["proposalId"] = proposalId,
            ["baseConfigurationRevision"] = baseRevision,
            ["confirmedConfigurationRevision"] = confirmedRevision,
        };
        ApiAcceptanceSupport.AddStep(steps, "full-flow-casting-confirmation", "passed",
            ("proposalId", JsonValue.Create(proposalId)),
            ("configurationRevision", JsonValue.Create(confirmedRevision)));
    }

    internal static bool SameCasting(JsonArray? actual, JsonArray? expected)
    {
        static string Key(JsonObject agent) =>
            $"{ApiAcceptanceSupport.GetString(agent, "agentId")}|{ApiAcceptanceSupport.GetString(agent, "role")}";
        if (actual is null || expected is null || actual.Count != expected.Count)
            return false;
        return actual.OfType<JsonObject>().Select(Key).Order(StringComparer.Ordinal)
            .SequenceEqual(expected.OfType<JsonObject>().Select(Key).Order(StringComparer.Ordinal));
    }

    private static async Task RunBacklogTaskAsync(
        IGatewayClient ownerApi,
        AcceptanceFixture fixture,
        JsonObject stages,
        List<JsonObject> steps,
        CancellationToken ct)
    {
        Dictionary<string, string> backlogPath = ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId));
        JsonObject before = await ApiAcceptanceJourney.InvokeJsonAsync(ownerApi, "readProjectBacklog",
            backlogPath, null, null, [200], ct).ConfigureAwait(false);
        long graphRevision = ApiAcceptanceNativeMaterial.Long(before, "revision")
            ?? throw new InvalidOperationException("The project backlog has no revision.");
        string taskId = ApiAcceptanceSupport.StableUuid(fixture.RootSessionId, "full-flow-backlog-task");
        string title = $"Full-flow acceptance task {taskId[..8]}";
        JsonObject captured = await ApiAcceptanceJourney.InvokeJsonAsync(ownerApi, "captureBacklogTask",
            backlogPath,
            new JsonObject
            {
                ["taskId"] = taskId,
                ["content"] = new JsonObject
                {
                    ["title"] = title,
                    ["description"] = "Created and archived by the full-flow acceptance journey.",
                },
                ["expectedGraphRevision"] = graphRevision,
            }, null, [200], ct).ConfigureAwait(false);
        JsonObject task = RequireTask(captured, fixture.ProjectId, taskId, title, archived: false);

        JsonObject readback = await ApiAcceptanceJourney.InvokeJsonAsync(ownerApi, "readProjectBacklog",
            backlogPath, null, null, [200], ct).ConfigureAwait(false);
        task = RequireTask(readback, fixture.ProjectId, taskId, title, archived: false);
        long taskRevision = ApiAcceptanceNativeMaterial.Long(task, "revision")!.Value;

        JsonObject archived = await ApiAcceptanceJourney.InvokeJsonAsync(ownerApi, "archiveProjectBacklogTask",
            ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId), ("taskId", taskId)),
            new JsonObject
            {
                ["expectedGraphRevision"] = ApiAcceptanceNativeMaterial.Long(readback, "revision"),
                ["expectedTaskRevision"] = taskRevision,
            }, null, [200], ct).ConfigureAwait(false);
        _ = RequireTask(archived, fixture.ProjectId, taskId, title, archived: true);
        JsonObject final = await ApiAcceptanceJourney.InvokeJsonAsync(ownerApi, "readProjectBacklog",
            backlogPath, null, null, [200], ct).ConfigureAwait(false);
        _ = RequireTask(final, fixture.ProjectId, taskId, title, archived: true);

        stages["backlogTask"] = new JsonObject
        {
            ["status"] = "passed",
            ["taskId"] = taskId,
            ["capturedGraphRevision"] = captured["revision"]?.DeepClone(),
            ["archivedGraphRevision"] = final["revision"]?.DeepClone(),
            ["archived"] = true,
        };
        ApiAcceptanceSupport.AddStep(steps, "full-flow-backlog-task", "passed",
            ("taskId", JsonValue.Create(taskId)));
    }

    internal static JsonObject RequireTask(JsonObject graph, string projectId, string taskId, string title, bool archived)
    {
        List<JsonObject> matches = (graph["tasks"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(task => ApiAcceptanceSupport.GetString(task["reference"] as JsonObject ?? [], "taskId") == taskId)
            .ToList();
        JsonObject? match = matches.Count == 1 ? matches[0] : null;
        if (ApiAcceptanceSupport.GetString(graph, "projectId") != projectId
            || match is null
            || ApiAcceptanceSupport.GetString(match["reference"] as JsonObject ?? [], "projectId") != projectId
            || ApiAcceptanceSupport.GetString(match, "title") != title
            || match["isArchived"] is not JsonValue isArchived || isArchived.GetValue<bool>() != archived
            || ApiAcceptanceNativeMaterial.Long(match, "revision") is not > 0)
            throw new InvalidOperationException(archived
                ? "The backlog readback does not show the task once as archived."
                : "The backlog readback does not show the captured task exactly once.");
        return match;
    }

    private static async Task RequirePricedUsageAsync(
        IGatewayClient runApi,
        AcceptanceFixture fixture,
        JsonObject approval,
        JsonObject stages,
        List<JsonObject> steps,
        CancellationToken ct)
    {
        decimal maxRunUsd = approval["maxRunUsd"]!.GetValue<decimal>();
        DateTimeOffset deadline = DateTimeOffset.UtcNow + UsageWaitLimit;
        while (true)
        {
            JsonObject usage = await ApiAcceptanceJourney.InvokeJsonAsync(runApi, "readRunUsage",
                ApiAcceptanceJourney.Path(("projectId", fixture.ProjectId), ("runId", fixture.RunId)),
                null, null, [200], ct).ConfigureAwait(false);
            JsonObject? usd = UsdAmount(usage);
            if (usd is not null && ApiAcceptanceNativeMaterial.Long(usd, "pricedEvents") is > 0)
            {
                decimal amount = usd["amount"]!.GetValue<decimal>();
                if (usage["isFullyPriced"] is not JsonValue fully || !fully.GetValue<bool>())
                    throw new AcceptanceBlockedException("The run usage is not fully priced, so the USD spend cannot be proved.");
                if (amount > maxRunUsd)
                    throw new InvalidOperationException($"The run spent {amount} USD, which is above the {maxRunUsd} USD limit.");
                stages["usage"] = new JsonObject
                {
                    ["status"] = "passed",
                    ["events"] = usage["events"]?.DeepClone(),
                    ["pricedEvents"] = usd["pricedEvents"]?.DeepClone(),
                    ["amountUsd"] = amount,
                    ["maxRunUsd"] = maxRunUsd,
                };
                ApiAcceptanceSupport.AddStep(steps, "full-flow-priced-usage", "passed",
                    ("amountUsd", JsonValue.Create(amount)));
                return;
            }
            if (DateTimeOffset.UtcNow >= deadline)
                throw new AcceptanceBlockedException("The run usage has no priced byok.tokens USD amount within the wait limit.");
            await Task.Delay(UsagePollInterval, ct).ConfigureAwait(false);
        }
    }

    private static JsonObject? UsdAmount(JsonObject usage) =>
        (usage["amounts"] as JsonArray ?? []).OfType<JsonObject>().SingleOrDefault(amount =>
            ApiAcceptanceSupport.GetString(amount, "meterSource") == "byok.tokens"
            && ApiAcceptanceSupport.GetString(amount, "unit") == "USD");
}
