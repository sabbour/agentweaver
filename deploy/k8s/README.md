# P0 Kubernetes base layout (Kustomize)

**Status:** scaffolding only. Applying this layout creates a namespace,
quota/limit guardrails, a default-deny network policy, and one stable
`ServiceAccount` (`foundation-probe`) — nothing that runs a workload. No
`Deployment`, `StatefulSet`, or `Service` exists here, and no
product workload runs from this base. Identity is the product host.
Relay is a library and release is a CLI, not runtime services.
The `foundation-probe` `ServiceAccount` is reserved for the acceptance-only
#1784 AKS Job. The operator CLI does not run under that ServiceAccount.
#1784 owns its image receipt, executable probe, pod workload-identity label,
and explicit egress overlay.

## Why Kustomize, not Helm

This slice has no templated values beyond the `foundation-probe` identity's
workload-identity client ID, which an environment-specific overlay patches
in (see below). Kustomize needs no extra runtime or chart repository and
keeps the base layout plain, versioned YAML — consistent with the
Node-stdlib-only, dependency-free tooling used elsewhere in this phase.

## Layout

| Path | Purpose |
| --- | --- |
| `namespace.yaml` | Dedicated, bounded namespace `agentweaver-v1-p0` with restricted Pod Security Standards labels. |
| `resourcequota.yaml` | Namespace-wide compute/object ceilings. |
| `limitrange.yaml` | Per-container default and max compute requests/limits. |
| `networkpolicy-default-deny.yaml` | Default-deny ingress and egress for every pod in the namespace, plus a narrow DNS-only egress allowance so workloads are not fully network-dead. Every workload must add its own scoped egress policy for anything beyond DNS. |
| `serviceaccounts/foundation-probe-sa.yaml` | Reserved `foundation-probe` ServiceAccount for the #1784 Job. Its pod must carry the workload-identity label. |
| `kustomization.yaml` | Ties the above together. |

## Workload identity wiring

Each `ServiceAccount` annotation (`azure.workload.identity/client-id`,
`azure.workload.identity/tenant-id`) is a `CHANGEME-*` placeholder in this
base layout. An environment overlay (not included here, since it is
environment-specific and not reusable) patches these from the
`identity.bicep` module's `identityClientIds`/tenant outputs after a real
deployment. No client ID or tenant ID is a secret, but neither is invented
or hardcoded here, since this base layout is not tied to any one deployed
environment.

The `CHANGEME` base is not deploy-ready. DNS-only egress does not permit
Key Vault, Blob, PostgreSQL, token exchange, or Monitor access by itself.
The DNS peer requires both the `kube-system` namespace label and the `k8s-app: kube-dns` pod label.
Only TCP and UDP port 53 receive this allowance.
Pods in other namespaces do not receive port-53 access.

The federated credential's trusted subject is
`system:serviceaccount:agentweaver-v1-p0:foundation-probe`, matching this
exact `ServiceAccount` name and namespace — see
`infra/bicep/modules/identity.bicep`.

## Validating locally

```powershell
kubectl kustomize deploy/k8s/base
```

This only renders the manifests; it requires no cluster connection.
