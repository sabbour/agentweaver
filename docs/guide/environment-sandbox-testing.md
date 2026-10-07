# Environment Sandbox testing

Run the focused Sandbox source tests from the repository root:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~EnvironmentSandboxLeasePostgresTests|FullyQualifiedName~AgentSandboxProviderTests|FullyQualifiedName~EnvironmentSandboxManagerTests" `
  --configuration Release --no-restore --verbosity quiet
```

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

Build the service and all referenced provider projects with:

```powershell
dotnet build services\environment\Agentweaver.Environment\Agentweaver.Environment.csproj `
  --configuration Release --no-restore --verbosity quiet
```
