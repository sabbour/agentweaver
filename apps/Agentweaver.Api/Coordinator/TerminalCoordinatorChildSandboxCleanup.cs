using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentweaver.Api.Coordinator;

/// <summary>Releases terminal children of terminal coordinators, including revisions no longer
/// referenced by the current subtask. The claim's dispatch holder and lifecycle generation fence
/// each release; the pod lifecycle itself retains any live preview.</summary>
public sealed class TerminalCoordinatorChildSandboxCleanup
{
    private const int SweepBatchSize = 16;
    private readonly IRunStore _runs;
    private readonly IAgentHostPodLifecycle? _pods;
    private readonly bool _podPerRun;
    private readonly ILogger<TerminalCoordinatorChildSandboxCleanup> _logger;
    private int _offset;

    public TerminalCoordinatorChildSandboxCleanup(
        IRunStore runs,
        ILogger<TerminalCoordinatorChildSandboxCleanup> logger,
        IOptions<SandboxRuntimeOptions> runtime,
        IAgentHostPodLifecycle? pods = null)
    {
        _runs = runs;
        _pods = pods;
        _podPerRun = runtime.Value.IsPodPerRun;
        _logger = logger;
    }

    public async Task ReleaseForParentAsync(string parentRunId, CancellationToken ct = default)
    {
        if (!_podPerRun || _pods is null || !RunId.TryParse(parentRunId, out var parentId))
            return;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var cleanupCt = deadline.Token;
        try
        {
            var parent = await _runs.GetAsync(parentId, cleanupCt).ConfigureAwait(false);
            if (parent is null || !IsTerminalParent(parent.Status))
                return;

            var children = await _runs.GetChildRunIdsAsync(parentRunId, cleanupCt).ConfigureAwait(false);

            foreach (var childId in children)
            {
                cleanupCt.ThrowIfCancellationRequested();
                try
                {
                    if (!RunId.TryParse(childId, out var id))
                        continue;
                    var child = await _runs.GetAsync(id, cleanupCt).ConfigureAwait(false);
                    // Active execution and pending review own their claims even if a parent has stopped.
                    if (child is null || !TerminalRunOutcome.IsTerminal(child.Status))
                        continue;

                    var claim = await _pods.GetAgentHostDispatchContextAsync(childId, cleanupCt)
                        .ConfigureAwait(false);
                    if (claim?.LifecycleGeneration != child.LifecycleGeneration
                        || string.IsNullOrWhiteSpace(claim.HolderToken))
                    {
                        _logger.LogDebug(
                            "Terminal child cleanup: no current fenced claim for child {ChildRunId}", childId);
                        continue;
                    }

                    // Recheck both sides after the claim read: a restarted generation or a reopened
                    // coordinator may now own the same identity. The holder CAS fences later takeovers.
                    parent = await _runs.GetAsync(parentId, cleanupCt).ConfigureAwait(false);
                    child = await _runs.GetAsync(id, cleanupCt).ConfigureAwait(false);
                    if (parent is null || !IsTerminalParent(parent.Status)
                        || child is null || !TerminalRunOutcome.IsTerminal(child.Status)
                        || child.LifecycleGeneration != claim.LifecycleGeneration)
                        continue;

                    if (await _pods.TryReleaseHeldAgentHostPodAsync(childId, claim.HolderToken, cleanupCt)
                        .ConfigureAwait(false))
                        _logger.LogInformation(
                            "Terminal child cleanup: requested release for child {ChildRunId} of coordinator {ParentRunId} (live previews remain retained)",
                            childId, parentRunId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cleanupCt.IsCancellationRequested)
                {
                    _logger.LogWarning(ex,
                        "Terminal child cleanup: failed to release child {ChildRunId} of {ParentRunId}; will retry",
                        childId, parentRunId);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Terminal child cleanup: timed out for {ParentRunId}; will retry", parentRunId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Terminal child cleanup: failed to enumerate children of {ParentRunId}; will retry",
                parentRunId);
        }
    }

    public async Task SweepAsync(CancellationToken ct = default)
    {
        if (!_podPerRun || _pods is null)
            return;

        // A rotating page bounds work on every tick and visits old leaked coordinators too.
        // Parent run status is rechecked through IRunStore before any release.
        var ids = await _runs.GetTerminalCoordinatorRunIdsAsync(_offset, SweepBatchSize, ct)
            .ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var visited = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
            {
                _logger.LogWarning("Terminal child cleanup: sweep timed out; continuing on next heartbeat");
                break;
            }
            try
            {
                await ReleaseForParentAsync(id, deadline.Token).ConfigureAwait(false);
                visited++;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                _logger.LogWarning("Terminal child cleanup: sweep timed out; continuing on next heartbeat");
                break;
            }
        }

        _offset = visited == SweepBatchSize ? _offset + visited : visited == ids.Count ? 0 : _offset + visited;
    }

    private static bool IsTerminalParent(RunStatus status) =>
        status is RunStatus.Failed or RunStatus.Completed or RunStatus.Merged
            or RunStatus.Declined or RunStatus.MergeFailed;
}
