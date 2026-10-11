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

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-environment-buildtest-command.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-environment-buildtest-command.png'" alt="Core saves the accepted repository seed before Environment reserves one stable operation. The native command Pod prepares that exact repository before command execution. A separate read-only collector seals the full baseline and declared output bytes from the prepared native path. Environment stores exact references, not file bytes. Core retains the document through Source Control and obtains the durable Events acknowledgment. Environment verifies that acknowledgment through Core before deleting the exact native Pods. Core then commits the terminal receipt under current owner checks." />
  </a>
  <figcaption>BuildTest repository preparation and output admission. Version 2 retains full native output until the journal acknowledges it. Command logs are not file evidence. Core owns workflow completion.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-environment-buildtest-command.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-environment-buildtest-command.drawio'">Open editable draw.io source</a></p>

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

All Sandbox routes require an authenticated caller and fresh Projects
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
| `GET /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/build-test/binding-preparation?sessionId={sessionId}&executionProfileReference={profile}` | Validate the current Sandbox profile binding for a session without starting a command. |
| `POST /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/build-test/commands` | Resolve the accepted Core checkpoint, reserve its immutable command intent, and execute the stable BuildTest operation. |
| `GET /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/build-test/commands/{operationId}` | Read the exact persisted operation under the current owner and Sandbox binding. |
| `POST /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/build-test/commands/{operationId}/reconcile` | Reconcile the same operation and immutable intent; do not create a replacement operation. |
| `GET /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/build-test/commands/{operationId}/workspace-capture` | Return the exact sealed version 2 native document as bounded binary bytes with no-store, after current owner checks. The digest and length must match the stored capture reference. After acknowledgment, read retained bytes through Source Control instead. |
| `POST /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/build-test/commands/{operationId}/workspace-capture/acknowledge` | Accept the original API request and exact Events journal acknowledgment. Verify it through Core, save the acknowledgment, then request exact-UID native Pod cleanup. Retry preserves the same capture and acknowledgment. |
| `POST /internal/provider-lifecycle/reports` | Provider report route. It fails closed until provider identity authentication and Core interruption acknowledgement are admitted. |

Provider lifecycle reports have a durable idempotent contract and lease/fence
reconciliation, but the mapped report route currently returns unavailable until
provider identity authentication and the Core interruption command are admitted. This
source does not claim end-to-end suspend, relocation, or resume.

Both v1 placement routes use the owner-scoped `ISandboxLeaseStore.GetCurrentAsync`
callback overload under the exact active Environment owner fence. The public
callback rechecks fresh `WriteProjects`; the internal callback rechecks fresh
`ReadRunSelection` and the same exact project/run binding. The store retains the
same transaction and owner advisory lock while the callback runs, then rereads
the current lease and compares its owner fence, resource generation, operation,
lease revision, provider/current fences, state, current flag, expiry, and update
stamp before commit and return. A mismatch fails closed; a callback exception
rolls back and returns no projection. The manager also compares the retained
snapshot with its first read before making the final authorization request.
The projection returns the tenant/project/run/Environment tuple, lifecycle and
provider fences, lease revision/expiry/current state, and the exact resource,
endpoint, and opaque placement references recorded by the lease. It does not derive lease data from
`EnvironmentSandboxResult`, caller arguments, run selection, or runtime
registration. The provider `ResourceId` is preserved as stored; it is not
relabeled as a Kubernetes UID because a planned `aw-claim-*` identity and a
returned Kubernetes UID are distinct values. Missing placement returns no
content; expired, stale, non-current, foreign, or non-active snapshots are
rejected. Provider options, release descriptors, and credentials are not
included. Profile mapping, receiver registration, and delivery receipts remain
the responsibility of the separately owned bootstrap profile adapter; absent
approved production transport remains unavailable.

The additive `providerPin` contains the successful consumer's provider, adapter version, options schema and revision, resource identity and generation, and negotiated capabilities.
It contains no option values, recovery metadata, credentials, or unresolved candidates.
The current placement read still denies an expired lease.
Historical material and usage receipts retain their original runtime and selection facts under separate current read authorization.

