# Environment Sandbox testing

Run the suspend flush source checks without PostgreSQL or native mounted storage:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~WorkspaceVolumeFlushTests|FullyQualifiedName~RuntimeWorkspaceSuspendHttpClientTests|FullyQualifiedName~RuntimeSuspendPlacementReleaseTests|FullyQualifiedName~EnvironmentWorkspaceVolumeApiTests" `
  --no-restore --verbosity quiet
dotnet test tests\Agentweaver.Providers.Storage.AzureFiles.Tests\Agentweaver.Providers.Storage.AzureFiles.Tests.csproj `
  --no-restore --verbosity quiet
```

These cases check exact next-generation receipts, saved replay, uncertain retry, authority loss, invalid evidence, and authenticated routes.
The shared client rejects caller-supplied Workspace evidence, changed owner responses, missing no-store, and incomplete flush results.
Release cases check exact post-flush detach and replay without changing the original attachment.
They reject stale placement, provider fences, changed counters, missing evidence, and incomplete release responses.
The Core authority client rejects denied or changed responses and missing, expired, or different actor credentials.
Fake Kubernetes checks reject changed PVC UIDs, access modes, capacity, and StorageClass.
The migration consistency test compares metadata only; it does not connect to PostgreSQL.
The corresponding PostgreSQL case checks saved evidence, replay, and conflict rejection.
Workspace detach and Sandbox lease cases reject authority lost immediately before commit without advancing their saved state.
Compilation alone does not prove that case ran.
These checks do not prove native cache capture, mounted flush durability, placement retirement, or a complete suspend/resume flow.

Run the bounded resume and affected pure source checks:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~RuntimeWorkspaceResumeTests|FullyQualifiedName~RuntimeResumeBootstrapTests|FullyQualifiedName~RuntimeSuspendPlacementReleaseTests|FullyQualifiedName~RuntimeWorkspaceSuspendHttpClientTests|FullyQualifiedName~EnvironmentEgressManagerTests|FullyQualifiedName~AgentSandboxProviderTests|FullyQualifiedName~SandboxBuildTestProductionWiringTests" `
  --no-restore --verbosity quiet
```

These cases distinguish a new mount receipt from actual content verification.
They reject changed source manifests, data, cache acknowledgments, target mounts, tree hashes, and Network generations.
The original actor and tenant must reach both stages and the native verifier.
Expired actors, denied or changed Core responses, and missing no-store responses are rejected.
Bootstrap cases check exact acknowledged configuration bytes, attempt IDs, and the revoked prior registration.
Resume preparation rejects missing or changed configuration acknowledgments before any owner request or Environment effect.
These resume-only checks do not require configuration acknowledgments in ordinary suspend receipts.
They reject a changed delivery, attempt, registration, or Host loss proof.
Journal-rebuild contract cases keep the old cache acknowledgment and current Host facts separate.
They check exact checkpoint, flushed position, digest format, attempt, and the explicit pending-cache state.
Native-cache receipts reject a journal-rebuild proof; unknown recovery modes and cache states are denied.
The prefix digest accepts legitimate gaps from interleaved sessions and authenticated fork history.
It rejects changed source configuration or cache references, missing required events, duplicate IDs, invalid ordering, and oversized prefixes.
Digest validation alone does not prove authenticated page completion or material bytes.
Host Configure tests accept a TURN with its original prior-runtime binding and a TURN from a visible parent session.
They verify each TURN against its own event payload, single stored object reference, and material read acknowledgment.
An unrelated same-run session is absent from the source-session replay and is not read as a fallback.
Host producer tests must separately reject page, cursor, owner, transport, mismatched-reference, and byte-hash failures without using later journal records.
Pending cache means no new native cache is claimed until a real authorized turn captures and persists genuine SDK files.
Suspend before such files exist must fail explicitly, not manufacture cache evidence.
Missing owner composition cannot reach Broker delivery.
The exact source-manager case reaches its first lifecycle-effect boundary; changed selection, source lease, flush, Host receipt, or volume fails before it.
That boundary test does not prove successful placement or the complete bootstrap producer.
The separate producer case runs the actual Environment bootstrap, placement, Sandbox, and Network managers with controlled owner, provider, Events, and Host responses.
It checks saved configuration delivery, full activation, exact replay, Host state loss, changed live receipts, and actor expiry after the probe.
Placement cases revoke resume authority after reservation, before saving the provider request, before attachment, before provisioning, and after provisioning.
Before provisioning, the owner completes the no-effect lease and detaches any attempted attachment without changing retained Storage or data.
After provisioning, the owner retains the exact returned resource for recovery rather than claiming that no effect occurred.
Those controlled responses do not prove native PostgreSQL, SDK, or Kubernetes execution.
Migration cases check the new owner-effect constraints and unchanged table shape without executing SQL.
Controlled Kubernetes reads check the actual Pod address and owned Service before configuration.
Foreign Service owners, selectors, and DNS names cannot provide a Runtime endpoint.
The PostgreSQL cases separately check current-owner-authorized historical flush reads and lifecycle rollback or replay denial.
Those cases compile but require their separately approved execution.
Durable bootstrap restart, concurrent attempt replacement, and old-grant refusal require separate native PostgreSQL checks.
Full Host restart recovery also requires actual journal-based cache reconstruction and exact-version deployed acceptance.
None of these pure results proves native TLS, real remount contents, Cilium traffic enforcement, or a complete deployed resume.

Run the focused Sandbox source tests from the repository root:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~EnvironmentSandboxLeasePostgresTests|FullyQualifiedName~AgentSandboxProviderTests|FullyQualifiedName~EnvironmentSandboxManagerTests" `
  --configuration Release --no-restore --verbosity quiet
