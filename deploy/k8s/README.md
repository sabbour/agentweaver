# P0 Kubernetes source layout (Kustomize)

**Status:** source definitions only. No cluster was changed. The root
Kustomization renders the complete P0 product installation: nine shared service
Deployments and Services, their ServiceAccounts, runtime ConfigMaps, the provider
catalog, namespace controls, and required network policies. The AgentHost image
and per-Sandbox image metadata are pinned through Environment's runtime configuration.
The root also includes the AgentHost runtime ConfigMap, but it is marked
incomplete and Environment's optional AgentHost launch profile stays unset until
the exact receiver endpoint, queue bound, and existing TLS Secret name are sourced.
Operator-owned Secrets, public CA data, Gateway placement/listeners, and TLS
certificates remain outside this installation. The product bundle includes the
Portal Web HTTPRoute to the existing `portal` listener. The separate migration
Job and acceptance Foundation Probe are not part of the product bundle.

The base contains no public Ingress or Gateway.
The separate candidate routing manifests use the native `approuting-istio`
Gateway class. AKS keeps its default NGINX controller type at `None`.
The Identity Service remains a ClusterIP Service.

## Layout

| Path | Purpose |
| --- | --- |
| `base/namespace.yaml`, `resourcequota.yaml`, `limitrange.yaml` | Bounded namespace and pod/resource ceilings. |
| `base/networkpolicy-default-deny.yaml` | Default-deny ingress/egress and DNS-only standard NetworkPolicy. |
| `base/environment-agenthost-network.yaml`, `base/service-owner-network.yaml` | Cilium policies for AgentHost, exact service-owner calls, Broker issuer validation, and workload-identity database access. |
| `base/events-and-sessions-network.yaml`, `base/projects-config-network.yaml` | Cilium policies for source-backed owner callers and exact database, identity, Blob, and marketplace egress. |
| `base/serviceaccounts.yaml`, `base/workloads/` | Product ServiceAccounts and static Deployment/Service resources for the shared workloads. |
| `base/identity-broker/` | Identity runtime Deployment, HTTPS ClusterIP Service, and narrow Cilium policy. |
| `migrations/identity-broker/` | Separate migration ServiceAccount, one-shot Job, and PostgreSQL/token egress policy. |
| `routing/identity-broker/` | Explicit Gateway namespace/policy, native certificates, HTTPS Gateway, Broker/Web/Gateway API/MCP HTTPRoutes, backend TLS policies, and narrow ingress policies. |
| `acceptance/foundation-probe/` | #1784 executable Job, ServiceAccount, and its own scoped egress overlay. |
| `profiles/` | The source-managed AgentHost runtime ConfigMap and runtime-value validators. |
| `provider-catalogs/` | Source-bound Projects & Config provider snapshot and immutable ConfigMap manifest. |
| `runtime-configs/` | Non-secret P0 runtime ConfigMaps and nested Environment Cilium options. |
| `kustomization.yaml` | Complete P0 product bundle; receipt-pinned images, workloads, runtime maps, provider catalog, namespace controls, and network policies. |
| `scripts/release/runtime-list.mjs` | Runtime workload contract and integration-harness List source; the release installer applies the checked-in Kustomize bundle. |

The runtime listens with HTTPS on port 8443. Its ClusterIP Service exposes
port 443. Cilium permits health probes from the node and requests only from
same-namespace pods labeled `agentweaver.io/identity-client: "true"`, plus
Environment's exact runtime-owner path on TCP 8443.
It permits DNS, Entra token exchange, the configured upstream OIDC hosts,
the exact PostgreSQL host, and the exact Key Vault host. The migration policy
permits DNS, Entra token exchange, and PostgreSQL only.

The shared owner policies keep external egress workload-scoped. OIDC consumers
can resolve and reach only the configured public Broker issuer on TCP 443.
Environment, Knowledge, and Orchestrator can reach Entra on TCP 443 and the
P0 PostgreSQL host on TCP 5432. Environment can reach only its configured owner
Services on TCP 8443 and the Kubernetes API entity on TCP 443. AgentHost can
reach only its configured Broker, Orchestrator, Environment, and Events
Services on TCP 8443. Events and Projects retain their service-specific exact
FQDN and owner rules.