The callback is for a bounded owner-scoped read, such as the fixed Core
authorization-context request. It must not recurse through a registration route
that reads the same Environment lease or perform provider effects. A successful
placement read is not authorization for a later configure or provider effect;
those operations recheck current authority and lease state independently.

The runtime bootstrap profile adapter uses this same manager callback.
It reads the fixed Orchestrator work-item context and resolves the exact registered
profile while the lease transaction remains active.
The callback does not request the full runtime registration or dispatch a provider effect.
Both placement routes return no-store responses.
This composition does not change Workspace attachment, retirement, or public write permission.

The Environment owner must already exist and be active. These routes do not
register or release an Environment lifecycle, create a Core run provider pin,
or start a model run. The admitted Core source has no run-terminal or durable
run-owner evidence API for this consumer. Therefore, no caller-supplied
`abandoned` flag, lease age, stale credential, timeout, Pod failure, or failed
lookup is treated as proof that a run ended.

## Trusted suspend Workspace flush

This source connects Environment to the trusted AgentHost flush operation for issue [#1930](https://github.com/sabbour/agentweaver/issues/1930).
It does not claim a complete deployed suspend/resume flow.

Core calls authenticated `POST /internal/runtime/suspend/resolve` with its current suspend operation and Host session proof.
This request cannot contain Workspace evidence.
Environment reads the current registration, attached Workspace, and volume snapshot through their owners.
It returns the exact Storage resource, provider binding, transition revision, and next data generation.
Resolve is a read-only step while Core's operation is Reserved.
Core stores the resolved request and its target phase version when it advances Reserved to Fenced.
Host delivery accepts only that saved Fenced request.
Retries must use that saved request, not new generations.
The shared request and client live in `Agentweaver.Identity`; Core does not need an Environment assembly reference.

`POST /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/workspace-volumes/{volumeId}/flush`
reserves the exact volume transition.
Environment checks fresh Projects authority, Core's exact current-operation response, registration, placement, and attached Workspace before and after Host delivery.
The trusted Host response must contain the matching native-turn receipt, durable cache acknowledgment, and Workspace flush receipt.
The Azure Files adapter verifies its pinned PVC, PV, full claim specification, and approved mount options around that delivery.
Both routes require an expiring authenticated bearer and return `no-store`.
The original actor is retained; no service credential replaces it.

Only a verified receipt advances data generation by one.
Migration `20261011133000_WorkspaceFlushEvidence` adds nullable `owner_effects.flush_evidence_json`.
The receipt and Host evidence are saved with the transition in the same owner transaction.
Authority is checked again after database waits and before commit.
Uncertain effects keep their evidence for reconciliation without advancing the generation.
Failure before provider dispatch records no effect.
Completed replay returns the saved evidence without repeating Host flush.
Conflicting evidence cannot replace a saved receipt.
A resource and a caller-supplied durability flag alone cannot complete a flush.

Placement retirement accepts changed Workspace counters only with the exact saved suspend flush for that retiring lease.
Core calls authenticated `POST /internal/runtime/suspend/release-placement` only after it commits the complete suspend manifest.
The shared release request contains the saved Host receipt and exact flush transition revision.
Environment asks Core's `POST /internal/runtime/suspend/require-release-current` to confirm that saved evidence before provider release and owner completion.
Detach and lease completion repeat the Projects and Core checks after owner database waits and before transaction commit.
Ordinary abandonment and retirement recovery apply the same check when their Workspace has trusted suspend flush evidence.
The Sandbox provider must confirm release before Environment detaches the exact Workspace and records the lease as Released.
Completed detach replay keeps the same resource, data generation, and transition.
Core's client rejects pending release, changed placement, and different detach counters.
Credential revocation alone is not placement release.
It does not rewrite the original attachment or immutable provider selection.
Workspace retention and reclaim rules remain unchanged.
Pure checks cover these source contracts; PostgreSQL and mounted CSI execution require their separate acceptance runs.

## Retained Workspace resume

The Environment source first prepares and verifies the retained Workspace.
Those two authenticated, no-store steps do not require a new Runtime registration or a configured model session.
Both retain the original expiring actor and ask Core to confirm the exact saved operation.

`POST /internal/runtime/resume/prepare` accepts `RuntimeWorkspaceResumeRequest`.
It contains the complete source manifest, saved Host receipt, and exact flush transition revision.
The optional `RestoredWorkspace` must be absent.
Resume requires the exact private configuration acknowledgment before any owner call or Environment effect.
Ordinary suspend receipts retain their existing validation; a receipt without saved configuration cannot authorize resume.
Environment checks the recorded flush, released source Sandbox, retained volume, and unchanged accepted provider selection.
Historical flush reads require the current active owner lock and an exact older lifecycle from that same owner.
An old lifecycle does not gain permission to change current state.

Prepare advances the Environment lifecycle once with a stable operation key.
Core and Projects authority are checked after database waits and before that transition commits, including replay.
Environment reapplies the same accepted Network rules with the source policy generation plus one.
The recorded previous policy generation must equal the manifest's source generation.
It provisions a new Sandbox with the retained Storage resource and data generation, then verifies the actual mount.
The response contains the exact new lease, Workspace attachment, and transition revision.
This is placement evidence, not verified Workspace content or permission to dispatch.
Core saves that complete response before requesting the second step.

`POST /internal/runtime/resume/verify` requires that saved `RestoredWorkspace`.
Environment compares it with a fresh owner-scoped mount and Sandbox observation.
It calls the new Host's read-only `/runtime/v1/workspace/resume` operation.
The actual tree hash must equal the manifest hash.
Environment then repeats the placement, Network object, and current-operation checks.
Only the final receipt contains both the native content proof and verified new Network generation.
Core must obtain it before advancing its execution fence, registering the new Runtime, restoring the existing cache, or dispatching.
Provider-checkpoint and guest-snapshot restoration remain explicitly unsupported on this path.

The new Host address comes from the owned Kubernetes Sandbox Service and its exact singleton Pod.
Environment checks the Service owner, DNS name, selector, Pod UID, and Pod address.
It connects to that observed Pod address while retaining normal TLS verification for the Service DNS name.
This permits the pre-configuration read without requiring a readiness-gated DNS address.
It does not bypass certificate validation or create another Service.
The original configured owner/profile remains the source of the observation address and MCP setting.
The saved provider request retains the resume source, so later bootstrap reads reconstruct the new address after restart.
No caller or new placement-specific configuration supplies that address.

After Core advances its execution fence, `POST /internal/runtime/resume/bootstrap` starts the new Runtime.
It requires Core's phase-4 request, current registration, exact saved mount, and server-issued resume attempt.
Environment reads the acknowledged server configuration from the protected Events material route.
The saved configuration contains no credential values; no caller supplies replacement configuration.
Events returns its exact bytes only after current Core and registration checks.
Public event streams expose neither those bytes nor their private object reference.

Environment uses the existing Broker delivery and Host activation paths.
It stores each attempt's immutable request and complete receipt in the existing `owner_effects` table.
The `RuntimeResumeBootstrap` migration changes only that table's check constraints.
It adds no table or grant, and service startup never applies it.
Attempts for one resume operation run in order.
Owner and lease locks cover database changes, not calls back into Environment.
Receipt replay still requires fresh Core, registration, placement, Host state, and native cache checks.
A saved receipt alone cannot authorize dispatch.

An acknowledged delivery followed by a fresh Host `Missing` state produces an explicit state-loss outcome.
A timeout or generic refusal does not authorize replacement.
Core must revoke the exact old registration under current authority before issuing a replacement attempt.
Each attempt has distinct bootstrap, consume, and exchange IDs under the same durable resume operation.
Ordinary Runtime bindings omit the resume fields and keep their existing hashes.
The Core replacement path and Host journal-based cache recovery require their separate implementation and acceptance.
Until that recovery succeeds, Core must not treat a missing cache or lost Host state as readiness.

The pure checks cover request, receipt, actor, authority, attempt identity, explicit loss proof, schema metadata, and controlled Kubernetes routing behavior.
They do not prove PostgreSQL execution, native TLS, actual remount contents, Cilium traffic enforcement, or a deployed resume.

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
`ready` on its own.
The separate [AgentHost handshake](agenthost.md) adds authenticated configuration and current readiness evidence.
Its internal readiness owner read retains the exact lease across fresh egress and Sandbox observations.
It uses the retained selection snapshot, not a recursive accepted-selection lookup.
It validates the stored recovery record and reads its nested provision request.
That request supplies the exact network generation and Workspace mount path.
Object readback does not prove Cilium datapath enforcement.

The internal bootstrap profile route also exposes a read-only `/workspace` suffix.
It returns `EnvironmentRuntimeWorkspaceContext` under current run-read authority and the exact registered runtime profile.
One owner transaction retains both the current Sandbox lease and the Workspace record during the Core context read.
The record must remain Attached at the exact saved transition revision, volume generation, data generation, Storage resource, and provider attachment.
The response includes the validated mount negotiation and transition revision, not provider options, credentials, or the PVC release descriptor.
Data generation zero is valid metadata.

This read does not flush Storage or produce a content checkpoint.
It supplies no tree hash and does not prove that a session suspended.
Core still requires genuine native completion, cache, checkpoint, and Workspace content evidence before a successful suspend manifest.

For the [proposed credential-less mode](identity-secrets.md#minimal-contract-changes-and-admission),
admission must also prove that credential-consuming services are outside the
untrusted guest, with no guest-accessible private memory, volumes, sockets or
cloud assertions. The existing `ISandboxProvider` negotiated capability and
lease/profile surfaces carry placement evidence; the selected L3/L4 and L7
providers must supply current forced-mediation evidence. Two containers in one
Kata guest, a VM-isolation descriptor, or policy readback alone do not prove this
boundary. These are proposed readiness additions, not current Sandbox capability
or deployment evidence; legacy placement must not be relabeled credential-less.

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

The optional typed `AgentHost` launch profile pins only a trusted ConfigMap name and TLS Secret name.
The adapter adds fixed private state/temporary volumes, readonly configuration/TLS mounts, UID/GID/fsGroup 1654, and HTTPS health probes.
It uses the exact negotiated workspace as the SDK working directory.
It validates the profile on both template and actual Pod readback.
Absent profiles preserve the legacy template without native-host configuration.
See the [AgentHost image and launch boundary](agenthost#startup-evidence-and-image).

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

## BuildTest command and output collection

BuildTest is a separate command operation on the current Sandbox lease.
The caller supplies a checkpoint reference and expected binding, not a command, image, argument list, or authority grant.
Environment resolves the complete checkpoint with Core under current authorization.
Core returns the immutable accepted command, execution options, selection hash, and stable operation ID.
The manager records the intent and request fingerprint in the existing `owner_effects` row before provider effects.
Attempts, Pod references, and results use the same operation.
The store writes UTC timestamps at PostgreSQL microsecond precision so JSON and row timestamps agree after restart.
A terminal snapshot is immutable except for its update timestamp and one verified version 2 journal acknowledgment.
Identical saves and additional durable attempt markers cannot replace its status, Pod references, or evidence.
An uncertain create remains `ReconciliationRequired`; it does not authorize another Pod.
Command create, read, and reconcile responses use `SandboxBuildTestOperationResult`, which contains the operation and replay flag.
Reconcile requests retain the same complete checkpoint and expected binding as the original command request.
Core validates a read result before it reconciles; invalid owner response shapes cannot start another command.

Configure `Environment:Sandbox:AgentSandbox:AcceptedBuildTestProfile` to enable the BuildTest capability.
The profile pins the executable allowlist, image digests, resource limits, offline egress, and trusted collector command.
An absent profile does not advertise the capability.
An invalid profile fails configuration validation; the provider does not use a default profile.
The active lease retains its accepted profile even if current server defaults change.
BuildTest reads the same string-enum JSON format that Sandbox provisioning persists.
It obtains the original mount request through the validated Sandbox recovery record, not a separate caller-supplied request.

Before each effect, Environment rechecks the owner, accepted command, Sandbox lease, Workspace generation, and Cilium binding.
The command Pod uses the exact writable Workspace PVC and an operation/role-scoped offline deny-all policy.
Only verified terminal success with exit code zero can start the separate required-output collector.
The collector uses the pinned image and executable with a read-only Workspace mount.
AgentHost routes `--build-test-output-collector-v1` before web configuration or native SDK startup.
This Linux-only mode accepts one bounded encoded request.
It opens accepted paths without following symlinks, checks regular files and mount identity, and returns a bounded receipt.
Native directory and no-follow flags match the executing Linux architecture; unsupported architectures fail before collection.
The stream matches the synchronous native file descriptor and keeps bounded cancellable reads.
The receipt binds the collector Pod UID, container, operation, checkpoint, and request fingerprint.
Missing required files or an invalid collector receipt are `Failed`.
Bound collector timeout, cancellation, or output-limit termination is `Interrupted`, even after the command exits successfully.
Logs are bounded command output, not file evidence.

BuildTest does not advance MAF checkpoints, turns, or run status.
Core retains that state and its interruption acknowledgement.
Disposable PostgreSQL tests verify owner-store reservation, restart replay, and terminal-evidence preservation.
They do not prove command-Pod execution, Kubernetes isolation, Cilium datapath enforcement, or complete native accounting.

### Version 2 repository preparation and full output

Version 2 binds a credential-free Git bundle and the full baseline inventory to the accepted command.
The source binding includes the current actor, selection, repository pin, workspace incarnation, branch, and content digests.
Authenticated MAF dispatch can provide `buildTestWorkspaces`, keyed by the confirmed BuildTest step ID.
Each value contains only `workspaceId`, `workspaceIncarnationId`, `baseSha`, and `branchName`.
These values locate an existing ready workspace; they do not grant access or select a repository.
The existing Source Control owner resolves the current accepted run and repository pin.
It opens that owned workspace and creates the bounded seed without cloning or redeeming repository credentials.
The owner derives source metadata from the verified seed, current pin, actor, and accepted selection.

The confirmed workflow command remains unchanged.
The MAF intent stores server-produced source metadata in a separate optional `workspaceInput` field.
The effective version 2 execution command binds this input in its immutable hash.
The exact confirmed-command comparison still applies.
An explicit source in an already-confirmed command must match the verified input exactly.
Version 1 requests and intents without input retain their existing JSON and hash behavior.
The selected execution profile must already support the version 2 collector mode; dispatch does not upgrade it.

The original execution checkpoint stores the bounded seed in a separate PostgreSQL `bytea` field.
Dispatch holds the existing execution lock and checks the current Source Control owner within the checkpoint transaction.
It stores the command, input metadata, origin reference, and seed before returning or reserving an Environment operation.
The owner rechecks the accepted selection, decision, and fence after preparation awaits.
Retry reads the saved intent before seed creation and does not recapture a changed checkout.
A conflicting workspace locator or changed owner is an error; it cannot replace saved input.
Completed input can remain in a later accepted decision without changing its saved origin.
Dispatch still checks the current source owner, selection, and fence.
Running input cannot continue under a different decision version.
Workspace preparation errors use the existing Source Control response and stop checkpoint creation.
Later checkpoints retain only the typed origin reference.
The JSON checkpoint limit remains 4 MiB.
The seed limit remains 67,188,876 bytes.

Explicit seed reads require the current owner and fence.
They validate the original intent, declared length, digests, and package before they return bytes.
Ordinary checkpoint reads do not load the binary field.
An identical append must match both the JSON metadata and the stored bytes.
The parent foreign key prevents deletion of an origin that has later checkpoints.
The seed is source input, not SDK cache data or a produced-output record.

Core's accepted-command route returns metadata only, including the bound seed digest and length.
The separate `/workspace-seed` route reads the original binary field through the existing checkpoint store.
It uses the same full checkpoint reference and current actor authorization.
Core rechecks the accepted selection, owner decision, execution fence, and command after the read.
Both routes also check the independently pinned Source Control owner for version 2 input.
Both routes return `Cache-Control: no-store`.
Version 1 commands have no seed route result.

Environment fetches the raw seed only from its fixed Core owner, not a caller-supplied address.
The response must be `application/octet-stream`, use `no-store`, and match the accepted length and SHA-256 digest.
Redirects, short or extra bytes, denied reads, and changed command metadata are errors.
The client deadline covers both response headers and the complete bounded body.
Environment rechecks Core metadata after the transfer and before reserving the command.
Later current-command checks do not fetch the seed again.
Stored operation updates, request fingerprints, and Core terminal receipts validate the bound metadata without seed bytes.
Reconciliation restores the freshly verified seed only after it matches the stored command metadata.
Version 2 still requires the actual validated bytes before execution; metadata alone is not executable input.

Source Control includes a full output-document foundation for version 2.
Its `AWSCAPT2` format retains the manifest, Git patch, and binary file package.
The 24-byte header declares each section length; existing section limits bound the complete document.
Parsing rejects invalid lengths, extra bytes, invalid file digests, and malformed packages.
Serialization also checks the saved manifest and section digests.

The source can construct an output document from the accepted seed and a complete file inventory.
It rejects missing baseline files, changed undeclared files, and outputs that differ from their declared evidence.
It uses a private Git index and disables hooks, filters, external diff commands, and network protocols.
The shared prepared-repository path retains the existing owner-scoped hash.
The same Source Control package constructs the native output document.
This does not create another output store or a second model runtime.

The Kubernetes client adds a bounded native WebSocket exchange for fixed preparation and collector containers.
It retains the existing service-account handler and trusted server certificate.
The helper must identify the exact Pod UID and request fingerprint before the client sends seed bytes.
Local TLS tests cover binary transfers at the exact admitted limit and reject invalid identity, protocol, output, or terminal evidence.
The provider uses this exchange to prepare the repository before command execution.
The pinned AgentHost image contains Git and the shared Source Control package.
Its trusted init container receives the exact accepted seed and calls the existing repository-preparation helper.
The command starts only after the preparation receipt matches its operation, checkpoint, source digest, native path, and Pod UID.
Its working directory is the prepared repository, not another local checkout.
The command container cannot mount the helper's private state volume.

After verified command success, a separate read-only collector reads the entire prepared repository through native no-follow file descriptors.
It excludes only the root Git metadata directory.
Symlinks, special files, nested mounts, changed file identities, and incomplete trees prevent capture.
The full package retains every baseline file and each declared output.
Missing baseline files, changed undeclared files, and undeclared additional files are errors.
Version 2 output obligations cannot exceed the existing 16 MiB package file limit.
The complete-tree limits remain 10,000 files and 64 MiB total file content.
A version 2 command requires this collector even when it declares no output files.
The collector request records the actual accepted command version.
Only explicit version 2 permits an empty output list; absent version fields retain strict version 1 behavior.
The provider checks this version against the accepted command before any transport request.
Workspace preparation and capture require valid version 2 source input.
This field supplies no authority and does not change command hashes or receipt versions.

The collector seals the complete document and its exact reference in private Pod state.
Both remain bound to the operation, source, request fingerprint, native repository path, and collector Pod UID.
A restart or retry reuses those sealed bytes instead of recapturing the tree.
A complete pending write is verified and promoted unchanged.
An incomplete unsealed write can be retried only under the exact private-state binding, with an explicit diagnostic.
Foreign or corrupt complete state remains an error.
The native command is not restarted during capture recovery.

Environment persists preparation and capture references, never seed or file-package bytes in operation JSON.
Core reads the exact binary document from Environment's fixed owner route only when Source Control has not retained it.
The transfer requires no-store, the exact document length and digest, and current owner checks before and after the read.
Core validates the accepted seed, full baseline, declared outputs, document sections, and native capture reference.
It then uses the existing Source Control pending-package rows and Events produced-output admission path.
Retry reconstructs the same document from retained pending or admitted bytes without a native export.

Environment receives the exact Events journal acknowledgment through its acknowledgment route.
It asks Core to verify the canonical admitted record, object reference, positive journal position, and all capture bindings.
Current command checks surround that verification.
Environment saves the verified acknowledgment before requesting deletion of the exact collector and command Pod UIDs.
Cleanup retries preserve the stored acknowledgment and do not execute or export the command again.
Core's final owner transaction rereads the admitted capture and persists the preparation, full capture reference, and acknowledgment before completing the workflow step.
Owner locks do not remain held across HTTP waits.

Controlled source tests cover full binary documents, baseline preservation, exact references, acknowledgment checks, and response-loss replay.
They do not prove native Kubernetes execution, deployed PVC isolation, Cilium enforcement, or complete BuildTest acceptance.

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
