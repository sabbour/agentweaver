using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Orchestrator;

internal sealed class RuntimeSkillContentOwner(
    RuntimeRegistrationOwner registrations,
    ProjectsRunSelectionClient projects)
{
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
