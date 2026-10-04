# P0 Kubernetes source layout (Kustomize)

**Status:** source definitions only. No cluster was changed. The base renders
the Identity runtime Deployment, an HTTPS `ClusterIP` Service, its dedicated
runtime `ServiceAccount`, and scoped Cilium egress. It also contains the
acceptance-only `foundation-probe` ServiceAccount. The separate migration
Job is not included in the base and does not run during ordinary host startup.

There is no public Ingress, Gateway, or ingress controller in these manifests.
AKS Application Routing remains configured with its default NGINX controller
type set to `None`. The Identity Service is cluster-internal.

## Layout

| Path | Purpose |
| --- | --- |
| `base/namespace.yaml`, `resourcequota.yaml`, `limitrange.yaml` | Bounded namespace and pod/resource ceilings. |
| `base/networkpolicy-default-deny.yaml` | Default-deny ingress/egress and DNS-only standard NetworkPolicy. |
| `base/serviceaccounts/foundation-probe-sa.yaml` | Reserved identity for the #1784 acceptance-only Job. |
| `base/identity-broker/` | Identity runtime ServiceAccount, HTTPS Deployment, ClusterIP Service, and narrow Cilium policy. |
| `migrations/identity-broker/` | Separate migration ServiceAccount, one-shot Job, and PostgreSQL/token egress policy. |
| `acceptance/foundation-probe/` | #1784 executable Job and its own scoped egress overlay. |

The runtime listens with HTTPS on port 8443. Its ClusterIP Service exposes
port 443. Cilium permits health probes from the node and requests only from
same-namespace pods labeled `agentweaver.io/identity-client: "true"`.
It permits DNS, Entra token exchange, the configured upstream OIDC hosts,
the exact PostgreSQL host, and the exact Key Vault host. The migration policy
permits DNS, Entra token exchange, and PostgreSQL only.

## Required operator inputs

The manifests reference, but do not create, these objects:

| Reference | Required contents |
| --- | --- |
| `identity-broker-runtime-config` ConfigMap | `ConnectionStrings__IdentityBroker`, public HTTPS issuer, external OIDC authority and client ID, exact `IdentityBroker__Clients__...` registration, redemption audience and Key Vault URI, runtime workload-identity tenant/client/token-file settings, and `IdentityBroker__DataProtectionKeyPath`. |
| `identity-broker-signing` Secret | `signing.pfx` and its `password`; the PFX signs/encrypts tokens and protects the durable data-protection key ring. |
| `identity-broker-upstream-oidc` Secret | `clientSecret` for the registered external OIDC client. |
| `identity-broker-client-secrets` Secret | Optional `IdentityBroker__Clients__<index>__ClientSecret` keys for each configured confidential client. Public clients have no secret. |
| `identity-broker-tls` Secret | Approved `tls.crt` and `tls.key` for Kestrel HTTPS. No certificate is created here. |
| `identity-broker-key-ring` PVC | Durable writable storage mounted for the protected ASP.NET data-protection key ring. The host runs one replica with a recreate strategy. |
| `identity-broker-migration-config` ConfigMap | `ConnectionStrings__IdentityBrokerMigration` and the separate migration workload-identity tenant, client, and absolute projected-token-file settings. |

All connection strings must omit passwords. The runtime database username is
the separately bootstrapped Entra runtime role. The migration connection uses
the separately bootstrapped schema-owner role. The runtime and migration
ServiceAccount client-ID and tenant annotations are placeholders; patch them
from `identityBrokerRuntimeIdentity` and `identityBrokerMigrationIdentity`
outputs and the selected tenant. Do not use the `foundation-probe` identity.

The image reference uses a non-deployable `registry.invalid`/zero-digest
sentinel. Replace it with the approved immutable registry image digest in an
environment overlay. Replace each `CHANGEME-*` egress host with the exact
approved endpoint; do not add wildcard or public egress rules. The upstream
OIDC authority and metadata/JWKS endpoints may require separate exact hosts.

The ordinary base never runs migrations. After separate approval, apply the
Identity bootstrap SQL as the PostgreSQL Entra administrator, then explicitly
run the migration Job from `deploy/k8s/migrations/identity-broker`. It uses a
different ServiceAccount and workload identity. The runtime has no schema
ownership or migration privileges; missing schema/configuration/authentication
fails startup.

## Workload identity wiring

The Bicep identity module defines three separate UAMIs:
`foundation-probe`, `identity-broker`, and `identity-broker-migration`.
Each federated subject must match its exact ServiceAccount in
`agentweaver-v1-p0`. The Identity runtime alone receives Key Vault Secrets User
on the existing vault. The migration identity has no Azure resource role.
PostgreSQL access comes from the separately approved SQL principals and grants,
not Azure RBAC.

The default-deny policy only allows DNS to `kube-system` pods labeled
`k8s-app: kube-dns`, on TCP/UDP port 53. Each workload has its own Cilium
policy for destinations beyond DNS. The `CHANGEME-*` values make these
definitions non-deployable until approved environment inputs are supplied.

## Validating locally

```powershell
kubectl kustomize deploy\k8s\base
kubectl kustomize deploy\k8s\migrations\identity-broker
kubectl kustomize deploy\k8s\acceptance\foundation-probe
```

These commands only render manifests. They require no cluster connection and
do not prove a running workload, image, token exchange, network policy, or Azure
permission.
