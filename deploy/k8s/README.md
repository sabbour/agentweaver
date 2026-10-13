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
| `base/environment-agenthost-network.yaml`, `base/service-owner-network.yaml` | Cilium policies for the per-Sandbox AgentHost and source-verified in-cluster service-owner edges. |
| `base/serviceaccounts/foundation-probe-sa.yaml` | Reserved identity for the #1784 acceptance-only Job. |
| `base/identity-broker/` | Identity runtime ServiceAccount, HTTPS Deployment, ClusterIP Service, and narrow Cilium policy. |
| `migrations/identity-broker/` | Separate migration ServiceAccount, one-shot Job, and PostgreSQL/token egress policy. |
| `routing/identity-broker/` | Explicit Gateway namespace/policy, native certificates, HTTPS Gateway, Broker/Web/Gateway API/MCP HTTPRoutes, backend TLS policies, and narrow ingress policies. |
| `acceptance/foundation-probe/` | #1784 executable Job and its own scoped egress overlay. |
| `profiles/projects-config/` | Separately selected Projects runtime source renderer; requires existing catalog and TLS references and explicit runtime inputs. |
| `scripts/release/runtime-list.mjs` | Checked-in source for the nine shared-service Kubernetes `List`, rendered from validated publication images and explicit provider-catalog inputs. |

The runtime listens with HTTPS on port 8443. Its ClusterIP Service exposes
port 443. Cilium permits health probes from the node and requests only from
same-namespace pods labeled `agentweaver.io/identity-client: "true"`.
It permits DNS, Entra token exchange, the configured upstream OIDC hosts,
the exact PostgreSQL host, and the exact Key Vault host. The migration policy
permits DNS, Entra token exchange, and PostgreSQL only.

The typed AgentHost Sandbox profile adds the stable `agentweaver.io/workload: agenthost`
and `agentweaver.io/identity-client: "true"` labels to the actual controller-owned Pod.
The latter selects the existing Identity Broker network rule; it grants no application
identity or authorization. The profile asks the Sandbox controller to create a private
ClusterIP Service. The provider accepts only HTTPS on TCP 8443 and verifies that the
Service selector resolves to the actual AgentHost Pod. This is not a shared AgentHost
Deployment or Service.

## Selected Projects runtime profile

The ordinary Broker base and Foundation Probe do not include this profile.
`profiles/projects-config/render.mjs` reads operator-reviewed JSON and prints
a Kubernetes `List`. It never runs a cloud command or applies that output.
The example leaves the image, image user/group, TLS Secret, catalog reference,
catalog source commit, authority, and audience unset. Missing inputs stop rendering.

The profile fixes the existing Projects identity and passwordless database mapping.
It exposes only private HTTPS on port 8443 with system trust unchanged.
It requires an existing Projects-only PEM Secret and a nonzero immutable
`ghcr.io/sabbour/agentweaver.projects.config` product digest.
The image user and group must match the reviewed image.

The catalog owner must supply an existing, immutable, nonempty ConfigMap
in `agentweaver-v1-p0`, reviewed against the exact `providerCatalog.sourceCommit`.
Its keys are relative catalog keys, such as `Registrations__0__Id`.
The renderer adds `ProjectsConfig__ProviderCatalog__` through `envFrom.prefix`.
Catalog keys cannot replace database, authentication, or HTTPS listener settings.
The real .NET catalog loader remains responsible for catalog validation.
Rendering checks the reference and commit format, not object existence,
catalog contents, immutability, source binding, or production suitability.
Those checks remain prerequisites for any separately approved deployment.

The profile neither creates that catalog nor changes provider selections.
It adds no Blob permission, credential, custom certificate trust, database grant,
migration job, or public route. Existing database roles, applied migrations,
runtime rights, trusted TLS, and healthy owner flows remain unverified here.
See [Projects & Config](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/projects-config.md)
for the startup and acceptance boundary.

## Managed routing candidate

`DefaultDomainCertificate` supplies an AKS-managed wildcard certificate and
the `identity-broker-tls` Secret in each namespace.
The Gateway terminates public HTTPS on port 443.
The HTTPRoute targets the Broker Service on port 443.
BackendTLSPolicy requires HTTPS to Kestrel and validates its certificate
with system trust and the exact route hostname.
The portal listener uses `portal.<managed-domain>`. Its Web route sends `/`
to the Web Service on port 8080. Its Gateway API route sends `/api/v1` and the
exact `/openapi/v1.json` path to the Gateway Service on port 8443. Exact
`/mcp` and `/.well-known/oauth-protected-resource/mcp` paths send MCP traffic
to the MCP Service on port 8443. Gateway and MCP
use their own service TLS Secrets, `gateway-tls` and `mcp-tls`. Each leaf
certificate must contain the exact service DNS name as a SAN and be directly
signed by the public CA in the operator-owned `service-backend-ca` ConfigMap.
MCP mounts that ConfigMap read-only to validate Gateway certificates. Private
keys stay in the service TLS Secrets.
BackendTLSPolicies validate the exact service DNS name against that CA.
These are source-defined routes, not proof that the managed Gateway is accepting traffic.
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
| `gateway-tls` and `mcp-tls` Secrets; `service-backend-ca` ConfigMap | Operator-owned Kestrel certificates for Gateway and MCP, plus the public `ca.crt` that signs both leaf certificates. The leaves must include `gateway.agentweaver-v1-p0.svc.cluster.local` and `mcp.agentweaver-v1-p0.svc.cluster.local` as their respective SANs. Release preflight verifies the CA and leaf validity, hostnames, and direct signature. |
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

