extern alias OrchestratorHost;

using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
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
        var selected = Select(snapshot, kind);
        Assert.Equal(expected, selected.GetProperty(field).GetString());
        ValidateEnum(selected, kind == "step" ? stepSchema : operationSchema, field);
    }

    [Fact]
    public async Task CommittedProjectionMatchesServedStepAndOperationContracts()
    {
        var (snapshot, operationId) = ProjectCommittedRecovery();
        using var document = await ReadOpenApi();
        var (stepSchema, operationSchema) = ResolveSchemas(document.RootElement);
        var step = Select(snapshot, "step");
        var operation = Select(snapshot, "operation");

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
            "mayRepeat", "indeterminate", "checkpointResultRecorded", "terminalReceiptRecorded", "reconcileCommitted");
        AssertEnumValues(operationSchema, "evidenceState",
            "intentOnly", "terminalReceiptRecorded", "committedAwaitingReceipt");
        AssertEnumValues(operationSchema, "effectRecoveryState", "mayRepeat", "reconcileCommitted");

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

    private static JsonElement Select(JsonElement snapshot, string kind)
    {
        var array = snapshot.GetProperty(kind == "step" ? "steps" : "platformGateOperations");
        return Assert.Single(array.EnumerateArray(), x => x.GetProperty("stepId").GetString() == "publish");
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
