using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafBacklogPrerequisiteEvidenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void ParsesTypedCaptureAndNoOutputWitnessRows()
    {
        var captureId = $"sc-output-{Guid.NewGuid():N}";
        var capture = new ProducedRunCaptureProof(
            ProducedRunCaptureLimits.ContractVersion,
            new SessionIdentity("project", "run", "root"),
            captureId,
            Guid.ParseExact(captureId["sc-output-".Length..], "N"),
            "issuer",
            "subject",
            "tenant",
            "pin",
            new string('a', 64),
            "workspace",
            Guid.NewGuid(),
            "main",
            "repository",
            1,
            new string('a', 40),
            new string('b', 40),
            new string('c', 64),
            1,
            new string('d', 64),
            0,
            new string('e', 64),
            ProducedRunCaptureLimits.PackageHeaderBytes,
            DateTimeOffset.UtcNow);
        var captureRow = new MafExecutionOwnerEvidenceCaptureRow(
            MafExecutionOwnerEvidenceContract.CurrentVersion,
            "capture",
            capture,
            "admitted",
            "object-key",
            1,
            DateTimeOffset.UtcNow,
            [new MafExecutionOwnerEvidenceOutputRow("work", "src/file.txt", new string('f', 64), 1, null)]);
        using var captureDocument = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new[] { captureRow }, JsonOptions));

        Assert.True(MafBacklogPrerequisiteEvidenceReader.TryParseOwnerEvidence(
            captureDocument.RootElement,
            out var parsedCapture,
            out var parsedMerge,
            out var parsedNoOutput));
        Assert.Equal(captureId, parsedCapture!.Capture.CaptureId);
        Assert.Equal("src/file.txt", Assert.Single(parsedCapture.Outputs).Path);
        Assert.Null(parsedMerge);
        Assert.Null(parsedNoOutput);

        var noOutputRow = new MafExecutionOwnerEvidenceNoOutputRow(
            MafExecutionOwnerEvidenceContract.CurrentVersion,
            "no-output",
            "plan",
            new string('a', 64),
            3,
            5);
        using var noOutputDocument = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new[] { noOutputRow }, JsonOptions));

        Assert.True(MafBacklogPrerequisiteEvidenceReader.TryParseOwnerEvidence(
            noOutputDocument.RootElement,
            out var missingCapture,
            out var missingMerge,
            out var parsedNoOutputRow));
        Assert.Null(missingCapture);
        Assert.Null(missingMerge);
        Assert.Equal("plan", parsedNoOutputRow!.WorkPlanId);
    }

    [Fact]
    public void RejectsUnknownOwnerEvidenceFields()
    {
        using var document = JsonDocument.Parse("""
            [{
              "contractVersion": 1,
              "kind": "no-output",
              "workPlanId": "plan",
              "outputSetSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "checkpointRevision": 3,
              "decisionStateVersion": 5,
              "unexpected": true
            }]
            """);

        Assert.False(MafBacklogPrerequisiteEvidenceReader.TryParseOwnerEvidence(
            document.RootElement,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void RejectsMalformedTypedAcceptedRunBinding()
    {
        using var document = JsonDocument.Parse("""
            [{
              "contractVersion": 1,
              "kind": "merge",
              "intentId": "intent",
              "acceptedRun": {
                "issuer": "",
                "subject": "subject",
                "tenantId": "tenant",
                "projectId": "project",
                "runId": "run",
                "rootSessionId": "root",
                "acceptedSelectionHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "projectRevision": 1,
                "projectConfigurationRevision": 1,
                "platformRuntimeRevision": 1,
                "contextRevision": "context",
                "fence": 1
              }
            }]
            """);

        Assert.False(MafBacklogPrerequisiteEvidenceReader.TryParseOwnerEvidence(
            document.RootElement,
            out _,
            out _,
            out _));
    }
}
