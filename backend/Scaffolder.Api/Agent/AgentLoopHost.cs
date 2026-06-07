using Microsoft.Agents.Core.Models;
using Scaffolder.Api.Agent.Tools;
using Scaffolder.Api.Persistence;

namespace Scaffolder.Api.Agent;

/// <summary>
/// Hosts the Microsoft Agent Framework loop for a single run.
///
/// Responsibilities:
/// 1. Receive the task prompt and session context (<see cref="AgentLoopContext"/>).
/// 2. Drive the agent turn loop, invoking the two sandboxed tools
///    (<see cref="ReadFileTool"/>, <see cref="WriteFileTool"/>) confined to the
///    run's artifact directory.
/// 3. Emit typed events to <see cref="EventLogService"/> for every step
///    (run.started, agent.message, tool.call, tool.result/rejected/error,
///    run.completed).
/// 4. Honor the cancellation token: when the caller cancels (max step count or
///    max wall-clock duration exceeded), the loop stops and the
///    <see cref="OperationCanceledException"/> propagates so the caller can record
///    the terminal run.bounded state (FR-029).
///
/// IMPLEMENTATION NOTE (STUB):
/// The installed Microsoft.Agents.Builder / Microsoft.Agents.Core packages
/// (1.5.184) provide the Bot-Framework-style activity/turn messaging primitives
/// (<see cref="Activity"/>, <see cref="MessageFactory"/>, <see cref="RoleTypes"/>,
/// <see cref="ActivityTypes"/>) but do NOT ship an LLM-driven agent loop with
/// tool-calling (that lives in the Microsoft.Agents.AI package, which is not yet
/// referenced by this project). Until that adapter is wired in, this host runs a
/// deterministic, framework-typed simulation of the loop: each agent turn is
/// modeled as a Microsoft.Agents.Core <see cref="Activity"/>, and the real model
/// calls are marked with TODO comments below. The event shapes, tool plumbing,
/// sandbox enforcement, and cancellation semantics are production-correct so that
/// downstream consumers (T028) can integrate against the final behavior.
/// Replace the <see cref="RunSimulatedTurnsAsync"/> body with the real
/// Microsoft.Agents.AI agent invocation when the model-source adapters land.
/// </summary>
public sealed class AgentLoopHost : IAgentLoopHost
{
    private readonly EventLogService _eventLog;
    private readonly ReadFileTool _readFileTool;
    private readonly WriteFileTool _writeFileTool;
    private readonly ILogger<AgentLoopHost> _logger;

