# Environment egress intent candidate

The `Agentweaver.Environment` source adds an unpublished Environment-manager
candidate for compiling and reconciling Cilium egress intent. It is not a
deployed service, does not label or provision AgentHost pods, and does not prove
that the Cilium datapath enforces a policy.

The same Environment service candidate also owns workspace-volume lifecycle routes
and registers the Azure Files CSI Storage provider. This page covers only egress;
the [foundation overview](./overview.md#workspace-volume-lifecycle) summarizes the
volume lifecycle, pinned release binding, and Replace cleanup guarantees.
Neither candidate is deployed by this source change.

## Ownership and operation

`EnvironmentEgressManager` is the operation entry point. Each apply, verify, revoke, or reconciliation reads the current caller from Projects & Config at
`GET /api/authorization/context`; authorization and run-selection responses
must be `Cache-Control: no-store`. The caller token is sent only to Projects &
Config, whose resource-server authentication validates the token audience and
purpose; it is not stored in the policy or provider pin. An optional tenant
selector is forwarded for Projects & Config to validate. Any token-bound tenant
must match the environment owner, and any bound project/run must match the
requested owner. An unbound project-level grant is still intersected with the
exact Environment tenant/project/run and immutable admitted selection.

The consumer uses current `WriteProjects` on the target project for
Apply/Verify/Revoke/Reconcile. It does not use `ReadProjects` as mutation authority;
`ReadProjects` remains metadata inspect/describe authority only. Apply and
Verify/Reconcile also require the separate positive `ReadRunSelection` grant to retrieve
the admitted run selection. That grant is not implied by `WriteProjects`; the
Projects selection endpoint independently enforces its existing Orchestrator
scope/role. If the caller has project write authority but lacks run-selection
access, the operation fails explicitly before Kubernetes access. Revoke needs
only `WriteProjects` and the Environment owner identity; it does not need to
read or compile the run selection.

`ProjectsConfig:BaseAddress` must be an absolute HTTPS URI without user info,
query, or fragment. The authenticated Projects and Kubernetes clients disable
automatic redirects and reject every 3xx response, so credentials are not
forwarded to a redirect target.

Every operation fetches fresh context before effects. Apply/Verify/Reconcile
recheck it after selection retrieval and after Kubernetes readback, before
pinning or recording reconciliation; Revoke rechecks after the tombstone
readback. A changed membership, binding, or effective grant rejects the
callback and cannot mark the Environment ready.

`EnvironmentOwnerIdentity` carries the tenant, project, run, and environment
IDs needed to bind policy operations to the current Projects context and
selection. `EnvironmentGenerationFence` carries that owner identity and its
`LifecycleGeneration`. The Cilium API separately receives `PolicyGeneration`
and `ExpectedPreviousPolicyGeneration`; these fence the policy resource and
must not be conflated with the Environment lifecycle generation. Policy
generations are positive, owner-CAS-checked values and need not be contiguous.

The Environment host, lifecycle store, and `environment` schema are owned by
`Agentweaver.Environment`. `EnvironmentGenerationFence.LifecycleGeneration` is
the owner-controlled CAS fence; it is distinct from every Cilium policy
generation and Storage resource/data generation. Registration and lifecycle
advancement use an explicit expected generation and idempotency key. Network
effects reserve before provider work, then complete only against the current
owner fence. A stale callback remains unresolved and blocks further effects
until the active owner verifies the exact current provider object and records
that observation through `/api/network-egress/reconcile`. Reconciliation
requires current target-project write authority, the separately admitted run
selection, an active owner fence, matching operation/resource identity, and
exact generation/hash/revocation readback. It cannot revive an unknown,
released, stale, or foreign owner.

Verify also requires the owner's latest verified policy generation to match
the requested generation, with no reserved or unresolved network effect. A
provider readback alone cannot make an untracked generation ready. Failed or
stale operations that never started a provider call do not block release;
operations that may have changed provider state remain unresolved until exact
reconciliation.

The owner producer is registered in the host, but the existing run/environment
registration producer has not yet been bound to it. There is deliberately no
public lifecycle-registration endpoint: Apply and Bind cannot create or default
an Environment owner, so unknown Environments remain denied until the producer
integration is supplied.

The compiler checks **platform baseline ∩ project narrowing ∩ admitted run
needs**. Rules carry purpose, destination kind, FQDN pattern or CIDR/Kubernetes
service, port, and protocol. Wildcards and network ranges are normalized, and a
project or run cannot widen its parent set. Malformed and unrepresentable rules
fail closed. FQDN rules require both admitted TCP/53 and UDP/53 rules to the
configured `kube-system/kube-dns` service. Direct FQDN egress to Azure Key Vault
or Entra endpoints—and wildcard ancestors broad enough to include those
destinations—is rejected; agent credentials must not be sent directly to those
services.

Previously persisted egress rules with only `host`, `port`, and `protocol` are
read as `PublicHttps` FQDN rules. Their old effective allowlist is retained as
the baseline, narrowing, and run needs, so this compatibility path cannot widen
access. `PublicHttps` requires L7 mediation; this Cilium-only slice therefore
fails closed for those legacy selections instead of treating unclassified
destinations as ordinary L3/L4 egress.

The selected layered Network Policy binding must include exactly one L3/L4
provider. Cilium options are accepted only when provider ID, adapter version,
schema version, and options revision match the immutable selection, and the
provider advertises all capabilities required by the compiled intent before
any Kubernetes mutation. Cilium
supports the compiled L3/L4/FQDN constraints here; it does not supply L7
mediation for `PublicHttps` or `RemoteMcp`, so those requirements fail rather
than becoming a blanket HTTPS rule. This slice contains no L7 adapter; if the
immutable selection contains an L7 candidate, the operation also fails
explicitly rather than silently ignoring or pinning an unapplied layer.

The Cilium adapter assigns a hashed environment/owner selector and derives its
resource name from the full owner tuple. It applies a positive policy generation
against the explicit expected predecessor using the observed Kubernetes
resource version, and verifies the
exact policy object, selector, spec, intent hash, and generation on readback.
It pins through the existing `ProviderResolver.PinNetworkPolicy` only after
that verification. Revoke advances the generation to a verified empty-egress
`egressDeny: [{}]` tombstone instead of deleting the policy, so a delayed apply cannot
recreate a revoked generation—even when revoke is the first observed operation.
The operation's current request trace records the selected layer/provider/adapter
version and observed intent/resource generations, verification, and revoke state.
It does not tag the owner tuple, bearer token, destination rules, or raw provider options.
`ReadyForDispatch` reports policy-object verification and L3/L4 pinning only;
it is not permission to start a model run. Any automated/model-run execution
must separately validate its existing Orchestrator action grant. `DatapathEnforcementVerified`
remains false because an API readback is not datapath evidence.

```mermaid
sequenceDiagram
    participant E as Environment manager
    participant P as Projects & Config
    participant C as Egress compiler
    participant K as Kubernetes Cilium API
    participant R as ProviderResolver
    E->>P: GET authorization context (fresh validated caller, optional tenant)
    P-->>E: Current grouped permission and revisions (no-store)
    E->>E: Require target-project WriteProjects and owner intersection
    opt Apply, verify, or reconcile
        E->>E: Require separate ReadRunSelection grant
    E->>P: GET immutable project/run selection (no-store)
    P-->>E: Baseline, narrowing, admitted needs, layered providers
    E->>C: Compile baseline ∩ narrowing ∩ run needs
    C-->>E: Purpose-grouped intent or explicit failure
    E->>P: Recheck fresh context before Kubernetes effect
    P-->>E: Same current membership and effective grants
    end
    alt Apply or verify
        E->>K: Apply generation with resourceVersion fence
        K-->>E: Applied policy object
        E->>K: Read and verify exact selector, spec, hash, and generation
        K-->>E: Verified Kubernetes object; datapath remains unproven
        E->>P: Recheck fresh context after async Kubernetes work
        P-->>E: Same current membership and effective grants
        E->>R: Pin L3/L4 resource and applied intent generation
        R-->>E: Pinned binding or explicit failure
        E-->>E: Report policy readiness only after verified pin
    else Revoke
        E->>K: Replace with next-generation deny-all tombstone
        K-->>E: Tombstone object
        E->>K: Read and verify owner, empty egress, hash, and generation
        K-->>E: Verified revocation fence or explicit failure
        E->>P: Recheck fresh WriteProjects authority
        P-->>E: Same current membership and effective grants
    else Reconcile
        E->>K: Read and verify the operation's exact current policy/tombstone
        K-->>E: Verified generation, hash, owner selector, and effect
        E->>P: Recheck WriteProjects and ReadRunSelection
        P-->>E: Same current membership and effective grants
        E->>E: Record reconciliation under the current owner fence
    end
```

## Integration and evidence boundary

The selector labels form the boundary that a future Environment/Sandbox
composition must place on the corresponding environment pods. This source slice
does not update sandbox claims or templates, add Kubernetes RBAC/workload
identity, or bind the existing run/environment registration producer. The host
is wired but is not deployed by this work. Policy verification proves only that the
expected Kubernetes object was persisted and observed. Real datapath probes and
live cluster evidence are reserved for the separate network-acceptance work;
no probe or deployment is performed by these tests.

The focused compiler and fake-Kubernetes tests cover FQDN wildcard and CIDR
intersection, ports/protocols, widening rejection, exact selector isolation,
generation races, apply/readback failures, positive and negative `WriteProjects`
and `ReadRunSelection` checks, unbound project authority narrowing, stale
authorization callbacks, Cilium option and capability negotiation, absent-policy
revocation fences, and the explicit L7 limitation. The
[testing guide](../guide/testing.md) lists the command and what the tests cannot
prove.
