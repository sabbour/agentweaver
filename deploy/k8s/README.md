# P0 Kubernetes source layout (Kustomize)

**Status:** source definitions only. No cluster was changed. The base renders
the Identity runtime Deployment, an HTTPS `ClusterIP` Service, its dedicated
runtime `ServiceAccount`, and scoped Cilium egress. It also contains the
acceptance-only `foundation-probe` ServiceAccount. The separate migration
Job is not included in the base and does not run during ordinary host startup.

The base contains no public Ingress or Gateway.
The separate candidate routing manifests use the native `approuting-istio`
Gateway class. AKS keeps its default NGINX controller type at `None`.
The Identity Service remains a ClusterIP Service.

## Layout

| Path | Purpose |
| --- | --- |
| `base/namespace.yaml`, `resourcequota.yaml`, `limitrange.yaml` | Bounded namespace and pod/resource ceilings. |
| `base/networkpolicy-default-deny.yaml` | Default-deny ingress/egress and DNS-only standard NetworkPolicy. |
| `base/serviceaccounts/foundation-probe-sa.yaml` | Reserved identity for the #1784 acceptance-only Job. |
| `base/identity-broker/` | Identity runtime ServiceAccount, HTTPS Deployment, ClusterIP Service, and narrow Cilium policy. |
| `migrations/identity-broker/` | Separate migration ServiceAccount, one-shot Job, and PostgreSQL/token egress policy. |
| `routing/identity-broker/` | Explicit Gateway namespace/policy, native certificates, HTTPS Gateway, HTTPRoute, backend TLS policy, and narrow Broker ingress. |
| `acceptance/foundation-probe/` | #1784 executable Job and its own scoped egress overlay. |

The runtime listens with HTTPS on port 8443. Its ClusterIP Service exposes
port 443. Cilium permits health probes from the node and requests only from
same-namespace pods labeled `agentweaver.io/identity-client: "true"`.
It permits DNS, Entra token exchange, the configured upstream OIDC hosts,
the exact PostgreSQL host, and the exact Key Vault host. The migration policy
permits DNS, Entra token exchange, and PostgreSQL only.

## Managed routing candidate

`DefaultDomainCertificate` supplies an AKS-managed wildcard certificate and
the `identity-broker-tls` Secret in each namespace.
The Gateway terminates public HTTPS on port 443.
The HTTPRoute targets the Broker Service on port 443.
BackendTLSPolicy requires HTTPS to Kestrel and validates its certificate
with system trust and the exact route hostname.
`CHANGEME-MANAGED-HOST` requires the `agentweaver` prefix within the certificate's
reported wildcard domain.
The zone root is not a service hostname.

These manifests do not prove DNS, TLS, or a running Broker.
The P0 namespace retains its `restricted` policy.
The current managed Gateway pod lacks the seccomp profile that this policy
requires. AKS rejects security-context overrides through its Gateway
customization ConfigMap.
Gateway acceptance remains blocked until an approved compatible placement exists.
No namespace policy downgrade or direct managed-Deployment patch is authorized.
`CHANGEME-GATEWAY-NAMESPACE` and `CHANGEME-GATEWAY-POLICY` require explicit approved inputs.
The guarded installer supports only the dedicated `agentweaver-v1-gateway`
namespace with an explicitly selected `restricted` or `baseline` enforcement policy.
Neither policy is a default. Existing ownership and policy must match.
The Gateway listener admits routes only from `agentweaver-v1-p0`.
The Broker ingress rule admits HTTPS on port 8443 only from that namespace's
pods with the exact generated Gateway label.
The base workload policy remains unchanged.
The upstream callback remains `/signin-oidc`.
A corrected callback registration requires separate approval after route
hostname, DNS, and TLS verification.
The installer defaults to a read-only callback plan.
Its separate registration option requires confirmation of the exact callback URI.
It fresh-reads the selected tenant and application before a public-only append.
An existing exact URI remains a no-op.
A route receipt does not prove a running Broker.
The separate readiness option requires successful trusted HTTPS responses
from both health endpoints at the observed Gateway IP.

## Required operator inputs

The ordinary base references, but does not create, these objects:

