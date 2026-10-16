# Environments, sandboxes, and volumes

## The mental model

An Environment is the durable owner of execution resources for a tenant, project, and run. A Sandbox lease is one placement of that execution. A workspace volume holds files independently of that placement.

This separation makes retirement and recovery explicit. Releasing a Sandbox does not delete the Environment or its workspace data. A ready Pod alone does not make an Environment ready for dispatch.

## Where this lives

- `services/environment/Agentweaver.Environment/EnvironmentLifecycleManager.cs` and `EnvironmentLifecycleStore.cs` — owner identity and lifecycle fences.
- `services/environment/Agentweaver.Environment/EnvironmentSandboxManager.cs` and `EnvironmentSandboxLeaseStore.cs` — lease admission, provider attempts, and reconciliation.
- `services/environment/Agentweaver.Environment/EgressIntentCompiler.cs`, `EnvironmentEgressManager.cs`, and `CiliumEgressPolicyAdapter.cs` — compiled egress and generation readback.
- `services/environment/Agentweaver.Environment/EnvironmentWorkspaceVolumeManager.cs` and `WorkspaceVolumeService.cs` — volume transitions and cleanup.
- `services/environment/Agentweaver.Environment/EnvironmentRuntimeWorkspaceResumeManager.cs` — retained-workspace resume.
- `services/environment/Agentweaver.Environment/EnvironmentSandboxBuildTestCommandManager.cs` — accepted BuildTest operations.
- `packages/Agentweaver.Providers.Sandbox.AgentSandbox/AgentSandboxProvider.cs` and `KubernetesAgentSandboxClient.cs` — Kubernetes resource lifecycle.
- `packages/Agentweaver.Providers.Storage.AzureFiles/AzureFilesCsiWorkspaceVolumeProvider.cs` and `KubernetesAzureFilesCsiClient.cs` — PVC/PV provisioning, flush, and release.

## Core concepts and invariants

### Generations protect different boundaries

The Environment lifecycle generation protects owner transitions. Sandbox resource and fencing generations protect placement. Storage resource/data generations protect volume state. Cilium policy generations protect applied network intent. These values are not interchangeable.

Registration derives a stable Environment ID from the trusted tenant, project, run, and idempotency key. Mutation paths obtain fresh Projects authority and the accepted selection, then recheck them before accepting asynchronous results. Sandbox mutation also requires separate run-selection permission.

### Provider effects have durable attempts

The lease store reserves one current Sandbox lease per Environment before dispatching a provider call. It saves the provider/options snapshot, request identity, and recovery intent. Each external effect has a durable attempt written before the call and an outcome recorded before owner completion.

An unresolved call blocks release and lifecycle advancement. Timeout, cancellation, an empty provider list, or a terminal lease does not prove that an external effect stopped.

## Provisioning and readiness

1. Environment validates the current owner fence and the exact accepted Sandbox candidate.
2. It verifies the bound workspace and applied network-policy generation.
3. It reserves the lease and workspace attachment, then records the provider attempt.
4. `AgentSandboxProvider` verifies the RuntimeClass and exact PVC identity. It creates a fenced `SandboxTemplate`, a zero-replica `SandboxWarmPool`, and a `SandboxClaim`.
5. Observation checks the controller-owned Sandbox and Pod, workspace mount, image, isolation settings, and startup evidence.
6. Environment rechecks authority and network evidence before reporting `ReadyForDispatch`.

The adapter disables automatic service-account token mounting and validates the restricted Pod profile. The optional AgentHost launch profile supplies its pinned image, configuration, private state, and HTTPS endpoint. The zero-replica warm pool does not prestart a spare worker.

## Egress compilation

`EgressIntentCompiler` intersects the platform baseline, project narrowing, and admitted run needs. It preserves destination, port, protocol, and purpose. It rejects project widening and needs outside the effective allowlist.

FQDN rules require admitted TCP and UDP DNS access to the cluster resolver. Direct Key Vault and Entra destinations are rejected. Requirements for L7 mediation remain distinct from Cilium's L3/L4 rule set.

The Cilium adapter writes owner-specific policy and verifies the exact generation and intent hash. Empty or revoked intent uses explicit deny-all rules. Readback proves the stored policy matches; it does not prove packet-level datapath enforcement.

## Workspace lifecycle

The Azure Files adapter requires the configured StorageClass, Azure Files CSI driver, approved mount options, supported consistency, capacity, and access mode. Provisioning waits for a bound PVC, verifies the matching PV and claim UID, and confirms the effective reclaim policy.

The owner binds and attaches that resource before Sandbox use. Trusted suspend flush verifies the exact mounted volume and returns evidence for the next data generation. Resume checks retained data, the new mount, current Host evidence, and the new network generation.

Replace commits the new resource generation before cleaning up the old one. Cleanup failure leaves durable work for retry; it does not roll back the replacement. Release uses the saved provider binding and exact UID preconditions to retain or delete the intended resource. An attached or bound volume cannot be released as if it were unused.

BuildTest commands use a separate stable operation tied to Core's accepted checkpoint. Version 2 prepares the saved repository seed and uses a separate read-only collector. Native output remains retained until the verified Events acknowledgment permits cleanup. Environment owns execution evidence; Core owns workflow completion.

## Reconciliation and failure behavior

Reconciliation resumes the recorded operation rather than provisioning a replacement. A late resource gets separate cleanup state without changing the original terminal lease. The current owner fence authorizes cleanup; the saved source fence identifies the old resource. Cleanup from an older lifecycle generation does not mutate the current workspace.

Provider or authority mismatches stop readiness. Unknown effects stay unresolved until exact observation or release evidence resolves them. No path treats a request to delete as proof that the native resource is absent.

## Related pages

- [Sandbox contracts and lifecycle](../architecture/environment-sandbox.md)
- [Egress contracts](../architecture/environment-egress.md)
- [Suspend flush tests](../guide/environment-sandbox-testing.md)
- [Source Control and repository capture](./source-control.md)