```

BuildTest contracts, checkpoint verification, configuration, and migration/model consistency have a separate non-container selection:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~SandboxBuildTestCommandContractTests|FullyQualifiedName~SandboxBuildTestWorkspaceCaptureTests|FullyQualifiedName~EnvironmentSandboxBuildTestAcceptedCommandVerifierTests|FullyQualifiedName~SandboxBuildTestProductionWiringTests" `
  --configuration Release --no-restore --verbosity quiet
```

These tests cover immutable accepted intent, bounded receipts, collector interruption, missing required output, and server-pinned capability configuration.
The migration/model test generates metadata only; it does not connect to PostgreSQL.
These results do not prove durable provider effects, Kubernetes isolation, Cilium datapath enforcement, or MAF recovery.

The accepted-command verifier uses a controlled HTTP handler to test version-2 seed retrieval.
It checks the fixed Core address, full checkpoint reference, original bearer and tenant, and `no-store` requests.
Cases reject denied or missing seeds, redirects, incorrect lengths or digests, and short or extra bytes.
The body-stall case checks the client deadline after response headers.
Changed owner metadata after retrieval prevents an executable command from returning.
Current-command checks use metadata only and reject changed operation IDs, selections, or execution fences.
The manager repeats these metadata checks without another seed transfer before provider effects.
The stored-state test reads and updates version-2 JSON without seed bytes.
Reconciliation restores the exact verified seed only after the stored command metadata matches.
These cases do not prove live TLS authority, native provider preparation, or a complete BuildTest run.

Version 2 capture tests require the exact preparation and full output reference even without declared output files.
They reject changed native paths, Pod UIDs, source bindings, document sections, and non-positive journal positions.
Only the exact verified acknowledgment can extend a completed Environment operation.
Operation JSON retains references, not seed or file bytes.
Controlled HTTP checks require Core's canonical acknowledgment and current command metadata before and after verification.
They reject a different journal position, denied authority, and owner changes.
These tests do not execute native Pods or prove cleanup on Kubernetes.

Core's matching non-container selection is:

```powershell
dotnet test tests\Agentweaver.Orchestrator.Core.Tests\Agentweaver.Orchestrator.Core.Tests.csproj `
  --filter "FullyQualifiedName~MafBuildTest" `
  --configuration Release --no-restore --verbosity quiet
