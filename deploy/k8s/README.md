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
| `provider-catalogs/` | Source-bound Projects & Config provider snapshot and immutable ConfigMap manifest. |
| `runtime-configs/` | Non-secret immutable P0 runtime ConfigMaps and nested Environment Cilium options. |
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
Service selector resolves to the actual AgentHost Pod. The profile does not create a
shared AgentHost Deployment or Service.

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

### Projects & Config provider catalog

The checked-in snapshot at
`deploy/k8s/provider-catalogs/projects-config-provider-catalog.snapshot.json`
is derived from the `v1.0.0` release source commit
`c24d0000fe5fc6163e4f38594c39dff782d3a46d`. Its rendered, immutable
ConfigMap manifest is
`deploy/k8s/provider-catalogs/projects-config-provider-catalog.json`. It is
named `projects-config-provider-catalog` in `agentweaver-v1-p0`; its
`agentweaver.io/provider-catalog-product-source` annotation identifies the
published source used to derive the registrations and defaults. Apply it
separately only after the incomplete service maps below have authoritative
values. Its `kubectl apply` command appears in the ordered operator handoff
below; do not apply any of these manifests from this PR.

The manifest uses the relative double-underscore keys expected by the
Projects runtime profile; the profile adds the
`ProjectsConfig__ProviderCatalog__` prefix. It registers Native Sessions,
Native Memory, Agent Sandbox, Azure Files CSI, AGT Policy, GitHub, Copilot Cost,
and Cilium, plus the foundational native Messaging provider. Cilium's
`AdapterVersion` is `1.0.0`. Coordinator decision: adapter code version shipped
in Environment 1.0.0. Its `OptionsRevision` is the selected bounded
label `p1-v1.0.0`, and the L3/L4 layer selects Cilium.

The namespace `agentweaver-v1-p0` and endpoint selectors are derived from
`base/environment-agenthost-network.yaml`,
`base/service-owner-network.yaml`, and `base/identity-broker/egress.yaml`.
Each selector pins `k8s:io.kubernetes.pod.namespace` to that namespace and
`agentweaver.io/workload` to the policy's exact workload label. Environment
still requires these options through its separate runtime configuration,
along with other startup inputs. The catalog and List entries do not prove
that the service runtime ConfigMaps are complete. The bootstrap List supplies
the catalog to both Projects & Config and Orchestrator, and records the
catalog-review commit in each pod's
`agentweaver.io/provider-catalog-source` annotation. Use the merge commit of
this catalog PR as `--provider-catalog-source-commit`; do not substitute the
published product source SHA used by the snapshot.

Release-source defaults are used for Sessions, Messaging, and Memory. The other
included P1 options revisions use the authorized bounded label
`p1-v1.0.0`; this is not an artifact or deployment claim.

The bootstrap List explicitly sets the five matching `p1-v1.0.0` revision
values for Environment Sandbox, Storage, and Cilium, Orchestrator AGT Policy,
and Events & Sessions Copilot Cost. It also sets the decided Cilium adapter
version. These `env` entries override same-named `envFrom` values while
preserving each existing runtime ConfigMap reference. The checked-in repository
now includes runtime ConfigMap snapshots; their unresolved inputs are listed
below.

