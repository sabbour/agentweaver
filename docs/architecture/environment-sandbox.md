# Environment Sandbox leases

This page documents the unpublished v1 Environment source candidate for issue
[#1852](https://github.com/sabbour/agentweaver/issues/1852). It is not a deployment
record and does not claim that a live Kubernetes cluster or AgentHost runtime was
tested.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-environment-sandbox-lifecycle.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-environment-sandbox-lifecycle.png'" alt="Environment verifies current Projects authority, the exact Workspace PVC generation, and Cilium policy generation before dispatch. It records an owner-fenced lease and immutable provider request, then provisions and observes the exact agent-sandbox resources. ReadyForDispatch requires the same verified network generation and actual Pod, PVC, and isolation evidence. A differing provider result that arrives after release is stored separately and reclaimed by fenced reconciliation without changing terminal lease evidence. Workspace retention is unchanged." />
  </a>
  <figcaption>Environment Sandbox lease and recovery sequence. A ready lease requires exact Workspace and network observations; retirement releases Sandbox placement only, not the Environment or its Workspace data. Late differing provider results use a separate durable cleanup record.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-environment-sandbox-lifecycle.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-environment-sandbox-lifecycle.drawio'">Open editable draw.io source</a></p>

## Ownership and admission

`EnvironmentSandboxManager` owns the Sandbox lease for the complete tenant,
project, run, and Environment tuple. `EnvironmentGenerationFence` protects the
Environment lifecycle; each Sandbox resource also has a resource generation, a
provider fencing generation, and a current fencing generation. The PostgreSQL
lease store uses the Environment owner's advisory lock and compare-and-swap
checks, and stores the immutable run selection, provider/options snapshot,
idempotency key, and provider recovery request before provider dispatch.

The store admits one current Sandbox lease per Environment. A lease in
`Provisioning`, `Active`, `Releasing`, or `ReconciliationRequired` consumes that
slot. This is owner-level duplicate prevention; it is not a cluster-wide CPU or
memory quota. An unresolved or ambiguous provider effect remains current and
blocks Environment lifecycle advancement. Old resource generations remain
stored after release.

A different valid provider resource returned after a lease is terminal is
stored in a separate cleanup record. The original terminal state, resource, and
partial-release receipt remain unchanged. Reconciliation can discover pending
cleanup under current owner authorization across lifecycle generations while
using the original provider fence and binding for exact release. A durable,
expiring claim prevents concurrent reconcilers from racing the same delete;
the validated release receipt is saved with a current-fence compare-and-swap.

The Sandbox selection must contain exactly one exclusive candidate. The
configured provider ID, adapter version, options schema, options revision,
hosting pattern, and advertised capabilities must match that candidate. The
manager does not substitute a default provider when the selected one is missing
or incompatible. The provider itself advertises VM isolation and PVC attachment;
Environment separately verifies the selected Cilium policy generation.

## Authorization and API

All six Sandbox routes require an authenticated caller and fresh Projects
authority. Provision, inspect, abandon, and reconcile require target-project
`WriteProjects` plus the separate `ReadRunSelection` permission before they
read the immutable run selection or contact Kubernetes. They compare the fresh
authorization and selection again after asynchronous work and before accepting
provider callbacks. The public placement projection requires fresh
`WriteProjects` authority; it does not read run selection or contact Kubernetes.
The internal run-bound placement read requires fresh `ReadRunSelection` authority
and exact non-null project/run token bindings. The Environment JWT middleware
continues to enforce the configured Environment audience for both routes.

| Route | Operation |
| --- | --- |
| `POST /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/provision` | Validate the exact Workspace generation and Cilium policy generation, reserve an owner lease, then provision the selected adapter. |
| `GET /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox?networkPolicyGeneration={generation}` | Inspect the current lease. It reports `ReadyForDispatch` only after fresh Cilium verification and exact provider observation. |
| `GET /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/v1/placement` | Return the versioned owner-local projection of the exact current, active, unexpired lease and its placement references. |
| `GET /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/v1/internal/placement` | Return the same projection to a run-bound caller with exact project/run bindings and `ReadRunSelection`; no run selection is fetched and no provider effect occurs. |
| `POST /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/abandon` | Explicitly abandon one current resource generation and provider fence. The request is not itself proof of ownership; fresh Projects authorization and the durable owner CAS are required. |
| `POST /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/reconcile` | Recover an interrupted operation by its recorded owner, operation ID, resource generation, and provider fence. It also claims and releases at most one pending late resource. A network-policy generation may be supplied to verify readiness. |

Both v1 placement routes use one `ISandboxLeaseStore.GetCurrentAsync` reader
under the exact active Environment owner fence. The public route rechecks fresh
`WriteProjects`; the internal route rechecks fresh `ReadRunSelection` and the
same exact project/run binding after the first lease read. Both recheck the
active lifecycle and read the locked current lease again. The two snapshots
must match on operation, owner fence, resource generation, lease revision,
provider/current fences, state, current flag, and expiry; this rejects a lease
retired or replaced during the authorization wait. The projection returns
the tenant/project/run/Environment tuple, lifecycle and provider fences, lease
revision/expiry/current state, and the exact resource, endpoint, and opaque
placement references recorded by the lease. It does not derive lease data from
`EnvironmentSandboxResult`, caller arguments, run selection, or runtime
registration. The provider `ResourceId` is preserved as stored; it is not
relabeled as a Kubernetes UID because a planned `aw-claim-*` identity and a
returned Kubernetes UID are distinct values. Missing placement returns no
content; expired, stale, non-current, foreign, or non-active snapshots are
rejected. Provider options, release descriptors, and credentials are not
included. Profile mapping, receiver registration, and delivery receipts remain
the responsibility of the separately owned bootstrap profile adapter; absent
approved production transport remains unavailable.

The Environment owner must already exist and be active. These routes do not
register or release an Environment lifecycle, create a Core run provider pin,
or start a model run. The admitted Core source has no run-terminal or durable
run-owner evidence API for this consumer. Therefore, no caller-supplied
`abandoned` flag, lease age, stale credential, timeout, Pod failure, or failed
lookup is treated as proof that a run ended.

## Workspace, network, and readiness

Provisioning accepts a Workspace volume ID and exact resource/data generations,
mount path, read-only choice, and Cilium policy generation. The volume must
already be bound to the Environment. Environment checks the owner-scoped
Workspace record, its current Storage selection and pinned provider/options,
and the PVC namespace, UID, and owner-generation annotations. It reserves the
exact volume generation as Attached through the Workspace owner CAS before
provider dispatch. Replace and release remain blocked until the exact Sandbox
placement is retired and Environment detaches that generation. This does not
change Workspace retention or reclaim state.

The manager verifies the requested Cilium policy generation and object before
Sandbox dispatch, then rechecks it after provider observation. The provider
reports actual Sandbox/Pod, RuntimeClass handler, PVC attachment, and startup
observations. Kubernetes Sandbox and container Ready conditions do not imply
Environment or AgentHost readiness. `ReadyForDispatch` requires an explicit
`configured` phase and is an Environment observation only; it is not Core
admission or permission to execute a run.

The current provider emits `scheduled`, `image ready`, and `started` phases
from Kubernetes observations. It does not emit `configured` or dispatch
`ready`: this source does not contain an AgentHost configure/readiness
handshake. Object readback also does not prove Cilium datapath enforcement.

## Agent Sandbox resources and recovery

The adapter uses the `extensions.agents.x-k8s.io/v1beta1` API. For each owner
operation it creates or validates an owner-specific `SandboxTemplate`, a
zero-replica `SandboxWarmPool`, and a `SandboxClaim` with its required
`warmPoolRef` and `DeleteForeground` shutdown policy. The template disables
service-token automount and environment injection, selects the configured VM
RuntimeClass, mounts the exact Workspace PVC, and carries the verified Cilium
selector labels. Ownership labels bind the tenant/project/run/Environment hash,
Environment lifecycle generation, Sandbox resource generation, provider fence,
and operation ID.

Before returning a resource, the adapter validates Kubernetes object names,
UIDs, owner labels, the pinned template and pool references, the RuntimeClass
handler, and the bound PVC's UID and Environment/volume-generation annotations.
Describe validates the claim UID, Sandbox controller owner reference, Pod
controller owner reference, PVC mount, Pod security settings, and the actual
RuntimeClass. `ListOwned` reads the namespace from the lease's persisted
provider-options snapshot rather than current defaults, then recovers only
claims with the exact owner and fence; Environment further matches the
recorded operation and resource generation.

The provision request is stored before dispatch. A crash after dispatch can be
recovered through exact `ListOwned` results. A lease abandoned before any
provider request was persisted can be completed as a known no-effect operation.
An uncertain or failed lookup is never interpreted as absence.

If a different valid provider resource arrives after release, Environment
records that exact result in the late-resource cleanup table instead of
overwriting the terminal lease. Reconciliation discovers it under the current
owner authorization, claims one item at a time, and releases it with the
original provider binding and fence. Claims expire for recovery after a
reconciler stops; receipt persistence verifies the current lifecycle fence,
claim token, and unexpired persisted claim atomically. A matching duplicate
receipt remains idempotent after completion. Provider failures remain visible
and leave the cleanup available for retry.

## Retirement and storage retention

An exact Agent Sandbox `Finished=True` condition with a supported `PodSucceeded`
or `PodFailed` reason and a matching observed resource generation can retire
Sandbox placement. Explicit abandonment requires current Projects authority and
a durable CAS that increments the current fencing generation before release.
The retired Sandbox cannot be revived or rebound.

Release rechecks the exact current retiring lease and fresh Projects authority,
then uses the recorded provider binding and claim UID for foreground
deletion. The adapter waits for the claim and its Sandbox/Pod children to be
absent before deleting the owner-specific warm pool and template. The durable
receipt must identify the exact provider resource and release idempotency key.
`KnownOwnedAbsent` is returned only after an exact successful lookup confirms
the claim and children are absent. Permission, transport, or ownership errors
remain errors.

Retiring Sandbox placement does not transition the Environment to `Released`
and does not release, delete, or erase its Workspace volume. Storage retention
and cleanup continue through the independent Workspace lifecycle. There is no
background abandoned-run reaper in this source slice; Core run-terminal and
supersede evidence must be added by its owning contract before automatic
run-status reclamation is enabled.

## Configuration

The Environment service requires the `Environment:Sandbox:AgentSandbox`
configuration section. ASP.NET environment variables use the corresponding
double-underscore names, for example
`Environment__Sandbox__AgentSandbox__OptionsRevision`.

| Key | Purpose |
| --- | --- |
| `OptionsSchemaVersion` | Must be `1`. |
| `OptionsRevision` | Bounded immutable revision selected for this adapter. |
| `Namespace` | Namespace for Sandbox objects; it must match the Cilium and Workspace PVC namespace. |
| `WorkspaceStorageProviderId` | Supported Storage adapter ID; currently `azure-files-csi`. |
| `ContainerImage` | AgentHost image reference used by the Sandbox template. |
| `RuntimeClassName` | Kubernetes RuntimeClass selected for the Pod. |
| `ExpectedRuntimeHandler` | Handler value that must be read back from that RuntimeClass. |
| `CpuRequest`, `MemoryRequest` | Bounded Kubernetes resource quantities used as both requests and limits. |
| `ReconciliationTimeoutSeconds`, `PollIntervalMilliseconds` | Bounds for provider deletion reconciliation. |

`Kubernetes:ApiServer` defaults to `https://kubernetes.default.svc/`.
`Kubernetes:ServiceAccountTokenFile` and
`Kubernetes:CertificateAuthorityFile` default to the in-cluster service-account
token and CA paths. The adapter requires trusted HTTPS and uses that service
account for Kubernetes requests. This source change does not install RBAC or
change cluster configuration.

## Test and evidence boundary

The focused commands and limits are listed in the
[Sandbox testing guide](../guide/environment-sandbox-testing.md). The provider
test and production-authority API integration use the production
`AgentSandboxProvider` and `KubernetesAgentSandboxClient` against a fake
Kubernetes HTTP API. The integration also uses real Broker, Projects, and
Environment HTTP paths plus disposable PostgreSQL; only the Workspace provider
and Cilium policy resource store are controlled. The fake Kubernetes boundary
records object ownership and UID-preconditioned deletes. These tests do not prove
RuntimeClass or nested-virtualization availability on AKS, datapath enforcement,
AgentHost configuration, run pin acceptance by Core, or deployed service
configuration.
