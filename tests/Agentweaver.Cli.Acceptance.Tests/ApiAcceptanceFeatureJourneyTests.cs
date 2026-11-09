using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Xunit;

namespace Agentweaver.Cli.Acceptance.Tests;

public sealed class ApiAcceptanceFeatureJourneyTests
{
    [Theory]
    [InlineData(2, "passed")]
    [InlineData(3, "failed")]
    public async Task SessionHistoryMetricRequiresExactArchivedInclusiveDetachedExclusiveCount(
        int metricCount,
        string expectedStatus)
    {
        const string projectId = "project-1";
        const string runId = "run-1";
        const string rootSessionId = "root-1";
        const long executionFence = 7;
        var api = new MetricGatewayClient(projectId, runId, rootSessionId, executionFence, metricCount);
        var fixture = new AcceptanceFixture(
            projectId,
            runId,
            rootSessionId,
            rootSessionId,
            executionFence,
            1,
            null,
            null,
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            null);
        var manifest = new AcceptanceManifest(
            new JsonObject(),
            new JsonObject
            {
                ["actorId"] = "actor-1",
                ["tenantId"] = "tenant-1",
                ["issuer"] = "https://broker.example/",
            },
            new JsonObject(),
            new JsonObject(),
            "session-history-metric",
            "manifest.json",
            ".");
        List<JsonObject> steps = [];
        JsonObject observations = [];

        (string status, string? failure) = await ApiAcceptanceJourney.RunAsync(
            manifest,
            fixture,
            api,
            new JsonObject(),
            steps,
            observations,
            _ => "idempotency",
            CancellationToken.None);

        Assert.True(status == expectedStatus, $"Expected {expectedStatus}; got {status}: {failure}");
        if (expectedStatus == "passed")
        {
            Assert.Null(failure);
            Assert.Equal(2, observations["sessionHistoryMetric"]?["sessionHistoryRecordsTotal"]?.GetValue<int>());
            Assert.Equal(1, observations["sessionHistoryMetric"]?["runsTotal"]?.GetValue<int>());
            Assert.Equal(0, observations["sessionHistoryMetric"]?["runsInRange"]?.GetValue<int>());
            Assert.DoesNotContain(steps, step => step["id"]?.GetValue<string>() == "valid-denied-actor-403");
        }
        else
        {
            Assert.Contains("archived-inclusive, detached-exclusive", failure);
        }

        int advance = api.Calls.FindIndex(call => call.Operation == "advanceTurnBoundary");
        int complete = api.Calls.FindIndex(call => call.Operation == "completeTurn");
        int archive = api.Calls.FindIndex(call => call.Operation == "archiveChild");
        int detach = api.Calls.FindIndex(call => call.Operation == "detachSession");
        Assert.True(advance >= 0 && advance < complete && complete < archive && archive < detach);
        int[] childStatusReads = api.Calls
            .Select((call, index) => (call, index))
            .Where(item => item.call.Operation == "readSessionStatus"
                && item.call.Invocation.PathParameters?["sessionId"] == api.ArchivedChildId)
            .Select(item => item.index)
            .ToArray();
        Assert.Equal(3, childStatusReads.Length);
        Assert.True(advance < childStatusReads[1] && childStatusReads[1] < complete && complete < childStatusReads[2]);
        Assert.Equal("completed", api.Calls[complete].Invocation.Body?["completion"]?.GetValue<string>());
        Assert.Equal(executionFence, api.Calls[archive].Invocation.Body?["executionFence"]?.GetValue<long>());
        Assert.Equal(executionFence, api.Calls[detach].Invocation.Body?["executionFence"]?.GetValue<long>());
        var metricCall = Assert.Single(api.Calls, call => call.Operation == "readProjectMetrics");
        Assert.Equal("2000-01-01", metricCall.Invocation.Query?["from"]);
        Assert.Equal("2000-01-02", metricCall.Invocation.Query?["to"]);
        Assert.Equal("day", metricCall.Invocation.Query?["bucket"]);
    }

