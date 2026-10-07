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

The manager test confirms that missing target-project `WriteProjects` authority
is rejected before run selection or Sandbox provider access. It is not a full
Environment API integration test. The source tests do not prove live RBAC,
Kata/nested-virtualization availability, Cilium datapath enforcement,
AgentHost configure/readiness, Core run pins, or automatic terminal-run
reclamation.

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
viewer/foreign/selection denials, role revocation after selection, and a late
provider callback gated at the Kubernetes claim-create response and fenced into
retirement. It runs the production Sandbox provider and Kubernetes client
against a fake Kubernetes HTTP API; only the Workspace provider and Cilium
policy-resource boundary are controlled. It is not a live Kubernetes or
datapath test. Sandbox observations remain `Pending` without AgentHost configure
evidence.

Build the service and all referenced provider projects with:

```powershell
dotnet build services\environment\Agentweaver.Environment\Agentweaver.Environment.csproj `
  --configuration Release --no-restore --verbosity quiet
```
