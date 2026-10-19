# P1 native acceptance probes

`run.mjs` exports the side-effect-free `runP1NativeProbes(input, dependencies)`
entry point. Importing the module does not start a process, contact a service,
or read credentials. The runner reports four separate P1 probes and returns
`{ kind: "p1-native-acceptance", schemaVersion: 1, overallStatus, startedAt,
completedAt, probes, receiptSha256 }`. The checksum covers the canonical JSON
envelope without `receiptSha256`; the aggregator verifies it before using the
envelope. Each probe has `passed`, `failed`, or `blocked` status, redacted
evidence, and an explicit cleanup result. Missing or mismatched pins, fences,
UIDs, or live evidence block; they are never inferred.

This is acceptance source, not a live acceptance command. Tests use injected
fakes only. A caller must explicitly provide an HTTPS Environment origin, same-
cluster pins, and the dependencies below. The two owner scopes may share an
actor, but their tenant or project/run/Environment selector must differ. Both
selectors are retained in owner receipts, and the cross-owner lifecycle and
binding checks count only when the actual Environment response is HTTP 403.

## Input and fixture ownership

The outer contract is:

```js
{
  environment: { baseUrl, issuer, audience, actorId, tenantId },
  pins: {
    context: { kubeContext, clusterIdentity: { sha256 } }, namespace, providerReceipt, runtimeReceipt, storageReceipt,
    sourceReceipt: { acceptedOptions, startupBudget }, imageReceipt, ownerFences, fixtureScope
  }
}
```

`fixtureScope` uses the shared Agent 106 contract exactly:

```js
{
  kind: 'p1-runner-owned',
  status: 'ready',
  invocationId: '...',
  fixtureId: '...',
  expiresAt: '...',
  allowedJourneys: [/* opaque values supplied by the fixture owner */],
  ownershipReceipts: [{
    resourceType: '...',
    resourceId: '...',
    invocationId: '...',
    creationReceipt: { resourceId: '...', /* sanitized actual owner response */ },
    observedReceipt: { resourceId: '...', /* sanitized actual owner readback */ },
    ownerFence: { /* current observed fence, including owner selectors */ }
  }]
}
```

The runner does not define or synthesize `allowedJourneys` values. Before each
mutating Environment/provider action, the injected journey authorizer must map
the source-owned action to one of the exact values supplied in the fixture
scope. A mismatched, missing, or expired ownership receipt blocks the run.
Both actual receipts must explicitly include a `resourceId` equal to the
ownership entry. Their current `ownerFence` must match the pinned owner
`projectId`, `runId`, `rootSessionId`, `sessionId`, and `executionFence` as
well as the actor/tenant/Environment selectors and lifecycle fence. The active
Sandbox UID must also match its current observed receipt. Existing
primary UI/workflow resources are never accepted as substitutes for
invocation-owned throwaway fixtures. The Sandbox and policy lifecycle actions
target only the explicitly pinned disposable resources. No Kubernetes
resource-delete command is supported.