    [Fact]
    public async Task BoardArchivedTaskDenialKeepsGraphAndVerifiesIndependentCleanup()
    {
        const string invocationId = "22222222-2222-4222-8222-222222222222";
        const string projectId = "11111111-1111-4111-8111-111111111111";
        var api = new ArchivedBoardGatewayClient(projectId);
        var manifest = new AcceptanceManifest(
            new JsonObject(),
            new JsonObject
            {
                ["issuer"] = "https://broker.example/",
                ["tenantId"] = "tenant-1",
            },
            new JsonObject(),
            new JsonObject { ["kind"] = "owned-disposable-board" },
            "board-archived-denial",
            "manifest.json",
            ".");
        List<JsonObject> steps = [];
        JsonObject observations = [];

        BoardExecutionResult result = await ApiAcceptanceBoard.RunAsync(
            manifest,
            invocationId,
            api,
            new UnusedGatewayClientFactory(),
            new UnusedAuthentication(),
            InteractionPolicy.Never,
            steps,
            observations,
            CancellationToken.None);

        Assert.True(result.Status == "passed", $"Expected passed; got {result.Status}: {result.Failure}");
        Assert.Null(result.Failure);
        Assert.Equal(409, observations["archivedTaskGuard"]?["deleteStatus"]?.GetValue<int>());
        Assert.Equal("taskNotEditable", observations["archivedTaskGuard"]?["deleteIssueCode"]?.GetValue<string>());
        Assert.Equal(true, observations["archivedTaskGuard"]?["persistedGraphUnchanged"]?.GetValue<bool>());
        Assert.Equal("cleaned", result.Fixture["cleanup"]?["state"]?.GetValue<string>());
        Assert.Equal("archived", result.Fixture["cleanup"]?["projectReadback"]?.GetValue<string>());
        Assert.Equal("empty", result.Fixture["cleanup"]?["assignmentsReadback"]?.GetValue<string>());
        Assert.Equal("not-attempted-archived-retained", result.Fixture["cleanup"]?["taskDeletion"]?.GetValue<string>());
        Assert.Contains(steps, step => step["id"]?.GetValue<string>() == "delete-archived-task-denied"
            && step["status"]?.GetValue<string>() == "passed"
            && step["httpStatus"]?.GetValue<int>() == 409
            && step["ownerIssueCode"]?.GetValue<string>() == "taskNotEditable");
        Assert.Contains(steps, step => step["id"]?.GetValue<string>() == "claimed-task-denial"
            && step["status"]?.GetValue<string>() == "blocked");
        Assert.Equal(409, api.LastDeleteStatus);
        Assert.Single(api.Operations, operation => operation == "deleteProjectBacklogTask");
        Assert.Contains("updateProject", api.Operations);
        Assert.Contains("revokeProjectRoleAssignment", api.Operations);
        Assert.Contains("getProject", api.Operations);
        Assert.Contains("listProjectRoleAssignments", api.Operations);
        Assert.DoesNotContain("assignProjectRole", api.Operations);
    }

