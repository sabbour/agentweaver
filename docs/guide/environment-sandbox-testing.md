# Test Environment and Sandbox behavior

Use this article to select tests for Environment workspace volumes, Sandbox
leases, suspend and resume, or BuildTest. Most checks use controlled owners,
providers, and Kubernetes responses. A passing source test does not prove a
live Kubernetes, CSI, or Cilium effect.

For root-registration and dispatch-egress regressions, run:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj `
  --filter "FullyQualifiedName~PublicConfirmedPlanRevalidatesPrePlanRootRegistrationBeforeEgress|FullyQualifiedName~PlacementEgressRequiresCompletePinnedEvidenceWithoutClaimingHostReadiness" `
  --configuration Release --verbosity quiet
```

These real-PostgreSQL cases cover pre-plan registration after public confirmation,
verified placement egress, denied candidate/pending plans and expired leases, and
supported child turn registration. They do not prove native child execution or a
completed checkpoint join. Egress verification is read-only and requires fresh
`ReadRunSelection` authority bound to the exact project and run, before and after
provider I/O. It does not require `WriteProjects`; applying or reconciling network
policy still does, and remains denied to run-bound tokens.

## Prerequisites

Install the .NET 10 SDK selected by `global.json`. PostgreSQL-backed tests use
disposable Testcontainers databases and require a Docker-compatible engine
that can pull the PostgreSQL test image. Do not point these tests at a shared
database.

Run commands from the repository root. The actual Host image check in this
article builds and runs a container; run it only after approving local image
construction and execution.

Each numbered section is an independent test procedure. Run only the sections
that match the behavior you changed or need to verify.

The [P1 creation evidence contract](../reference/p1-creation-evidence.md) has
separate read-only join selections:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~EnvironmentP1CreationRead" --configuration Release --verbosity quiet
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj `
  --filter "FullyQualifiedName~RuntimeP1CreationBindingHttpClientTests|FullyQualifiedName~P1Evidence" `
  --configuration Release --verbosity quiet
```

The PostgreSQL case creates records through real owner transactions, exercises
the full current-resource read under the owner lock in a read-only transaction,
and compares retained rows before and after the read. Planned Sandbox names
and provider-returned claim UIDs are deliberately distinct. CI requires its
passing execution in the Environment suite. Controlled
Host tests exercise the actual HTTP consumer and reject missing, foreign,
stale, replaced or child-root joins. These are source contract tests, not live
provider or native CLI acceptance.

## 1. Test suspend and flush contracts

Run the focused suspend and flush tests without PostgreSQL or native mounted
storage:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~WorkspaceVolumeFlushTests|FullyQualifiedName~RuntimeWorkspaceSuspendHttpClientTests|FullyQualifiedName~RuntimeSuspendPlacementReleaseTests|FullyQualifiedName~EnvironmentWorkspaceVolumeApiTests" `
  --no-restore --verbosity quiet
dotnet test tests\Agentweaver.Providers.Storage.AzureFiles.Tests\Agentweaver.Providers.Storage.AzureFiles.Tests.csproj `
  --no-restore --verbosity quiet
```

These tests check next-generation flush receipts, saved replay, uncertain
retries, authority loss, invalid evidence, authenticated routes, and
post-flush detach behavior. Controlled Kubernetes responses reject changed
PVC UIDs, access modes, capacity, and StorageClass values. The migration
consistency test compares metadata; it does not connect to PostgreSQL. The
related PostgreSQL case checks saved evidence, replay, and conflict rejection,
but compiling that case is not evidence that it ran.

The tests do not prove native cache capture, mounted flush durability,
placement retirement, or a complete deployed suspend/resume flow.

## 2. Test resume and provider behavior

Run the bounded resume and provider selections:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~RuntimeWorkspaceResumeTests|FullyQualifiedName~RuntimeResumeBootstrapTests|FullyQualifiedName~RuntimeSuspendPlacementReleaseTests|FullyQualifiedName~RuntimeWorkspaceSuspendHttpClientTests|FullyQualifiedName~EnvironmentEgressManagerTests|FullyQualifiedName~AgentSandboxProviderTests|FullyQualifiedName~SandboxBuildTestProductionWiringTests" `
  --no-restore --verbosity quiet
```

The selected cases distinguish a mount receipt from content verification.
They check saved source configuration, mount identity, cache acknowledgments,
checkpoint boundaries, journal-prefix digests, and Network generations.
They reject expired actors, denied or changed Core responses, missing
`no-store`, changed references, duplicate events, invalid order, and
oversized prefixes. Digest validation does not prove that authenticated pages
were completely read or that their material bytes were restored.

The Host configuration checks accept only TURN records with their original
runtime or visible-parent binding and a material-read acknowledgment. An
unrelated same-run session is not used as a fallback. Pending cache state
means that no new native cache is claimed until an authorized turn captures
and persists SDK files. Suspend must fail rather than fabricate cache evidence
when those files do not exist.

Controlled Kubernetes reads verify the Pod endpoint and Service ownership
before configuration. A foreign Service owner, selector, or DNS name cannot
provide the Runtime endpoint. The separate PostgreSQL cases check
current-owner-authorized historical flush reads, lifecycle rollback and
replay, durable bootstrap restart, and concurrent attempt replacement. Those
cases require their PostgreSQL execution; compilation alone does not prove
them. None of these controlled tests proves native TLS, remount contents,
Cilium datapath enforcement, or a complete deployed resume.

## 3. Test Sandbox leases and BuildTest contracts

The P1 creation-binding slice has a focused source selection:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~EnvironmentP1InvocationBindingTests|FullyQualifiedName~EnvironmentP1CreationLinkPostgresTests" `
  --configuration Release --no-restore --verbosity quiet
