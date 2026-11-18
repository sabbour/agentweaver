using System.Text.Json;
using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;

namespace Agentweaver.EventsAndSessions;

/// <summary>Reads accepted selection and pins a run before allowing its Copilot runtime admission.</summary>
internal sealed class RuntimeRunAdmissionService(
    HttpClient client, ProjectsAuthorizationContextOptions options,
    IProjectsAuthorizationContextClient projects, NativeUsageReceiptConsumer consumer,
    RuntimeModelBindingsResolver models, TimeProvider timeProvider)
{
    /// <summary>Requires acceptRunSelection authority and rechecks selection and caller authority around pinning.</summary>
    internal async Task<RuntimeRunAdmissionReceipt> ReadAsync(
        HttpContext context, string projectId, string runId,
        RuntimeRunAdmissionRequest request, CancellationToken cancellationToken)
    {
        RuntimeRunAdmissionContract.ValidateRequest(request);
        var first = await projects.GetCurrentAsync(context, cancellationToken).ConfigureAwait(false);
        NativeUsageApplicationService.RequireReadAuthority(first);
        if (first.BoundProjectId != projectId || first.BoundRunId != runId ||
            !first.EffectiveAuthority.Any(resource => resource.ResourceType == "project" &&
                resource.ResourceId == projectId && !resource.Permissions.IsDefault &&
                resource.Permissions.Any(permission =>
                    permission.Permission == "acceptRunSelection" && permission.RoleRevision > 0)))
            throw new RuntimeAuthorizationException("runtime_run_admission_authority_denied");
        if (!Uri.TryCreate(options.OwnerBaseAddress, UriKind.Absolute, out var owner))
            throw new RuntimeAuthorizationException("runtime_run_admission_owner_unavailable");
        var bearer = await NativeUsageApplicationService.BearerAsync(context, timeProvider)
            .ConfigureAwait(false);
        try
        {
            var actor = new RuntimeActorAuthorization(bearer, first.TenantId);
            async Task<JsonElement> ReadSelection(CancellationToken token)
            {
                using var document = await RuntimeOwnerHttpTransport.SendAsync<JsonDocument>(
                    client, owner,
                    $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/selection",
                    actor, null, token).ConfigureAwait(false);
                var snapshot = document.RootElement.Clone();
                RuntimeRunAdmissionContract.ValidateSelectionScope(snapshot, projectId, runId);
                if (RuntimeRunAdmissionContract.SelectionHash(snapshot) != request.AcceptedSelectionHash)
                    throw new RuntimeAuthorizationException("runtime_run_admission_selection_changed");
                return snapshot;
            }
            var selection = await ReadSelection(cancellationToken).ConfigureAwait(false);
            var model = RuntimeAcceptedModelSelection.Read(selection);
            var rateCardModels = RuntimeAcceptedModelSelection.ReadAll(selection)
                .Where(candidate => candidate.SourceMode == ModelSourceMode.Byok &&
                    candidate.ProviderBindingRevision is not null)
                .ToImmutableArray();
            RuntimeModelBindingPin pin;
            if (model.SourceMode == ModelSourceMode.HostedCopilot &&
                model.ConnectionId is not null && model.CredentialReference is null)
            {
                _ = models.Resolve(model.Reference, model.SourceMode, model.ModelBindingPin);
                pin = models.Pin(model.Reference, ModelSourceMode.HostedCopilot, model.ReasoningEffort);
            }
            else if (model.SourceMode == ModelSourceMode.Byok)
            {
                if (model.ProviderBindingRevision is { } providerBinding)
                {
                    _ = models.ResolveAcceptedRuntimeBinding(model, providerBinding);
                    pin = model.ModelBindingPin
                        ?? throw new RuntimeAuthorizationException("runtime_model_binding_pin_required");
                }
                else
                {
                    _ = models.Resolve(model.Reference, model.SourceMode, model.ModelBindingPin);
                    pin = model.ModelBindingPin
                        ?? models.Pin(model.Reference, ModelSourceMode.Byok, model.ReasoningEffort);
                }
            }
            else
                throw new RuntimeAuthorizationException("runtime_run_admission_model_mode_invalid");
            foreach (var rateCardModel in rateCardModels)
            {
                if (rateCardModel.ProviderBindingRevision is not { } providerBinding)
                    throw new RuntimeAuthorizationException("runtime_run_admission_model_mode_invalid");
                _ = models.ResolveAcceptedRuntimeBinding(rateCardModel, providerBinding);
            }
            return await consumer.ReadRunAdmissionAsync(
                first.TenantId, projectId, runId, request, selection, model, pin, rateCardModels, async token =>
                {
                    _ = await ReadSelection(token).ConfigureAwait(false);
                    var current = await projects.GetCurrentAsync(context, token).ConfigureAwait(false);
                    NativeUsageApplicationService.RequireReadAuthority(current);
                    if (current.TenantId != first.TenantId || current.BoundProjectId != projectId ||
                        current.BoundRunId != runId || current.ActorId != first.ActorId ||
                        !current.EffectiveAuthority.Any(resource => resource.ResourceType == "project" &&
                            resource.ResourceId == projectId && !resource.Permissions.IsDefault &&
                            resource.Permissions.Any(permission =>
                                permission.Permission == "acceptRunSelection" && permission.RoleRevision > 0)) ||
                        !bearer.IsUsable())
                        throw new RuntimeAuthorizationException("runtime_run_admission_authority_changed");
                }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            bearer.Invalidate();
        }
    }
}