    private sealed class MetricGatewayClient(
        string projectId,
        string runId,
        string rootSessionId,
        long executionFence,
        int metricCount) : IGatewayClient
    {
        private readonly bool[] _registered = new bool[2];
        private bool _turnCompleted;
        private long _archivedChildStateVersion = 1;
        private bool _archived;
        private bool _detached;

        private readonly string _archivedChildId = ApiAcceptanceSupport.StableUuid(runId, "metric-archived-child");
        private readonly string _detachedChildId = ApiAcceptanceSupport.StableUuid(runId, "metric-detached-child");
        public string ArchivedChildId => _archivedChildId;
        public CliIdentity Identity { get; } = new(
            new CliTarget(
                new Uri("https://gateway.example/"),
                new Uri("https://broker.example/"),
                "agentweaver-cli",
                "https://broker.example/",
                "tenant-1"),
            ["projects.orchestrator"],
            projectId,
            runId);

        public List<(string Operation, GatewayInvocation Invocation)> Calls { get; } = [];
        public IReadOnlyList<JsonObject> CallsEvidence => [];
        IReadOnlyList<JsonObject> IGatewayClient.Calls => CallsEvidence;

        public Task<JsonObject> DiscoverAsync(CancellationToken ct)
        {
            JsonObject paths = [];
            foreach (string operation in new[]
            {
                "readRunStatus", "readSessionStatus", "readDecisions", "registerChild",
                "advanceTurnBoundary", "completeTurn", "archiveChild", "detachSession",
                "readSessionTree", "readProjectMetrics",
            })
            {
                paths[$"/api/{operation}"] = new JsonObject
                {
                    ["get"] = new JsonObject { ["operationId"] = operation },
                };
            }
            return Task.FromResult(new JsonObject { ["paths"] = paths });
        }

        public Task<JsonObject> ValidateAuthorizationContextAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GatewayResponse> InvokeAsync(
            string operationId,
            GatewayInvocation invocation,
            CancellationToken ct)
        {
            Calls.Add((operationId, invocation));
            JsonObject body = operationId switch
            {
                "readRunStatus" => RunStatus(),
                "readSessionStatus" => SessionStatus(invocation.PathParameters?["sessionId"] ?? rootSessionId),
                "readDecisions" => new JsonObject
                {
                    ["executionFence"] = executionFence,
                    ["stateVersion"] = 1L,
                },
                "registerChild" => RegisterChild(invocation),
                "advanceTurnBoundary" => AdvanceTurnBoundary(),
                "completeTurn" => CompleteTurn(),
                "archiveChild" => ArchiveChild(),
                "detachSession" => DetachChild(),
                "readSessionTree" => ReadTree(),
                "readProjectMetrics" => Metrics(invocation),
                _ => throw new InvalidOperationException($"Unexpected Gateway operation: {operationId}"),
            };
            return Task.FromResult(new GatewayResponse(
                operationId == "registerChild" ? 201 : 200,
                body,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new JsonObject()));
        }

        public Task<GatewayResponse> InvokeRawAsync(
            string method,
            string url,
            byte[]? body,
            IReadOnlyDictionary<string, string>? headers,
            bool outputRequested,
            Func<string, ValueTask>? onEventChunk,
            CancellationToken ct) =>
            throw new NotSupportedException();

        private JsonObject RunStatus() => new()
        {
            ["projectId"] = projectId,
            ["runId"] = runId,
            ["rootSessionId"] = rootSessionId,
            ["executionFence"] = executionFence,
            ["stateVersion"] = 1L,
        };

        private JsonObject SessionStatus(string sessionId) => new()
        {
            ["identity"] = new JsonObject
            {
                ["projectId"] = projectId,
                ["runId"] = runId,
                ["sessionId"] = sessionId,
            },
            ["rootSessionId"] = rootSessionId,
            ["executionFence"] = executionFence,
            ["stateVersion"] = sessionId == _archivedChildId ? _archivedChildStateVersion : 1L,
            ["lifecycle"] = sessionId == _archivedChildId && _turnCompleted ? "completed" : "active",
        };

        private JsonObject RegisterChild(GatewayInvocation invocation)
        {
            string childId = invocation.Body?["sessionId"]?.GetValue<string>()
                ?? throw new InvalidOperationException("The child session ID is missing.");
            int index = childId == _archivedChildId ? 0 : childId == _detachedChildId ? 1 : -1;
            if (index < 0)
                throw new InvalidOperationException("The runner registered an unexpected child session.");
            _registered[index] = true;
            return new JsonObject
            {
                ["identity"] = new JsonObject
                {
                    ["projectId"] = projectId,
                    ["runId"] = runId,
                    ["sessionId"] = childId,
                },
            };
        }

        private JsonObject AdvanceTurnBoundary()
        {
            _archivedChildStateVersion += 2;
            return new JsonObject
            {
                ["session"] = new JsonObject { ["sessionId"] = _archivedChildId },
                ["stateVersion"] = _archivedChildStateVersion,
                ["executionFence"] = 0L,
            };
        }

        private JsonObject CompleteTurn()
        {
            _turnCompleted = true;
            _archivedChildStateVersion++;
            return new JsonObject
            {
                ["session"] = new JsonObject { ["sessionId"] = _archivedChildId },
                ["executionFence"] = 0L,
                ["executionState"] = "completed",
                ["stateVersion"] = _archivedChildStateVersion,
            };
        }

        private JsonObject ArchiveChild()
        {
            _archived = true;
            return new JsonObject
            {
                ["command"] = "archive",
                ["sourceSessionId"] = rootSessionId,
                ["targetSessionId"] = _archivedChildId,
                ["state"] = "archived",
                ["executionFence"] = executionFence,
            };
        }

        private JsonObject DetachChild()
        {
            _detached = true;
            return new JsonObject
            {
                ["command"] = "detach",
                ["sourceSessionId"] = _detachedChildId,
                ["state"] = "detached",
                ["executionFence"] = executionFence,
            };
        }

        private JsonObject ReadTree()
        {
            var nodes = new JsonArray(Node(rootSessionId, "active"));
            if (_registered[0])
                nodes.Add(Node(_archivedChildId, _archived ? "archived" : "active"));
            if (_registered[1] && !_detached)
                nodes.Add(Node(_detachedChildId, "active"));
            return new JsonObject
            {
                ["rootSessionId"] = rootSessionId,
                ["nodes"] = nodes,
            };
        }

        private JsonObject Node(string sessionId, string lifecycle) => new()
        {
            ["identity"] = new JsonObject
            {
                ["projectId"] = projectId,
                ["runId"] = runId,
                ["sessionId"] = sessionId,
            },
            ["lifecycle"] = lifecycle,
        };

        private JsonObject Metrics(GatewayInvocation invocation)
        {
            DateTimeOffset from = DateTimeOffset.Parse(
                invocation.Query?["from"] ?? throw new InvalidOperationException("The metrics request omitted from."),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal);
            DateTimeOffset to = DateTimeOffset.Parse(
                invocation.Query?["to"] ?? throw new InvalidOperationException("The metrics request omitted to."),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal);
            return new JsonObject
            {
                ["projectId"] = projectId,
                ["from"] = JsonSerializer.SerializeToNode(from),
                ["to"] = JsonSerializer.SerializeToNode(to),
                ["runsInRange"] = 0L,
                ["runsTotal"] = 1L,
                ["sessionHistoryRecordsTotal"] = (long)metricCount,
            };
        }
    }

