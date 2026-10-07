# Environment Sandbox leases

This page documents the unpublished v1 Environment source candidate for issue
[#1852](https://github.com/sabbour/agentweaver/issues/1852). It is not a deployment
record and does not claim that a live Kubernetes cluster or AgentHost runtime was
tested.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-environment-sandbox-lifecycle.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-environment-sandbox-lifecycle.png'" alt="Environment verifies current Projects authority, the exact Workspace PVC generation, and Cilium policy generation before dispatch. It records an owner-fenced lease and immutable provider request, then provisions and observes the exact agent-sandbox resources. ReadyForDispatch requires the same verified network generation and actual Pod, PVC, and isolation evidence. Retirement advances the owner fence and releases only Sandbox placement; Workspace retention is unchanged." />
  </a>
  <figcaption>Environment Sandbox lease and recovery sequence. A ready lease requires exact Workspace and network observations; retirement releases Sandbox placement only, not the Environment or its Workspace data.</figcaption>
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

The Sandbox selection must contain exactly one exclusive candidate. The
configured provider ID, adapter version, options schema, options revision,
hosting pattern, and advertised capabilities must match that candidate. The
manager does not substitute a default provider when the selected one is missing
or incompatible. The provider itself advertises VM isolation and PVC attachment;
Environment separately verifies the selected Cilium policy generation.

## Authorization and API

All four Sandbox routes require an authenticated caller and fresh Projects
authority. The manager requires target-project `WriteProjects` and the separate
`ReadRunSelection` permission before it reads the immutable run selection or
contacts Kubernetes. It compares the fresh authorization and selection again
after asynchronous work and before accepting provider callbacks.

| Route | Operation |
| --- | --- |
| `POST /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/provision` | Validate the exact Workspace generation and Cilium policy generation, reserve an owner lease, then provision the selected adapter. |
| `GET /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox?networkPolicyGeneration={generation}` | Inspect the current lease. It reports `ReadyForDispatch` only after fresh Cilium verification and exact provider observation. |
| `POST /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/abandon` | Explicitly abandon one current resource generation and provider fence. The request is not itself proof of ownership; fresh Projects authorization and the durable owner CAS are required. |
| `POST /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/reconcile` | Recover an interrupted operation by its recorded owner, operation ID, resource generation, and provider fence. A network-policy generation may be supplied to verify readiness. |

The Environment owner must already exist and be active. These routes do not
register or release an Environment lifecycle, create a Core run provider pin,
or start a model run. The admitted Core source has no run-terminal or durable
run-owner evidence API for this consumer. Therefore, no caller-supplied
`abandoned` flag, lease age, stale credential, timeout, Pod failure, or failed
lookup is treated as proof that a run ended.

## Workspace, network, and readiness

Provisioning accepts a Workspace volume ID and exact resource/data generations,
mount path, read-only choice, and Cilium policy generation. Environment checks
the owner-scoped Workspace record, its current Storage selection and pinned
provider/options, and the PVC namespace, UID, and owner-generation annotations.
It negotiates a PVC attachment against the planned Sandbox resource before
provider dispatch. This operation attaches a PVC to the Sandbox Pod; it does not
change Workspace retention or reclaim state.

The manager verifies the requested Cilium policy generation and object before
Sandbox dispatch, then rechecks it after provider observation. The provider
reports actual Sandbox/Pod, RuntimeClass handler, PVC attachment, and startup
observations. A provider Ready condition without the exact current network
generation cannot make the lease ready. `ReadyForDispatch` is an Environment
observation only; it is not Core admission or permission to execute a run.

The current provider emits `scheduled`, `image ready`, `started`, and `ready`
startup phases from Kubernetes observations. It does not emit `configured`:
this source does not contain an AgentHost configure or readiness handshake.
Object readback also does not prove Cilium datapath enforcement.

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
RuntimeClass. `ListOwned` recovers only claims with the exact owner and fence;
Environment further matches the recorded operation and resource generation.

The provision request is stored before dispatch. A crash after dispatch can be
recovered through exact `ListOwned` results. A lease abandoned before any
provider request was persisted can be completed as a known no-effect operation.
An uncertain or failed lookup is never interpreted as absence.

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
test uses a fake Kubernetes HTTP handler. The production-authority API
integration uses real Broker, Projects, and Environment HTTP paths plus
disposable PostgreSQL; it controls only the Workspace provider, Sandbox
provider, and Cilium resource-store boundaries. These tests do not prove
RuntimeClass or nested-virtualization availability on AKS, datapath enforcement,
AgentHost configuration, run pin acceptance by Core, or deployed service
configuration.
