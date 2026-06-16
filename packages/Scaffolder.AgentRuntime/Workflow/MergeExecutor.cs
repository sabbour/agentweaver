using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime.Workflow;

/// <summary>
/// Executor that performs the merge of the worktree branch into the originating branch.
/// Delegates to IMergeCoordinator.ExecuteMergeAsync and maps the result to MergeOutput.
/// </summary>
public sealed class MergeExecutor : Executor<MergeInput, MergeOutput>
{
    private readonly IMergeCoordinator _mergeCoordinator;
    private readonly ILogger<MergeExecutor> _logger;
    private readonly Func<string, ChannelWriter<RunEvent>?> _getRecordingWriter;

    public MergeExecutor(
        IMergeCoordinator mergeCoordinator,
        ILogger<MergeExecutor> logger,
        Func<string, ChannelWriter<RunEvent>?>? getRecordingWriter = null)
        : base("merge")
    {
        _mergeCoordinator = mergeCoordinator;
        _logger = logger;
        _getRecordingWriter = getRecordingWriter ?? (_ => null);
    }

    public override async ValueTask<MergeOutput> HandleAsync(
        MergeInput input, IWorkflowContext context, CancellationToken ct)
    {
        var writer = _getRecordingWriter(input.RunId);
        WorkflowStepEvents.Emit(writer, _logger, input.RunId, "merge", "started", "Merging changes");

        var result = await _mergeCoordinator.ExecuteMergeAsync(input, ct).ConfigureAwait(false);

        MergeOutput output = result.Outcome switch
        {
            MergeExecutionOutcome.Merged =>
                new MergeOutput(input.RunId, "merged", result.MergeResult, result.MergeMode),

            MergeExecutionOutcome.Blocked =>
                new MergeOutput(input.RunId, "blocked", result.Reason),

            MergeExecutionOutcome.Conflict =>
                new MergeOutput(input.RunId, "merge_failed", result.MergeResult),

            MergeExecutionOutcome.LockFailed =>
                new MergeOutput(input.RunId, "merge_failed", result.LockFailureReason),

            MergeExecutionOutcome.InternalError =>
                new MergeOutput(input.RunId, "merge_failed", "unexpected_error"),

            _ => throw new InvalidOperationException($"Unexpected merge execution outcome: {result.Outcome}")
        };

        var mergeStatus = output.Status == "merged" ? "completed" : "failed";
        WorkflowStepEvents.Emit(writer, _logger, input.RunId, "merge", mergeStatus, "Merging changes");

        return output;
    }
}