| Reference | Required contents |
| --- | --- |
| `identity-broker-runtime-config` ConfigMap | `ConnectionStrings__IdentityBroker`, public HTTPS issuer, external OIDC authority and client ID, exact `IdentityBroker__Clients__...` registration, redemption audience and Key Vault URI, runtime workload-identity tenant/client/token-file settings, and `IdentityBroker__DataProtectionKeyPath`. |
| `identity-broker-signing` Secret | `signing.pfx` and its `password`; the PFX signs/encrypts tokens and protects the durable data-protection key ring. |
| `identity-broker-client-secrets` Secret | Optional `IdentityBroker__ExternalProvider__ClientSecret` for a confidential upstream client and `IdentityBroker__Clients__<index>__ClientSecret` keys for configured confidential clients. Public clients omit their secrets. |
| `identity-broker-tls` Secret | Approved `tls.crt` and `tls.key` for Kestrel HTTPS. No certificate is created here. |
| `identity-broker-key-ring` PVC | Durable writable storage mounted for the protected ASP.NET data-protection key ring. The host runs one replica with a recreate strategy. |
| `identity-broker-migration-config` ConfigMap | `ConnectionStrings__IdentityBrokerMigration` and the separate migration workload-identity tenant, client, and absolute projected-token-file settings. |

The guarded installer has separate default-off options for initial application
state and a run-owned public acceptance-client configuration.
`--bootstrap-identity-broker-state` creates only missing signing material and
the 1 GiB, ReadWriteOnce key-ring PVC.
It preserves existing material and rejects an orphaned key ring without its
original signing Secret.
The certificate protects tokens and stored keys, not public HTTPS.
The native managed TLS Secret remains separate.

`--bootstrap-identity-broker-runtime` creates the explicit nonsecret ConfigMap
and missing runtime objects from this base. Existing objects must match.
The client ID includes the supplied acceptance-run UUID.
The redirect is an explicit HTTP loopback callback with a port.
This option creates no upstream application or credential.
The route-derived issuer remains configuration-only until actual health and
public routing observations pass.
See the [installer guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md)
for the required inputs and retained-state boundaries.

The external OIDC client secret is optional. When omitted, the Broker uses
authorization-code flow with PKCE as a public OIDC client. When a confidential
upstream client is explicitly configured, provide its secret through the
operator-managed `identity-broker-client-secrets` Secret.

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

## One-time bootstrap commands

The Broker executable provides an explicit
`--bootstrap-identity-postgres` maintenance command. It uses the operator's
Azure CLI login only on the operator host, then connects with Npgsql using
`VerifyFull` TLS and the configured PostgreSQL FQDN. It creates the configured
database, two exact Entra workload roles, and the `identity_broker` schema
only when no unexpected user database or partial bootstrap exists. It does
not run migrations or run as part of ordinary startup. Supply
`ConnectionStrings:IdentityBrokerBootstrap` and the
`IdentityBroker:Bootstrap:*` values for the approved exact server, admin,
database, role names, and UAMI principal object IDs.

The PostgreSQL bootstrap is available through the existing installer only
with both `--execute` and the separate, default-off
`--bootstrap-identity-postgres` option. It supports the guarded full-foundation
path and AKS-only redeployment to the verified existing P0 target; the
full-foundation empty-resource-group guard remains unchanged. For the approved
private PostgreSQL endpoint, the installer creates a temporary TCP-only proxy
pod and loopback-only port-forward. The operator host keeps the password-free
Azure CLI Entra token and connects through that tunnel using `VerifyFull` TLS
with the PostgreSQL FQDN as the certificate target. It removes only the exact
run-owned pod and ConfigMap, stops its exact port-forward process, and removes
temporary local files. It does not create a public route, app registration,
secret, or Azure role assignment. An interrupted database creation requires
manual reconciliation of the exact database target before retry. Ordinary
`--execute` does not run the database bootstrap.

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
kubectl kustomize deploy\k8s\routing\identity-broker
kubectl kustomize deploy\k8s\acceptance\foundation-probe
```

These commands only render manifests. They require no cluster connection and
do not prove a running workload, image, token exchange, network policy, or Azure
permission.
