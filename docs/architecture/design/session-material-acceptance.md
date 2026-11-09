# Specification: Integrated session-material acceptance

**Status:** The native-material-recovery source journey is implemented.
No deployed session-material acceptance evidence is claimed here.

## Problem and goals

Acceptance needs to join its own authorized invocation to the material actually
committed and restored by the owner. A session identifier or healthy service alone
cannot establish that relationship.

## Non-goals

This scenario adds no product MCP surface, acceptance program, material-deletion
endpoint, live fault privilege, or permission to run against a deployment.

The integrated acceptance harness records a bounded session-material scenario beside its selected
native CLI/API or browser journey. Retained MCP adapters are compatibility
tooling, not a supported acceptance surface. The surface result, exact-source evidence, controlled
source tests, and deployed runtime outcome remain separate evidence classes.
The scenario is part of the existing integrated result; it does not add another
acceptance program or change the native probe envelope.

## Contracts and boundaries

### Evidence and boundaries

The scenario carries the checked deployment source SHA and tree, service
readbacks, provider-catalog receipt, and AgentHost image measurement into the
redacted JSONL result. It also records the authorized actor and tenant selectors,
the runner-owned project/run/session fence, invocation and fixture IDs, and
available selection revisions. It never retains bearer credentials.

The optional `native-material-recovery` CLI journey now joins the accepted
invocation to real production evidence. It submits one approved, read-only
casting request through `formulateCastingProposal`, validates the native
completion and turn/cache acknowledgments, and reads the user and assistant
turns through `readSessionTurnContent`. The runner then uses
`replaceRuntimeHost` and `readRuntimeReplacement` to verify ordinary Host
replacement, cache restoration, and an exact request retry on the new
registration.

The completion receipt binds the request to its source, image, actor, tenant,
project, run, root session, execution fence, runtime registration, accepted
selection, and approved BYOK model. The runner checks that the public turn
readbacks match their material acknowledgments. It checks that replacement
uses the saved cache material, retains the workspace and logical execution
binding, and has a complete reconstruction interval with no new event, model
send, or tool effect. The retry must return the original proposal and
completion with current-registration replay evidence and no new native send.
The existing fixture cleanup must also pass.

This source path does not add a capture-only route, a permanent run latch, a
generic proof framework, new roles or grants, or a new material-deletion
endpoint. It does not weaken the hosted hard-credit guard. Existing Events
reads and writes still enforce their current authorization, registration,
source, and fence checks. Journal-owned material remains under its current
retention policy.

The native journey does not test authority revocation, stale-fence or stale-
registration denial, changed-payload retry, absent or expired optional cache,
or missing or expired required turn material. Those checks remain unexecuted;
the result must not report them as passed. A separate denied-actor check also
remains a full-P1 obligation. Archive and operating-system acceptance remain
separate work under #2449. A passed native-material row is not a passed full
P1 result and is not evidence that a deployed run has occurred.

## Intended behavior and requirements

### Native-material-recovery journey

The journey requires the exact source and runtime proof inputs used by the
native CLI acceptance runner. Its `fixturePlan.nativeMaterialRecovery` approval
must name a BYOK model selection, reasoning effort, positive prompt capacity,
exactly one native request, and explicit approval for ordinary Host
replacement. It uses the setup-owned invocation and current owner bindings;
the client cannot supply owner fences or receipts.

For the selected journey, the runner:

1. Submits one production read-only casting request and validates the actual
   native completion, source and image pins, runtime registration, model
   approval, and ordered user, assistant, and SDK-cache acknowledgments.
2. Reads the user and assistant turn material through the public Gateway
   operation and compares each response with its acknowledgment.
3. Replaces the Host through the ordinary owner lifecycle. It checks the old
   and new Pod and runtime identities, retained workspace generations, the
   current accepted selection and execution fence, the restored cache
   acknowledgment, and the complete no-event reconstruction interval.
4. Retries the exact original casting request. It checks the original
   proposal and completion against replacement lineage and current
   registration, with no new native send or tool effect.
5. Cancels and independently verifies the owned fixture through its existing
   cleanup path. Cleanup failure keeps the session-material row blocked.

The result contains redacted identifiers, counts, positions, and hashes. It
does not retain the turn text or bearer credentials. Only the native
session-material row can pass after its receipts and cleanup validate. Other
journeys retain the material blocker. Full-P1 status remains separate.

## Acceptance and validation

### Controlled evidence

The controlled tests cover source behavior only. They are not deployed
receipts, and the native runner does not consume their results.

Run the controlled cases from the repository root:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --configuration Release --filter FullyQualifiedName~AzureByokNativeAccountingLostReplyReplaysWithFreshTotalsAndRejectsStaleProof
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-restore --configuration Release --filter "FullyQualifiedName~ActualSdkFilesystemCallbacksCaptureOpaqueFilesAndResumeTheSameLogicalSession|FullyQualifiedName~LostOrIncompatibleCacheRebuildsNativeContextWithoutReplayingATurn"
dotnet test tests\Agentweaver.EventsAndSessions.Tests\Agentweaver.EventsAndSessions.Tests.csproj --configuration Release --filter FullyQualifiedName~SessionMaterialEndpointRetentionTests
dotnet test tests\Agentweaver.EventsAndSessions.Tests\Agentweaver.EventsAndSessions.Tests.csproj --configuration Release --filter "FullyQualifiedName~ExactMaterialRetriesAndRestartReturnOriginalRecordedAcknowledgment|FullyQualifiedName~ExpiredRequiredTurnsFailAndOnlyExpiredOptionalCacheMapsToNotFound"
dotnet test tests\Agentweaver.Cli.Acceptance.Tests\Agentweaver.Cli.Acceptance.Tests.csproj --configuration Release --filter FullyQualifiedName~ApiAcceptanceNativeMaterialTests
```

The Broker integration case and Events/Postgres cases use disposable
Testcontainers PostgreSQL and fail when a Docker-compatible engine is
unavailable. The two `RuntimeSessionRecoveryTests` cases and the endpoint
retention tests are controlled local tests without that database fixture.
None is deployed acceptance. The native-material tests use controlled inputs;
they check journey receipt validation, replay, and cleanup precedence. Source
tests do not attest a live actor, deployment, image, owner receipt, repeated
external-effect behavior, or cleanup result. They do not change the result of a
live journey.

The native journey is not run by dry-run. A live run needs the exact apphost
and manifest, approved non-production target and actor, valid source and
provider receipts, the approved BYOK model and cost, and approval to replace
the runner-owned Host. This source change did not run live acceptance.

## Failure and recovery

Keep a missing, stale, or mismatched completion, material, replacement, replay,
or cleanup receipt blocked. Do not send an unbound turn, treat a required-turn
failure as optional cache absence, or infer cleanup from fixture creation.
Keep the separate fault, denied-actor, and #2449 obligations visible; do not
clear them because this journey passed.

## Rollout and compatibility

Use the existing result and native probe envelope. The optional native journey
does not become a prerequisite for ordinary CLI use or all-OS archive
verification. Keep its deployed receipts, source-test results, full-P1 status,
and #2449 archive/OS work independently visible. This specification does not
authorize a live run.
