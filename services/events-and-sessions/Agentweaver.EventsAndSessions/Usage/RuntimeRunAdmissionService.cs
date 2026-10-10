using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;

namespace Agentweaver.EventsAndSessions;

internal sealed class RuntimeRunAdmissionService(
    HttpClient client, ProjectsAuthorizationContextOptions options,
    IProjectsAuthorizationContextClient projects, NativeUsageReceiptConsumer consumer,
    RuntimeModelBindingsResolver models, TimeProvider timeProvider)
{
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
            if (model.SourceMode != ModelSourceMode.HostedCopilot || model.ConnectionId is null ||
                model.CredentialReference is not null)
                throw new RuntimeAuthorizationException("runtime_run_admission_model_mode_invalid");
            var pin = models.Pin(model.Reference, ModelSourceMode.HostedCopilot);
            return await consumer.ReadRunAdmissionAsync(
                first.TenantId, projectId, runId, request, selection, model, pin, async token =>
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
