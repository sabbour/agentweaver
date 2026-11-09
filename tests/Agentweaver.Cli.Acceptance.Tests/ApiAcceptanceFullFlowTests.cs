using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Agentweaver.Cli.Api;
using Agentweaver.Cli.Auth;
using Xunit;

namespace Agentweaver.Cli.Acceptance.Tests;

public sealed class ApiAcceptanceFullFlowTests
{
    private const string ProjectId = "project-1";
    private const string RunId = "run-1";
    private const string SessionId = "session-1";
    private const string Prompt = "Read-only casting prompt";
    private const string Output = "{\"roleIds\":[\"backend\",\"tester\"]}";
    private static readonly string HashA = new('a', 64);

    [Fact]
    public async Task FullFlowConfirmsCastingCapturesAndArchivesTaskAndProvesPricedUsage()
    {
        FakeFullFlowGateway api = new();

        (JsonObject evidence, Exception? error) = await RunAsync(api);

        Assert.Null(error);
        Assert.Equal("passed", evidence["status"]?.GetValue<string>());
        foreach (string stage in new[] { "casting", "castingConfirmation", "backlogTask", "usage" })
            Assert.Equal("passed", evidence["stages"]?[stage]?["status"]?.GetValue<string>());
        Assert.False(evidence["criticalPathCoverage"]?["complete"]?.GetValue<bool>());
        Assert.Equal(2, evidence["criticalPathCoverage"]?["gaps"]?.AsArray().Count);
        Assert.Equal(["listProjectCastingRoles", "confirmProjectCastingProposal", "getProjectConfiguration",
                "readProjectBacklog", "captureBacklogTask", "readProjectBacklog", "archiveProjectBacklogTask",
                "readProjectBacklog"],
            api.OwnerCalls);
        Assert.Equal(1L, api.ConfirmRequest?["expectedDraftRevision"]?.GetValue<long>());
        Assert.Equal(2, api.UsageReads);
        string json = evidence.ToJsonString();
        Assert.DoesNotContain(Prompt, json, StringComparison.Ordinal);
        Assert.DoesNotContain("roleIds", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("duplicate-role", "failed", "casting")]
    [InlineData("reserved-role", "failed", "casting")]
    [InlineData("unknown-role", "failed", "casting")]
    [InlineData("one-agent", "failed", "casting")]
    [InlineData("confirm-not-confirmed", "failed", "castingConfirmation")]
    [InlineData("config-not-updated", "failed", "castingConfirmation")]
    [InlineData("task-missing", "failed", "backlogTask")]
    [InlineData("task-not-archived", "failed", "backlogTask")]
    [InlineData("over-budget", "failed", "usage")]
    [InlineData("not-fully-priced", "blocked", "usage")]
    [InlineData("never-priced", "blocked", "usage")]
    public async Task FullFlowOraclesRejectWrongOutcomes(string scenario, string status, string stage)
    {
        FakeFullFlowGateway api = new() { Scenario = scenario };

        (JsonObject evidence, Exception? error) = await RunAsync(api);

        Assert.NotNull(error);
        Assert.Equal(status == "blocked", error is AcceptanceBlockedException);
        Assert.Equal(status, evidence["status"]?.GetValue<string>());
        Assert.Equal(stage, evidence["failedStage"]?.GetValue<string>());
        Assert.Equal(status, evidence["stages"]?[stage]?["status"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("no-model")]
    [InlineData("hosted-model")]
    [InlineData("other-byok-model")]
    [InlineData("no-budget")]
    [InlineData("zero-budget")]
    [InlineData("budget-above-limit")]
    [InlineData("copilot-budget-only")]
    [InlineData("session-budget-only")]
    public void FullFlowBudgetPreflightBlocksWithoutByokBindingAndBoundedUsdBudget(string scenario)
    {
        JsonObject selection = Selection(scenario);

        AcceptanceBlockedException error = Assert.Throws<AcceptanceBlockedException>(
            () => ApiAcceptanceFixtureSetup.RequireFullFlowBudget(selection, Manifest()));

        Assert.Contains("No run was started", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FullFlowBudgetPreflightRecordsTheLowestQualifyingHardLimit()
    {
        JsonObject selection = Selection("valid");
        selection["runLimits"]!["spendBudgets"]!.AsArray().Add(Budget("run", "byok.tokens", "USD", 2m));

        JsonObject budget = ApiAcceptanceFixtureSetup.RequireFullFlowBudget(selection, Manifest());

        Assert.Equal(2m, budget["hardLimitUsd"]!.GetValue<decimal>());
        Assert.Equal("approved/byok", budget["modelSelectionReference"]!.GetValue<string>());
    }

    private static JsonObject Selection(string scenario) => new()
    {
        ["modelSelection"] = scenario == "no-model" ? null : new JsonObject
        {
            ["reference"] = scenario == "other-byok-model" ? "other/byok" : "approved/byok",
            ["sourceMode"] = scenario == "hosted-model" ? "hostedCopilot" : "byok",
        },
        ["runLimits"] = new JsonObject
        {
            ["spendBudgets"] = scenario switch
            {
                "no-budget" => new JsonArray(),
                "zero-budget" => new JsonArray(Budget("run", "byok.tokens", "USD", 0m)),
                "budget-above-limit" => new JsonArray(Budget("run", "byok.tokens", "USD", 6m)),
                "copilot-budget-only" => new JsonArray(Budget("run", "copilot.nano_aiu", "AIC", 1m)),
                "session-budget-only" => new JsonArray(Budget("session", "byok.tokens", "USD", 1m)),
                _ => new JsonArray(Budget("run", "byok.tokens", "USD", 5m)),
            },
        },
    };

    private static JsonObject Budget(string scope, string meter, string unit, decimal limit) => new()
    {
        ["budgetId"] = Guid.NewGuid().ToString("D"),
        ["revision"] = 1L,
        ["scope"] = scope,
        ["meterSource"] = meter,
        ["unit"] = unit,
        ["hardLimit"] = limit,
    };

    private static AcceptanceManifest Manifest() => new(
        new JsonObject(), new JsonObject(), new JsonObject(),
        new JsonObject
        {
            ["expectedPlatformRuntimeRevision"] = 1L,
            ["fullFlow"] = new JsonObject
            {
                ["approvedModelSelectionReference"] = "approved/byok",
                ["approvedModelSourceMode"] = "byok",
                ["approvedReasoningEffort"] = "medium",
                ["approvedMaxPromptTokens"] = 4096L,
                ["maxRunUsd"] = 5m,
                ["maxDurationMinutes"] = 30L,
            },
        },
        ApiAcceptanceSupport.FullFlowJourney, "input.json", ".");

    private static async Task<(JsonObject Evidence, Exception? Error)> RunAsync(FakeFullFlowGateway api)
    {
        TimeSpan interval = ApiAcceptanceFullFlow.UsagePollInterval;
        TimeSpan limit = ApiAcceptanceFullFlow.UsageWaitLimit;
        ApiAcceptanceFullFlow.UsagePollInterval = TimeSpan.FromMilliseconds(10);
        ApiAcceptanceFullFlow.UsageWaitLimit = TimeSpan.FromMilliseconds(200);
        try
        {
            AcceptanceFixture fixture = new(
                ProjectId, RunId, SessionId, SessionId, 7, 1, null, null,
                new JsonObject(), new JsonObject { ["configurationRevision"] = 1L },
                new JsonObject(), new JsonObject(), new JsonObject(), null)
            {
                P1SetupInvocationId = Guid.NewGuid().ToString("D"),
                NativeMaterialPrompt = Prompt,
                RuntimeStartup = Startup(),
            };
            AcceptanceManifest manifest = Manifest();
            JsonObject observations = new();
            Exception? error = null;
            try
            {
                await ApiAcceptanceFullFlow.RunAsync(api.Run, api.Owner, manifest, fixture, observations, [], CancellationToken.None);
            }
            catch (Exception caught)
            {
                error = caught;
            }
            return (observations["fullFlow"]!.AsObject(), error);
        }
        finally
        {
            ApiAcceptanceFullFlow.UsagePollInterval = interval;
            ApiAcceptanceFullFlow.UsageWaitLimit = limit;
        }
    }

    [Fact]
    public async Task BudgetBlockedRunIsCancelledAndFixtureIsCleanedWithoutRootReadback()
    {
        const string actor = "actor-1";
        string projectId = Guid.NewGuid().ToString("D");
        string runId = Guid.NewGuid().ToString("D");
        string ownerAssignment = Guid.NewGuid().ToString("D");
        string orchestratorAssignment = Guid.NewGuid().ToString("D");
        AcceptanceFixtureState state = new(Guid.NewGuid().ToString("D"))
        {
            Journey = ApiAcceptanceSupport.FullFlowJourney,
            SetupAttempted = true,
            SetupComplete = true,
            VerifyCleanupReadback = true,
            SelectionKnown = true,
            RunId = runId,
            RootSessionId = Guid.NewGuid().ToString("D"),
        };
        state.Setup = new JsonObject
        {
            ["projectId"] = projectId,
            ["projectName"] = state.ProjectName,
            ["projectRevision"] = 2L,
            ["assignmentId"] = orchestratorAssignment,
            ["assignmentRevision"] = 1L,
            ["ownerAssignmentId"] = ownerAssignment,
            ["ownerAssignmentRevision"] = 1L,
        };
        JsonObject Revoked(string id, string role) => new()
        {
            ["assignmentId"] = id, ["projectId"] = projectId, ["principalId"] = actor, ["role"] = role,
            ["state"] = "revoked", ["revision"] = 2L, ["grantedBy"] = actor, ["revokedBy"] = actor,
        };
        JsonObject archived = new()
        {
            ["projectId"] = projectId, ["name"] = state.ProjectName, ["state"] = "archived",
            ["revision"] = 3L, ["configurationRevision"] = 1L,
        };
        List<string> calls = [];
        ScriptedClient api = new((operation, invocation) =>
        {
            calls.Add(operation);
            return operation switch
            {
                "cancelProjectRun" => new JsonObject
                {
                    ["runId"] = runId, ["state"] = "cancelled", ["alreadyTerminal"] = false,
                    ["cancelledAt"] = "2026-11-09T18:00:00Z",
                },
                "revokeProjectRoleAssignment" when invocation.PathParameters!["assignmentId"] == ownerAssignment =>
                    Revoked(ownerAssignment, "owner"),
                "revokeProjectRoleAssignment" => Revoked(orchestratorAssignment, "orchestrator"),
                "updateProject" or "getProject" => archived.DeepClone().AsObject(),
                "listProjectRoleAssignments" => new JsonObject { ["projectId"] = projectId, ["assignments"] = new JsonArray() },
                _ => throw new InvalidOperationException($"Unexpected operation {operation}."),
            };
        });

        JsonObject cleanup = await ApiAcceptanceFixtureSetup.CleanupAsync(api, api, null, state, actor, CancellationToken.None);

        Assert.Equal("cleaned", ApiAcceptanceSupport.GetString(cleanup, "state"));
        Assert.Equal("not-attempted", ApiAcceptanceSupport.GetString(cleanup["runTerminalization"]!.AsObject(), "rootAcceptance"));
        Assert.Equal(
            ["cancelProjectRun", "revokeProjectRoleAssignment", "updateProject", "revokeProjectRoleAssignment", "getProject", "listProjectRoleAssignments"],
            calls);
    }

    private sealed class ScriptedClient(Func<string, GatewayInvocation, JsonObject> handle) : IGatewayClient
    {
        public CliIdentity Identity => null!;
        public IReadOnlyList<JsonObject> Calls => [];
        public Task<JsonObject> DiscoverAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject> ValidateAuthorizationContextAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<GatewayResponse> InvokeAsync(string operationId, GatewayInvocation invocation, CancellationToken ct) =>
            Task.FromResult(new GatewayResponse(200, handle(operationId, invocation),
                new Dictionary<string, string>(), new JsonObject()));

        public Task<GatewayResponse> InvokeRawAsync(
            string method, string url, byte[]? body, IReadOnlyDictionary<string, string>? headers,
            bool outputRequested, Func<string, ValueTask>? onEventChunk, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static string Sha(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static JsonObject Startup() => new()
    {
        ["runtimeInstanceId"] = "11111111-1111-4111-8111-111111111111",
        ["registrationRevision"] = 4L,
        ["acceptedSelectionHash"] = HashA,
        ["imageDigest"] = $"sha256:{HashA}",
    };

    private static JsonObject Material(string kind, long position, string content) => new()
    {
        ["eventId"] = Guid.NewGuid().ToString("D"),
        ["position"] = position,
        ["kind"] = kind,
        ["contentSha256"] = Sha(content),
        ["bindingSha256"] = HashA,
    };

    private static JsonObject Completion() => new()
    {
        ["requestId"] = Guid.NewGuid().ToString("D"),
        ["runtimeInstanceId"] = "11111111-1111-4111-8111-111111111111",
        ["registrationRevision"] = 4L,
        ["acceptedSelectionHash"] = HashA,
        ["executionFence"] = 7L,
        ["promptSha256"] = Sha(Prompt),
        ["outputSha256"] = Sha(Output),
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
        ["image"] = new JsonObject { ["digest"] = $"sha256:{HashA}" },
        ["nativeCursorBeforeSha256"] = HashA,
        ["nativeCursorAfterSha256"] = new string('b', 64),
        ["nativeMessageId"] = Guid.NewGuid().ToString("D"),
        ["nativeTurnStartEventId"] = Guid.NewGuid().ToString("D"),
        ["nativeTurnEndEventId"] = Guid.NewGuid().ToString("D"),
        ["nativeCompletionReceiptEventId"] = Guid.NewGuid().ToString("D"),
        ["registrationSha256"] = HashA,
        ["sourceSha256"] = HashA,
        ["sdkSessionSha256"] = HashA,
        ["nativeEventIntervalSha256"] = HashA,
        ["cacheBaselineCursorSha256"] = HashA,
        ["cacheBaselineAfterCursorSha256"] = HashA,
        ["userMaterial"] = Material("turnContent", 1, Prompt),
        ["assistantMaterial"] = Material("turnContent", 2, Output),
        ["cacheMaterial"] = Material("sdkCache", 3, "cache"),
    };

    private sealed class FakeFullFlowGateway
    {
        private readonly JsonObject _completion = Completion();
        private readonly List<JsonObject> _tasks = [];
        private long _graphRevision = 3;
        private long _configurationRevision = 1;
        private JsonArray _confirmedCasting = [];

        public FakeFullFlowGateway()
        {
            Run = new Client(this, owner: false);
            Owner = new Client(this, owner: true);
        }

        public string? Scenario { get; init; }
        public IGatewayClient Run { get; }
        public IGatewayClient Owner { get; }
        public List<string> OwnerCalls { get; } = [];
        public JsonObject? ConfirmRequest { get; private set; }
        public int UsageReads { get; private set; }

        private JsonArray DraftCasting() => Scenario switch
        {
            "duplicate-role" => [Cast("a1", "backend", 0), Cast("a2", "backend", 1)],
            "reserved-role" => [Cast("a1", "backend", 0), Cast("a2", "scribe", 1)],
            "unknown-role" => [Cast("a1", "backend", 0), Cast("a2", "pilot", 1)],
            "one-agent" => [Cast("a1", "backend", 0)],
            _ => [Cast("a1", "backend", 0), Cast("a2", "tester", 1)],
        };

        private static JsonObject Cast(string agentId, string role, long order) =>
            new() { ["agentId"] = agentId, ["role"] = role, ["order"] = order };

        private JsonObject Proposal(string state) => new()
        {
            ["proposalId"] = "22222222-2222-4222-8222-222222222222",
            ["projectId"] = ProjectId,
            ["baseConfigurationRevision"] = 1L,
            ["draftRevision"] = 1L,
            ["state"] = state,
            ["draft"] = new JsonObject { ["casting"] = DraftCasting() },
            ["confirmedConfigurationRevision"] = state == "confirmed" ? 2L : null,
            ["generation"] = new JsonObject
            {
                ["requestId"] = _completion["requestId"]?.DeepClone(),
                ["mode"] = "formulate",
                ["state"] = "completed",
                ["failureCode"] = null,
                ["nativeCompletion"] = _completion.DeepClone(),
            },
        };

        private JsonObject Graph() => new()
        {
            ["projectId"] = ProjectId,
            ["revision"] = _graphRevision,
            ["tasks"] = new JsonArray(_tasks.Select(task => (JsonNode)task.DeepClone()).ToArray()),
        };

        private JsonNode Handle(string operationId, GatewayInvocation invocation, bool owner)
        {
            if (owner)
                OwnerCalls.Add(operationId);
            switch (operationId)
            {
                case "formulateCastingProposal":
                    return Proposal("pending");
                case "readSessionTurnContent":
                    string eventId = invocation.PathParameters!["eventId"];
                    bool user = eventId == _completion["userMaterial"]!["eventId"]!.GetValue<string>();
                    return new JsonObject
                    {
                        ["eventId"] = eventId,
                        ["identity"] = new JsonObject { ["projectId"] = ProjectId, ["runId"] = RunId, ["sessionId"] = SessionId },
                        ["text"] = user ? Prompt : Output,
                    };
                case "readSessionTree":
                    return new JsonObject
                    {
                        ["rootSessionId"] = SessionId,
                        ["nodes"] = new JsonArray(new JsonObject
                        {
                            ["identity"] = new JsonObject { ["projectId"] = ProjectId, ["runId"] = RunId, ["sessionId"] = SessionId },
                            ["parentSessionId"] = null,
                            ["kind"] = "coordinator",
                            ["lifecycle"] = "active",
                            ["executionFence"] = 7L,
                        }),
                    };
                case "listProjectCastingRoles":
                    return new JsonArray(
                        new JsonObject { ["id"] = "backend" },
                        new JsonObject { ["id"] = "tester" },
                        new JsonObject { ["id"] = "scribe" });
                case "confirmProjectCastingProposal":
                    ConfirmRequest = invocation.Body!.DeepClone().AsObject();
                    if (Scenario != "config-not-updated")
                    {
                        _configurationRevision = 2;
                        _confirmedCasting = DraftCasting();
                    }
                    return Proposal(Scenario == "confirm-not-confirmed" ? "pending" : "confirmed");
                case "getProjectConfiguration":
                    return new JsonObject
                    {
                        ["projectId"] = ProjectId,
                        ["revision"] = _configurationRevision,
                        ["configuration"] = new JsonObject { ["casting"] = _confirmedCasting.DeepClone() },
                    };
                case "readProjectBacklog":
                    return Graph();
                case "captureBacklogTask":
                    JsonObject body = invocation.Body!.AsObject();
                    Assert.Equal(_graphRevision, body["expectedGraphRevision"]!.GetValue<long>());
                    _graphRevision++;
                    if (Scenario != "task-missing")
                        _tasks.Add(new JsonObject
                        {
                            ["reference"] = new JsonObject { ["projectId"] = ProjectId, ["taskId"] = body["taskId"]!.DeepClone() },
                            ["revision"] = 1L,
                            ["state"] = "backlog",
                            ["isArchived"] = false,
                            ["title"] = body["content"]!["title"]!.DeepClone(),
                        });
                    return Graph();
                case "archiveProjectBacklogTask":
                    JsonObject archive = invocation.Body!.AsObject();
                    Assert.Equal(_graphRevision, archive["expectedGraphRevision"]!.GetValue<long>());
                    Assert.Equal(1L, archive["expectedTaskRevision"]!.GetValue<long>());
                    _graphRevision++;
                    if (Scenario != "task-not-archived")
                    {
                        _tasks[0]["isArchived"] = true;
                        _tasks[0]["revision"] = 2L;
                    }
                    return Graph();
                case "readRunUsage":
                    UsageReads++;
                    bool priced = Scenario != "never-priced" && UsageReads > 1;
                    return new JsonObject
                    {
                        ["projectId"] = ProjectId,
                        ["runId"] = RunId,
                        ["events"] = UsageReads,
                        ["isFullyPriced"] = Scenario != "not-fully-priced",
                        ["amounts"] = priced
                            ? new JsonArray(new JsonObject
                            {
                                ["meterSource"] = "byok.tokens",
                                ["unit"] = "USD",
                                ["amount"] = Scenario == "over-budget" ? 5.01m : 0.02m,
                                ["pricedEvents"] = 1L,
                                ["unpricedEvents"] = 0L,
                            })
                            : new JsonArray(),
                    };
                default:
                    throw new InvalidOperationException($"Unexpected operation {operationId}.");
            }
        }

        private sealed class Client(FakeFullFlowGateway gateway, bool owner) : IGatewayClient
        {
            public CliIdentity Identity => null!;
            public IReadOnlyList<JsonObject> Calls => [];
            public Task<JsonObject> DiscoverAsync(CancellationToken ct) => throw new NotSupportedException();
            public Task<JsonObject> ValidateAuthorizationContextAsync(CancellationToken ct) => throw new NotSupportedException();

            public Task<GatewayResponse> InvokeAsync(string operationId, GatewayInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new GatewayResponse(200, gateway.Handle(operationId, invocation, owner),
                    new Dictionary<string, string>(), new JsonObject()));

            public Task<GatewayResponse> InvokeRawAsync(
                string method, string url, byte[]? body, IReadOnlyDictionary<string, string>? headers,
                bool outputRequested, Func<string, ValueTask>? onEventChunk, CancellationToken ct) =>
                throw new NotSupportedException();
        }
    }
}