```

It checks recovered version-2 metadata without binary input in JSON, and current owner decision, fence, and selection rejection.
Version-2 terminal checkpoints recover and replay success, failure, and stale results without seed bytes in JSON.
Metadata validation does not replace the final execution check that requires the actual seed bytes.

Full-output source checks preserve baseline files and binary output bytes in the canonical document.
They reject baseline deletion, undeclared changes, additional files, altered bytes, and section or seed mismatches.
Pending bytes reconstruct the same complete document; an unadmitted row cannot supply a journal acknowledgment.
Core cannot persist a successful version 2 receipt before the exact acknowledgment is present.
Controlled binary transfers check the fixed Environment route, exact length and digest, media type, and no-store.
Lost acknowledgment responses retry the same operation without command execution or native export.

Run the pure document and partial-write checks without a native Git process or container:

```powershell
dotnet test tests\Agentweaver.SourceControl.Tests\Agentweaver.SourceControl.Tests.csproj `
  --filter "FullyQualifiedName~GitWorkspaceCaptureDocumentTests" `
  --configuration Release --no-restore --verbosity quiet
```

These checks distinguish a truncated unsealed write from a complete document.
Foreign headers, invalid lengths, corrupt complete packages, and extra bytes do not become recoverable partial state.
The checks do not prove native private-volume persistence or a full provider run.

Run the BuildTest owner-store tests with the existing disposable PostgreSQL fixture:

```powershell
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj `
  --filter "FullyQualifiedName~EnvironmentSandboxBuildTestCommandPostgresTests" `
  --configuration Release --no-restore --verbosity quiet
```

These tests create the owner, attached Workspace, and active Sandbox through their production stores.
They cover concurrent reservation, replay from a new store and connection pool, immutable-command conflicts, and stale-binding rejection without an effect row.
They also cover retirement, durable policy-attempt markers, PostgreSQL timestamp precision, and terminal-result preservation.
Replay preserves the original result; a later save cannot replace terminal evidence or return a terminal operation to Running.
These store tests do not execute command Pods, prove HTTP authorization, or verify live Kubernetes isolation and Cilium enforcement.

The version 2 manager cases use the same PostgreSQL stores with controlled provider and Core acknowledgment responses.
They require saved acknowledgment before either Pod delete.
They repeat the exact cleanup after a failed delete and store restart.
Denied acknowledgment or revoked authority prevents cleanup.
Authority revoked after collector cleanup prevents command-Pod cleanup.
Retry does not prepare, capture, export, or execute the command again.
These added cases are source-only until their approved PostgreSQL run completes; they do not prove native Pod cleanup or Events admission.

The provider cases compare version 1 and version 2 through the existing controlled Kubernetes HTTP handler.
Version 2 requires the pinned preparation container, exact prepared command path, separate read-only collector, and private capture volume.
The collector has no command deadline after capture, so its sealed bytes remain available before acknowledgment.
Changed private mounts, command paths, Pod UIDs, or saved preparation receipts fail before native effects.
The existing TLS exchange cases also check the exact helper command on the Kubernetes `exec` route.
These added cases are source-only until their approved run completes.

Pure request tests cover empty version 2 preparation and capture, legacy JSON, and unchanged receipt versions.
Absent or version 1 fields cannot admit empty output lists.
Default lists and unknown versions remain invalid.
Controlled Kubernetes tests cover empty version 2 collector creation and restoration.
Forged or mismatched collector versions fail before any HTTP request.
The six PostgreSQL acknowledgment cases still require a separately approved run.

## Controlled actual Host image check

This separate check requires approval for local image construction and execution.
Run it from the clean, reviewed source checkout, with its exact commit:

```powershell
$env:AGENTWEAVER_CONTAINER_ENGINE = "podman"
$env:AGENTWEAVER_BUILDTEST_NATIVE_IMAGE_CHECK = "1"
$env:AGENTWEAVER_BUILDTEST_REPOSITORY_ROOT = (Get-Location).Path
$env:AGENTWEAVER_BUILDTEST_SOURCE_SHA = "<reviewed-buildtest-sha>"
dotnet test tests\Agentweaver.SourceControl.Tests\Agentweaver.SourceControl.Tests.csproj `
  --filter "FullyQualifiedName=Agentweaver.SourceControl.Tests.GitWorkspaceManagerTests.ActualHostImageRetainsCompletePreparedRepositoryBytesAcrossCollectorProcesses" `
  --configuration Release --no-restore --verbosity quiet
