using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Orchestrator;
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
    }
}