The typed AgentHost Sandbox profile adds the stable `agentweaver.io/workload: agenthost`
and `agentweaver.io/identity-client: "true"` labels to the actual controller-owned Pod.
The latter selects the existing Identity Broker network rule; it grants no application
identity or authorization. The profile asks the Sandbox controller to create a private
ClusterIP Service. The provider accepts only HTTPS on TCP 8443 and verifies that the
Service selector resolves to the actual AgentHost Pod. The profile does not create a
shared AgentHost Deployment or Service.

## Runtime configuration inputs

The runtime ConfigMap JSON files and static workload manifests under
`runtime-configs/` and `base/workloads/`, plus the AgentHost ConfigMap under
`profiles/agenthost/`, are the product's Kustomize source. The Projects and
Events profile scripts validate reviewed runtime values only; they do not emit
Kubernetes resources. Update the appropriate checked-in
ConfigMap data and use the root Kustomization to render or apply the complete
bundle. Images, ServiceAccounts, ports, TLS mounts, and the provider-catalog
prefix stay in Kustomize, not in a JavaScript-generated workload list.

The Projects runtime uses the existing password-free database mapping and
workload identity. Its static Deployment reads the immutable provider catalog
with the `ProjectsConfig__ProviderCatalog__` prefix. The catalog, TLS Secret,
database grants, migrations, runtime rights, and trusted TLS remain separate
prerequisites. See [Projects & Config](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/projects-config.md)
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
published source used to derive the registrations and defaults. The root
Kustomize composition includes this catalog with the workloads, runtime
ConfigMaps, and network policies. This source change does not apply any
manifests to the cluster.

The manifest uses the relative double-underscore keys expected by the
Projects runtime; the static Deployment adds the
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
along with other startup inputs. The catalog and Kustomize resources do not
prove that the service runtime ConfigMaps are ready. The Kustomize workloads
supply the catalog to both Projects & Config and Orchestrator, and record the
catalog-review commit in each pod's
`agentweaver.io/provider-catalog-source` annotation. Use the merge commit of
this catalog PR as `--provider-catalog-source-commit`; do not substitute the
published product source SHA used by the snapshot.

Release-source defaults are used for Sessions, Messaging, and Memory. The other
included P1 options revisions use the authorized bounded label
`p1-v1.0.0`; this is not an artifact or deployment claim.

The Kustomize workloads explicitly set the five matching `p1-v1.0.0` revision
values for Environment Sandbox, Storage, and Cilium, Orchestrator AGT Policy,
and Events & Sessions Copilot Cost. It also sets the decided Cilium adapter
version. These `env` entries override same-named `envFrom` values while
preserving each existing runtime ConfigMap reference. The checked-in repository includes runtime ConfigMap snapshots; their value
provenance and remaining live gates are listed below.