The owner entries in `storageReceipt.owners`, `ownerFences.owners`, and
`runtimeReceipt.owners` each contain `ownerId`, `actorId`, `tenantId`,
`projectId`, `runId`, `environmentId`, `rootSessionId`, `sessionId`, and
`executionFence`. `ownerFences` also contains
`lifecycleFence` (`lifecycleGeneration`, `currentFencingGeneration`,
`leaseRevision`) and the exact `environmentFence`. Storage entries carry the
current `volume` snapshot, provider binding, PVC/PV name and UID, claim UID,
mount path, and a normalized logical `volume.owner` (`kind`: `Run`, `Agent`, or
`Team`; `id`). This owner is projected from the actual Environment
`specification.owner` and must match fresh Environment reads. It is distinct
from the invocation fixture's `ownerId`. Storage entries also carry a mount
allowlist entry for volume
`agentweaver-workspace`, container `agenthost`, and the exact PVC. Runtime
owner entries identify the current `sandboxResourceId`/`sandboxUid`,
`networkPolicyResourceId`/`networkPolicyUid`, and the invocation-owned
`lateSandboxResourceId`/`lateSandboxUid`. They also pin the actual
AgentSandbox label values for `agentweaver.dev/managed-sandbox`,
`agentweaver.dev/sandbox-owner`, `agentweaver.dev/sandbox-lifecycle`,
`agentweaver.dev/sandbox-generation`, `agentweaver.dev/sandbox-fence`,
`agentweaver.dev/sandbox-operation`, and `agents.x-k8s.io/claim-uid`; these
are matched to native Pod readbacks, not reconstructed from owner IDs. Before
each native Pod lookup, the selector is rebuilt from the current placement's
lifecycle, resource, and provider-fencing generations, the actual creation
operation ID, and the exact SandboxClaim UID; the same labels are used for
subsequent ownership validation. Provider
receipt entries pin those owner PVC/PV UIDs and provider bindings to
`context.clusterIdentity.sha256`. The Kubernetes process adapter independently
checks the PVC/PV UIDs, claim reference, and both Azure Files owner annotations
against the current volume specification.
The `context.clusterIdentity.sha256` pin is the lower-case SHA-256 digest of
the selected cluster CA certificate set. Certificates are normalized to DER,
deduplicated, sorted, and length-prefixed before hashing with the
`agentweaver.cluster-identity.v1\0cluster-ca\0` domain prefix. The Environment
service derives the pin from its projected service-account CA file; the native
adapter derives it from the selected kubeconfig's CA data. Different HTTPS API
addresses for the same cluster therefore produce the same pin without requiring
permission to read the `kube-system` namespace. The adapter still requires a
clean HTTPS API URL and the selected kubeconfig's normal TLS validation. See the
[Environment Sandbox architecture reference](../../../docs/architecture/environment-sandbox.md#ownership-and-admission)
for the shared C# and Node.js contract.
For AgentSandbox, `sandboxResourceId` is the SandboxClaim UID and
`sandboxUid` is the distinct AgentHost Pod UID. The claim UID must match the
Pod's `agents.x-k8s.io/claim-uid` label; `volume.claimUid` is the separate
workspace PVC UID used only to verify the mounted volume. The late Sandbox
resource ID and UID both identify its SandboxClaim. Native cleanup readbacks
must check for dependent Pods as well as the SandboxClaim so a removed claim
with a leftover Pod remains uncertain.
The complete provider binding is cross-checked between the provider receipt
and fresh Environment volume readback; it is not echoed as native evidence.

Environment reads use the production projections: lifecycle is
`{ fence: { owner, lifecycleGeneration }, state }`; current Sandbox placement
uses the exact owner selector and fence fields plus
`resource: { resourceId, generation }`. Current fencing generation and lease
revision come from the placement projection, not assumed lifecycle fields.
Provision must return its actual `Resource` reference and the following
current-placement read must match its resource ID and generation. Native Pod
readback must prove the exact AgentHost PVC mount.

`runtimeReceipt.network` carries the current generation, exact
`namespace/name` allowed and denied service identities, owning policy resource
ID/UID, source hash and selector labels, plus a revoke request with the next
policy generation, expected current generation, idempotency key, and exact
Environment fence. Its source-owned policy must be current and non-revoked.
The revocation response uses the Environment contract's `revoked` and
`appliedState` fields; native Cilium observations use `appliedIntentGeneration`
and `revoked`.

`runtimeReceipt.activation` is a secret-safe projection of the actual
`RuntimeHostReadinessReceipt`: `contractVersion`, `runtimeInstanceId`,
`registrationRevision`, numeric `executionFence`, `image` (`digest`,
`platform`, `compressedPullBytes`), and actual `startupPhases` observations
(`phase`, `contractVersion`, `observedAt`, and optional image fields). The
required phases are observed `scheduled`, `imageReady`, and `started`; the
runner never synthesizes `configured` or `ready`. Optional per-phase image
fields remain optional; when present, they must match the receipt image.
`runtimeReceipt.registration` is the current sanitized registration readback:
runtime ID, revision, state, expiry, and binding identity/fence/placement
fields (actor, tenant, project, run, session, Environment, placement
UID/generation, profile, execution fence, and Environment current/provider
fences). It must match the actual current placement and readiness receipt.
Do not include `ModelCredentialReference`, configure URLs, or observation
URLs. Accepted options and startup budgets remain in `sourceReceipt`; observed
phase timestamps are checked against that budget. `imageReceipt.reference`
must be digest-pinned and match the readiness receipt's image digest.

## Injected dependencies

```js
{
  fetch(url, init), // controlled HTTP transport; redirects are rejected
  authProvider: {
    getValidatedToken({ issuer, audience, actorId, tenantId, ownerId,
      projectId, runId, environmentId })
  },
  journeyAuthorizer: {
    authorize({ action, fixtureId, invocationId, allowedJourneys, owner })
  },
  processProvider: {
    run({ recipeId, args })
  },
  runtimeReadinessProvider: {
    readCurrent({ owner, placement, expectedPodUid, expectedRuntimeInstanceId })
  },
  now() // optional, milliseconds since epoch
}
```

`getValidatedToken` returns `{ validated: true, token, issuer, audience,
actorId, tenantId }`; the token is held only in memory and sent as Bearer auth
with `X-Agentweaver-Tenant`. It must never be placed in `input`, evidence,
logs, or result objects. `authorize` returns `{ allowed: true, journey }`,
where `journey` must exactly match a supplied opaque fixture value.

## Standalone credit-denial receipt reader

`run.mjs` also exports
`runP1NativeCreditDenialReaders({ fixedZero, positive }, dependencies)`. It
validates two independently pinned cases, but it is not part of
`runP1NativeProbes` or the four-probe overall status.

Each input pin includes owner selectors (`ownerId`, `actorId`, `tenantId`,
`projectId`, `runId`, `sessionId`, `environmentId`), runtime instance ID,
registration revision, execution fence, registration and source hashes, source
grant ID and revision, expected hard credit limit, and stage. The zero-limit
pin also has the fixed request ID and hash. The positive-limit pin has a
dispatch ID. The two pins must use different RuntimeHost instance IDs. Each
pin also carries its actual current context as `current` (`registration`,
`sourceGrant`, and `sourceReceipt`) and its matching nonsecret
`RuntimeHostSessionProof` as `proof`. Do not create these contexts from
readiness data or from the expected pin values. Runtime, grant, request, and
dispatch IDs must be nonempty GUIDs. A typed `maxPromptTokens` value may be
null or an integer from 1 through 200000; other credential-like fields block.

The dependency is an already-authorized Host caller with `postJson(path, body)`.
This module supplies only these fixed read requests:

```js
postJson('/runtime/v1/turns/fixed-credit-denials/read', {
  proof, requestId, requestHash
});
postJson('/runtime/v1/turns/denials/read', {
  proof, dispatchId, stage
});
```

The caller uses the existing `RuntimeHostSessionProof` to authorize each
request. No Environment bearer or caller-selected URL is accepted. The Host
returns only its existing nullable `{ receipt, evidence }` or
`{ receipt, noSend }` pair. The validator checks the current owner binding,
active Observe grant, current source tuple, request or dispatch, denial stage
and limit, and complete empty-event evidence. Fixed denial wire values are
`hostPreQueue` and `hardLimitZero`; native denial wire values are
`nativeTurnBegin` and `hardLimitReached`. Both no-send records must bind their
native-session hash to SHA-256 of the current SDK session ID's UTF-8 bytes.
Registration and grant expiry are checked again after each read using the
optional `now()` clock. The reader returns safe per-case summaries, not the
raw receipts, proof, or endpoints. Read errors are blocked without including
their messages. A missing stored pair remains blocked; the reader never
creates a denial record.

The Host bridge source is owned separately and its acceptance is pending.
Fake-only tests prove the client request and validation behavior; they do not
prove that the bridge is accepted, that actual current contexts were supplied,
or that live P1 credit-denial acceptance has run.

`processProvider.run` accepts only the runner's fixed recipe IDs:

- `observe-cluster`
- `verify-hubble` (required preflight before any probe mutation)
- `observe-storage`
- `observe-sandbox`
- `observe-network-policy`
- `probe-service` (five-second bound)
- `observe-owned-resource`
- `mount-file` (`stat`, `write`, `read`, or exact-content `unlink`)

These are structured operations, not command strings. The adapter must map
them to fixed, source-owned safe argv recipes, constrain all targets to the
pinned same-cluster namespace and exact owner UID, and return structured
readback evidence. A service probe must use ready endpoints, return the exact
target Service UID and each tested TCP port's exact EndpointSlice target port
and fresh Cilium datapath verdict, bound to the source Pod UID and policy
UID/generation. It must reread the policy identity and Service UID after the
tests. A timeout or a policy object by itself is not proof of a deny. Every
recipe result must include the expected cluster identity. Cilium policy
collections may use Kubernetes' generic `v1/List` envelope, but each selected
policy must still have the exact Cilium API version, kind, UID, generation,
hash, and selector. The adapter must not evaluate input-provided shell,
scripts, argv, or commands. No direct Kubernetes deletes are used; Sandbox
release/reconcile and policy revoke use Environment routes.
`observe-owned-resource` requires nonempty `expectedPodUids` from invocation
inventory. Only late-resource discovery may explicitly pass an empty list with
`allowEmptyPodUidInventory: true`; it reads the actual linked Pod UIDs before
reconcile, and the runner supplies those same UIDs to the post-reconcile
absence check. This exception does not permit empty UID inventories for known
Sandbox resources.
Every `mount-file` response must also echo the exact target `podUid` and
`ownerId`. The runner checks these fields on marker stat, write, read, and
unlink; after release/rebind, cleanup is bound to the new current Pod UID and
the original exact marker bytes.

`process-provider.mjs` exports
`createP1NativeProcessProvider({ kubeContext, clusterIdentity })`. Its only
native executables are the fixed `kubectl` and `hubble` commands. Before each
recipe, it hashes the selected context's normalized CA certificate set and
requires the result to match the Azure Files provider's cluster identity. The
flattened kubeconfig read captures only the selected cluster fields, not user
credentials. The service-probe recipe performs a
bounded Bash `/dev/tcp` attempt from the exact `agenthost` container and reads
matching recent flow records using Hubble at `127.0.0.1:4245`; it fails closed
if a ready endpoint, exact current Pod, or consistent Cilium verdict is
missing. A Hubble executable and authenticated, owner-verified Relay for the
pinned cluster at `127.0.0.1:4245` must already be available. Before any probe
mutation, the runner checks the fixed Hubble status recipe; a missing CLI or
unhealthy Relay blocks all probes. The adapter does not install Hubble or
start a port-forward, and importing it contacts no service. Focused tests
inject a fake command runner and never invoke `kubectl` or `hubble`.

The adapter uses a fixed, source-owned `sh` script only for the four marker
operations. The path must be one direct child of the pinned AgentHost mount,
the current Pod UID and owner label are re-read before execution, and writes
use exclusive creation. Unlink is allowed only when the exact marker bytes
still match. The script and all values are passed as separate arguments; user
input is never evaluated as shell code.

TCP checks use a fixed Bash `/dev/tcp` script under the standard `timeout`
utility instead of `nc`, which is not installed in the AgentHost image.

## Probe behavior

1. **Storage owner isolation** verifies both owner PVC/PV/provider-binding and
   mount allowlist observations, reads/writes invocation-unique bytes through
   the actual `agenthost` mount, checks cross-owner visibility, requires real
   HTTP 403 lifecycle/bind denials, and checks native resource and byte
   retention across Environment release/rebind on the disposable non-runtime
   owner.
2. **Network datapath** verifies the exact live Cilium object UID, generation,
   source hash and selector, then probes every pinned allowed and denied
   in-cluster service identity from the exact current Pod UID. At completion,
   the owned Environment policy is revoked through its route and two native
   readbacks must confirm its generation-bound deny tombstone remains present.
3. **Sandbox owner lifecycle** abandons/reclaims/restarts only the
   invocation-owned non-runtime Sandbox, reconciles the observed late resource
   through Environment, and verifies the late UID is gone without a direct
   Kubernetes deletion.
4. **AgentHost readiness** reads the current placement and native runtime,
   binds the activation and registration fences to its exact Pod UID and
   placement generation, and verifies exact digest-pinned image reuse plus
   `scheduled`, `imageReady`, and `started` readiness against accepted options
   and budgets. Credit-denial receipts are a separate check and are not included
   in this probe or its overall status.

Unique marker files are removed only when their exact current owner Pod UID
and marker contents are still observed. Final Sandbox cleanup uses the
Environment abandon/reconcile routes only after matching the in-memory
invocation-created resource ID, Pod UID, and owner binding. Native absence
readbacks list only the pinned namespace, check the exact known claim and Pod
UIDs before owner or claim labels, and treat any present-but-mismatched identity
as uncertain rather than absent. Cleanup uncertainty is reported in the result
and makes an otherwise-passing probe `blocked`.

Run the focused tests from the repository root:

```powershell
node --test scripts/azure/p1-native-probes/*.test.mjs
```

## Native result and surface integration

The stable native probe IDs are:

- `storage-owner-isolation`
- `network-policy-datapath`
- `sandbox-owner-lifecycle`
- `agenthost-image-readiness`

Each entry is `{ name, status, code?, reason?, evidence, cleanup }`, where
`status` is `passed`, `failed`, or `blocked`, and `cleanup` is
`{ status, actions }` with per-action `complete` or `uncertain` receipts. The
runner returns these in
`{ kind: "p1-native-acceptance", schemaVersion: 1, overallStatus, startedAt,
completedAt, probes, receiptSha256 }`. `overallStatus` is P1-only; it must not
overwrite a separate UI/surface status.

## Full P1 aggregation

The full P1 CLI accepts the exact output envelopes from this runner and the
dispatch-recovery runner:

```powershell
npm run harness:p1 -- --surface api --input <manifest.json> --output <evidence.jsonl> --identity-module <authorized-identity-provider.mjs> --native-probe-envelope <native.json> --dispatch-recovery-envelope <recovery.json>
```

The aggregator maps the four native probe entries by exact `name`, preserves
their `status`, `evidence`, and `cleanup`, and adds the two dispatch-recovery
entries. It keeps surface status separate from native probe status. If either
envelope is missing, every required probe in that envelope remains blocked.
Malformed, inconsistent, duplicate, or extra envelope fields are rejected;
they are not ignored.
A probe cannot claim `passed` with empty evidence. Native passes also require
completed or not-required cleanup; read-only dispatch-recovery passes must
report no created resources.

Supply envelopes written by live probe calls. Test fixtures and scripted
results are not live evidence. The CLI validates the envelope contract but
checks the checksum to detect changes after the envelope was written. The
checksum does not authenticate who wrote a file, so a file alone is not proof
that its producer contacted the live owners.
