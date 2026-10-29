# Session-material acceptance scenario

The P1 harness records a bounded session-material scenario beside its selected
API, MCP, or UI journey. The surface result, exact-source evidence, controlled
source tests, and deployed runtime outcome remain separate evidence classes.
The scenario is part of the existing P1 result; it does not add another
acceptance program or change the native probe envelope.

## Evidence and boundaries

The scenario carries the checked deployment source SHA and tree, service
readbacks, provider-catalog receipt, and AgentHost image measurement into the
redacted JSONL result. It also records the authorized actor and tenant selectors,
the runner-owned project/run/session fence, invocation and fixture IDs, and
available selection revisions. It never retains bearer credentials.

The existing Host producer path is supported: accepted workflow-bound dispatch
reaches `RuntimeAgentHost.DispatchAsync`, which calls
`AuthorizedRuntimeSession.SendNativeTurnAsync` and commits user/assistant
turns and SDK cache through the Events owner. The public native dispatch proof
also observes the native sends and joined results.

The P1 consumer still lacks a contract that binds its accepted invocation to
that dispatch and correlates the resulting material acknowledgment/readback.
`run-p1` does not invoke Orchestrator dispatch, the native CLI stops after its
read-only authority join, and the Host readiness evidence is not a
post-turn material receipt. Events material reads use current Projects
authorization, a provider-binding pin, and an authority recheck; TurnContent
also requires the bound-project read entitlement, while SDK cache uses the
stronger `NativeUsage.RequireReadAuthority` rule. These read paths are not
exposed by the current P1 provider. Events writes separately validate the
caller bearer against current runtime registration, source, and fence.
Therefore the P1 flow cannot yet prove that its own accepted turn produced the
observed acknowledgment, committed position, and cursor. This is a missing
P1 accepted-invocation-to-authorized-readback consumer contract, not a missing
production material producer.

Compatible replacement/recovery, missing or expired material, and
revocation/stale/retry fault hooks are separate unsupported acceptance
boundaries; they remain blocked rather than inferred from the producer path or
controlled tests. Existing journal-owned material remains under its current
retention policy. The P1 fixture's own cleanup is recorded separately; this
scenario does not require or imply a new material-deletion endpoint.

These boundaries leave the deployed session-material scenario **blocked**.
The result records the intended typed turn route as not sent, its result as not
observed, and event ID, committed position, cursor, and runtime registration
revision as unavailable. It does not infer successful execution from a session
identifier, source receipt, or controlled fixture. The full P1 result cannot be
success-shaped while this required scenario is blocked; the selected surface
status remains independent.

## Required scenario when the authorized boundary exists

An authorized implementation must use the current owner routes and native
runtime path, bound to the same exact source, image, actor, tenant, project,
run, session, invocation, and current execution fence:

1. Submit one typed turn and retain redacted request and response evidence,
   the current runtime instance and registration revision, the owner
   acknowledgment, and the committed event position/cursor.
2. Replace the runtime through the supported owner lifecycle and prove that a
   compatible native cache restores the recorded session without another
   model turn.
3. Independently exercise absent and expired optional SDK cache. Both may
   rebuild from retained turns. Verify that rebuilding invokes no model turn
   and repeats no tool or external effect.
4. Separately prove that absent or expired required turn material fails the
   turn; it is not an optional cache miss.
5. Exercise current-authority revocation, stale fence/registration, exact
   duplicate retry and changed-payload retry, and the required-material
   failure through supported owner boundaries. If an authorized fault or
   restart boundary is unavailable, preserve the exact gap as blocked.
6. Record cleanup of the invocation-owned P1 fixture through its existing
   owner path. Keep its result visible; retained journal material follows the
   existing retention policy and is not deleted by this scenario.

## Controlled evidence

The scenario lists the precise existing controlled test cases for the owner
turn/acknowledgment, replacement restore, cache rebuild, retry, and required
material boundaries. These references are a coverage map, not test receipts:
`controlledEvidence.status` stays `not-executed` in the P1 result because this
runner does not execute or consume .NET test results.

Run the controlled cases from the repository root:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --configuration Release --filter FullyQualifiedName~AzureByokNativeAccountingLostReplyReplaysWithFreshTotalsAndRejectsStaleProof
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-restore --configuration Release --filter "FullyQualifiedName~ActualSdkFilesystemCallbacksCaptureOpaqueFilesAndResumeTheSameLogicalSession|FullyQualifiedName~LostOrIncompatibleCacheRebuildsNativeContextWithoutReplayingATurn"
dotnet test tests\Agentweaver.EventsAndSessions.Tests\Agentweaver.EventsAndSessions.Tests.csproj --configuration Release --filter FullyQualifiedName~SessionMaterialEndpointRetentionTests
dotnet test tests\Agentweaver.EventsAndSessions.Tests\Agentweaver.EventsAndSessions.Tests.csproj --configuration Release --filter "FullyQualifiedName~ExactMaterialRetriesAndRestartReturnOriginalRecordedAcknowledgment|FullyQualifiedName~ExpiredRequiredTurnsFailAndOnlyExpiredOptionalCacheMapsToNotFound"
```

The Broker integration case and Events/Postgres cases use disposable
Testcontainers PostgreSQL and fail when a Docker-compatible engine is
unavailable. The two `RuntimeSessionRecoveryTests` cases and the endpoint
retention tests are controlled local tests without that database fixture.
None is deployed acceptance. Source tests do not attest a live actor,
deployment, image, owner receipt, repeated external-effect behavior, or
cleanup result, and their presence never changes the P1 session-material
status.

The JSONL scenario records non-attempted operations explicitly, distinguishes
optional-cache recovery from required-turn failure, and preserves the precise
authorized-boundary blockers. It performs no live operation and creates no
additional fixture, identity, or cleanup work.