```

The PostgreSQL class checks immutable registration links, concurrent exact
replay, actual volume provider identity, Sandbox result and lease fences,
rollback after current-binding rejection, and rejection of retroactive
enrollment. The Environment CI runner requires a passing TRX result for each
named PostgreSQL method; missing or skipped methods fail CI even when the
assembly builds. Local compilation does not replace these executed receipts.
These cases do not apply live migrations, create cloud resources, join Host
mount observations, or rerun P1 acceptance.

Run the focused Sandbox and lease tests:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~EnvironmentSandboxLeasePostgresTests|FullyQualifiedName~AgentSandboxProviderTests|FullyQualifiedName~EnvironmentSandboxManagerTests" `
  --configuration Release --no-restore --verbosity quiet
```

The provider tests use an in-memory HTTP handler, not a Kubernetes cluster.
They check Sandbox templates and requests, deterministic duplicate
provisioning, RuntimeClass and PVC reads, attachment evidence, readiness
withholding, and foreground deletes with UID preconditions. Manager tests
check current project authority, the exact versioned placement projection,
and rejection of expired, stale, non-current, or foreign leases.

BuildTest command contracts, checkpoint verification, configuration, and
migration/model consistency have a separate selection:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~SandboxBuildTestCommandContractTests|FullyQualifiedName~SandboxBuildTestWorkspaceCaptureTests|FullyQualifiedName~EnvironmentSandboxBuildTestAcceptedCommandVerifierTests|FullyQualifiedName~SandboxBuildTestProductionWiringTests" `
  --configuration Release --no-restore --verbosity quiet
```

These tests check immutable accepted intent, bounded receipts, collector
interruption, required output, server-pinned capability settings, and
versioned seed retrieval. Controlled HTTP responses check the fixed Core
address, original bearer and tenant, `no-store`, byte lengths and digests, and
deadlines after response headers. They do not prove live TLS authority,
native provider preparation, durable provider effects, Kubernetes isolation,
or MAF recovery. The migration/model test generates metadata; it does not
connect to PostgreSQL.

Run the matching Orchestrator Core selection:

```powershell
dotnet test tests\Agentweaver.Orchestrator.Core.Tests\Agentweaver.Orchestrator.Core.Tests.csproj `
  --filter "FullyQualifiedName~MafBuildTest" `
  --configuration Release --no-restore --verbosity quiet
```

It checks recovery of versioned checkpoint metadata, current owner decision,
execution fence, selection, and terminal replay. Metadata validation does not
replace the final execution check that requires the actual seed bytes.

## 4. Test captured documents and durable BuildTest operations

Run the pure document and partial-write checks without a native Git process or
container:

```powershell
dotnet test tests\Agentweaver.SourceControl.Tests\Agentweaver.SourceControl.Tests.csproj `
  --filter "FullyQualifiedName~GitWorkspaceCaptureDocumentTests" `
  --configuration Release --no-restore --verbosity quiet
```

These checks distinguish an incomplete, unsealed write from a complete
document. They reject foreign headers, invalid lengths, corrupt packages, and
extra bytes. They do not prove private-volume persistence or a full provider
run.

Run the BuildTest owner-store tests with the disposable PostgreSQL fixture:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~EnvironmentSandboxBuildTestCommandPostgresTests" `
  --configuration Release --no-restore --verbosity quiet
```

These tests use production stores to check concurrent reservations, replay
after store restart, immutable-command conflicts, stale-binding rejection,
retirement, terminal evidence, and timestamp handling. Version 2 manager
cases require a saved acknowledgment before Pod cleanup; a retry does not
prepare, capture, export, or execute the command again. The store tests do not
execute command Pods, prove HTTP authorization, or verify live Kubernetes
isolation and Cilium enforcement.

The six PostgreSQL acknowledgment cases require a separately approved run.
Pure request tests reject empty version 2 preparation or capture when the
fields are absent or version 1, and controlled Kubernetes tests reject forged
or mismatched collector versions before an HTTP request.

## 5. Run the actual Host image check

This opt-in check builds the actual Host Dockerfile for Linux amd64 and runs
the production Host image and Git package. It requires approval for local
image construction and execution. Start from the clean, reviewed source
checkout and provide its exact commit:

```powershell
$env:AGENTWEAVER_CONTAINER_ENGINE = "podman"
$env:AGENTWEAVER_BUILDTEST_NATIVE_IMAGE_CHECK = "1"
$env:AGENTWEAVER_BUILDTEST_REPOSITORY_ROOT = (Get-Location).Path
$env:AGENTWEAVER_BUILDTEST_SOURCE_SHA = "<reviewed-buildtest-sha>"
dotnet test tests\Agentweaver.SourceControl.Tests\Agentweaver.SourceControl.Tests.csproj `
  --filter "FullyQualifiedName=Agentweaver.SourceControl.Tests.GitWorkspaceManagerTests.ActualHostImageRetainsCompletePreparedRepositoryBytesAcrossCollectorProcesses" `
  --configuration Release --no-restore --verbosity quiet
