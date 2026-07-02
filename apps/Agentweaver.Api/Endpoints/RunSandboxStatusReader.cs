using Agentweaver.Api.Contracts;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

internal static class RunSandboxStatusReader
{
    public static SandboxStatusDto? GetSandboxStatus(Run run, IReadOnlyList<RunEvent>? streamEvents)
    {
        var selectedInfo = streamEvents?
            .FirstOrDefault(e => string.Equals(e.Type, "sandbox.selected", StringComparison.Ordinal))
            is { Payload: var payload }
                ? SandboxSelectionInfo.FromPayload(payload)
                : null;

        if (selectedInfo is not null)
        {
            return new SandboxStatusDto
            {
                Backend = selectedInfo.Backend ?? run.SandboxBackend ?? string.Empty,
                IsRealIsolation = selectedInfo.IsRealIsolation,
                SelectionReason = selectedInfo.Reason,
                HasNetworkWarning = streamEvents!.Any(e => string.Equals(e.Type, "sandbox.warning", StringComparison.Ordinal)),
                ClaimName = selectedInfo.ClaimName ?? run.SandboxClaimName,
                PodName = selectedInfo.PodName ?? run.SandboxPodName,
                Namespace = selectedInfo.Namespace ?? run.SandboxNamespace,
            };
        }

        if (string.IsNullOrWhiteSpace(run.SandboxBackend))
            return null;

        return new SandboxStatusDto
        {
            Backend = run.SandboxBackend!,
            IsRealIsolation = string.Equals(run.SandboxBackend, "kubernetes-sandbox-claim", StringComparison.Ordinal),
            SelectionReason = null,
            HasNetworkWarning = false,
            ClaimName = run.SandboxClaimName,
            PodName = run.SandboxPodName,
            Namespace = run.SandboxNamespace,
        };
    }
}
