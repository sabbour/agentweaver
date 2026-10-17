using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Projects the current accepted run skills into a registration-bound runtime response.
/// Registration ownership and accepted selection are rechecked around the skills read.
/// </summary>
internal sealed class RuntimeSkillContentOwner(
    RuntimeRegistrationOwner registrations,
    ProjectsRunSelectionClient projects)
{
    /// <summary>
    /// Reads and validates the skill content projection for the current runtime registration.
    /// </summary>
    /// <param name="context">The authenticated runtime request context.</param>
    /// <param name="runtimeInstanceId">The runtime registration whose skills are requested.</param>
    /// <param name="cancellationToken">Cancels registration and project-owner reads.</param>
    /// <returns>The validated skill projection bound to the current registration and accepted selection.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when registration or skill content is stale or invalid.</exception>
    public Task<SkillRuntimeContentProjectionV1> ReadCurrentAsync(
        HttpContext context,
        Guid runtimeInstanceId,
        CancellationToken cancellationToken) =>
        registrations.ExecuteCurrentAsync(
            context,
            runtimeInstanceId,
            async (registration, token) =>
            {
                var binding = registration.Binding;
                var skills = await projects.ReadAcceptedRunSkillsAsync(context, registration, token)
                    .ConfigureAwait(false);
                var projection = new SkillRuntimeContentProjectionV1(
                    SkillRuntimeContentContract.CurrentVersion,
                    registration.RuntimeInstanceId,
                    registration.Revision,
                    binding.ProjectConfigurationRevision,
                    binding.ExecutionFence,
                    binding.TenantId,
                    binding.ProjectId,
                    binding.RunId,
                    binding.SessionId,
                    binding.AgentId,
                    binding.AcceptedSelectionHash,
                    skills);
                try
                {
                    SkillRuntimeContentContract.ValidateProjection(projection);
                }
                catch (ArgumentException)
                {
                    throw new RuntimeAuthorizationException("runtime_skill_content_invalid");
                }
                return projection;
            },
            cancellationToken);
}
