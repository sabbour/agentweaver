using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

/// <summary>Joins retained Environment creation identities to a current Core root registration.</summary>
public sealed class RuntimeP1CreationBindingHttpClient(
    HttpClient client,
    Uri orchestratorAddress,
    Uri environmentAddress,
    RuntimeActorAuthorization actor,
    TimeProvider timeProvider)
{
    private readonly Uri _environment = RuntimeOwnerHttpTransport.RequireOwnerAddress(environmentAddress);
    private readonly RuntimeP1InvocationBindingHttpClient _core =
        new(client, orchestratorAddress, actor, timeProvider);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 24,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Reads both owners without creating, changing, or retrofitting any resource.</summary>
    public async Task<EnvironmentP1CreationBinding> ReadRootAsync(
        RuntimeRegistration registration, CancellationToken cancellationToken)
    {
        var root = await _core.ReadRootAsync(registration, cancellationToken).ConfigureAwait(false);
        var binding = registration.Binding;
        var path = $"/internal/projects/{Uri.EscapeDataString(binding.ProjectId)}/runs/{Uri.EscapeDataString(binding.RunId)}" +
            $"/environments/{Uri.EscapeDataString(binding.EnvironmentId)}/coordination/sessions/{Uri.EscapeDataString(binding.SessionId)}" +
            $"/runtime-bootstrap/profiles/{Uri.EscapeDataString(binding.ProfileId)}/p1-creation-binding";
        using var document = await RuntimeOwnerHttpTransport.SendBoundedAsync<JsonDocument>(
            client, _environment, path, actor, null, 64 * 1024, allowNotFound: false, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_p1_creation_binding_invalid");
        EnvironmentP1CreationBinding creation;
        try
        {
            creation = document.Deserialize<EnvironmentP1CreationBinding>(JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new RuntimeAuthorizationException("runtime_p1_creation_binding_invalid");
        }
        RequireBinding(creation, root, registration, timeProvider.GetUtcNow());
        var after = await _core.ReadRootAsync(registration, cancellationToken).ConfigureAwait(false);
        if (after != root)
            throw new RuntimeAuthorizationException("runtime_p1_binding_changed");
        cancellationToken.ThrowIfCancellationRequested();
        return creation;
    }

    internal static void RequireBinding(
        EnvironmentP1CreationBinding creation, P1InvocationBinding root,
        RuntimeRegistration registration, DateTimeOffset now)
    {
        var binding = registration.Binding;
        var fence = new EnvironmentGenerationFence(
            new(binding.TenantId, binding.ProjectId, binding.RunId, binding.EnvironmentId),
            binding.EnvironmentLifecycleGeneration);
        var sandbox = new ProviderResourceRef(
            ProviderSeam.Sandbox, binding.PlacementProviderId!, binding.PlacementUid, binding.PlacementGeneration);
        if (creation.ContractVersion != 1 || creation.Binding != root ||
            creation.EnvironmentFence != fence || creation.PodObservation is not { } pod ||
            pod.EnvironmentFence != fence || pod.Resource != sandbox ||
            pod.FencingGeneration != binding.EnvironmentProviderFencingGeneration ||
            pod.ObservedAt == default || pod.ObservedAt > now ||
            pod.ProvisionOperationId == Guid.Empty || pod.ClaimUid != sandbox.ResourceId ||
            pod.WorkspaceAttachment is null || !pod.IsolationConfigurationVerified ||
            creation.Environment is not { } environment ||
            creation.WorkspaceMetadata is not { } metadata ||
            creation.WorkspaceProvision is not { ProviderResource: { Seam: ProviderSeam.Storage } } workspace ||
            creation.SandboxProvision is not { } provision ||
            environment.ResourceKind != "Environment" || environment.ResourceId != binding.EnvironmentId ||
            environment.ProviderResource is not null || environment.ResourceGeneration is not null ||
            environment.ProviderFencingGeneration is not null || environment.TransitionRevision is not null ||
            environment.LeaseRevision is not null ||
            metadata.ResourceKind != "WorkspaceVolumeMetadata" ||
            workspace.ResourceKind != "WorkspaceVolume" || workspace.ResourceId != metadata.ResourceId ||
            metadata.ResourceGeneration != 0 || metadata.TransitionRevision is not > 0 ||
            metadata.ProviderResource is not null || metadata.ProviderFencingGeneration is not null ||
            metadata.LeaseRevision is not null || workspace.ResourceGeneration is not > 0 ||
            workspace.ResourceGeneration != workspace.ProviderResource.Generation ||
            workspace.TransitionRevision is not > 0 || workspace.TransitionRevision <= metadata.TransitionRevision ||
            workspace.ProviderFencingGeneration is not null || workspace.LeaseRevision is not null ||
            provision.ResourceKind != "Sandbox" || provision.OperationId != pod.ProvisionOperationId ||
            provision.ResourceId != provision.OperationId.ToString("D") ||
            provision.ProviderResource != sandbox || provision.ResourceGeneration != sandbox.Generation ||
            provision.ProviderFencingGeneration != pod.FencingGeneration || provision.LeaseRevision is not > 0 ||
            provision.TransitionRevision is not null ||
            environment.CompletedAt > metadata.CreatedAt || metadata.CompletedAt > workspace.CreatedAt ||
            workspace.CompletedAt > provision.CreatedAt ||
            new[] { environment, metadata, workspace, provision }.Any(receipt =>
                receipt.OperationId == Guid.Empty || receipt.CreatedAt.UtcTicks / 10 < root.AcceptedAt.UtcTicks / 10 ||
                receipt.CompletedAt < receipt.CreatedAt || receipt.CompletedAt > now))
            throw new RuntimeAuthorizationException("runtime_p1_creation_binding_mismatch");
    }
}
