using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

/// <summary>Reads Core-owned P1 root provenance without granting runtime or resource effects.</summary>
/// <remarks>Supply a client using RuntimeOwnerHttpTransport's redirect-disabled owner handler.</remarks>
public sealed class RuntimeP1InvocationBindingHttpClient(
    HttpClient client,
    Uri orchestratorAddress,
    RuntimeActorAuthorization actor,
    TimeProvider timeProvider)
{
    internal const int MaximumResponseBytes = 16 * 1024;
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(orchestratorAddress);
    private readonly RuntimeRegistrationHttpClient _registrations = new(client, orchestratorAddress);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 16
    };

    /// <summary>
    /// Requires a live, unchanged root registration around the authenticated Core read.
    /// Child registrations are rejected: this binding contains no child or Environment lineage.
    /// </summary>
    public async Task<P1InvocationBinding> ReadRootAsync(
        RuntimeRegistration registration, CancellationToken cancellationToken)
    {
        RuntimeSessionBootstrap.RequireCurrent(registration, actor, timeProvider);
        await RequireUnchangedAsync(registration, cancellationToken).ConfigureAwait(false);
        var binding = registration.Binding;
        using var document = await RuntimeOwnerHttpTransport.SendBoundedAsync<JsonDocument>(
            client, _address,
            $"/internal/projects/{Uri.EscapeDataString(binding.ProjectId)}/runs/{Uri.EscapeDataString(binding.RunId)}/p1-invocation-binding",
            actor, null, MaximumResponseBytes, allowNotFound: false, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_p1_binding_invalid");
        P1InvocationBinding provenance;
        try
        {
            provenance = document.Deserialize<P1InvocationBinding>(JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new RuntimeAuthorizationException("runtime_p1_binding_invalid");
        }
        RequireRootBinding(provenance, registration);
        await RequireUnchangedAsync(registration, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return provenance;
    }

    private void RequireRootBinding(P1InvocationBinding provenance, RuntimeRegistration registration)
    {
        var binding = registration.Binding;
        if (provenance.ContractVersion != 1 ||
            provenance.CoreInvocationId == Guid.Empty || provenance.SetupInvocationId == Guid.Empty ||
            provenance.MembershipId == Guid.Empty || provenance.MembershipRevision < 1 ||
            provenance.RootStateVersion < 1 || provenance.AcceptedAt == default ||
            provenance.AcceptedAt > timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_p1_binding_invalid");
        RuntimeContractValidation.ValidateHash(provenance.SetupReceiptHash);
        RuntimeContractValidation.ValidateHash(provenance.AcceptedSelectionHash);
        if (provenance.TenantId != binding.TenantId || provenance.ProjectId != binding.ProjectId ||
            provenance.RunId != binding.RunId || provenance.ActorIssuer != binding.ActorIssuer ||
            provenance.ActorSubject != binding.ActorId ||
            provenance.ExecutionFence != binding.ExecutionFence ||
            provenance.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            actor.TenantSelector is { } tenant && tenant != binding.TenantId)
            throw new RuntimeAuthorizationException("runtime_p1_binding_mismatch");
        if (provenance.RootSessionId != binding.SessionId)
            throw new RuntimeAuthorizationException("runtime_p1_root_session_mismatch");
    }

    private async Task RequireUnchangedAsync(
        RuntimeRegistration expected, CancellationToken cancellationToken)
    {
        var current = await _registrations.ReadCurrentAsync(
            expected.RuntimeInstanceId, actor, cancellationToken).ConfigureAwait(false);
        RuntimeSessionBootstrap.RequireCurrent(current, actor, timeProvider);
        if (current != expected)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
    }
}
