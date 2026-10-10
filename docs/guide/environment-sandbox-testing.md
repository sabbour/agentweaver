# Environment Sandbox testing

Run the focused Sandbox source tests from the repository root:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~EnvironmentSandboxLeasePostgresTests|FullyQualifiedName~AgentSandboxProviderTests|FullyQualifiedName~EnvironmentSandboxManagerTests" `
  --configuration Release --no-restore --verbosity quiet
```

BuildTest contracts, checkpoint verification, configuration, and migration/model consistency have a separate non-container selection:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~SandboxBuildTestCommandContractTests|FullyQualifiedName~EnvironmentSandboxBuildTestAcceptedCommandVerifierTests|FullyQualifiedName~SandboxBuildTestProductionWiringTests" `
  --configuration Release --no-restore --verbosity quiet
```

These tests cover immutable accepted intent, bounded receipts, collector interruption, missing required output, and server-pinned capability configuration.
The migration/model test generates metadata only; it does not connect to PostgreSQL.
These results do not prove durable provider effects, Kubernetes isolation, Cilium datapath enforcement, or MAF recovery.

The lease tests use the test project's disposable PostgreSQL 16 container. They
cover per-Environment capacity, idempotent reservation, owner lifecycle fencing,
explicit abandonment, supported terminal evidence, exact release receipts,
monotonic generations, immutable provider requests, late provision callbacks,
and the no-effect abandon path. Docker must be available for those tests.

The Agent Sandbox provider test uses an in-memory `HttpMessageHandler`, not a
Kubernetes cluster. It checks the actual v1beta1 template, warm-pool and claim
requests, `warmPoolRef` and `DeleteForeground`, deterministic duplicate
provisioning, RuntimeClass and PVC reads, Pod/PVC attachment evidence, withheld
readiness without a verified network generation, and foreground deletes with
UID preconditions.

The manager tests confirm that missing target-project `WriteProjects`
authority is rejected before run selection or Sandbox provider access. They
also verify that the versioned placement projection preserves the exact
resource identity, rejects expired/stale/non-current/foreign leases, and detects
lease changes during the post-read authorization check. They are not a full
Environment API integration test. The source tests do not prove live RBAC,
Kata/nested-virtualization availability, Cilium datapath enforcement, AgentHost
configure/readiness, Core run pins, or automatic terminal-run reclamation.

The production-authority HTTP and PostgreSQL integration is in the Identity
Broker test project:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj `
  --filter "FullyQualifiedName~BrokerIssuedOwnerAndSeparateRunSelectionAuthorizeWorkspaceVolumeHttpEffects" `
  --configuration Release --no-restore --verbosity quiet
```

This test uses a broker-issued JWT, real Projects membership/role checks and
immutable run selection, Environment HTTP routes, and disposable PostgreSQL
lease/lifecycle state. It covers authorized provision, inspect, repeated
placement-only abandonment, exact resource UID/generation/fence, stale fences,
viewer/foreign/selection denials, and a late provider callback gated at the
Kubernetes claim-create response and fenced into retirement. It also reads the
versioned placement projection with an owner that lacks `ReadRunSelection`,
asserts that no selection read occurs, and rejects a viewer without
`WriteProjects`. The internal run-bound placement route succeeds for an
Orchestrator token with exact project/run bindings and `ReadRunSelection`, while
the public placement route still rejects that token for lack of `WriteProjects`.
The same integration checks unbound owners, viewers, missing roles, mismatched
project/run/tenant, wrong Environment audience, and role revocation between
fresh Core checks. The placement callback test pauses immediately before its
final Core authorization request while the store holds the Environment owner
transaction and advisory lock. It starts a competing retirement CAS, verifies
the wait with PostgreSQL lock metadata and `pg_blocking_pids`, revokes the real
Core role, and then allows the authorization request to run. The response must
be a typed 403 with no placement projection or provider effect; the retirement
must proceed only after the denied callback rolls back. A subsequent provider
request against the retired lease must still fail its own current-lease check.
Placement reads do not fetch run selection or cause provider effects. It runs
the production Sandbox provider and Kubernetes client against a fake
Kubernetes HTTP API; only the Workspace provider and Cilium
policy-resource boundary are controlled. It is not a live Kubernetes or
datapath test. Sandbox observations remain `Pending` without AgentHost configure
evidence.

The separate native SDK harness reuses the canonical placement manager.
Its profile callback reads actual Orchestrator work-item context under the retained lease.
The module tests retire a lease before lifecycle advancement, as the admitted lifecycle guard requires.
The readiness module test retains the actual PostgreSQL advisory lock during Cilium and Sandbox observations.
It observes a competing retirement through `pg_locks` and releases it only after readiness completes.
The retained snapshot supplies selection data without a recursive Projects selection request.
The combined source scenarios then exercise actual Broker delivery, SDK callbacks,
immutable Orchestrator receipts, and reference-only Events accounting.
The [AgentHost tests](../architecture/agenthost#focused-source-checks) separately cover authenticated configuration and current readiness.
See the [native accounting scenarios](./testing#cost-bindings-pricing-and-usage-storage)
for their exact command and local evidence boundary.

## Orchestrator runtime context and recovery

The Orchestrator requires the selected Sandbox binding from the immutable,
persisted run-selection context for the session's exact current execution
fence. It does not rebuild a missing binding from accepted-selection JSON or a
run snapshot. If that versioned context is absent, the runtime-owner-context
endpoint returns `runtime_owner_context_unavailable`; a lease or owner
registration alone does not prove that an SDK process is attached or ready.

Run the focused PostgreSQL owner-store races with:

```powershell
dotnet test tests\Agentweaver.Orchestrator.Core.Tests\Agentweaver.Orchestrator.Core.Tests.csproj `
  --filter "FullyQualifiedName~RevalidatesAfter" `
  --configuration Release --no-restore --verbosity quiet
```

These five cases cover revocation after a blocked fork-command insert,
revocation after a blocked owner-outbox sequence update, and fresh authority
checks after registered-duplicate command-row waits. They assert rollback of
the reservation or registration and the explicit unregistered result. They use
real PostgreSQL lock waits with a test authority callback; use the Broker
integration below for Projects-role revocation through the actual HTTP path.

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj `
  --filter "FullyQualifiedName~BrokerIssuedRunTokenRegistersSessionsDeliversAtTurnBoundaryAndKeepsGatePending" `
  --configuration Release --no-restore --verbosity quiet
```

The Broker-backed Orchestrator/Events integration waits for the real owner SQL
insert or outbox update, revokes the Projects role, and then verifies the late
authority check. A revoked Prepare leaves no reservation. If revocation occurs
after Events commits a fork but before owner registration, the owner returns an
explicit unregistered result with no child, request, or forked outbox record;
the Events journal lineage remains. A registered duplicate blocked on its
command row is also denied after revocation.

The same integration verifies that an active, confirmed-mapped child cannot
obtain runtime-owner context when its exact current-fence Sandbox context is
missing, and that this denial has no owner side effects. Failure and recovery
advance the current decision and execution fence, cancel a pending gate, and
carry the exact Sandbox binding into a new versioned context without changing
prior decision or context versions. Run-wide grants are superseded, not
reissued or resurrected. Recovered logical run state does not make physical
effects available: they remain `unavailable`, and the recovery outbox records
`physicalEffectsReplayed=false`. These checks do not prove physical SDK
readiness or replay.

Build the service and all referenced provider projects with:

```powershell
dotnet build services\environment\Agentweaver.Environment\Agentweaver.Environment.csproj `
  --configuration Release --no-restore --verbosity quiet
```