```

The check builds the actual Host Dockerfile as Linux amd64.
It uses that production image and its Git package, not a second Git image or a host Git installation step.
The existing Source Control fixture creates the accepted seed through `GitWorkspaceManager`.
The actual Host collector prepares the seed in its exact native repository path.
The command runs there as user 1000, matching the provider Pod security context.
The separate collector reads the Workspace volume read-only and stores sealed bytes in its own private volume.
The canonical parser checks the complete baseline and both declared output files, including binary bytes.
New collector processes must return the same reference and document after the native output changes.
The check also recovers an interrupted reference write and rejects foreign Pod and operation identities.
Repeated native preparation must not reset command output.
A new capture rejects a symlink in the actual prepared repository.

The check owns one uniquely named image, three volumes, and its exact named containers.
It removes only those resources and its test-created `Temp` directory.
Docker ignores `Temp`, so local seed fixtures do not enter the image build context.
Actual bytes, source and image identity, and the native path are retained under `artifacts/buildtest-native`.
The check remains disabled unless explicitly selected.
Unexecuted source cases are not native or restart evidence.
This check does not prove Environment acknowledgment storage, Events admission, Kubernetes cleanup, Cilium enforcement, or deployment.

The release tests also run the managed collector in a cached Linux ASP.NET runtime:

```powershell
$env:AGENTWEAVER_CONTAINER_ENGINE = "podman"
node --test scripts\release\tests\buildtest-output-collector.test.mjs
```

Linux CI uses Docker and its already cached, pinned runtime image.
The test mounts the managed application and a test-owned Workspace volume read-only.
The collector runs without network access as user 1654, before web configuration or native SDK startup.
It checks regular and empty files, exact hashes, absent files, symlinks, non-regular files, byte limits, and malformed requests.
The collector opens native file descriptors synchronously and retains bounded cancellable reads.
Its directory and no-follow flags match the executing Linux architecture.
Local arm64 execution proves managed collector behavior only; the production profile still requires a Linux amd64 image.
These checks do not prove command-Pod execution, deployed PVC isolation, or Cilium enforcement.

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

The same integration checks BuildTest binding preparation from the current selected profile.
The BuildTest route rejects viewers, foreign owners, missing selection authority,
and a different execution profile. An unknown operation returns 404.
An unresolved command returns 503 without a BuildTest row or provider creation.
These checks use the production manager and stores, but do not execute an accepted
Orchestrator checkpoint, a command Pod, or the output collector.

The separate native SDK harness reuses the canonical placement manager.
Its profile callback reads actual Orchestrator work-item context under the retained lease.
The module tests retire a lease before lifecycle advancement, as the admitted lifecycle guard requires.
The readiness module test retains the actual PostgreSQL advisory lock during Cilium and Sandbox observations.
It observes a competing retirement through `pg_locks` and releases it only after readiness completes.
It persists the versioned recovery record through the lease store, then reads it from a new store.
It checks the nested provision request's exact network generation and valid Workspace mount path.

The Workspace metadata module test creates the actual attached volume and active Sandbox through their production stores.
It runs the production placement reader against a controlled Core context HTTP response.
It checks the exact mount, Storage resource, volume generation, data generation zero, transition revision, and registered profile.
Changed volume, data, Storage resource, and attachment state all fail.
The test observes a competing detach through `pg_locks` while the combined owner read remains active.
The response contains no provider descriptor, tree hash, or content checkpoint.
These checks do not prove Storage flush, a completed suspend, or deployment.
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