    private sealed class ArchivedBoardGatewayClient(string projectId) : IGatewayClient
    {
        private const string ActorId = "actor-1";
        private const string OwnerAssignmentId = "33333333-3333-4333-8333-333333333333";
        private JsonObject _graph = new()
        {
            ["projectId"] = projectId,
            ["revision"] = 0L,
            ["tasks"] = new JsonArray(),
            ["dependencies"] = new JsonArray(),
        };
        private string? _projectName;
        private string? _taskId;
        private long _taskRevision;
        private long _graphRevision;
        private bool _taskArchived;
        private bool _projectArchived;
        private bool _ownerRevoked;
        private bool _ownerAssigned;

        public CliIdentity Identity { get; } = new(
            new CliTarget(
                new Uri("https://gateway.example/"),
                new Uri("https://broker.example/"),
                "agentweaver-cli",
                "https://broker.example/",
                "tenant-1"),
            ["projects.admin"]);

        public List<string> Operations { get; } = [];
        public int LastDeleteStatus { get; private set; }
        IReadOnlyList<JsonObject> IGatewayClient.Calls => [];

        public Task<JsonObject> DiscoverAsync(CancellationToken ct)
        {
            JsonObject paths = [];
            foreach (string operation in new[]
            {
                "getAuthorizationContext", "createProject", "getProject", "assignProjectRole",
                "updateProject", "revokeProjectRoleAssignment", "listProjectRoleAssignments",
                "readProjectBacklog", "captureBacklogTask", "archiveProjectBacklogTask",
                "deleteProjectBacklogTask",
            })
            {
                paths[$"/api/{operation}"] = new JsonObject
                {
                    ["get"] = new JsonObject { ["operationId"] = operation },
                };
            }
            return Task.FromResult(new JsonObject { ["paths"] = paths });
        }

