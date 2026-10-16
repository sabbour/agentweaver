using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

public sealed class RuntimeSkillContentHttpClient(
    HttpClient client,
    Uri orchestratorAddress,
    RuntimeActorAuthorization actor)
{
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(orchestratorAddress);

    internal async Task<SkillRuntimeContentProjectionV1> ReadAsync(
        RuntimeRegistration registration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        RuntimeContractValidation.Validate(registration);

        var projection = await RuntimeOwnerHttpTransport.SendAsync<SkillRuntimeContentProjectionV1>(
            client,
            _address,
            $"/internal/runtime/registrations/{registration.RuntimeInstanceId:D}/skills",
            actor,
            null,
            cancellationToken).ConfigureAwait(false);
        ValidateForRegistration(projection, registration);
        return projection;
    }

    internal static void ValidateForRegistration(
        SkillRuntimeContentProjectionV1 projection,
        RuntimeRegistration registration)
    {
        try
        {
            SkillRuntimeContentContract.ValidateProjection(projection);
        }
        catch (ArgumentException)
        {
            throw new RuntimeAuthorizationException("runtime_skill_content_invalid");
        }
        var binding = registration.Binding;
        if (projection.RuntimeInstanceId != registration.RuntimeInstanceId ||
            projection.RegistrationRevision != registration.Revision ||
            projection.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            projection.ExecutionFence != binding.ExecutionFence ||
            projection.TenantId != binding.TenantId ||
            projection.ProjectId != binding.ProjectId ||
            projection.RunId != binding.RunId ||
            projection.SessionId != binding.SessionId ||
            projection.AgentId != binding.AgentId ||
            projection.AcceptedSelectionHash != binding.AcceptedSelectionHash)
            throw new RuntimeAuthorizationException("runtime_skill_content_binding_mismatch");
    }
}
