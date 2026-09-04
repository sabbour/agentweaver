using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Shared execution-provider resolution for the non-run AI generation features (blueprint,
/// workflow, skill, and casting generation). These features have no <c>Run</c> entity and therefore
/// no run-bound capability snapshot; they must not fabricate a synthetic run id and call GitHub
/// Copilot as if one existed. Instead this resolves the caller's EFFECTIVE model provider — project-
/// scoped when a project id is supplied, platform-scoped otherwise — via
/// <see cref="EffectiveModelProviderResolver"/>, exactly the same precedence used by every other
/// consumer, and mints a short-lived, purpose-bound non-run capability when the result is
/// GitHub-Copilot-sourced, reusing the mechanism already used by backlog decomposition and
/// marketplace classification.
/// </summary>
public sealed class GenerationModelProviderExecutor(
    EffectiveModelProviderResolver resolver,
    GitHubConnectionsPersistenceStore persistence)
{
    private static readonly TimeSpan CapabilityLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Resolves the execution plan for one non-run generation call. Throws
    /// <see cref="GitHubCopilotUnauthorizedException"/> when the effective provider is GitHub
    /// Copilot but no capability could be issued, or when no model provider is configured at all —
    /// generators already classify this exception into their existing user-facing failure result.
    /// </summary>
    /// <param name="projectId">
    /// The project this generation call is scoped to, or <see langword="null"/> for platform-scoped
    /// generation (e.g. drafting a blueprint before any project exists).
    /// </param>
    /// <param name="entraObjectId">
    /// The calling user's identity, used to bind the issued capability for replay protection. Required
    /// whenever the effective provider turns out to be GitHub Copilot.
    /// </param>
    /// <param name="purpose">The non-run operation this capability authorizes.</param>
    public async Task<GenerationExecutionPlan> PrepareAsync(
        ProjectId? projectId,
        string? entraObjectId,
        ProjectModelProviderCapabilityPurpose purpose,
        CancellationToken ct)
    {
        var effective = await resolver.ResolveAsync(projectId, ct).ConfigureAwait(false);
        if (effective is EffectiveModelProviderResult.Byok)
            return new GenerationExecutionPlan(ModelSource.Byok, Capability: null);

        if (effective is not (EffectiveModelProviderResult.ProjectGitHubCopilot or EffectiveModelProviderResult.PlatformGitHubCopilot))
            throw new GitHubCopilotUnauthorizedException(
                "No model provider is configured. Connect a GitHub Copilot account or configure a BYOK provider.");

        if (string.IsNullOrWhiteSpace(entraObjectId))
            throw new GitHubCopilotUnauthorizedException(
                "GitHub Copilot requires the authenticated caller's identity to issue a capability.");

        var scopeProjectId = projectId?.ToString();
        var now = DateTimeOffset.UtcNow;
        var capability = await persistence.TryIssueProjectCopilotCapabilityAsync(
            purpose, scopeProjectId, entraObjectId, now, now.Add(CapabilityLifetime), ct).ConfigureAwait(false);
        if (capability is null)
            throw new GitHubCopilotUnauthorizedException(
                "GitHub Copilot requires a live project-scoped or platform-default capability.");

        return new GenerationExecutionPlan(
            ModelSource.GitHubCopilot,
            new CopilotOperationCapability(capability.Value, scopeProjectId, entraObjectId, purpose));
    }
}

/// <summary>
/// The execution plan for one non-run generation call: which model source to use, and — only when
/// GitHub Copilot is in effect — the pre-issued capability to redeem instead of a run snapshot.
/// </summary>
public sealed record GenerationExecutionPlan(ModelSource ModelSource, CopilotOperationCapability? Capability);