        public Task<JsonObject> ValidateAuthorizationContextAsync(CancellationToken ct)
        {
            JsonArray permissions = new();
            if (_ownerAssigned)
            {
                permissions.Add(new JsonObject { ["permission"] = "writeProjects", ["roleRevision"] = 1L });
                permissions.Add(new JsonObject { ["permission"] = "readProjects", ["roleRevision"] = 1L });
            }
            JsonObject? project = !_ownerAssigned ? null : new JsonObject
            {
                ["resourceType"] = "project",
                ["resourceId"] = projectId,
                ["permissions"] = permissions,
            };
            return Task.FromResult(new JsonObject
            {
                ["contractVersion"] = 1L,
                ["issuer"] = "https://broker.example/",
                ["tenantId"] = "tenant-1",
                ["actorId"] = ActorId,
                ["membershipRevision"] = 1L,
                ["boundProjectId"] = null,
                ["boundRunId"] = null,
                ["effectiveAuthority"] = project is null ? new JsonArray() : new JsonArray(project),
            });
        }

        public Task<GatewayResponse> InvokeAsync(
            string operationId,
            GatewayInvocation invocation,
            CancellationToken ct)
        {
            Operations.Add(operationId);
            JsonObject body = operationId switch
            {
                "createProject" => CreateProject(invocation),
                "getProject" => Project(),
                "assignProjectRole" => AssignOwner(),
                "updateProject" => ArchiveProject(invocation),
                "revokeProjectRoleAssignment" => RevokeOwner(),
                "listProjectRoleAssignments" => ListAssignments(),
                "readProjectBacklog" => _graph.DeepClone() as JsonObject ?? [],
                "captureBacklogTask" => CaptureTask(invocation),
                "archiveProjectBacklogTask" => ArchiveTask(invocation),
                "deleteProjectBacklogTask" => RejectArchivedDelete(),
                _ => throw new InvalidOperationException($"Unexpected Board Gateway operation: {operationId}"),
            };
            int status = operationId switch
            {
                "createProject" or "assignProjectRole" => 201,
                "deleteProjectBacklogTask" => 409,
                _ => 200,
            };
            return Task.FromResult(new GatewayResponse(
                status,
                body,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new JsonObject()));
        }

        public Task<GatewayResponse> InvokeRawAsync(
            string method,
            string url,
            byte[]? body,
            IReadOnlyDictionary<string, string>? headers,
            bool outputRequested,
            Func<string, ValueTask>? onEventChunk,
            CancellationToken ct) =>
            throw new NotSupportedException();

        private JsonObject CreateProject(GatewayInvocation invocation)
        {
            _projectName = invocation.Body?["name"]?.GetValue<string>();
            _ownerAssigned = true;
            return new JsonObject
            {
                ["projectId"] = projectId,
                ["name"] = _projectName,
                ["state"] = "active",
                ["revision"] = 1L,
                ["configurationRevision"] = 1L,
            };
        }

        private JsonObject AssignOwner()
        {
            _ownerAssigned = true;
            return new JsonObject
            {
                ["assignmentId"] = OwnerAssignmentId,
                ["projectId"] = projectId,
                ["principalId"] = ActorId,
                ["role"] = "owner",
                ["state"] = "active",
                ["revision"] = 1L,
                ["grantedBy"] = ActorId,
            };
        }

