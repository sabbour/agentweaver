using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    GitHubConnectionsPersistenceStore persistence,
    ByokProviderConfigurationService? byokSettings = null,
    AiExecutionPlanAccessor? executionPlanAccessor = null,
    AiExecutionPlanService? executionPlans = null,
    IRunEventStream? eventStream = null)
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
        var acceptedPlan = executionPlanAccessor?.Current;
        if (acceptedPlan is not null && executionPlans is not null)
            acceptedPlan = await executionPlans.RevalidateAcceptedAsync(acceptedPlan, ct).ConfigureAwait(false);
        var effective = acceptedPlan?.Provider
            ?? await resolver.ResolveAsync(projectId, ct).ConfigureAwait(false);
        if (effective is EffectiveModelProviderResult.Byok)
        {
            var expectedByok = (EffectiveModelProviderResult.Byok)effective;
            var configuration = executionPlanAccessor?.FrozenByokConfiguration
                ?? (byokSettings is null
                ? null
                : await byokSettings.GetAsync(ct).ConfigureAwait(false));
            if (configuration is null || !Matches(configuration, expectedByok))
            {
                if (acceptedPlan is not null)
                    throw await executionPlans!.ChangedAsync(acceptedPlan, ct).ConfigureAwait(false);
                throw new GitHubCopilotUnauthorizedException(
                    "The effective BYOK provider changed before model invocation.");
            }
            if (acceptedPlan is not null && executionPlanAccessor?.FrozenByokConfiguration is null)
                executionPlanAccessor?.FreezeByokConfiguration(configuration);
            await RecordProviderProvenanceAsync(
                eventStream,
                effective,
                acceptedPlan?.ResolutionScope
                    ?? (projectId is null
                        ? EffectiveModelProviderProvenance.ScopePlatform
                        : EffectiveModelProviderProvenance.ScopeProject),
                projectId,
                purpose,
                ct).ConfigureAwait(false);
            return new GenerationExecutionPlan(
                ModelSource.Byok,
                Capability: null,
                ByokProviderConfiguration: configuration);
        }

        if (effective is not (EffectiveModelProviderResult.ProjectGitHubCopilot or EffectiveModelProviderResult.PlatformGitHubCopilot))
            throw new GitHubCopilotUnauthorizedException(
                "No model provider is configured. Connect a GitHub Copilot account or configure a BYOK provider.");

        if (string.IsNullOrWhiteSpace(entraObjectId))
            throw new GitHubCopilotUnauthorizedException(
                "GitHub Copilot requires the authenticated caller's identity to issue a capability.");

        var scopeProjectId = projectId?.ToString();
        var now = DateTimeOffset.UtcNow;
        var capability = await persistence.TryIssueProjectCopilotCapabilityAsync(
            purpose,
            scopeProjectId,
            entraObjectId,
            now,
            now.Add(CapabilityLifetime),
            ct,
            expectedBindingId: effective.ProviderId(),
            expectedCredentialVersion: effective.CredentialVersion()).ConfigureAwait(false);
        if (capability is null)
        {
            if (acceptedPlan is not null)
                throw await executionPlans!.ChangedAsync(acceptedPlan, ct).ConfigureAwait(false);
            throw new GitHubCopilotUnauthorizedException(
                "GitHub Copilot requires a live project-scoped or platform-default capability.");
        }

        await RecordProviderProvenanceAsync(
            eventStream,
            effective,
            acceptedPlan?.ResolutionScope
                ?? (projectId is null
                    ? EffectiveModelProviderProvenance.ScopePlatform
                    : EffectiveModelProviderProvenance.ScopeProject),
            projectId,
            purpose,
            ct).ConfigureAwait(false);
        return new GenerationExecutionPlan(
            ModelSource.GitHubCopilot,
            new CopilotOperationCapability(capability.Value, scopeProjectId, entraObjectId, purpose),
            ByokProviderConfiguration: null);
    }

    internal static async Task RecordProviderProvenanceAsync(
        IRunEventStream? eventStream,
        EffectiveModelProviderResult provider,
        string resolutionScope,
        ProjectId? projectId,
        ProjectModelProviderCapabilityPurpose purpose,
        CancellationToken ct)
    {
        if (eventStream is null)
            return;

        var executionId = $"ai-operation-{Guid.NewGuid():N}";
        var payload = JsonSerializer.SerializeToNode(
            provider.ToProvenancePayload(executionId, modelId: null, resolutionScope))!.AsObject();
        payload["operation"] = purpose.ToString();
        payload["projectId"] = projectId?.ToString();
        await eventStream.AppendAsync(
            executionId,
            new RunEvent(
                0,
                EventTypes.RunModelProviderResolved,
                payload,
                DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);
    }

    internal static bool Matches(
        ByokProviderConfiguration configuration,
        EffectiveModelProviderResult.Byok expected) =>
        string.Equals(configuration.Id, expected.ProviderId, StringComparison.Ordinal)
        && string.Equals(
            configuration.ExecutionFingerprint(),
            expected.ConfigurationFingerprint,
            StringComparison.Ordinal);
}

/// <summary>
/// The execution plan for one non-run generation call: which model source to use, and — only when
/// GitHub Copilot is in effect — the pre-issued capability to redeem instead of a run snapshot.
/// </summary>
public sealed record GenerationExecutionPlan(
    ModelSource ModelSource,
    CopilotOperationCapability? Capability,
    ByokProviderConfiguration? ByokProviderConfiguration);
