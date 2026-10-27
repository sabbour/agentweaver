# Environments, sandboxes, and volumes

Use this page to understand how Agentweaver manages execution resources,
isolated runtime placement, and workspace files. An Environment is the durable
owner of resources for a tenant, project, and run. A Sandbox lease is one
placement of that execution. A workspace volume holds files independently of
the placement.

Releasing a Sandbox does not delete the Environment or its workspace data. A
ready Pod alone does not make an Environment ready for dispatch.

P1-specific creation additionally checks Core's current invocation binding
before reserving or issuing effects. Environment retains the exact creation
operation and result in its own transaction; a saved Core binding or a planned
provider name alone cannot prove a resource was created. Resources created
without that retained binding cannot be enrolled later. See
[P1 invocation-bound creation](../architecture/environment-sandbox.md#p1-invocation-bound-creation)
for the remote-read race, and [P1 creation evidence](../reference/p1-creation-evidence.md)
for the authenticated retained-record read and bounded Host observation join.
Native CLI consumption remains separate.

## Find the implementation

| Source | Responsibility |
| --- | --- |
| `EnvironmentLifecycleManager.cs` and `EnvironmentLifecycleStore.cs` | Owner identity and lifecycle fences |
| `EnvironmentSandboxManager.cs` and `EnvironmentSandboxLeaseStore.cs` | Lease admission, provider attempts, and reconciliation |
| `EgressIntentCompiler.cs`, `EnvironmentEgressManager.cs`, and `CiliumEgressPolicyAdapter.cs` | Egress compilation and generation readback |
| `EnvironmentWorkspaceVolumeManager.cs` and `WorkspaceVolumeService.cs` | Volume transitions and cleanup |
| `EnvironmentRuntimeWorkspaceResumeManager.cs` | Retained-workspace resume |
| `EnvironmentSandboxBuildTestCommandManager.cs` | Accepted BuildTest operations |
| `AgentSandboxProvider.cs` and `KubernetesAgentSandboxClient.cs` | Kubernetes Sandbox lifecycle |
| `AzureFilesCsiWorkspaceVolumeProvider.cs` and `KubernetesAzureFilesCsiClient.cs` | Azure Files provisioning, flush, and release |

The service files are under `services/environment/Agentweaver.Environment/`.
Provider files are under `packages/Agentweaver.Providers.Sandbox.AgentSandbox/`
and `packages/Agentweaver.Providers.Storage.AzureFiles/`.

## Understand lifecycle fences

Different generations protect different boundaries:

- Environment lifecycle generation protects owner transitions.
- Sandbox resource and fencing generations protect placement.
- Storage resource and data generations protect volume state.
- Cilium policy generations protect applied network intent.

Do not use these values interchangeably. Registration derives a stable
Environment ID from trusted tenant, project, run, and idempotency values.
Mutation paths obtain fresh Projects authority and the accepted selection,
then recheck them before accepting asynchronous results. Sandbox mutation also
requires separate run-selection permission.

The lease store reserves one current Sandbox lease per Environment before
calling a provider. It saves the provider/options snapshot, request identity,
and recovery intent. Each external effect has a durable attempt before the
call and a recorded outcome before owner completion. An unresolved call blocks
release and lifecycle advancement. Timeout, cancellation, an empty provider
list, or a terminal lease does not prove that an external effect stopped.

## Provision a Sandbox

Before provisioning, you need current owner authority, the accepted Sandbox
candidate, a bound workspace, and applied network-policy evidence.

1. Environment validates the current owner fence and exact accepted candidate.
2. It verifies the bound workspace and applied network-policy generation.
3. It reserves the lease and workspace attachment, then records the provider
   attempt.
4. `AgentSandboxProvider` verifies the RuntimeClass and exact PVC identity. It
   creates a fenced `SandboxTemplate`, a zero-replica `SandboxWarmPool`, and a
   `SandboxClaim`.
5. Observation checks the controller-owned Sandbox and Pod, workspace mount,
   image, isolation settings, and startup evidence.
6. Environment rechecks authority and network evidence before reporting
   `ReadyForDispatch`.

The adapter disables automatic service-account token mounting and validates
the restricted Pod profile. The optional AgentHost launch profile supplies its
pinned image, configuration, private state, and HTTPS endpoint. It also mounts
`service-backend-ca` read-only at `/var/run/service-backend-ca` so AgentHost can
validate internal owner HTTPS calls. Environment's bootstrap and sandbox HTTPS
clients validate against the same CA while preserving direct-to-Pod-IP routing
and the TLS hostname check. A zero-replica warm pool does not prestart a spare
worker.

## Compile network egress

`EgressIntentCompiler` intersects the platform baseline, project narrowing, and
admitted run needs. It preserves destination, port, protocol, and purpose. It
rejects project widening and needs outside the effective allowlist.

Fully qualified domain name (FQDN) rules require admitted TCP and UDP DNS
access to the cluster resolver.
Direct Key Vault and Entra destinations are rejected. L7 mediation needs remain
separate from Cilium's layer 3 (IP) and layer 4 (transport) rule set; L7 means
the application layer.

The Cilium adapter writes owner-specific policy and verifies the exact
generation and intent hash. Empty or revoked intent uses explicit deny-all
rules. Readback proves the stored policy matches; it does not prove packet-level
datapath enforcement.

## Manage workspace data

The Azure Files adapter requires the configured StorageClass, Azure Files
Container Storage Interface (CSI) driver, approved mount options, supported
consistency, capacity, and access mode. Provisioning waits for a bound
persistent volume claim (PVC), verifies the matching persistent volume (PV)
and claim UID, and confirms the effective reclaim policy.

The owner binds and attaches that resource before Sandbox use. Trusted suspend
flush verifies the exact mounted volume and returns evidence for the next data
generation. Resume checks retained data, the new mount, current Host evidence,
and the new network generation.

Replace commits the new resource generation before cleaning up the old one.
Cleanup failure leaves durable work for retry; it does not roll back the
replacement. Release uses the saved provider binding and exact UID
preconditions to retain or delete the intended resource. An attached or bound
volume cannot be released as if unused.

BuildTest commands use a separate stable operation tied to Core's accepted
checkpoint. Version 2 prepares the saved repository seed and uses a separate
read-only collector. Native output remains retained until the verified Events
acknowledgment permits cleanup. Environment owns execution evidence; Core owns
workflow completion.

## Reconcile uncertain operations

Reconciliation resumes the recorded operation rather than provisioning a
replacement. A late resource gets separate cleanup state without changing the
original terminal lease. The current owner fence authorizes cleanup; the saved
source fence identifies the old resource. Cleanup from an older lifecycle
generation does not mutate the current workspace.

Provider or authority mismatches stop readiness. Unknown effects stay
unresolved until exact observation or release evidence resolves them. A delete
request is not proof that the native resource is absent.

For supported Environment and workspace settings, see the
[runtime configuration reference](../reference/runtime-configuration.md) and
[provider configuration guide](../guide/configure-providers.md).

## Related content

- [Sandbox contracts and lifecycle](../architecture/environment-sandbox.md)
- [Egress contracts](../architecture/environment-egress.md)
- [Suspend flush tests](../guide/environment-sandbox-testing.md)
- [Source Control and repository capture](./source-control.md)
