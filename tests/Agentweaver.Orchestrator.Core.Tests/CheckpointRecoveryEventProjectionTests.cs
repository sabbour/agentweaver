using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class CheckpointRecoveryEventProjectionTests
{
    private static readonly Guid Operation = Guid.Parse("5d1f6c93-7ec0-43d9-a08b-8a719f692c09");
    private static readonly Guid PreviousRuntime = Guid.Parse("369a6c85-f870-4e8c-a64e-d709e62c6fb9");
    private static readonly SessionIdentity Identity = new("project", "run", "root");

    [Theory]
    [InlineData("pending", "progress")]
    [InlineData("ready", "ready")]
    [InlineData("blocked", "blocked")]
    [InlineData("indeterminate", "indeterminate")]
    [InlineData("failed", "failed")]
    public void ProgressRequiresTheSavedPhaseToMatchItsType(string phase, string suffix)
    {
        var admission = Admission();
        var key = $"checkpoint-recovery:{Operation:D}:0";
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key))[..16]);
        var payload = JsonSerializer.Serialize(new
        {
            operationId = Operation,
            status = new
            {
                phase, sourceCheckpointId = "checkpoint", sourceCheckpointRevision = 3,
                previousExecutionFence = 4, executionFence = 5, previousRuntimeInstanceId = PreviousRuntime,
                previousRegistrationRevision = 2
            }
        });
        var saved = CheckpointRecoveryEventProjection.Parse(
            id, 2, "orchestrator.run.checkpoint_recovery_" + suffix, 1, DateTimeOffset.UtcNow,
            key, payload, Operation, Identity, admission.Status);
        Assert.Equal(phase, saved.Status.Phase);
        var wrongType = suffix == "ready" ? "failed" : "ready";
        var error = Assert.Throws<CoordinationException>(() => CheckpointRecoveryEventProjection.Parse(
            id, 2, "orchestrator.run.checkpoint_recovery_" + wrongType, 1, DateTimeOffset.UtcNow,
            key, payload, Operation, Identity, admission.Status));
        Assert.Equal(503, error.StatusCode);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("indeterminate")]
    public void ChildAdmissionPreservesPendingPhase(string eventState)
    {
        var value = AdmissionPayload();
        value["result"]!["checkpointRecovery"]!["boundaryKind"] = "childJoin";
        value["runtimeEffectsState"] = eventState;
        var actual = ParseAdmission(value, "orchestrator.run.checkpoint_recovery_" + eventState);
        Assert.Equal("pending", actual.Status.Phase);
        Assert.Equal(eventState, actual.RuntimeEffectsState);
        Assert.EndsWith("_" + eventState, actual.EventType);
    }

    [Fact]
    public void LegacyAdmissionDoesNotInventOptionalFields()
    {
        var payload = AdmissionPayload();
        var status = payload["result"]!["checkpointRecovery"]!.AsObject();
        Assert.False(status.ContainsKey("operationId"));
        Assert.False(status.ContainsKey("currentStep"));
        Assert.False(status.ContainsKey("decisionStateVersion"));
        Assert.False(status.ContainsKey("recoveryCheckpointHash"));
        var item = ParseAdmission(payload, "orchestrator.run.checkpoint_recovery_pending");
        var response = new CheckpointRecoveryEventPage(Identity, Operation, 1, [item], null, false);
        using var document = JsonDocument.Parse(CoordinationEndpoints.SerializeCheckpointRecoveryEvents(response));
        var saved = document.RootElement.GetProperty("events")[0].GetProperty("status");
        Assert.Equal(Operation, document.RootElement.GetProperty("events")[0].GetProperty("operationId").GetGuid());
        Assert.False(saved.TryGetProperty("currentStep", out _));
        Assert.False(saved.TryGetProperty("decisionStateVersion", out _));
        Assert.False(saved.TryGetProperty("recoveryCheckpointHash", out _));
        payload.Remove("runtimeEffectsState");
        Assert.Null(ParseAdmission(payload, "orchestrator.run.checkpoint_recovery_pending").RuntimeEffectsState);

        status.Remove("sourceCheckpointId");
        Assert.Equal(503, Assert.Throws<CoordinationException>(() =>
            ParseAdmission(payload, "orchestrator.run.checkpoint_recovery_pending")).StatusCode);
    }

    [Theory]
    [InlineData("sourceCheckpointId")]
    [InlineData("sourceCheckpointRevision")]
    [InlineData("previousExecutionFence")]
    [InlineData("executionFence")]
    [InlineData("previousRuntimeInstanceId")]
    [InlineData("previousRegistrationRevision")]
    public void ProgressRejectsChangedAdmissionBinding(string field)
    {
        var admission = Admission();
        var payload = AdmissionPayload()["result"]!["checkpointRecovery"]!.DeepClone().AsObject();
        payload[field] = field switch
        {
            "sourceCheckpointId" => JsonValue.Create("different"),
            "previousRuntimeInstanceId" => JsonValue.Create(Guid.NewGuid().ToString("D")),
            _ => JsonValue.Create(9)
        };
        var wrapped = new JsonObject { ["operationId"] = Operation.ToString("D"), ["status"] = payload };
        var key = $"checkpoint-recovery:{Operation:D}:0";
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key))[..16]);
        var error = Assert.Throws<CoordinationException>(() => CheckpointRecoveryEventProjection.Parse(
            id, 2, "orchestrator.run.checkpoint_recovery_progress", 1, DateTimeOffset.UtcNow,
            key, wrapped.ToJsonString(), Operation, Identity, admission.Status));
        Assert.Equal(503, error.StatusCode);
    }

    [Fact]
    public void UnsupportedEventVersionAndInvalidOptionalFieldFailClosed()
    {
        var payload = AdmissionPayload();
        var versionError = Assert.Throws<CoordinationException>(() => CheckpointRecoveryEventProjection.Parse(
            Operation, 1, "orchestrator.run.checkpoint_recovery_pending", 2, DateTimeOffset.UtcNow,
            "private-key", payload.ToJsonString(), Operation, Identity, null));
        Assert.Equal(503, versionError.StatusCode);
        payload["result"]!["checkpointRecovery"]!["decisionStateVersion"] = "not-a-number";
        Assert.Equal(503, Assert.Throws<CoordinationException>(() =>
            ParseAdmission(payload, "orchestrator.run.checkpoint_recovery_pending")).StatusCode);
    }

    [Fact]
    public void ProgressRejectsAnEventIdThatDoesNotMatchItsOperationAndPhaseVersion()
    {
        var admission = Admission();
        var status = AdmissionPayload()["result"]!["checkpointRecovery"]!.DeepClone();
        var payload = new JsonObject { ["operationId"] = Operation.ToString("D"), ["status"] = status };
        var key = $"checkpoint-recovery:{Operation:D}:0";
        var error = Assert.Throws<CoordinationException>(() => CheckpointRecoveryEventProjection.Parse(
            Guid.NewGuid(), 2, "orchestrator.run.checkpoint_recovery_progress", 1, DateTimeOffset.UtcNow,
            key, payload.ToJsonString(), Operation, Identity, admission.Status));
        Assert.Equal(503, error.StatusCode);
    }

    [Fact]
    public void OversizedStoredPayloadAndResponseFailWithoutTruncation()
    {
        var payload = AdmissionPayload();
        payload["padding"] = new string('x', 1024 * 1024 + 1);
        Assert.Equal(503, Assert.Throws<CoordinationException>(() =>
            ParseAdmission(payload, "orchestrator.run.checkpoint_recovery_pending")).StatusCode);

        var admission = Admission();
        var oversized = admission with
        {
            Status = admission.Status with { CurrentStep = new string('x', 8 * 1024 * 1024) }
        };
        var page = new CheckpointRecoveryEventPage(Identity, Operation, 1, [oversized], null, false);
        Assert.Equal(503, Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.SerializeCheckpointRecoveryEvents(page)).StatusCode);
    }

    private static CheckpointRecoveryEvent Admission() =>
        ParseAdmission(AdmissionPayload(), "orchestrator.run.checkpoint_recovery_pending");

    private static CheckpointRecoveryEvent ParseAdmission(JsonObject payload, string type) =>
        CheckpointRecoveryEventProjection.Parse(
            Operation, 1, type, 1, DateTimeOffset.UtcNow, "private-key", payload.ToJsonString(),
            Operation, Identity, null);

    private static JsonObject AdmissionPayload() => new()
    {
        ["result"] = new JsonObject
        {
            ["operationId"] = Operation.ToString("D"),
            ["session"] = new JsonObject
            {
                ["projectId"] = Identity.ProjectId, ["runId"] = Identity.RunId, ["sessionId"] = Identity.SessionId
            },
            ["previousExecutionFence"] = 4,
            ["executionFence"] = 5,
            ["checkpointRecovery"] = new JsonObject
            {
                ["phase"] = "pending", ["sourceCheckpointId"] = "checkpoint", ["sourceCheckpointRevision"] = 3,
                ["previousExecutionFence"] = 4, ["executionFence"] = 5,
                ["previousRuntimeInstanceId"] = PreviousRuntime.ToString("D"), ["previousRegistrationRevision"] = 2
            }
        },
        ["runtimeEffectsState"] = "pending", ["physicalEffectsReplayed"] = false
    };
}
