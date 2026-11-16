extern alias OrchestratorHost;

using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.IdentityModel.Tokens;
using OrchestratorHost::Agentweaver.Orchestrator;
using Xunit;
using Xunit.Sdk;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayCommittedRecoveryOpenApiTests
{
    private const string Path = "/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/status";

    [Theory]
    [InlineData("step", "effectRecoveryState", "reconcileCommitted")]
    [InlineData("operation", "evidenceState", "committedAwaitingReceipt")]
    [InlineData("operation", "effectRecoveryState", "reconcileCommitted")]
    public async Task ProjectedCommittedValueIsInServedEnum(string kind, string field, string expected)
    {
        var (snapshot, _) = ProjectCommittedRecovery();
        using var document = await ReadOpenApi();
        var (stepSchema, operationSchema) = ResolveSchemas(document.RootElement);
        var selected = Select(snapshot, kind, "publish");
        Assert.Equal(expected, selected.GetProperty(field).GetString());
        ValidateEnum(selected, kind == "step" ? stepSchema : operationSchema, field);
    }

    [Fact]
    public async Task CommittedProjectionMatchesServedStepAndOperationContracts()
    {
        var (snapshot, operationId) = ProjectCommittedRecovery();
        using var document = await ReadOpenApi();
        var (stepSchema, operationSchema) = ResolveSchemas(document.RootElement);
        var step = Select(snapshot, "step", "publish");
        var operation = Select(snapshot, "operation", "publish");

        Assert.Equal("publish", step.GetProperty("stepId").GetString());
        Assert.Equal("running", step.GetProperty("checkpointStatus").GetString());
        Assert.Equal(JsonValueKind.Null, step.GetProperty("readiness").ValueKind);
        Assert.Equal("publish", operation.GetProperty("stepId").GetString());
        Assert.Equal("openPullRequest", operation.GetProperty("gate").GetString());
        Assert.Equal(operationId, operation.GetProperty("operationId").GetGuid());

        Assert.Equal(new[] { "checkpointStatus", "stepId" }, Required(stepSchema).Order().ToArray());
        Assert.Equal(new[] { "evidenceState", "gate", "operationId", "stepId" },
            Required(operationSchema).Order().ToArray());
        ValidateStep(step, stepSchema);
        ValidateOperation(operation, operationSchema);

        AssertEnumValues(stepSchema, "effectRecoveryState",
            "mayRepeat", "indeterminate", "checkpointResultRecorded", "terminalReceiptRecorded", "reconcileCommitted", "awaitingApproval");
        AssertEnumValues(operationSchema, "gate",
            "buildTest", "openPullRequest", "scribe", "merge", "independentReview", "rubberDuck");
        AssertEnumValues(operationSchema, "evidenceState",
            "intentOnly", "terminalReceiptRecorded", "committedAwaitingReceipt");
        AssertEnumValues(operationSchema, "effectRecoveryState", "mayRepeat", "reconcileCommitted", "awaitingApproval");

        foreach (var field in new[] { "workItemId", "associationId", "readiness", "effectRecoveryState" })
            Assert.DoesNotContain(field, Required(stepSchema));
        Assert.DoesNotContain("effectRecoveryState", Required(operationSchema));

        foreach (var field in Required(stepSchema))
        {
            AssertRejected(step, field, remove: true, obj => ValidateStep(obj, stepSchema));
            AssertRejected(step, field, remove: false, obj => ValidateStep(obj, stepSchema));
        }
        foreach (var field in Required(operationSchema))
        {
            AssertRejected(operation, field, remove: true, obj => ValidateOperation(obj, operationSchema));
            AssertRejected(operation, field, remove: false, obj => ValidateOperation(obj, operationSchema));
        }
        foreach (var field in new[] { "checkpointStatus", "readiness", "effectRecoveryState" })
            AssertRejected(step, field, "invalid-state", obj => ValidateStep(obj, stepSchema));
        foreach (var field in new[] { "gate", "evidenceState", "effectRecoveryState" })
            AssertRejected(operation, field, "invalid-state", obj => ValidateOperation(obj, operationSchema));

        foreach (var field in new[] { "workItemId", "associationId", "readiness", "effectRecoveryState" })
        {
            ValidateStep(Changed(step, field, remove: true), stepSchema);
            ValidateStep(Changed(step, field, remove: false), stepSchema);
        }
        ValidateOperation(Changed(operation, "effectRecoveryState", remove: true), operationSchema);
        ValidateOperation(Changed(operation, "effectRecoveryState", remove: false), operationSchema);
        AssertRejected(operation, "operationId", "not-a-uuid", obj => ValidateOperation(obj, operationSchema));
    }

    [Fact]
    public async Task PendingReviewStepApprovalStateIsInServedEnum()
    {
        var (snapshot, _) = ProjectPendingReview();
        using var document = await ReadOpenApi();
        var (stepSchema, _) = ResolveSchemas(document.RootElement);
        var step = Select(snapshot, "step", "review");

        Assert.Equal("awaitingApproval", step.GetProperty("effectRecoveryState").GetString());
        ValidateEnum(step, stepSchema, "effectRecoveryState");
    }

    [Fact]
    public async Task PendingReviewOperationApprovalStateIsInServedEnum()
    {
        var (snapshot, intent) = ProjectPendingReview();
        using var document = await ReadOpenApi();
        var (_, operationSchema) = ResolveSchemas(document.RootElement);
        var operation = Select(snapshot, "operation", "review");

        Assert.Equal("independentReview", operation.GetProperty("gate").GetString());
        Assert.Equal("awaitingApproval", operation.GetProperty("effectRecoveryState").GetString());
        Assert.Equal("intentOnly", operation.GetProperty("evidenceState").GetString());
        Assert.Equal(intent.OperationId, operation.GetProperty("operationId").GetGuid());
        Assert.Equal(intent.ApprovalRequestId, operation.GetProperty("approvalRequestId").GetString());
        ValidateEnum(operation, operationSchema, "effectRecoveryState");
    }

    [Fact]
    public async Task PendingReviewGateIsInServedEnum()
    {
        var (snapshot, _) = ProjectPendingReview();
        using var document = await ReadOpenApi();
        var (_, operationSchema) = ResolveSchemas(document.RootElement);
        var operation = Select(snapshot, "operation", "review");

        Assert.Equal("independentReview", operation.GetProperty("gate").GetString());
        ValidateEnum(operation, operationSchema, "gate");
    }

    [Fact]
    public async Task ScribeGateIsInServedEnum()
    {
        var (snapshot, operationId) = ProjectScribeRecovery();
        using var document = await ReadOpenApi();
        var (_, operationSchema) = ResolveSchemas(document.RootElement);
        var step = Select(snapshot, "step", "scribe");
        var operation = Select(snapshot, "operation", "scribe");

        Assert.Equal("mayRepeat", step.GetProperty("effectRecoveryState").GetString());
        Assert.Equal("scribe", operation.GetProperty("gate").GetString());
        Assert.Equal("intentOnly", operation.GetProperty("evidenceState").GetString());
        Assert.Equal("mayRepeat", operation.GetProperty("effectRecoveryState").GetString());
        Assert.Equal(operationId, operation.GetProperty("operationId").GetGuid());
        ValidateEnum(operation, operationSchema, "gate");
    }

    [Fact]
    public async Task MergeGateIsInServedEnum()
    {
        var (snapshot, operationId) = ProjectMergeRecovery();
        using var document = await ReadOpenApi();
        var (_, operationSchema) = ResolveSchemas(document.RootElement);
        var step = Select(snapshot, "step", "merge");
        var operation = Select(snapshot, "operation", "merge");

        Assert.Equal("mayRepeat", step.GetProperty("effectRecoveryState").GetString());
        Assert.Equal("merge", operation.GetProperty("gate").GetString());
        Assert.Equal("intentOnly", operation.GetProperty("evidenceState").GetString());
        Assert.Equal("mayRepeat", operation.GetProperty("effectRecoveryState").GetString());
        Assert.Equal(operationId, operation.GetProperty("operationId").GetGuid());
        ValidateEnum(operation, operationSchema, "gate");
    }

    [Fact]
    public async Task RubberDuckGateIsInServedEnum()
    {
        var (snapshot, operationId) = ProjectRubberduckRecovery();
        using var document = await ReadOpenApi();
        var (_, operationSchema) = ResolveSchemas(document.RootElement);
        var step = Select(snapshot, "step", "rubberduck");
        var operation = Select(snapshot, "operation", "rubberduck");

        Assert.Equal("mayRepeat", step.GetProperty("effectRecoveryState").GetString());
        Assert.Equal("rubberDuck", operation.GetProperty("gate").GetString());
        Assert.Equal("intentOnly", operation.GetProperty("evidenceState").GetString());
        Assert.Equal("mayRepeat", operation.GetProperty("effectRecoveryState").GetString());
        Assert.Equal(operationId, operation.GetProperty("operationId").GetGuid());
        ValidateEnum(operation, operationSchema, "gate");
    }

    [Fact]
    public async Task CompletedReviewReceiptMatchesServedSchema()
    {
        var fixture = CreateReviewFixture(CreatePlan(("review", WorkflowPlatformGate.IndependentReview)), "review");
        var complete = CompleteReview(fixture);
        var snapshot = SerializeRecovery(complete, fixture.Plan.Plan, 5, "review-terminal");
        using var document = await ReadOpenApi();
        var (stepSchema, operationSchema) = ResolveSchemas(document.RootElement);
        var step = Select(snapshot, "step", "review");
        var operation = Select(snapshot, "operation", "review");

        Assert.Equal("succeeded", step.GetProperty("checkpointStatus").GetString());
        Assert.Equal("terminalReceiptRecorded", step.GetProperty("effectRecoveryState").GetString());
        Assert.Equal("independentReview", operation.GetProperty("gate").GetString());
        Assert.Equal("terminalReceiptRecorded", operation.GetProperty("evidenceState").GetString());
        Assert.Equal(JsonValueKind.Null, operation.GetProperty("effectRecoveryState").ValueKind);
        ValidateStep(step, stepSchema);
        ValidateOperation(operation, operationSchema);
    }

    private static void ValidateStep(JsonElement obj, JsonElement schema)
    {
        RequireProperties(obj, schema);
        ValidateString(obj, schema, "stepId");
        foreach (var field in new[] { "workItemId", "associationId" })
            ValidateNullableString(obj, schema, field);
        foreach (var field in new[] { "checkpointStatus", "readiness", "effectRecoveryState" })
            ValidateEnum(obj, schema, field);
    }

    private static void ValidateOperation(JsonElement obj, JsonElement schema)
    {
        RequireProperties(obj, schema);
        ValidateString(obj, schema, "stepId");
        ValidateString(obj, schema, "operationId");
        var idSchema = Property(schema, "operationId");
        Assert.Equal("uuid", idSchema.GetProperty("format").GetString());
        Assert.True(Guid.TryParseExact(obj.GetProperty("operationId").GetString(), "D", out _), "operationId must be a UUID");
        foreach (var field in new[] { "gate", "evidenceState", "effectRecoveryState" })
            ValidateEnum(obj, schema, field);
    }

    private static void RequireProperties(JsonElement obj, JsonElement schema)
    {
        foreach (var field in Required(schema))
            Assert.True(obj.TryGetProperty(field, out _), $"Missing required property {field}");
    }

    private static void ValidateString(JsonElement obj, JsonElement schema, string field)
    {
        Assert.Equal("string", Property(schema, field).GetProperty("type").GetString());
        Assert.True(obj.TryGetProperty(field, out var value), $"Missing {field}");
        Assert.True(value.ValueKind == JsonValueKind.String, $"{field} must be a string");
    }

    private static void ValidateNullableString(JsonElement obj, JsonElement schema, string field)
    {
        var valueSchema = StringBranch(Property(schema, field), nullable: true);
        Assert.Equal("string", valueSchema.GetProperty("type").GetString());
        if (obj.TryGetProperty(field, out var value))
            Assert.True(value.ValueKind is JsonValueKind.String or JsonValueKind.Null, $"{field} must be a string or null");
    }

    private static void ValidateEnum(JsonElement obj, JsonElement schema, string field)
    {
        var nullable = field is "readiness" or "effectRecoveryState";
        var valueSchema = StringBranch(Property(schema, field), nullable);
        Assert.Equal("string", valueSchema.GetProperty("type").GetString());
        var values = valueSchema.GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray();
        if (!obj.TryGetProperty(field, out var value))
        {
            Assert.DoesNotContain(field, Required(schema));
            return;
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            Assert.True(nullable, $"{field} cannot be null");
            return;
        }
        Assert.True(value.ValueKind == JsonValueKind.String, $"{field} must be a string");
        Assert.True(values.Contains(value.GetString()), $"{field}: '{value.GetString()}' is absent from the served enum");
    }

    private static void AssertEnumValues(JsonElement schema, string field, params string[] expected)
    {
        var valueSchema = StringBranch(Property(schema, field), field == "effectRecoveryState");
        Assert.Equal("string", valueSchema.GetProperty("type").GetString());
        var values = valueSchema.GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray();
        foreach (var value in expected)
            Assert.Contains(value, values);
    }

    private static JsonElement StringBranch(JsonElement schema, bool nullable)
    {
        if (!nullable)
        {
            Assert.False(schema.TryGetProperty("anyOf", out _));
            return schema;
        }
        var branches = schema.GetProperty("anyOf").EnumerateArray().ToArray();
        Assert.Equal(2, branches.Length);
        Assert.Single(branches, x => x.GetProperty("type").GetString() == "null");
        return Assert.Single(branches, x => x.GetProperty("type").GetString() == "string");
    }

    private static JsonElement Property(JsonElement schema, string field) =>
        schema.GetProperty("properties").GetProperty(field);

    private static string[] Required(JsonElement schema) =>
        schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToArray();

    private static JsonElement Select(JsonElement snapshot, string kind, string stepId)
    {
        var array = snapshot.GetProperty(kind == "step" ? "steps" : "platformGateOperations");
        return Assert.Single(array.EnumerateArray(), x => x.GetProperty("stepId").GetString() == stepId);
    }

    private static JsonElement Changed(JsonElement original, string field, bool remove)
    {
        var copy = JsonNode.Parse(original.GetRawText())!.AsObject();
        if (remove) copy.Remove(field); else copy[field] = null;
        return JsonSerializer.SerializeToElement(copy);
    }

    private static JsonElement Changed(JsonElement original, string field, string value)
    {
        var copy = JsonNode.Parse(original.GetRawText())!.AsObject();
        copy[field] = value;
        return JsonSerializer.SerializeToElement(copy);
    }

    private static void AssertRejected(JsonElement original, string field, bool remove, Action<JsonElement> validate) =>
        Assert.ThrowsAny<XunitException>(() => validate(Changed(original, field, remove)));

    private static void AssertRejected(JsonElement original, string field, string value, Action<JsonElement> validate) =>
        Assert.ThrowsAny<XunitException>(() => validate(Changed(original, field, value)));

    private static (JsonElement Step, JsonElement Operation) ResolveSchemas(JsonElement root)
    {
        var operation = root.GetProperty("paths").GetProperty(Path).GetProperty("get");
        Assert.Equal("readSessionStatus", operation.GetProperty("operationId").GetString());
        var sessionRef = operation.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString();
        Assert.Equal("#/components/schemas/SessionStatusSnapshot", sessionRef);
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var branches = Property(schemas.GetProperty("SessionStatusSnapshot"), "executionRecovery")
            .GetProperty("anyOf").EnumerateArray().ToArray();
        Assert.Equal(2, branches.Length);
        Assert.Single(branches, x => x.TryGetProperty("type", out var type) && type.GetString() == "null");
        var recoveryRef = Assert.Single(branches, x => x.TryGetProperty("$ref", out _)).GetProperty("$ref").GetString();
        Assert.Equal("#/components/schemas/MafExecutionRecoverySnapshot", recoveryRef);
        var recovery = schemas.GetProperty(recoveryRef!["#/components/schemas/".Length..]);
        var stepRef = Property(recovery, "steps").GetProperty("items").GetProperty("$ref").GetString();
        var operationRef = Property(recovery, "platformGateOperations").GetProperty("items").GetProperty("$ref").GetString();
        Assert.Equal("#/components/schemas/MafExecutionRecoveryStepSnapshot", stepRef);
        Assert.Equal("#/components/schemas/MafExecutionRecoveryPlatformOperationSnapshot", operationRef);
        return (schemas.GetProperty(stepRef!["#/components/schemas/".Length..]),
            schemas.GetProperty(operationRef!["#/components/schemas/".Length..]));
    }

    private static async Task<JsonDocument> ReadOpenApi()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa.ExportParameters(true)),
            () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("Contract checks must not call an owner."));
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static (JsonElement Snapshot, Guid OperationId) ProjectCommittedRecovery()
    {
        var capabilities = ImmutableHashSet.Create(StringComparer.Ordinal, "sandbox.workspace.write");
        var sandbox = Pin(ProviderSeam.Sandbox, "sandbox.test", capabilities);
        var workflow = new WorkflowDefinition("workflow.core", "revision-1", "catalog-1",
            WorkflowDefinitionOrigin.BuiltIn, 32,
            [
                new("implement", "Open work", WorkflowStepMode.Open, 0, new(1, 8), [],
                    ["engineer", "researcher"], ["execution", "research", "validation"], ["worktree", "sandboxed"],
                    ["sandbox.workspace.write"], null, null),
                new("publish", "Publish", WorkflowStepMode.Platform, 1, new(1, 1), ["implement"],
                    [], [], [], [], null, WorkflowPlatformGate.OpenPullRequest)
            ]);
        var workflowResult = WorkflowDefinitionValidator.ValidateAndSnapshot(workflow);
        Assert.True(workflowResult.IsValid, string.Join("; ", workflowResult.Issues.Select(x => x.Message)));
        var plan = new WorkPlan("plan-1", "workflow.core", "revision-1", "catalog-1",
            [new("implementation", "implement", "Implement", "Implement the requested change.", "engineer",
                "agent-1", "execution", "model:provider/alpha", "sandbox.test", "worktree", [], ["src/implementation.cs"])]);
        var selection = new WorkPlanRunSelectionContext(
            [new RoleRunSelection("engineer", ["agent-1"], ["model:provider/alpha"], ["worktree", "sandboxed"]),
             new RoleRunSelection("researcher", ["agent-2"], ["model:provider/alpha"], ["worktree", "sandboxed"])],
            sandbox)
        {
            ModelTaskSelections = [new ModelTaskSelectionReference(ModelTaskKind.Straightforward, null, null, "model:provider/alpha")]
        };
        var planResult = WorkPlanValidator.ValidateAndSnapshot(workflowResult.Value!, plan, selection);
        Assert.True(planResult.IsValid, string.Join("; ", planResult.Issues.Select(x => x.Message)));

        var identity = new SessionIdentity("project-1", "run-1", "root");
        var hash = new string('A', 64);
        var sourceCapabilities = ImmutableHashSet.Create(StringComparer.Ordinal,
            SourceControlCapabilities.RepositoryRead, SourceControlCapabilities.RepositoryCheckout);
        var sourceBinding = Pin(ProviderSeam.SourceControl, "github", sourceCapabilities);
        var accepted = new SourceControlAcceptedRunBinding("issuer", "actor", "tenant-1", identity.ProjectId,
            identity.RunId, identity.SessionId, hash, 1, 1, 1, "context", 7);
        var pin = new SourceControlRepositoryPin("source-pin", accepted, sourceBinding, new("octo", "repo"),
            new(new SecretRef("api", "1"), SourceControlSecretPurposes.Api),
            new(new SecretRef("checkout", "1"), SourceControlSecretPurposes.Checkout),
            null, 123, "main", false, DateTimeOffset.UnixEpoch);
        var workspace = new SourceControlPullRequestWorkspaceLocator(
            "workspace", Guid.NewGuid(), new string('a', 40), "feature/agent-change", new string('c', 40), "main");
        var intent = MafOpenPullRequestContract.CreateIntent(Guid.NewGuid(), identity, "open-pr-intent", 2,
            plan.Id, "publish", 3, 7, hash, pin, workspace, 1);
        var checkpoint = new MafExecutionCheckpoint(3, plan.Id, 4,
            MafExecutionProgress.Empty with
            {
                NonModelSteps = MafExecutionProgress.Empty.NonModelSteps.Add("publish", MafExecutionTaskStatus.Running)
            })
        {
            OpenPullRequestIntents = ImmutableDictionary<string, MafExecutionOpenPullRequestIntent>.Empty
                .WithComparers(StringComparer.Ordinal).Add("publish", intent),
            RecoveryLineage = new MafCheckpointRecoveryLineage("source-checkpoint", 1, new string('a', 64), 7, Guid.NewGuid(), 8)
        };
        var projected = MafExecutionRecoveryProjector.Project(
            new MafExecutionCheckpointSnapshot(new CheckpointInfo("root", "checkpoint-3"), checkpoint),
            planResult.Value!, 4, true, null);
        Assert.Equal("available", projected.State);
        Assert.Single(projected.Steps, item => item.StepId == "publish");
        Assert.Single(projected.PlatformGateOperations, item => item.StepId == "publish");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        return (JsonSerializer.SerializeToElement(projected, options), intent.OperationId);
    }

    private static (JsonElement Snapshot, MafExecutionReviewIntent Intent) ProjectPendingReview()
    {
        var fixture = CreateReviewFixture(CreatePlan(("review", WorkflowPlatformGate.IndependentReview)), "review");
        return (SerializeRecovery(fixture.Running, fixture.Plan.Plan, 3, "review-checkpoint"), fixture.Intent);
    }

    private static (JsonElement Snapshot, Guid OperationId) ProjectScribeRecovery()
    {
        var fixture = CreatePlan(("scribe", WorkflowPlatformGate.Scribe));
        var checkpoint = CreateInitialCheckpoint(fixture.Plan, 1);
        var identity = new SessionIdentity("project-1", "run-1", "root");
        var actor = new CoordinationActor("https://identity.test/", "owner");
        const long fence = 7;
        var selectionHash = new string('a', 64);
        var intent = MafScribeGateContract.CreateIntent(
            identity, fixture.Plan, checkpoint, "scribe", fence, selectionHash, actor);
        var running = MafScribeGateContract.Prepare(
            new(new("root", "initial"), checkpoint), intent);
        return (SerializeRecovery(running, fixture.Plan, 1, "scribe-checkpoint"),
            MafScribeGateContract.OperationId(intent));
    }

    private static (JsonElement Snapshot, Guid OperationId) ProjectMergeRecovery()
    {
        var fixture = CreatePlan(("merge", WorkflowPlatformGate.Merge));
        var checkpoint = CreateInitialCheckpoint(fixture.Plan, 1);
        var identity = new SessionIdentity("project-1", "run-1", "root");
        const long fence = 7;
        var selectionHash = new string('a', 64);
        var pin = CreateSourceControlPin(identity, selectionHash, fence);
        var intent = MafMergeContract.CreateIntent(
            Guid.NewGuid(), identity, "merge-checkpoint", 2, fixture.Plan.Plan.Id,
            fixture.Plan.Workflow.Definition.Id, fixture.Plan.Workflow.Definition.Revision,
            "merge", 1, fence, selectionHash, pin, 42, new string('a', 40), SourceControlMergeMethod.Rebase);
        var running = CoordinationEndpoints.PrepareMergeCheckpoint(
            new(new("root", "initial"), checkpoint), intent)!;
        return (SerializeRecovery(running, fixture.Plan, 1, "merge-checkpoint"), intent.OperationId);
    }

    private static (JsonElement Snapshot, Guid OperationId) ProjectRubberduckRecovery()
    {
        var fixture = CreatePlan(
            ("independent-review", WorkflowPlatformGate.IndependentReview),
            ("rubberduck", WorkflowPlatformGate.RubberDuck),
            ("merge", WorkflowPlatformGate.Merge));
        var review = CreateReviewFixture(fixture, "independent-review");
        var reviewComplete = CompleteReview(review);
        var owner = CreateRuntimeRegistration(review.Identity, review.ActorId, review.Fence, review.SelectionHash);
        var step = fixture.Plan.Workflow.Definition.Steps.Single(item => item.Id == "rubberduck");
        var intent = MafRubberduckReviewContract.CreateIntent(
            Guid.NewGuid(), review.Identity, "rubberduck-checkpoint", reviewComplete.Revision + 1,
            fixture.Plan, step, reviewComplete, 5, review.ActorId, review.Fence, review.SelectionHash, owner);
        var running = CoordinationEndpoints.PrepareRubberduckCheckpoint(
            new(new("root", "review-terminal"), reviewComplete), intent)!;
        return (SerializeRecovery(running, fixture.Plan, 5, "rubberduck-checkpoint"), intent.OperationId);
    }

    private static ReviewFixture CreateReviewFixture(PlanFixture fixture, string stepId)
    {
        var identity = new SessionIdentity("project-1", "run-1", "root");
        const string actorId = "reviewer-1";
        const long fence = 1;
        const long decisionVersion = 3;
        var selectionHash = new string('a', 64);
        var initial = CreateInitialCheckpoint(fixture.Plan, decisionVersion);
        var step = fixture.Plan.Workflow.Definition.Steps.Single(item => item.Id == stepId);
        var intent = MafIndependentReviewContract.CreateIntent(
            Guid.NewGuid(), identity, "review-checkpoint", 2, fixture.Plan, step, initial,
            decisionVersion, actorId, fence, selectionHash);
        var running = CoordinationEndpoints.PrepareReviewCheckpoint(
            new(new("root", "initial"), initial), intent)!;
        return new(fixture, identity, actorId, fence, selectionHash, running, intent);
    }

    private static MafExecutionCheckpoint CompleteReview(ReviewFixture fixture)
    {
        var confirmed = ConfirmedDecision(fixture.Plan.Plan, fixture.Plan.SelectionContext, fixture.ActorId);
        var requested = CoordinatorDecisionFlow.RequestApproval(
            confirmed, fixture.Intent.ApprovalRequestId, fixture.Intent.EvidenceHash, fixture.ActorId);
        Assert.True(requested.IsSuccess);
        Assert.Null(MafIndependentReviewContract.ReadDecision(
            fixture.Intent, new(requested.State!, 4, fixture.SelectionHash), fixture.ActorId));
        var answered = requested.State!.ApplyGateAnswer(new(
            fixture.Intent.ApprovalRequestId, fixture.ActorId, fixture.Fence,
            CoordinatorGateChoices.Approve, null));
        Assert.True(answered.IsAccepted);
        var receipt = MafIndependentReviewContract.ReadDecision(
            fixture.Intent, new(answered.State, 5, fixture.SelectionHash), fixture.ActorId)!;
        return CoordinationEndpoints.PrepareReviewCompletionCheckpoint(
            new(new("root", "review-checkpoint"), fixture.Running with { DecisionStateVersion = 5 }),
            fixture.Intent, receipt)!;
    }

    private static CoordinatorDecisionState ConfirmedDecision(
        WorkPlanSnapshot plan, WorkPlanRunSelectionContext selectionContext, string actor)
    {
        var outcome = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(1),
            new("outcome-1", "Implement the change.", "The result is reviewed.",
                "Only the requested changes.", "The source is available.", []),
            "outcome-request", actor);
        var confirmedOutcome = outcome.State!.ApplyGateAnswer(
            new("outcome-request", actor, 1, CoordinatorGateChoices.Approve, null)).State;
        var selected = CoordinatorDecisionFlow.SelectWorkflow(
            confirmedOutcome, new(plan.Workflow.Definition, [plan.Workflow.Definition]), null, null, null);
        var proposed = CoordinatorDecisionFlow.ProposeWorkPlan(
            selected.State, plan.Plan, selectionContext, "plan-request", actor);
        Assert.True(proposed.IsSuccess);
        return proposed.State!.ApplyGateAnswer(
            new("plan-request", actor, 1, CoordinatorGateChoices.Approve, null)).State;
    }

    private static MafExecutionCheckpoint CreateInitialCheckpoint(WorkPlanSnapshot plan, long decisionVersion)
    {
        var nonModelSteps = ImmutableDictionary<string, MafExecutionTaskStatus>.Empty
            .WithComparers(StringComparer.Ordinal);
        foreach (var step in plan.Workflow.Definition.Steps.Where(item => item.Mode == WorkflowStepMode.Platform))
            nonModelSteps = nonModelSteps.Add(step.Id, MafExecutionTaskStatus.Pending);
        return new MafExecutionCheckpoint(1, plan.Plan.Id, decisionVersion, MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add("first", MafExecutionTaskStatus.Succeeded),
            NonModelSteps = nonModelSteps
        })
        {
            Results = ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal).Add("first", "Reviewed implementation output.")
        };
    }

    private static JsonElement SerializeRecovery(
        MafExecutionCheckpoint checkpoint, WorkPlanSnapshot plan, long decisionVersion, string checkpointId)
    {
        var projected = MafExecutionRecoveryProjector.Project(
            new(new("root", checkpointId), checkpoint), plan, decisionVersion, true, null);
        Assert.Equal("available", projected.State);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        return JsonSerializer.SerializeToElement(projected, options);
    }

    private static PlanFixture CreatePlan(params (string StepId, WorkflowPlatformGate Gate)[] gates)
    {
        var steps = new List<WorkflowStepDefinition>
        {
            new("implement", "Open work", WorkflowStepMode.Open, 0, new(1, 8), [],
                ["engineer", "researcher"], ["execution", "research", "validation"],
                ["worktree", "sandboxed"], ["sandbox.workspace.write"], null, null)
        };
        var predecessor = "implement";
        foreach (var (stepId, gate) in gates)
        {
            steps.Add(new(stepId, stepId, WorkflowStepMode.Platform, steps.Count, new(1, 1), [predecessor],
                [], [], [], [], null, gate));
            predecessor = stepId;
        }
        var workflow = new WorkflowDefinition(
            "workflow.core", "revision-1", "catalog-1", WorkflowDefinitionOrigin.BuiltIn, 32,
            steps.ToImmutableArray());
        var workflowResult = WorkflowDefinitionValidator.ValidateAndSnapshot(workflow);
        Assert.True(workflowResult.IsValid, string.Join("; ", workflowResult.Issues.Select(issue => issue.Message)));
        var sandbox = Pin(ProviderSeam.Sandbox, "sandbox.test",
            ImmutableHashSet.Create(StringComparer.Ordinal, "sandbox.workspace.write"));
        var selectionContext = new WorkPlanRunSelectionContext(
            [new RoleRunSelection("engineer", ["agent-1"], ["model:provider/alpha"], ["worktree", "sandboxed"]),
             new RoleRunSelection("researcher", ["agent-2"], ["model:provider/alpha"], ["worktree", "sandboxed"])],
            sandbox)
        {
            ModelTaskSelections = [new ModelTaskSelectionReference(
                ModelTaskKind.Straightforward, null, null, "model:provider/alpha")]
        };
        var plan = new WorkPlan("plan-1", workflow.Id, workflow.Revision, workflow.CatalogVersion,
            [new("first", "implement", "Implement", "Implement the requested change.", "engineer",
                "agent-1", "execution", "model:provider/alpha", "sandbox.test", "worktree", [],
                ["src/implementation.cs"])]);
        var planResult = WorkPlanValidator.ValidateAndSnapshot(workflowResult.Value!, plan, selectionContext);
        Assert.True(planResult.IsValid, string.Join("; ", planResult.Issues.Select(issue => issue.Message)));
        return new(planResult.Value!, selectionContext);
    }

    private static SourceControlRepositoryPin CreateSourceControlPin(
        SessionIdentity identity, string selectionHash, long fence)
    {
        var capabilities = ImmutableHashSet.Create(StringComparer.Ordinal,
            SourceControlCapabilities.RepositoryRead, SourceControlCapabilities.RepositoryCheckout);
        var binding = Pin(ProviderSeam.SourceControl, "github", capabilities);
        var accepted = new SourceControlAcceptedRunBinding(
            "issuer", "actor", "tenant-1", identity.ProjectId, identity.RunId, identity.SessionId,
            selectionHash, 1, 1, 1, "context", fence);
        return new("source-pin", accepted, binding, new("octo", "repo"),
            new(new SecretRef("api", "1"), SourceControlSecretPurposes.Api),
            new(new SecretRef("checkout", "1"), SourceControlSecretPurposes.Checkout),
            null, 123, "main", false, DateTimeOffset.UnixEpoch);
    }

    private static RuntimeRegistration CreateRuntimeRegistration(
        SessionIdentity identity, string actorId, long fence, string selectionHash)
    {
        var binding = new RuntimeBinding(
            "https://identity.example/", actorId, "tenant-1", identity.ProjectId, identity.RunId,
            identity.SessionId, RuntimeActionContract.CoordinatorRootStepId, "root-turn",
            1, 1, 1, "context-v1", selectionHash, fence, "environment-1", "placement-1",
            1, "profile-1", new Uri("https://runtime.example/runtime/v1/configure"),
            new Uri("https://runtime.example/runtime/v1/observe"));
        return new(Guid.NewGuid(), 1, binding, RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private sealed record PlanFixture(WorkPlanSnapshot Plan, WorkPlanRunSelectionContext SelectionContext);

    private sealed record ReviewFixture(
        PlanFixture Plan,
        SessionIdentity Identity,
        string ActorId,
        long Fence,
        string SelectionHash,
        MafExecutionCheckpoint Running,
        MafExecutionReviewIntent Intent);

    private static PinnedProviderBinding Pin(ProviderSeam seam, string id, ImmutableHashSet<string> capabilities)
    {
        var registration = new ProviderRegistration(
            new ProviderDescriptor(seam, id, new Version(1, 0, 0), 1, ProviderHostingPattern.InProcess, capabilities),
            true, "options-v1", 1);
        var catalog = ProviderCatalog.Create([registration], [new ProviderSelection(seam, id)], []).Value!;
        var resolver = new ProviderResolver(catalog);
        var candidate = resolver.Resolve(new ProviderResolutionRequest(seam, null, new Version(1, 0, 0), 1,
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal))).Value!.Candidate!;
        return resolver.Pin("run-1", candidate, "resource",
            new ResourceNegotiation(new ProviderResourceRef(seam, id, "resource", 1), capabilities)).Value!;
    }
}