The separate P1 identity template uses the same federation module for
`projects-config`, `environment`, `knowledge`, `events-and-sessions`, and
`orchestrator` ServiceAccounts in this namespace.
It creates no database or Azure resource grant.
Workload manifests must use each service's observed client ID, not the Broker
or Probe identity.
Schema initialization and runtime database grants require separate approval.

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

## Published v1 release rollout

The local `npm run release:deploy` command is a separate, opt-in consumer of a
published v1 composition. Its default is a read-only dry-run. It requires a
release-stage manifest with exact image digests and the matching
`publication.json` receipt from the immutable source-bound GitHub result. By
default, the checked-in `scripts/release/runtime-list.mjs` source renders a
Kubernetes `List` containing one restricted Deployment and private ClusterIP
Service for each of the nine shared product services. It takes the Projects
provider-catalog ConfigMap name and reviewed source commit explicitly. An
operator may instead supply a complete rendered list with `--runtime-manifest`.
The tool verifies the GitHub
annotated tag and Release, source SHA, all ten public GHCR manifests, the exact
Azure/kubectl target, and referenced prerequisites before it can render or apply.
The measured AgentHost image is not deployed as a shared workload: anonymous
GHCR verification resolves the exact published `linux/amd64` child manifest,
checks its config against the prepared source labels, and measures the published
manifest, config, and compressed layers. The Environment Deployment pins that
child digest, platform, and byte total for the per-Sandbox provider. Resume also
checks the live Environment pin and liveness/readiness probes. The Foundation
Probe is excluded.

The target must be passed explicitly each time: subscription
`90ab3701-83f0-4ba1-a90a-f2e68683adab`, tenant
`72f988bf-86f1-41af-91ab-2d7cd011db47`, resource group `aw-v1-p0`, cluster
`aw-v1-p0-aks`, namespace `agentweaver-v1-p0`, and node bounds 2–3. The tool
uses the existing Azure CLI and kubectl contexts only after verifying them
against those values. It does not login, fetch AKS credentials, switch contexts,
or consult any 0.x target.

Only nine pairs of `Deployment` and `Service` objects may appear in the supplied
runtime List. AgentHost must not appear there. All referenced ServiceAccounts,
runtime ConfigMaps, Secrets, PVCs, and service-specific Cilium policies must
already exist in this namespace.
Deployments must pin the exact GHCR digest, use the existing per-service
identity, use HTTPS health endpoints, and retain the restricted pod/container
security profile. No Secret or ConfigMap data is supplied through the release
tool. It will not create a Namespace, identity, access policy, migration Job,
database, ingress, Gateway, or TLS certificate, and it will not broaden the
default-deny network policy.

The checked-in source defines required Cilium policy names for Identity Broker,
Events & Sessions, Projects & Config, AgentHost, Environment, Gateway, MCP,
Orchestrator, Knowledge, and Web. The optional routing overlay defines Web
ingress only from node health probes and the exact `identity-broker` Gateway
pods in the explicitly selected Gateway namespace, on HTTP port 8080. The source also
defines the Web root route, Gateway API routes for `/api/v1` and exact
`/openapi/v1.json`, and exact `/mcp` plus the protected-resource metadata path to MCP; the release tool does not create
or manage these routes.
The other owner policies cover source-verified internal service edges; they do
not supply missing external endpoints or prove live traffic.

No current source release is deployable: `releases/foundation.json` remains
`draft`, no product publication receipt exists, and the nine-service runtime
List is generated from checked-in source but has not been deployed. The source
now supports a run-scoped AgentHost
Sandbox profile, but this does not prove production scheduling, a live
controller-created Service, or complete runtime delivery. Existing
service-specific runtime configuration, including service ConfigMaps, TLS
Secrets, the Projects provider catalog, and database/access/TLS prerequisites
also remain operator-owned. The renderer only references those existing objects;
it does not supply values, create them, or guess database mappings.

An explicit `--execute` applies only those nine shared-workload Deployments and
ClusterIP Services. `--resume` may skip an already-applied stage only when its
checkpoint binds the exact tag, source SHA, release manifest, publication
receipt, runtime List, and target. It always repeats live target, image
provenance, rollout, pod-readiness, and running-image-ID checks. A successful
verification removes that checkpoint; checkpoint I/O errors are reported.