```

The test prepares a seed through `GitWorkspaceManager`, captures the
repository from the actual Host image, and verifies the baseline and declared
output files, including binary bytes. It checks repeated capture, interrupted
reference writes, foreign Pod and operation identities, and symlinks. The
collector reads the Workspace read-only and stores sealed bytes in its
separate private volume.

The test owns its uniquely named image, three volumes, containers, and
test-created `Temp` directory. It removes only those resources. It retains
the actual bytes, source and image identity, and native path under
`artifacts/buildtest-native`. Do not treat an unexecuted source case as native
or restart evidence. This check does not prove Environment acknowledgment
storage, Events admission, Kubernetes cleanup, Cilium enforcement, or
deployment.

The release suite has a separate managed-collector check:

```powershell
$env:AGENTWEAVER_CONTAINER_ENGINE = "podman"
node --test scripts\release\tests\buildtest-output-collector.test.mjs
```

Linux CI uses Docker and its cached pinned runtime image. The check runs the
collector without network access as user 1654 against a read-only,
test-owned Workspace volume. It checks file types, hashes, byte limits,
symlinks, and malformed requests. Local arm64 execution validates managed
collector behavior only; the production profile requires Linux amd64. The
check does not prove command-Pod execution, deployed PVC isolation, or Cilium
enforcement.

## 6. Run PostgreSQL lifecycle and authorization tests

The lease and lifecycle tests use disposable PostgreSQL 16 containers through
Testcontainers. For the named Windows Podman machine, set its Docker
connection before running the tests:

```powershell
$env:DOCKER_HOST = 'npipe://./pipe/podman-machine-default'
$env:TESTCONTAINERS_HOST_OVERRIDE = 'localhost'
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --no-restore `
  --filter 'FullyQualifiedName~EnvironmentLifecyclePostgresTests|FullyQualifiedName~EnvironmentSandboxLeasePostgresTests' `
  --logger 'console;verbosity=minimal'
```

For another container engine, set its matching Docker-compatible endpoint.
These tests cover per-Environment capacity, idempotent reservation, lifecycle
fencing, abandonment, release receipts, monotonic generations, immutable
provider requests, durable provider attempts, late callbacks, and
no-effect abandonment. They do not prove live Kubernetes provider behavior,
Cilium enforcement, or deployed role grants.

The Broker integration uses a Broker-issued token, real Projects membership
and role checks, immutable run selection, Environment HTTP routes, disposable
PostgreSQL state, and a fake Kubernetes HTTP API:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj `
  --filter "FullyQualifiedName~BrokerIssuedOwnerAndSeparateRunSelectionAuthorizeWorkspaceVolumeHttpEffects" `
  --configuration Release --no-restore --verbosity quiet
```

It checks authorized placement, stale-fence and role denials, late provider
callbacks, and current authority at the effect boundary. Kubernetes requests
use a fake API, so this is not a live cluster or Cilium datapath test.
Placement observations remain `Pending` without AgentHost configure evidence.
The same fixture does not execute an accepted Orchestrator checkpoint,
command Pod, or output collector.

## 7. Test Orchestrator recovery

Run the focused PostgreSQL owner-store cases:

```powershell
dotnet test tests\Agentweaver.Orchestrator.Core.Tests\Agentweaver.Orchestrator.Core.Tests.csproj `
  --filter "FullyQualifiedName~RevalidatesAfter" `
  --configuration Release --no-restore --verbosity quiet
```

These cases use real PostgreSQL lock waits and a test authority callback to
check rollback after authority changes during a wait. They do not exercise
Projects role revocation over HTTP. To run the Broker-backed recovery case:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj `
  --filter "FullyQualifiedName~BrokerIssuedRunTokenRegistersSessionsDeliversAtTurnBoundaryAndKeepsGatePending" `
  --configuration Release --no-restore --verbosity quiet
```

The Broker integration checks current role revocation, exact run registration,
and persisted recovery context. Recovered logical run state does not prove
that physical SDK effects were replayed or that an SDK process is ready.

## 8. Build the Environment service

Build the service and its referenced provider projects with:

```powershell
dotnet build services\environment\Agentweaver.Environment\Agentweaver.Environment.csproj `
  --configuration Release --no-restore --verbosity quiet
```

The build verifies compilation only. Use the test selections above for
contract and database behavior, and [Azure acceptance](./azure-acceptance)
for the separate live-environment checks.

## Expected result

The selected commands complete successfully for the exact source revision.
Their results cover only the contracts and fixtures described in each
procedure; they do not prove live Kubernetes, CSI, Cilium, mounted-data, or
deployed suspend/resume behavior.
