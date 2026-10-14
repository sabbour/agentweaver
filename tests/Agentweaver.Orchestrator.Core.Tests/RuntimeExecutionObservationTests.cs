using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class RuntimeExecutionObservationTests
{
    [Theory]
    [InlineData((int)MafExecutionTaskStatus.Pending, false)]
    [InlineData((int)MafExecutionTaskStatus.Running, true)]
    [InlineData((int)MafExecutionTaskStatus.Succeeded, true)]
    [InlineData((int)MafExecutionTaskStatus.Failed, true)]
    [InlineData((int)MafExecutionTaskStatus.Indeterminate, true)]
    public void FixedWorkObservationIncludesRunningAndTerminalStatuses(int statusValue, bool expected) =>
        Assert.Equal(
            expected,
            MafExecutionCheckpointStore.IsObservationFixedWorkStatus((MafExecutionTaskStatus)statusValue));

    [Fact]
    public void ResultProjectionReturnsDigestAndLengthWithoutResultText()
    {
        const string resultText = "private child result";
        var status = MafExecutionTaskStatus.Succeeded;
        var state = new MafExecutionCheckpoint(
            1,
            "work-plan",
            1,
            MafExecutionProgress.Empty with
            {
                WorkItems = ImmutableDictionary<string, MafExecutionTaskStatus>.Empty
                    .WithComparers(StringComparer.Ordinal)
                    .Add("work-item", status)
            })
        {
            Results = ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("work-item", resultText)
        };
        var checkpoint = new MafExecutionCheckpointSnapshot(
            new CheckpointInfo("root", "checkpoint-1"), state);

        var observation = RuntimeExecutionObservationContract.Create(
            "root",
            "child",
            3,
            1,
            checkpoint,
            "work-item",
            status,
            pendingIntent: null,
            resultText);
        var serialized = JsonSerializer.Serialize(observation);
        var expectedDigest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(resultText)));

        Assert.Equal(expectedDigest, observation.ResultDigest?.Sha256);
        Assert.Equal(Encoding.UTF8.GetByteCount(resultText), observation.ResultDigest?.Utf8ByteLength);
        Assert.Null(observation.PendingDispatch);
        Assert.DoesNotContain(resultText, serialized, StringComparison.Ordinal);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var expectedJson =
            "{\"contractVersion\":1,\"rootSessionId\":\"root\",\"sessionId\":\"child\",\"executionFence\":3," +
            "\"workPlanId\":\"work-plan\",\"associationId\":\"work-item\",\"checkpointRevision\":1," +
            "\"checkpointId\":\"checkpoint-1\",\"checkpointDecisionStateVersion\":1," +
            "\"currentDecisionStateVersion\":1,\"status\":\"Succeeded\",\"resultDigest\":{\"sha256\":\"" +
            expectedDigest + "\",\"utf8ByteLength\":20}}";
        Assert.Equal(
            Encoding.UTF8.GetBytes(expectedJson),
            JsonSerializer.SerializeToUtf8Bytes(observation, jsonOptions));
    }

    [Fact]
    public void VersionTwoObservationReportsVerifiedParent()
    {
        var checkpoint = CreateCheckpoint(hasParentMetadata: true, parentCheckpointId: "checkpoint-parent");

        var observation = CreateV2Observation(checkpoint);
        var json = JsonSerializer.SerializeToElement(
            observation, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(2, observation.ContractVersion);
        Assert.Equal("root", observation.Observation.RootSessionId);
        Assert.Equal(1, observation.Observation.ContractVersion);
        Assert.True(observation.CheckpointParent.HasParentMetadata);
        Assert.Equal("checkpoint-parent", observation.CheckpointParent.ParentCheckpointId);
        AssertExactProperties(
            json,
            "contractVersion", "observation", "checkpointParent");
        AssertExactProperties(
            json.GetProperty("observation"),
            "contractVersion", "rootSessionId", "sessionId", "executionFence", "workPlanId",
            "associationId", "checkpointRevision", "checkpointId", "checkpointDecisionStateVersion",
            "currentDecisionStateVersion", "status");
        var parent = json.GetProperty("checkpointParent");
        AssertExactProperties(parent, "hasParentMetadata", "parentCheckpointId");
        Assert.Equal("checkpoint-parent",
            parent.GetProperty("parentCheckpointId").GetString());
    }

    [Fact]
    public void VersionTwoObservationDistinguishesVerifiedRootFromUnknownMetadata()
    {
        var root = CreateV2Observation(
            CreateCheckpoint(hasParentMetadata: true, parentCheckpointId: null));
        var unknown = CreateV2Observation(
            CreateCheckpoint(hasParentMetadata: false, parentCheckpointId: null));

        Assert.True(root.CheckpointParent.HasParentMetadata);
        Assert.Null(root.CheckpointParent.ParentCheckpointId);
        Assert.False(unknown.CheckpointParent.HasParentMetadata);
        Assert.Null(unknown.CheckpointParent.ParentCheckpointId);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var rootJson = JsonDocument.Parse(JsonSerializer.Serialize(root, jsonOptions));
        using var unknownJson = JsonDocument.Parse(JsonSerializer.Serialize(unknown, jsonOptions));
        var rootParent = rootJson.RootElement.GetProperty("checkpointParent");
        var unknownParent = unknownJson.RootElement.GetProperty("checkpointParent");
        Assert.True(rootParent.GetProperty("hasParentMetadata").GetBoolean());
        Assert.False(unknownParent.GetProperty("hasParentMetadata").GetBoolean());
        Assert.Equal(JsonValueKind.Null, rootParent.GetProperty("parentCheckpointId").ValueKind);
        Assert.Equal(JsonValueKind.Null, unknownParent.GetProperty("parentCheckpointId").ValueKind);
        AssertExactProperties(rootParent, "hasParentMetadata", "parentCheckpointId");
        AssertExactProperties(unknownParent, "hasParentMetadata", "parentCheckpointId");
    }

    [Fact]
    public void VersionTwoObservationRejectsContradictoryOrInvalidParentMetadata()
    {
        var contradictory = Assert.Throws<CoordinationException>(() =>
            CreateV2Observation(
                CreateCheckpoint(hasParentMetadata: false, parentCheckpointId: "checkpoint-parent")));
        var invalid = Assert.Throws<CoordinationException>(() =>
            CreateV2Observation(
                CreateCheckpoint(hasParentMetadata: true, parentCheckpointId: "invalid/parent")));

        Assert.Equal("maf_execution_checkpoint_parent_metadata_invalid", contradictory.Code);
        Assert.Equal(StatusCodes.Status409Conflict, contradictory.StatusCode);
        Assert.Equal("maf_execution_checkpoint_parent_metadata_invalid", invalid.Code);
        Assert.Equal(StatusCodes.Status409Conflict, invalid.StatusCode);
        Assert.Throws<RuntimeAuthorizationException>(() =>
            new RuntimeExecutionCheckpointParent(false, "checkpoint-parent").Validate());
    }

    [Fact]
    public void VersionOneObservationIgnoresInvalidVersionTwoOnlyParentMetadata()
    {
        var observation = RuntimeExecutionObservationContract.Create(
            "root",
            "child",
            3,
            4,
            CreateCheckpoint(hasParentMetadata: false, parentCheckpointId: "invalid/parent"),
            "work-item",
            MafExecutionTaskStatus.Running,
            pendingIntent: null,
            result: null);

        Assert.Equal(1, observation.ContractVersion);
        Assert.Equal(RuntimeExecutionTaskStatus.Running, observation.Status);
    }

    [Fact]
    public void VersionTwoObservationRequiresNestedValuesAndParentFields()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(
            CreateV2Observation(CreateCheckpoint(hasParentMetadata: true, parentCheckpointId: null)),
            options);

        var missingObservation = JsonNode.Parse(json)!;
        missingObservation.AsObject().Remove("observation");
        Assert.Throws<JsonException>(() => DeserializeV2(missingObservation.ToJsonString(), options));

        var missingParent = JsonNode.Parse(json)!;
        missingParent.AsObject().Remove("checkpointParent");
        Assert.Throws<JsonException>(() => DeserializeV2(missingParent.ToJsonString(), options));

        var nullObservation = JsonNode.Parse(json)!;
        nullObservation["observation"] = null;
        Assert.Throws<RuntimeAuthorizationException>(() =>
            DeserializeV2(nullObservation.ToJsonString(), options).Validate());

        var nullParent = JsonNode.Parse(json)!;
        nullParent["checkpointParent"] = null;
        Assert.Throws<RuntimeAuthorizationException>(() =>
            DeserializeV2(nullParent.ToJsonString(), options).Validate());

        var missingFlag = JsonNode.Parse(json)!;
        missingFlag["checkpointParent"]!.AsObject().Remove("hasParentMetadata");
        Assert.Throws<JsonException>(() => DeserializeV2(missingFlag.ToJsonString(), options));

        var missingParentId = JsonNode.Parse(json)!;
        missingParentId["checkpointParent"]!.AsObject().Remove("parentCheckpointId");
        Assert.Throws<JsonException>(() => DeserializeV2(missingParentId.ToJsonString(), options));
    }

    [Fact]
    public void VersionTwoObservationRejectsWrongVersionAndUnknownFields()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(
            CreateV2Observation(CreateCheckpoint(hasParentMetadata: true, parentCheckpointId: null)),
            options);

        var wrongVersion = JsonNode.Parse(json)!;
        wrongVersion["contractVersion"] = 1;
        Assert.Throws<RuntimeAuthorizationException>(() =>
            DeserializeV2(wrongVersion.ToJsonString(), options).Validate());

        var wrongObservationVersion = JsonNode.Parse(json)!;
        wrongObservationVersion["observation"]!["contractVersion"] = 2;
        Assert.Throws<RuntimeAuthorizationException>(() =>
            DeserializeV2(wrongObservationVersion.ToJsonString(), options).Validate());

        var unknownWrapperField = JsonNode.Parse(json)!;
        unknownWrapperField["unexpected"] = true;
        Assert.Throws<JsonException>(() =>
            DeserializeV2(unknownWrapperField.ToJsonString(), options));

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<RuntimeExecutionCheckpointParent>(
                "{\"hasParentMetadata\":true,\"parentCheckpointId\":null,\"unexpected\":true}",
                options));
    }

    [Fact]
    public void VersionTwoObservationRejectsForeignOrStaleCheckpoint()
    {
        var checkpoint = CreateCheckpoint(hasParentMetadata: true, parentCheckpointId: null);

        var foreign = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.RequireBoundObservationCheckpoint(
                checkpoint with { Info = new CheckpointInfo("another-root", checkpoint.Info.CheckpointId) },
                "root",
                "work-plan"));
        var stale = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.RequireBoundObservationCheckpoint(
                checkpoint with { State = checkpoint.State with { WorkPlanId = "old-work-plan" } },
                "root",
                "work-plan"));

        Assert.Equal("maf_execution_checkpoint_stale", foreign.Code);
        Assert.Equal(StatusCodes.Status409Conflict, foreign.StatusCode);
        Assert.Equal("maf_execution_checkpoint_stale", stale.Code);
        Assert.Equal(StatusCodes.Status409Conflict, stale.StatusCode);
    }

    [Fact]
    public void ObservationRejectsStaleOwnerContext()
    {
        var actor = new CoordinationActor("issuer", "actor");
        var identity = new SessionIdentity("project", "run", "child");
        var selectionHash = new string('a', 64);
        var owner = new SessionRuntimeOwnerState(
            "root", null, "tenant", selectionHash, 3, 1, 7, "turn");
        var decision = new CoordinatorDecisionCurrentState(
            CoordinatorDecisionState.Create(owner.ExecutionFence), 5, selectionHash);
        var staleOwner = new RuntimeOwnerContext(
            1,
            actor.Issuer,
            actor.Subject,
            owner.TenantId,
            identity.ProjectId,
            identity.RunId,
            identity.SessionId,
            "agent",
            "model",
            owner.RuntimeTurnId,
            1,
            1,
            1,
            "context",
            selectionHash,
            owner.ExecutionFence,
            owner.LogicalTurnOrdinal,
            owner.StateVersion + 1,
            decision.StateVersion);

        var exception = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.RequireCurrentObservationOwner(
                staleOwner, actor, identity, owner, decision));

        Assert.Equal("runtime_owner_context_stale", exception.Code);
        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
    }

    private static RuntimeExecutionObservationV2 CreateV2Observation(
        MafExecutionCheckpointSnapshot checkpoint) =>
        RuntimeExecutionObservationContract.CreateV2(
            "root",
            "child",
            3,
            4,
            checkpoint,
            "work-item",
            MafExecutionTaskStatus.Running,
            pendingIntent: null,
            result: null);

    private static RuntimeExecutionObservationV2 DeserializeV2(
        string json,
        JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<RuntimeExecutionObservationV2>(json, options)!;

    private static void AssertExactProperties(JsonElement element, params string[] expected) =>
        Assert.Equal(expected, element.EnumerateObject().Select(property => property.Name).ToArray());

    private static MafExecutionCheckpointSnapshot CreateCheckpoint(
        bool hasParentMetadata,
        string? parentCheckpointId) =>
        new(
            new CheckpointInfo("root", "checkpoint-current"),
            new MafExecutionCheckpoint(
                2,
                "work-plan",
                3,
                MafExecutionProgress.Empty))
        {
            HasParentMetadata = hasParentMetadata,
            ParentCheckpointId = parentCheckpointId
        };
}
