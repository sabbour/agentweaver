using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// UNIFIED AUTONOMOUS STEERING (Fix-A(3a) Path-2) — the coordinator-side DI seam over
/// <see cref="RunOrchestrator.StartChildRevisionHandoffAsync"/>. The lockout rotation path
/// (<c>CoordinatorAssemblyService.DispatchLockoutHandoffAsync</c>) consumes THIS abstraction rather than
/// resolving the concrete <see cref="RunOrchestrator"/> directly, so the context-carrying handoff can be
/// faked in the coordinator's orchestration unit tests (which do not construct a live orchestrator /
/// worktree / workflow stack). The production implementation
/// (<see cref="RunOrchestratorChildRevisionHandoff"/>) is a thin pass-through — the real launch logic
/// (new SDK session, prior-worktree reuse/fallback, guidance injection) stays entirely in
/// <see cref="RunOrchestrator"/>.
/// </summary>
public interface IChildRevisionHandoff
{
    /// <summary>
    /// Hands off a lockout-rejected subtask to a DIFFERENT (non-locked-out) agent: mints a NEW SDK
    /// session for <paramref name="newAgentRun"/> (lockout-correct) while reusing
    /// <paramref name="priorChild"/>'s worktree/branch when safe and injecting the accumulated review
    /// <paramref name="feedback"/> into the new agent's task prompt.
    /// </summary>
    Task StartChildRevisionHandoffAsync(
        Run newAgentRun, Run priorChild, AccumulatedReviewFeedback feedback, CancellationToken ct);
}

/// <summary>
/// Production <see cref="IChildRevisionHandoff"/> — a thin pass-through to the concrete
/// <see cref="RunOrchestrator"/> (resolved lazily to avoid a constructor DI cycle with the coordinator
/// services). Adds no behavior; it exists only so the coordinator can consume the handoff via an
/// interface that unit tests can substitute.
/// </summary>
public sealed class RunOrchestratorChildRevisionHandoff(IServiceProvider serviceProvider) : IChildRevisionHandoff
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public Task StartChildRevisionHandoffAsync(
        Run newAgentRun, Run priorChild, AccumulatedReviewFeedback feedback, CancellationToken ct) =>
        _serviceProvider.GetRequiredService<RunOrchestrator>()
            .StartChildRevisionHandoffAsync(newAgentRun, priorChild, feedback, ct);
}
