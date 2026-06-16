using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime.Workflow;

/// <summary>
/// Helper that emits workflow.step events to both the server console and the run stream.
/// </summary>
public static class WorkflowStepEvents
{
    public static void Emit(
        ChannelWriter<RunEvent>? stream,
        ILogger logger,
        string step,
        string status,
        string label,
        int sequence = 0)
    {
        var payload = new { step, status, label };
        logger.LogInformation("[workflow] {Step} → {Status}", step, status);
        stream?.TryWrite(new RunEvent(sequence, EventTypes.WorkflowStep, payload));
    }
}
