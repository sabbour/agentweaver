# P1 creation evidence reads

These source-only reads join the retained creation operations from Environment
to the current Core P1 root binding. They do not create resources, retrofit
legacy records, change grants, or run a native acceptance probe.

## Environment contract

`GET /internal/projects/{projectId}/runs/{runId}/environments/{environmentId}/coordination/sessions/{sessionId}/runtime-bootstrap/profiles/{profileId}/p1-creation-binding`

The route uses the existing runtime-profile read authorization: authenticated
Environment audience, exact project/run token bindings, fresh `ReadRunSelection`,
current Core owner context, current profile and placement, and an active owner
fence. The read executes inside the placement's owner lock. Core's P1 binding is
read before and after the database read, and current owner context and authority
are rechecked before the response is accepted. A child session cannot use its
parent's root binding.

The response is `EnvironmentP1CreationBinding` with these JSON fields:

| Field | Meaning |
| --- | --- |
| `contractVersion` | Exactly `1`. |
| `binding` | Full current Core-owned `P1InvocationBinding`, including Core/setup invocation IDs, receipt and selection hashes, actor, membership, root session, execution fence, root state version, and acceptance time. |
| `environmentFence` | Exact tenant/project/run/Environment tuple and lifecycle generation. |
| `environment` | Original completed Environment registration operation. |
| `workspaceMetadata` | Original completed volume-metadata creation operation. |
| `workspaceProvision` | Original completed provider-volume creation operation for the currently attached resource generation. |
| `sandboxProvision` | Original completed Sandbox provider operation for the current placement. |
| `podObservation` | Current provider-observed Pod configuration through the existing authenticated observation path. |

Each operation receipt contains `operationId`, `resourceKind`, `resourceId`,
nullable `resourceGeneration`, `providerFencingGeneration`,
`transitionRevision`, `leaseRevision`, nullable `providerResource`, and
`createdAt`/`completedAt`. These are stored creation results, not current
generation values copied onto old operations. The Sandbox's `resourceId` is the
stored operation ID string; its `providerResource.resourceId` is the distinct
provider-returned identity. Pod, SandboxClaim, Sandbox, and storage claim
identities remain distinct.

The reader requires all four exact completed records with the same full Core
binding. Missing, foreign, ambiguous, pending, uncertain, stale, or replaced
records fail closed. Current Workspace and Sandbox resources, provider
bindings, attachment, lifecycle/provider fences, lease revision and expiry
must still join. Lease and Workspace transition revisions may have advanced
since creation; creation revisions remain unchanged, and the same resource,
generation and provider binding must remain current. Provider options,
credentials and recovery/release descriptors are checked internally but never
returned.

Successful responses have `Cache-Control: no-store`. Runtime binding failures
return `409` with a stable `code`; current Projects authorization failures
retain the existing authorization status mapping. There is no successful
empty or fabricated-evidence response.

## Host consumer

`GET /runtime/v1/readiness/p1-evidence`

This distinct authenticated Host route returns `RuntimeHostP1EvidenceReceipt`:
`contractVersion: 1`, `observedAt`, `creation` (the Environment contract above),
and `host` (the existing `RuntimeHostReadinessEvidenceReceipt`).

The Host brackets Environment's read with authenticated Core root reads and
live unchanged registration checks. It brackets its existing readiness and
read-only mount observation with two creation reads, rejects changed creation
identities or Pod configuration, and joins the exact registration and
Downward-API Host Pod UID. Both owner clients use bounded responses, no-store,
the original bearer and tenant selector, and fixed owner addresses. Missing
owner data or changed authority fails rather than falling back to ordinary
readiness. Host authorization failures retain the existing safe `403` mapping;
unavailable owner reads return `503`.

Ordinary `/runtime/v1/readiness`, `/health/ready`, and
`/runtime/v1/readiness/evidence` are unchanged. They do not require P1 creation
records and must not be relabeled as invocation-specific acceptance.

## Scoped downstream consumer

The source-owned consumer in
`scripts/azure/p1-native-probes/owner-providers.mjs` reads the Environment
creation-binding route before Host, validates the Host P1-evidence envelope
against that creation read, reads Environment creation again, then rechecks the
Core root binding and complete Runtime registration. It requires the
authenticated owner response to carry `Cache-Control: no-store`; responses are
bounded, redirect-free, and checked against the exact record fields and
nullability in the C# contracts.

The join matches the entire Environment `binding` to Core and registration,
checks the trusted actor, tenant, selection, root session and fences, validates
all four immutable completed operations and their timestamp ordering, and
joins the Environment fence to the provider-observed Pod. For Sandbox creation,
the planned `resourceId` is the operation GUID. Its distinct
`providerResource.resourceId` is the provider-returned SandboxClaim UID and is
matched to registration placement and the Pod's claim/resource identity. It is
not treated as the Pod UID. Host's existing readiness, Pod, and mount checks
remain separate joins. Across the before/Host/after bracket, only Pod
`observedAt` may vary; all creation identities and other fields must remain
stable.

A successful owner join is still only partial observation. It returns
`status: "blocked"`, `code: "native_no_send_effect_proof_missing"`,
`acceptanceEvidence: false`, and `nativeNoSendProven: false`, with a small
sanitized summary of the joined operation IDs and claim/Pod identities. The
journey authorizer continues to deny, the readiness adapter throws, and the
CLI always exits blocked with an empty reserved output; it does not call auth,
journey, readiness, or process methods or execute the runner. A fake
`passed`/`acceptanceEvidence: true` provider result cannot bypass that block.
Missing or invalid legacy authority keeps the generic missing-authority
diagnostic.

This slice does not provide the native no-send, cursor, effect, or dispatch
acknowledgments or the remaining runner proof slots. It does not demonstrate
child execution, network datapath enforcement, storage-byte isolation, generic
replacement, or live acceptance. Controlled fixtures validate contract
handling only; no live owner, database, cluster, or P1 run is used.

## Evidence limits

The successful read joins actual retained creation provenance to current
owner-authorized configuration observations and the Host's existing
read-only mount observation. It is not proof of child execution, a native
no-send check, network datapath enforcement, storage byte isolation, or a
released/deployed product. The source-owned native CLI consumer now performs
this join, but remains blocked on the missing native proof slots described
above; owner reads alone do not remove its acceptance blocker.