GitHub has no adapter-local or project-configuration revision setting, but its
existing accepted run-selection input contains
`providers[].candidates[].optionsRevision`. Projects & Config copies the
catalog candidate's revision into that input, and Orchestrator checks it
against the catalog before use. The checked-in Deployments pass the same
catalog ConfigMap to Projects & Config and Orchestrator using the
`ProjectsConfig__ProviderCatalog__` prefix. Exact keys and schema values for included providers are documented in
[Projects & Config](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/projects-config.md#checked-in-p1-provider-catalog).
The catalog defaults only documented providers, selects Cilium for the L3/L4
Network Policy layer, maps Copilot Cost to the source-defined
`copilot.nano_aiu` meter, and grants no project overrides. Consumer
configuration and runtime readiness remain separate prerequisites. The
catalog ConfigMap contains no credentials, endpoint values, or secret references.

### P0 runtime ConfigMap snapshots

The manifests under `deploy/k8s/runtime-configs/` contain no credentials. All
nine service runtime ConfigMaps are checked-in snapshots and are resources in
the root Kustomization. The versioned Identity Broker snapshot preserves the
P0 acceptance client and loopback redirect and adds the public `agentweaver-web`
client for the Portal callback. The Environment, Orchestrator, Identity Broker,
and Cilium maps are immutable. The Events and Sessions map is mutable so an operator can
update its versioned rate-card document without rebuilding the product. After
changing that mutable map, apply the full Kustomize bundle and restart the
affected workload so it reads the new values. Replacing an immutable runtime
map requires an explicitly reviewed Kustomize resource and reference update;
do not try to patch immutable data in place.
Each static Deployment references its service-named
`<service>-runtime-config` map through `envFrom`. Environment mounts
`environment-cilium-config` read-only at
`/app/appsettings.Production.json`. The nested Cilium selector keys contain
`/` and `:`, so they are supplied as JSON rather than Kubernetes environment
variable names. The Environment Deployment supplies Cilium `AdapterVersion`
`1.0.0` by coordinator decision and the selected `OptionsRevision`
`p1-v1.0.0`.

The Environment, Orchestrator, Events and Sessions, MCP, Projects & Config,
Gateway, Web, and Knowledge maps have source-backed runtime inputs and no
incomplete marker. This source change does not run a live runtime preflight.
The helper validates the rendered install and live operator-owned prerequisites
before applying it. The Cilium-only map is
complete for its options but does not by itself establish Environment readiness.
The per-Sandbox `agenthost-runtime-v1` map is also rendered and marked incomplete.
It carries only source-backed owner addresses, the broker issuer, shared model
bindings, and the measured AgentHost image identity. It is not mounted because
the optional Environment AgentHost profile is absent. Do not enable that profile
until a per-Sandbox receiver endpoint/audience, approved `MaximumPendingTurns`,
and an exact existing AgentHost TLS Secret name are available.

| Manifest | Value provenance | Remaining live gates |
| --- | --- | --- |
| `identity-broker-runtime-config.json` | Immutable `identity-broker-runtime-config-v2` preserves acceptance client 0 and adds public client `agentweaver-web` with the Portal `/auth/callback` redirect and the Web's scopes. `IdentityBroker__WebOrigin` sets the exact Portal origin for credentialed Broker OAuth CORS. Client 0's loopback redirect remains acceptance-only. The external Entra app remains the Broker's upstream provider and its `/signin-oidc` callback is unchanged. No Secret data or run labels are included. | The Broker Deployment references the v2 map so Kustomize creates the new immutable map and rolls the Pod instead of mutating the previous map. The acceptance bootstrap emits the same v2 map and keeps client 0 run-scoped. |
| `environment-runtime-config.json` | Database host, name, role, and tenant come from P0 handoff §7. The client ID is the live user-assigned identity matched to the handoff principal ID. The projected token path follows the checked-in runtime input profile. Broker authority/audience come from the existing non-secret `identity-broker-runtime-config-v2`. Service addresses and ports come from the checked-in Kustomize Service definitions. Namespace, StorageClass, and RuntimeClass are the approved P0 AKS values. Approved sizing: 1 CPU and 2 GiB per Sandbox, 600-second reconciliation and provisioning timeouts, 250-ms polls, startup budgets 30/90/30/30/30/180 seconds, and a 100-GiB Azure Files cap. These values pass the Agent Sandbox and Azure Files validators; the checked-in namespace LimitRange permits the 2-GiB container limit. Provider IDs, schema versions, image receipt inputs, and options revisions come from the catalog and Kustomize workloads. | No additional snapshot values are awaiting a source. Live preflight and database readiness remain separate acceptance gates. |
| `profiles/agenthost/runtime-config.example.yaml` | Broker, Orchestrator, Environment, and Events addresses match the Kustomize Services; the authority comes from the non-secret Broker issuer. Model bindings/revision match Events. Image digest, `linux/amd64` platform, and compressed bytes match the receipt-pinned Kustomize image and Environment's existing deployment inputs. | Incomplete: `AgentHost.ConfigureEndpoint` and `Authentication.Audience` require the exact per-Sandbox HTTPS service endpoint; `AgentHost.MaximumPendingTurns` has no approved P0 value; `Environment__Sandbox__AgentSandbox__AgentHost__TlsSecretName` has no approved existing Secret name. The Environment AgentHost profile remains unset. |
| `orchestrator-runtime-config.json` | Database and workload identity follow the same P0 handoff and live identity lookup. Schema is `orchestrator` from handoff §7. Issuer/audiences come from the existing non-secret Broker ConfigMap. Owner addresses match checked-in Kustomize Services. The approved AGT Policy resource is `agt-policy:p1-v1.0.0`, generation `1`, revision `p1-v1.0.0`. The platform document defaults to deny and has one exact `action_id ==` allow rule for each ID in `RuntimeActionContract.ActionIds`: `model.turn`, `tool.read`, `tool.write`, `exec.shell`, `exec.read`, `exec.write`, and `exec.stop`. The provider test validates and evaluates this document. The `grant_id != '' and fence != ''` expression did not allow a request with the runtime's numeric fence, so the policy uses the explicit catalog-action fallback. `ExecutableActionGuard` checks the current grant and fence before policy evaluation. | No additional snapshot values are awaiting a source. Live preflight and database readiness remain separate acceptance gates. |
| `events-and-sessions-runtime-config.json` | Database, role, tenant, and schema follow handoff §7 and the live identity lookup. Issuer/audiences come from the Broker ConfigMap; owner addresses match checked-in Kustomize Services. Provider and Copilot Cost resource IDs are coordinator-approved opaque labels, not Azure resource IDs; both generations are `1`. Sessions and Messaging revisions, schema, polling, retention, claim lease, and message lifetime use release-source defaults. Copilot Cost schema `2`, adapter options revision `p1-v1.0.0`, meter `copilot.nano_aiu`, unit `AIC`, and the nano-AIU conversion follow the adapter contract. Its versioned JSON rate card is runtime ConfigMap data, not compiled model pricing. Card ID `github-copilot-published`, version `2026-10-17`, source URL, retrieval date, 33 published model entries, and content hash identify the GitHub snapshot. The shared model map preserves the `reasoning` and `coding` aliases and adds exact `gpt-6.1-sol` and `gpt-6-luna` keys as `HostedCopilot` at revision `p1-v1.0.1`; the per-Sandbox AgentHost settings use the same map. The approved Session Material URI is `https://awv1p0blob.blob.core.windows.net/session-material`. | No additional snapshot values are awaiting a source. The container's private access and workload-identity request remain live acceptance checks. |
| `gateway-runtime-config.json` | Issuer and audience come from the existing non-secret Broker ConfigMap. Five HTTPS owner addresses match checked-in Kustomize Services. `Gateway__WebOrigin` is `https://portal.6ac31fa7dd86cf00014876f0.eastus2euap.aksapp.io`, the Portal listener hostname under the AKS App Routing managed domain. The root Kustomization includes the matching Web HTTPRoute. | The live Gateway currently has only its `https` listener; apply the reviewed Gateway source with the `portal` listener and verify App Routing DNS/TLS before claiming live Portal readiness. |
| `mcp-runtime-config.json` | Issuer and audience come from the existing non-secret Broker ConfigMap. The HTTPS Gateway address matches the checked-in Kustomize Service. | No additional snapshot values are awaiting a source. Live preflight and Gateway readiness remain separate acceptance gates. |
| `web-runtime-config.json` | Gateway path and Broker URL/issuer follow the Web host and browser runtime defaults plus the existing non-secret Broker ConfigMap. Client ID is `agentweaver-web`; the configured scopes are `openid profile email api.read offline_access projects.admin projects.orchestrator`, with `projects.admin` omitted from run-bound requests. The browser derives `/auth/callback` from the Portal origin, and that exact redirect belongs to the Broker public client, not the upstream Entra app. | The Web route is source-managed, but the live Gateway still needs its `portal` listener and live DNS/TLS verification. |
| `knowledge-runtime-config.json` | Broker issuer/audience come from the existing non-secret Broker ConfigMap. Projects and Events addresses match checked-in Kustomize Services. The Knowledge client ID maps by read-only Azure lookup to `aw-v1-p0-id-knowledge`; tenant and token path follow the checked-in profile. PostgreSQL username follows the existing workload-identity role naming convention. Provider resource ID is `agentweaver.knowledge`; generation `1` is the first instance. | Verify that PostgreSQL role `aw-v1-p0-id-knowledge` exists and has the required grants before deployment; the read-only role query was unavailable. Do not change the resource ID for this store; increment generation only when it is replaced. |
| `projects-config-runtime-config.json` | Database and workload identity values follow the approved Projects runtime mapping. Authentication authority and audience come from the existing non-secret Broker ConfigMap. The product Deployment uses the existing catalog reference; Broker run-binding is not enabled, so Identity Broker ingress remains for its direct Copilot connection and Remote MCP Projects API calls. | No additional snapshot values are awaiting a source. Live preflight and database readiness remain separate acceptance gates. |
| `environment-cilium-config.json` | Namespace and endpoint selectors follow the checked-in P0 Cilium policies. Options schema `1` follows the release-source adapter contract. Adapter version is `1.0.0` by coordinator decision; options revision `p1-v1.0.0` is set in the Environment Deployment. | None for the Cilium options in this file. Environment runtime values are source-backed; live readiness is checked separately. |

Built-in task defaults reference `gpt-6.1-sol` for planning, review, design,
security, creative writing, and complex work, and `gpt-6-luna` for code-writing
and straightforward work, all at high reasoning effort. Runtime model binding
uses an exact reference-key lookup, so the map now contains entries under both
model IDs as well as the existing `reasoning` and `coding` aliases. The aliases
remain available to project and run selections. Projects & Config stores those
selections in its database; this snapshot change does not write or migrate that
state. A project-wide `ModelSelection` still pins one model and effort for every
task, while task, role, and agent routes apply when that pin is absent.

GitHub's [Models and pricing](https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing)
page was checked on 2026-10-17. The runtime card contains the published USD
per-million-token rates and context tiers for all 33 listed models. It records
the source URL, snapshot version, retrieval date, and GitHub's `1 AIC = $0.01 USD`
unit definition. It does not derive or configure scalar multipliers.

Missing rates and incomplete measurements remain explicitly `Unpriced` with a
reason and event count. Unpriced usage remains visible, does not block admission
or dispatch, and does not count toward Copilot credit limits. Known priced usage
continues to count; malformed or tampered receipts fail closed. A model not
advertised by the Copilot SDK cannot start a session. Foundry rate-card support is tracked in
[#1924](https://github.com/sabbour/agentweaver/issues/1924).

### Updating Copilot prices and enabling models without a rebuild

The Events runtime ConfigMap is mutable so an operator can update its rate-card
JSON. The card itself remains a versioned snapshot: update `version`,
`snapshotVersion`, and `retrievedOn` whenever a new source snapshot is supplied.
Keep `EventsAndSessions__Cost__Copilot__OptionsRevision` aligned with the
provider catalog (`p1-v1.0.0` for this snapshot); the rate-card version identifies
pricing data and changes independently.
Copy only rates published on GitHub's pricing page. Leave unlisted model or token
rates absent; they remain `Unpriced` rather than being estimated. Startup rejects
a malformed card. The runtime records its exact JSON SHA-256 in cost snapshots
and receipts.

To enable a Copilot model, use its exact ID from the SDK model list. AgentHost
already calls `Models.ListAsync` and checks the configured ID; this path needs no
new network egress or cloud permission. Update
`AgentHost__ModelBindings__<reference>__ModelId` and
`AgentHost__ModelBindings__<reference>__SourceMode=HostedCopilot` in
`events-and-sessions-runtime-config.json`, then increment
`AgentHost__ModelBindingsRevision` there. Add published token rates under the
same exact ID when they exist. The provider catalog registers provider adapters;
it is not the per-model allowlist. Projects & Config accepts a selected
reference only when it appears in `AvailableModelSelectionReferences`; reuse an
existing available reference such as `reasoning` or `coding`, or add the exact
task-default reference when configuring built-in task routes. A model ID used
as a task reference must also be an exact key in `AgentHost:ModelBindings`.
Projects returns `model_selection_unavailable` when a selected reference is not
available or no model selection is configured. A missing rate for an otherwise
available model does not block admission or dispatch.

Edit the checked-in runtime ConfigMap and Deployment source, then use the same
root Kustomize path as every other product change:

```powershell
kubectl kustomize deploy\k8s
kubectl apply -k deploy\k8s
kubectl rollout restart deployment/events-and-sessions -n agentweaver-v1-p0
kubectl rollout status deployment/events-and-sessions -n agentweaver-v1-p0
```

Kubernetes does not restart a process when data in a referenced ConfigMap
changes. Restart the affected workload after changing environment-backed
configuration. Keep the Events runtime map and Environment AgentHost runtime
settings aligned when changing model bindings. The profile script validates
operator-selected Events values; it does not produce manifests. These steps
change configuration and deployment state only; they do not rebuild the product.

The P0 handoff supplies the PostgreSQL roles, schemas, and Entra principal IDs.
Read-only Azure identity lookup supplied the corresponding client IDs; the
PostgreSQL ARM inventory confirmed the server ID and the AKS metadata confirmed
the P0 OIDC issuer. The Broker issuer is read from the non-secret
`identity-broker-runtime-config-v2`; no Secret data is needed for these inputs.
The catalog snapshot's `sourceCommit` and ConfigMap annotation identify the
published product source `c24d0000fe5fc6163e4f38594c39dff782d3a46d`. The deploy
command's `--provider-catalog-source-commit` identifies the catalog review
commit instead.

The source-backed values are covered by local configuration checks. The live
runtime preflight checks the selected target, source-managed configuration, and
operator-owned prerequisites before applying anything. The full product
installation uses one Kustomize bundle:

```powershell
kubectl apply -k deploy\k8s
```

The root composition includes all product Deployments, Services, ServiceAccounts,
runtime ConfigMaps, the Environment Cilium options, Projects provider catalog,
default-deny/DNS NetworkPolicies, and Cilium policies required by the
bootstrap. The owner policies include exact Broker-issuer HTTPS, scoped
workload-identity PostgreSQL/Entra access, Environment's Kubernetes API path,
and configured owner-service calls; they do not grant broad external egress.
It resolves the Identity Broker egress template from the checked-in non-secret
P0 map and pins product images through Kustomize `images:` entries.
Secrets, public CA data, migration resources, and Gateway placement remain
operator-owned. The three existing Gateway ingress policies are included
without creating Gateway placement resources. Do not apply or bootstrap while a
workload-required runtime ConfigMap is marked incomplete or while the approved
Gateway namespace remains unresolved. The unreferenced
`agenthost-runtime-v1` ConfigMap is intentionally incomplete and does not block
preflight while Environment's optional AgentHost profile remains unset. The
Gateway namespace and enforcement policy (`restricted` or `baseline`) remain
explicit inputs. `Gateway__WebOrigin` is the source-backed Portal URL; the live
Gateway still requires its `portal` listener and App Routing DNS/TLS
verification. The release helper checks these inputs and receipt-pinned images
before it invokes `kubectl apply -k`.

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
| `identity-broker-runtime-config-v2` ConfigMap | `ConnectionStrings__IdentityBroker`, public HTTPS issuer, external OIDC authority and client ID, exact `IdentityBroker__Clients__...` registrations, redemption audience and Key Vault URI, runtime workload-identity tenant/client/token-file settings, and `IdentityBroker__DataProtectionKeyPath`. It preserves acceptance client 0 and adds the public `agentweaver-web` client. |
| Portal Web route and existing App Routing Gateway | The root bundle contains the P0 `web-application` HTTPRoute for `portal.6ac31fa7dd86cf00014876f0.eastus2euap.aksapp.io`; the Gateway placement remains operator-owned in `agentweaver-v1-gateway`. Its `portal` listener uses the existing `DefaultDomainCertificate` managed-domain certificate. The live Gateway currently has no `portal` listener, so apply the reviewed Gateway source and verify DNS/TLS before acceptance. |
| `aw-v1-p0-id-knowledge` PostgreSQL role | Knowledge uses this Entra workload-identity role with resource ID `agentweaver.knowledge` and generation `1`. Verify the role and its grants before deployment; the role name is source-backed but live existence has not been established. |
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
The P0 runtime prerequisite composition binds the Identity
Broker policy to its exact P0 PostgreSQL, Key Vault, and Entra hosts. It leaves
the existing Gateway ingress policy selectors unchanged until the Gateway
namespace and enforcement policy are approved.

## Validating locally

```powershell
kubectl kustomize deploy\k8s\base
kubectl kustomize deploy\k8s
kubectl kustomize deploy\k8s\migrations\identity-broker
kubectl kustomize deploy\k8s\routing\identity-broker
kubectl kustomize deploy\k8s\acceptance\foundation-probe
```

These commands only render manifests. They require no cluster connection and
do not prove a running workload, image, token exchange, network policy, or Azure
permission.

## Published v1 release rollout

Use the root Kustomization for the complete product installation. The
interactive operator path is `kubectl apply -k deploy\k8s`, after all
workload-required runtime inputs and Gateway placement values have been
resolved. The optional AgentHost ConfigMap can remain incomplete while its
Environment profile is unset. The guarded
local `npm run release:deploy` and `npm run release:deploy-bootstrap` commands
use that same source bundle. Their default is a read-only dry-run; `--execute`
applies the rendered Kustomize bundle only after receipt, image, target, runtime
preflight, TLS/CA, and existing Broker-routing checks pass. The bootstrap
command is pre-acceptance only; it does not record release acceptance.

The helper verifies the GitHub annotated tag and Release, tagged source SHA,
publication receipt, all ten public GHCR images, and the exact Azure/kubectl
target. It creates a temporary Kustomize copy with product image digests
replaced by the verified receipt values, then checks that those exact pins are
present in the rendered install. It runs `kubectl apply --dry-run=client -k`
before any write and uses `kubectl apply -k` for execution. It does not create a
JavaScript-generated Deployment or Service list. `scripts/release/runtime-list.mjs`
remains the shared service contract used by the integration harness and
preflight checks. The measured AgentHost image is configured in the Environment
runtime map for per-Sandbox creation; AgentHost is not a shared Deployment.
Anonymous GHCR verification resolves its exact published `linux/amd64` child
manifest, checks image labels, and measures the manifest, config, and compressed
layers. The Environment settings pin that digest, platform, and byte total.

Pass the target explicitly each time: subscription
`90ab3701-83f0-4ba1-a90a-f2e68683adab`, tenant
`72f988bf-86f1-41af-91ab-2d7cd011db47`, resource group `aw-v1-p0`, cluster
`aw-v1-p0-aks`, namespace `agentweaver-v1-p0`, and node bounds 2–3. The helper
uses existing Azure CLI and kubectl contexts only after verifying them against
those values. It does not login, fetch AKS credentials, switch contexts, or
consult any 0.x target.

The Kustomize bundle includes the nine shared product Deployments and private
ClusterIP Services, ServiceAccounts, runtime ConfigMaps, provider catalog,
default-deny/DNS NetworkPolicies, and required Cilium policies. Each workload
uses its service identity, HTTPS health endpoints, and restricted pod/container
security profile. The bundle does not contain Secrets, private keys, public CA
data, a database migration Job, database grants, TLS certificates, or Gateway
placement resources. Those remain explicit operator prerequisites; the
installer does not broaden default-deny policy.

The checked-in Cilium policies cover Identity Broker, Events & Sessions,
Projects & Config, AgentHost, Environment, Gateway, MCP, Orchestrator, Knowledge,
and Web. Events accepts requests from AgentHost, Orchestrator, Gateway,
Knowledge, and Environment, plus node health probes. Its egress is limited to
the configured service owners, Broker issuer, Entra, PostgreSQL, and Blob
endpoint. Projects accepts requests from Identity Broker, Orchestrator,
Gateway, Events, Knowledge, and Environment, plus node health probes. Its
egress is limited to Broker, Entra, PostgreSQL, GitHub API, and raw content
hosts; it does not permit optional Blob storage. Both policies constrain DNS
and use exact Cilium FQDN and port rules. The three Gateway ingress policies
remain blocked on the approved Gateway namespace and namespace policy mode
(`restricted` or `baseline`). This source does not create or select Gateway
placement. Route and certificate resources remain outside the root product
bundle.

The release helper verifies the source-managed resources in the rendered
Kustomize bundle and checks operator-owned ServiceAccounts, ConfigMaps, Secrets,
PVCs, certificates, and routing prerequisites in the target namespace. It
rejects every ConfigMap marked incomplete and Gateway policies that still
contain `CHANGEME-GATEWAY-NAMESPACE`. An explicit `--execute` applies the whole
Kustomize installation, then repeats live target, receipt/image provenance,
rollout, pod-readiness, and running-image-ID checks. `--resume` can skip the
apply stage only when its checkpoint binds the exact tag, source SHA, release
manifest, publication receipt, rendered Kustomize resources, and target. A
successful verification removes that checkpoint; checkpoint I/O errors are
reported.