    public AgentLoopHost(
        EventLogService eventLog,
        ReadFileTool readFileTool,
        WriteFileTool writeFileTool,
        ILogger<AgentLoopHost> logger)
    {
        _eventLog = eventLog;
        _readFileTool = readFileTool;
        _writeFileTool = writeFileTool;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(AgentLoopContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        _logger.LogInformation(
            "AgentLoopHost starting run {RunId} (model source {ModelSource})",
            context.RunId, context.ModelSource);

        await _eventLog.AppendLifecycleEventAsync(
            context.RunId,
            EventType.RunStarted,
            new
            {
                taskPrompt = context.TaskPrompt,
                modelSource = context.ModelSource.ToString()
            },
            ct);

        try
        {
            // TODO: Replace this simulated turn loop with a real Microsoft.Agents.AI
            // agent invocation once the model-source adapters (copilot_sdk /
            // microsoft_foundry) are referenced. The real loop will:
            //   - build the agent from the selected ModelSource adapter,
            //   - register read_file / write_file as the only allowed tools,
            //   - stream model turns, dispatching tool calls through the same
            //     EventLogService + sandboxed tool path used below,
            //   - terminate when the model signals task completion.
            await RunSimulatedTurnsAsync(context, ct);

            await _eventLog.AppendLifecycleEventAsync(
                context.RunId,
                EventType.RunCompleted,
                new { reason = "Task completed by agent." },
                ct);

            _logger.LogInformation("AgentLoopHost completed run {RunId}", context.RunId);
        }
        catch (OperationCanceledException)
        {
            // Bounds (max steps / max duration) are enforced by the caller via the
            // cancellation token. Do not emit run.completed; let the caller record
            // the terminal run.bounded state (FR-029).
            _logger.LogInformation(
                "AgentLoopHost cancelled run {RunId} (bounds exceeded)", context.RunId);
            throw;
        }
    }

    /// <summary>
    /// Deterministic, framework-typed simulation of the agent turn loop. Exercises
    /// the full event + tool surface (agent message, read, write) so downstream
    /// consumers see realistic output. Each turn is represented as a
    /// Microsoft.Agents.Core <see cref="Activity"/>.
    /// </summary>
    private async Task RunSimulatedTurnsAsync(AgentLoopContext context, CancellationToken ct)
    {
        // Turn 1: the agent inspects the workspace before acting.
        await EmitAgentMessageAsync(
            context.RunId,
            "Reviewing the task and inspecting the workspace before making changes.",
            ct);

        // Tool call: read an entry-point file. A NOT_FOUND here is a valid, observable
        // outcome (the workspace may be empty) and exercises the tool.error path.
        await InvokeReadFileAsync(context, "README.md", ct);

        // Turn 2: the agent records its plan/output for the requested task.
        await EmitAgentMessageAsync(
            context.RunId,
            "Writing the task summary to the artifact directory.",
            ct);

        var summary =
            "# Agent Run Summary" + Environment.NewLine + Environment.NewLine +
            "Task:" + Environment.NewLine + context.TaskPrompt + Environment.NewLine;
        await InvokeWriteFileAsync(context, "AGENT_OUTPUT.md", summary, ct);

        // Final turn: signal completion.
        await EmitAgentMessageAsync(
            context.RunId,
            "Task complete. The requested changes have been written to the artifact directory.",
            ct);
    }

    /// <summary>
    /// Emits an agent message, modeling the turn as a Microsoft.Agents.Core
    /// <see cref="Activity"/> via <see cref="MessageFactory"/>.
    /// </summary>
    private async Task EmitAgentMessageAsync(Guid runId, string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Model the agent turn as a Microsoft.Agents.Core message activity authored
        // by the agent role. MessageFactory already sets Type = ActivityTypes.Message.
        var activity = MessageFactory.Text(text, string.Empty, string.Empty);
        activity.From = new ChannelAccount(id: runId.ToString(), role: RoleTypes.Agent);

        await _eventLog.AppendAgentMessageAsync(runId, activity.Text ?? text, ct);
    }

    private async Task InvokeReadFileAsync(
        AgentLoopContext context, string requestedPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var (_, callId) = await _eventLog.AppendToolCallAsync(
            context.RunId, ReadFileTool.ToolName, requestedPath, ct);

        var result = await _readFileTool.InvokeAsync(requestedPath, context.ArtifactDir, ct);

        await PersistToolOutcomeAsync(context.RunId, callId, result, ct);
    }

    private async Task InvokeWriteFileAsync(
        AgentLoopContext context, string requestedPath, string content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var (_, callId) = await _eventLog.AppendToolCallAsync(
            context.RunId, WriteFileTool.ToolName, requestedPath, ct);

        var result = await _writeFileTool.InvokeAsync(
            requestedPath, content, context.ArtifactDir, ct);

        await PersistToolOutcomeAsync(context.RunId, callId, result, ct);
    }

    /// <summary>
    /// Maps a <see cref="ToolInvocationResult"/> to the correct event:
    /// success -> tool.result, PATH_ESCAPE -> tool.rejected, any other error -> tool.error.
    /// </summary>
    private async Task PersistToolOutcomeAsync(
        Guid runId, Guid callId, ToolInvocationResult result, CancellationToken ct)
    {
        if (result.IsSuccess)
        {
            await _eventLog.AppendToolResultAsync(runId, callId, result.Content, ct);
            return;
        }

        var error = result.Error!;
        if (error.Code == "PATH_ESCAPE")
        {
            await _eventLog.AppendToolRejectedAsync(runId, callId, error.Code, error.Message, ct);
        }
        else
        {
            await _eventLog.AppendToolErrorAsync(runId, callId, error.Code, error.Message, ct);
        }
    }
}