        private JsonObject Project() => new()
        {
            ["projectId"] = projectId,
            ["name"] = _projectName,
            ["state"] = _projectArchived ? "archived" : "active",
            ["revision"] = _projectArchived ? 2L : 1L,
            ["configurationRevision"] = 1L,
        };

        private JsonObject ListAssignments()
        {
            return new JsonObject
            {
                ["projectId"] = projectId,
                ["assignments"] = _ownerRevoked
                    ? new JsonArray()
                    : new JsonArray(new JsonObject
                    {
                        ["assignmentId"] = OwnerAssignmentId,
                        ["principalId"] = ActorId,
                        ["role"] = "owner",
                        ["revision"] = 1L,
                        ["createdBy"] = ActorId,
                    }),
            };
        }

        private JsonObject ArchiveProject(GatewayInvocation invocation)
        {
            _projectArchived = true;
            return new JsonObject
            {
                ["projectId"] = projectId,
                ["name"] = invocation.Body?["name"]?.DeepClone(),
                ["state"] = "archived",
                ["revision"] = 2L,
                ["configurationRevision"] = 1L,
            };
        }

        private JsonObject RevokeOwner()
        {
            _ownerRevoked = true;
            return new JsonObject
            {
                ["assignmentId"] = OwnerAssignmentId,
                ["projectId"] = projectId,
                ["principalId"] = ActorId,
                ["role"] = "owner",
                ["state"] = "revoked",
                ["revision"] = 2L,
                ["grantedBy"] = ActorId,
                ["revokedBy"] = ActorId,
            };
        }

        private JsonObject CaptureTask(GatewayInvocation invocation)
        {
            _taskId = invocation.Body?["taskId"]?.GetValue<string>();
            _taskRevision = 0;
            _graphRevision = 1;
            _graph = Graph(_graphRevision, [TaskSnapshot(_taskRevision, archived: false)]);
            return _graph.DeepClone() as JsonObject ?? [];
        }

        private JsonObject ArchiveTask(GatewayInvocation invocation)
        {
            Assert.Equal(_graphRevision, invocation.Body?["expectedGraphRevision"]?.GetValue<long>());
            Assert.Equal(_taskRevision, invocation.Body?["expectedTaskRevision"]?.GetValue<long>());
            _graphRevision++;
            _taskRevision++;
            _taskArchived = true;
            _graph = Graph(_graphRevision, [TaskSnapshot(_taskRevision, archived: true)]);
            return _graph.DeepClone() as JsonObject ?? [];
        }

        private JsonObject RejectArchivedDelete()
        {
            LastDeleteStatus = 409;
            Assert.True(_taskArchived);
            return new JsonObject
            {
                ["issues"] = new JsonArray(new JsonObject { ["code"] = "taskNotEditable" }),
            };
        }

        private JsonObject TaskSnapshot(long revision, bool archived) => new()
        {
            ["reference"] = new JsonObject { ["projectId"] = projectId, ["taskId"] = _taskId },
            ["revision"] = revision,
            ["state"] = "backlog",
            ["isArchived"] = archived,
            ["isAutomationInvocationPending"] = false,
            ["position"] = 0L,
            ["title"] = "Archived task guard",
            ["description"] = "Invocation-owned archived-task deletion guard.",
        };

        private JsonObject Graph(long revision, JsonArray tasks) => new()
        {
            ["projectId"] = projectId,
            ["revision"] = revision,
            ["tasks"] = tasks,
            ["dependencies"] = new JsonArray(),
        };
    }

    private sealed class UnusedGatewayClientFactory : IGatewayClientFactory
    {
        public IGatewayClient Create(CliIdentity identity) => throw new NotSupportedException();
    }

    private sealed class UnusedAuthentication : ICliAuthentication
    {
        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) => throw new NotSupportedException();
        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) => throw new NotSupportedException();
        public Task LogoutAsync(CliIdentity identity, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetBearerAsync(CliIdentity identity, InteractionPolicy interaction, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