GitHub has no adapter-local or project-configuration revision setting, but its
existing accepted run-selection input contains
`providers[].candidates[].optionsRevision`. Projects & Config copies the
catalog candidate's revision into that input, and Orchestrator checks it
against the catalog before use. The bootstrap List passes the same catalog
ConfigMap to Projects & Config and Orchestrator using the
`ProjectsConfig__ProviderCatalog__` prefix. Exact keys and schema values for included providers are documented in
[Projects & Config](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/projects-config.md#checked-in-p1-provider-catalog).
The catalog defaults only documented providers, selects Cilium for the L3/L4
Network Policy layer, maps Copilot Cost to the source-defined
`copilot.nano_aiu` meter, and grants no project overrides. Consumer
configuration and runtime readiness remain separate prerequisites. The
catalog ConfigMap contains no credentials, endpoint values, or secret references.

### P0 runtime ConfigMap snapshots

The manifests under `deploy/k8s/runtime-configs/` are immutable and contain no
credentials. The bootstrap List requires the three service-named
`<service>-runtime-config` maps through `envFrom`. It also mounts
`environment-cilium-config` read-only at
`/app/appsettings.Production.json`. The nested Cilium selector keys contain
`/` and `:`, so they are supplied as JSON rather than Kubernetes environment
variable names. The List supplies Cilium `AdapterVersion` `1.0.0` by
coordinator decision and the selected `OptionsRevision` `p1-v1.0.0`.

The Environment and Orchestrator service maps carry
`agentweaver.io/runtime-config-status: incomplete`. Kubernetes does not enforce
this annotation, but runtime preflight rejects any referenced ConfigMap with
this marker. The Events and Sessions map now has its approved provider and cost
identity labels, but this source change does not run a live runtime preflight.
Keep the markers while any required input remains unresolved. The deployment
preflight reads applied ConfigMaps, so it cannot run against these un-applied
source files while their markers are present. Do not apply these manifests or
run the bootstrap while any listed input remains unresolved.
The Cilium-only map is complete for its options; it does not make the Environment
runtime configuration deployable.

| Manifest | Value provenance | Required inputs not yet authoritative |
| --- | --- | --- |
| `environment-runtime-config.json` | Database host, name, role, and tenant come from P0 handoff §7. The client ID is the live user-assigned identity matched to the handoff principal ID. The projected token path follows the checked-in runtime input profile. Broker authority/audience come from the existing non-secret `identity-broker-runtime-config`. Service addresses and ports come from the generated runtime List. Namespace, StorageClass, and RuntimeClass are the approved P0 AKS values. Approved sizing: 1 CPU and 2 GiB per Sandbox, 600-second reconciliation and provisioning timeouts, 250-ms polls, startup budgets 30/90/30/30/30/180 seconds, and a 100-GiB Azure Files cap. These values pass the Agent Sandbox and Azure Files validators; the checked-in namespace LimitRange permits the 2-GiB container limit. Provider IDs, schema versions, image receipt inputs, and options revisions come from the catalog and bootstrap List. | No additional snapshot values are awaiting a source. The runtime preflight has not been run, so this map remains marked incomplete. |
| `orchestrator-runtime-config.json` | Database and workload identity follow the same P0 handoff and live identity lookup. Schema is `orchestrator` from handoff §7. Issuer/audiences come from the existing non-secret Broker ConfigMap. Owner addresses come from the generated runtime List. The approved AGT Policy resource is `agt-policy:p1-v1.0.0`, generation `1`, revision `p1-v1.0.0`. The platform document defaults to deny and has one exact `action_id ==` allow rule for each ID in `RuntimeActionContract.ActionIds`: `model.turn`, `tool.read`, `tool.write`, `exec.shell`, `exec.read`, `exec.write`, and `exec.stop`. The provider test validates and evaluates this document. The `grant_id != '' and fence != ''` expression did not allow a request with the runtime's numeric fence, so the policy uses the explicit catalog-action fallback. `ExecutableActionGuard` checks the current grant and fence before policy evaluation. | No additional snapshot values are awaiting a source. The runtime preflight has not been run, so this map remains marked incomplete. |
| `events-and-sessions-runtime-config.json` | Database, role, tenant, and schema follow handoff §7 and the live identity lookup. Issuer/audiences come from the Broker ConfigMap; owner addresses come from the runtime List. Provider and Copilot Cost resource IDs are coordinator-approved opaque labels, not Azure resource IDs; both generations are `1`. Sessions and Messaging revisions, schema, polling, retention, claim lease, and message lifetime use release-source defaults. Copilot Cost schema `2`, meter `copilot.nano_aiu`, unit `AIC`, the nano-AIU conversion, and the provider-published token-rate snapshot come from release-source code. The card ID is `github-copilot-published`, version `2026-10-17`; it records GitHub's published rates, source URL, and retrieval date. Model prices are not configured as multipliers. The shared model map binds `reasoning` to `gpt-6.1-sol` and `coding` to `gpt-6-luna` as `HostedCopilot` at revision `p1-v1.0.0`; the AgentHost profile uses the same map. The approved Session Material URI is `https://awv1p0blob.blob.core.windows.net/session-material`; the Events profile renderer requires and uses this explicit input. The container and role grant are separate prerequisites and were not changed here. | None. The Session Material container and its role grant remain separate prerequisites. The source manifest has not been applied or preflighted. |
| `environment-cilium-config.json` | Namespace and endpoint selectors follow the checked-in P0 Cilium policies. Options schema `1` follows the release-source adapter contract. Adapter version is `1.0.0` by coordinator decision; options revision `p1-v1.0.0` is selected and supplied by the bootstrap List. | None for the Cilium options in this file. The separate Environment runtime map remains incomplete. |

The platform default model is `gpt-6.1-sol` through the `reasoning` reference;
a project can override it with `gpt-6-luna` through `coding`. Projects & Config
stores these selections in its database, so this snapshot change does not write
or migrate that state. v1 resolves one model per run from the project selection
or platform default; it has no task-type-based or per-agent model routing yet
([#2035](https://github.com/sabbour/agentweaver/issues/2035)).
The requested `high` reasoning effort is not applied in v1 because the runtime
binding path has no setting for it
([#2036](https://github.com/sabbour/agentweaver/issues/2036)).

GitHub's [Models and pricing](https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing)
page was checked on 2026-10-17. The provider-owned card prices input, cached
input, cache-write, and output tokens directly from the published USD rates for
`gpt-6.1-sol` and `gpt-6-luna`, with default and long-context tiers. It records
the source URL, snapshot version, retrieval date, and GitHub's `1 AIC = $0.01 USD`
unit definition. It does not derive or configure scalar multipliers.

Unknown models and incomplete measurements remain explicitly `Unpriced` with a
reason and event count. Unpriced usage remains visible, does not block admission
or dispatch, and does not count toward Copilot credit limits. Known priced usage
continues to count; malformed or tampered receipts fail closed. Foundry
rate-card support is tracked in
[#1924](https://github.com/sabbour/agentweaver/issues/1924).

The P0 handoff supplies the PostgreSQL roles, schemas, and Entra principal IDs.
Read-only Azure identity lookup supplied the corresponding client IDs; the
PostgreSQL ARM inventory confirmed the server ID and the AKS metadata confirmed
the P0 OIDC issuer. The Broker issuer is read from the non-secret
`identity-broker-runtime-config`; no Secret data is needed for these inputs.
The catalog snapshot's `sourceCommit` and ConfigMap annotation identify the
published product source `c24d0000fe5fc6163e4f38594c39dff782d3a46d`. The deploy
command's `--provider-catalog-source-commit` identifies the catalog review
commit instead.

After every listed unsourced value has an authoritative source, run the
repository's local configuration checks and review the rendered data. Remove an
`incomplete` annotation only for maps whose inputs are fully sourced and pass
those checks. Apply the completed maps only with separate deployment approval;
the live runtime preflight then checks the applied ConfigMaps and other
Kubernetes prerequisites before acceptance. This PR does not apply any map or
run that preflight. The future apply order is:

```powershell
kubectl apply -f deploy\k8s\provider-catalogs\projects-config-provider-catalog.json
kubectl apply -f deploy\k8s\runtime-configs\environment-runtime-config.json
kubectl apply -f deploy\k8s\runtime-configs\environment-cilium-config.json
kubectl apply -f deploy\k8s\runtime-configs\orchestrator-runtime-config.json
kubectl apply -f deploy\k8s\runtime-configs\events-and-sessions-runtime-config.json
```

These commands are documented for the later operator handoff only; do not run
them from this PR. The three service maps remain incomplete and none of these
ConfigMaps has been applied.

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
The source routes do not prove that the managed Gateway accepts traffic.
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
approved endpoint; do not add wildcard or public egress rules. Add separate
exact hosts for the upstream OIDC authority and metadata/JWKS endpoints when
their hostnames differ.

The ordinary base never runs migrations. After separate approval, apply the
Identity bootstrap SQL as the PostgreSQL Entra administrator, then explicitly
run the migration Job from `deploy/k8s/migrations/identity-broker`. It uses a
different ServiceAccount and workload identity. The runtime has no schema
ownership or migration privileges; missing schema/configuration/authentication
fails startup.

## One-time bootstrap commands

The Broker executable provides an explicit
`--bootstrap-identity-postgres` maintenance command. It uses the operator's
Azure CLI login only on the operator host, then uses Npgsql with
`VerifyFull` TLS to connect to the configured PostgreSQL FQDN. It creates the configured
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
Azure CLI Entra token and connects through that tunnel with `VerifyFull` TLS
and the PostgreSQL FQDN as the certificate target. It removes only the exact
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
operator can instead supply a complete rendered list with `--runtime-manifest`.
The tool verifies the GitHub
annotated tag and Release, source SHA, all ten public GHCR manifests, the exact
Azure/kubectl target, and referenced prerequisites before it can render or apply.
The measured AgentHost image is not deployed as a shared workload. Anonymous
GHCR verification resolves the exact published `linux/amd64` child manifest.
It checks the image config against the prepared source labels and measures the
published manifest, config, and compressed layers. The Environment Deployment pins that
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

The supplied runtime List can contain only nine pairs of `Deployment` and
`Service` objects. AgentHost must not appear there. All referenced ServiceAccounts,
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
pods in the explicitly selected Gateway namespace, on HTTP port 8080. The source also defines the Web root route and Gateway API routes for `/api/v1`
and exact `/openapi/v1.json`. It defines exact `/mcp` and the protected-resource
metadata path to MCP. The release tool does not create or manage these routes.
The other owner policies cover source-verified internal service edges; they do
not supply missing external endpoints or prove live traffic.

No current source release is deployable: `releases/foundation.json` remains
`draft`, no product publication receipt exists, and the nine-service runtime
List is generated from checked-in source but has not been deployed. The source
now supports a run-scoped AgentHost
Sandbox profile, but the profile does not prove production scheduling, a live
controller-created Service, or complete runtime delivery. Existing
service-specific runtime configuration, including service ConfigMaps, TLS
Secrets, the Projects provider catalog, and database/access/TLS prerequisites
also remain operator-owned. The renderer only references those existing objects;
it does not supply values, create them, or guess database mappings.

An explicit `--execute` applies only those nine shared-workload Deployments and
ClusterIP Services. `--resume` can skip an already-applied stage only when its
checkpoint binds the exact tag, source SHA, release manifest, publication
receipt, runtime List, and target. It always repeats live target, image
provenance, rollout, pod-readiness, and running-image-ID checks. A successful
verification removes that checkpoint; checkpoint I/O errors are reported.
