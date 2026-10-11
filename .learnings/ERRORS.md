# Errors

## [ERR-20261008-001] MAF advisory-lock scope

**Logged**: 2026-10-08T10:32:00-07:00
**Priority**: medium
**Status**: resolved
**Area**: backend

### Summary
The MAF checkpoint PostgreSQL advisory-lock key contained a NUL byte.

### Error
```text
22021: invalid byte sequence for encoding "UTF8": 0x00
```

### Context
- The focused `MafCheckpointStorePostgresTests` used project, run, and session IDs joined with `\0` as a PostgreSQL text parameter.
- PostgreSQL text values cannot contain a zero byte.
- After replacing the NUL separator with a JSON-serialized scope tuple, integration assertions exposed that `CheckpointInfo` and `ImmutableDictionary` require field/entry comparison rather than reference equality.

### Suggested Fix
Use an unambiguous JSON-serialized tuple as the text advisory-lock key and compare restored checkpoint identity/progress fields structurally; rerun the focused PostgreSQL tests.

### Resolution
- **Resolved**: 2026-10-08T10:35:00-07:00
- **Notes**: Advisory locking now uses a JSON-serialized project/run/session tuple. Checkpoint replay compares identity and progress fields; focused PostgreSQL and planner tests pass.

### Metadata
- Reproducible: yes
- Related Files: services/orchestrator/Agentweaver.Orchestrator/MafExecutionCheckpointStore.cs

---

## [ERR-20261008-016] MAF checkpoint type patch target

**Logged**: 2026-10-08T14:27:00-07:00
**Priority**: low
**Status**: resolved
**Area**: backend

### Summary
A patch targeted the MAF checkpoint record in the planner file instead of the checkpoint-store file.

### Error
```text
Failed to apply patch: expected MafExecutionCheckpoint lines were not found in MafExecutionPlanner.cs.
```

### Context
- `MafExecutionCheckpoint` is declared in `MafExecutionCheckpointStore.cs`; `MafExecutionPlanner.cs` contains progress and planner records.
- The patch was rejected atomically and made no source changes.

### Suggested Fix
Patch each declaration in the file where it is defined, using the current exact context.

### Resolution
- **Resolved**: 2026-10-08T14:27:00-07:00
- **Notes**: Corrected the checkpoint declaration target before making the implementation edit.

### Metadata
- Reproducible: yes
- Related Files: services/orchestrator/Agentweaver.Orchestrator/MafExecutionCheckpointStore.cs

---

## [ERR-20261008-017] MAF Host guard test compile

**Logged**: 2026-10-08T14:50:00-07:00
**Priority**: low
**Status**: pending
**Area**: tests

### Summary
The focused MAF test build failed after adding premature-response and invalid-phase coverage.

### Error
```text
CS0103: The name 'prematureClient' does not exist in the current context
CS0200: SandboxStartupPhaseObservation.Phase cannot be assigned to
```

### Context
- The premature-response test handler was inserted into a neighboring test instead of the test that used it.
- `SandboxStartupPhaseObservation` validates its enum in its constructor and exposes a get-only phase property.

### Suggested Fix
Place the HTTP handler in the intended test and exercise the existing DTO enum guard by deserializing a malformed readiness receipt instead of using a record `with` assignment.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Orchestrator.Core.Tests/MafExecutionDispatchTests.cs

---

## [ERR-20261008-003] MAF test discard shadowing

**Logged**: 2026-10-08T10:38:00-07:00
**Priority**: low
**Status**: pending
**Area**: tests

### Summary
The MAF PostgreSQL test callback used `_` as a named parameter, so `_ = await ...` attempted to assign a checkpoint snapshot to that parameter.

### Error
```text
CS0029: Cannot implicitly convert type 'MafExecutionCheckpointSnapshot' to 'SpawnedSession'
```

### Context
- The focused test build failed in `CoordinationOwnerStorePostgresTests.cs` while testing the new spawn/checkpoint transaction callback.
- In this scope `_` was an ordinary parameter identifier, not a discard.

### Suggested Fix
Give the callback parameter a descriptive name and rerun the focused tests.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Orchestrator.Core.Tests/CoordinationOwnerStorePostgresTests.cs

---

## [ERR-20261008-004] MAF spawn/checkpoint integration test

**Logged**: 2026-10-08T10:40:00-07:00
**Priority**: medium
**Status**: resolved
**Area**: tests

### Summary
The focused MAF test run reached PostgreSQL but the new spawn/checkpoint join integration test failed.

### Error
```text
System.InvalidOperationException: The accepted run selection has no valid pinned model reference.
```

### Context
- The new PG transaction test reused a coordination selection fixture whose snapshot contains only `source`; the checkpoint store correctly requires `modelSelection.reference`.
- The focused rerun isolated the failure to the fixture at `CoordinationOwnerStorePostgresTests.CreateSelection`.

### Suggested Fix
Give the shared test selection fixture a valid pinned model reference, then rerun the focused MAF test set.

### Resolution
- **Resolved**: 2026-10-08T10:40:00-07:00
- **Notes**: Added a valid `modelSelection.reference` to the coordination selection fixture; the focused MAF suite passed 13/13.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Orchestrator.Core.Tests/CoordinationOwnerStorePostgresTests.cs

---

## [ERR-20261008-005] preflight patch context

**Logged**: 2026-10-08T11:25:00-07:00
**Priority**: low
**Status**: resolved
**Area**: backend

### Summary
A combined patch for the Events pricing preflight did not match the current consumer source.

### Error
```text
Failed to apply patch: Error: Failed to find expected lines in NativeUsageReceiptConsumer.cs
```

### Context
- The patch assumed a different local `Price`/scope helper context than the file contains.
- The patch was rejected atomically, so it made no source changes.

### Suggested Fix
Patch the existing consumer in smaller hunks using the current exact method bodies.

### Resolution
- **Resolved**: 2026-10-08T11:25:00-07:00
- **Notes**: Read the current file and will use exact local contexts for incremental edits.

### Metadata
- Reproducible: yes
- Related Files: services/events-and-sessions/Agentweaver.EventsAndSessions/Usage/NativeUsageReceiptConsumer.cs

---

## [ERR-20261008-006] Events test assets missing

**Logged**: 2026-10-08T11:28:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The focused Events test command could not start because the test project's NuGet assets file was absent.

### Error
```text
NETSDK1004: Assets file '...\\tests\\Agentweaver.EventsAndSessions.Tests\\obj\\project.assets.json' not found. Run a NuGet package restore to generate this file.
```

### Context
- `dotnet test ... --configuration Release --filter FullyQualifiedName~NativeCostPreflight --no-restore` was run after changing only source and test files.
- The worktree had no restored assets for `Agentweaver.EventsAndSessions.Tests`.

### Suggested Fix
Rerun the focused test with restore enabled.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.EventsAndSessions.Tests/Agentweaver.EventsAndSessions.Tests.csproj

### Resolution
- **Resolved**: 2026-10-08T11:29:00-07:00
- **Notes**: Ran the focused Events preflight tests with restore enabled; all 4 passed.

---

## [ERR-20261008-002] MAF child snapshot namespace

**Logged**: 2026-10-08T10:36:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The orchestrator build could not resolve `SessionIdentity` in the new MAF child snapshot record.

### Error
```text
CS0246: The type or namespace name 'SessionIdentity' could not be found
```

### Context
- `MafExecutionPlanner.cs` declares `MafExecutionChildSnapshot` and needed the `Agentweaver.Abstractions` namespace.
- The initial orchestrator Release build stopped at this compile error.

### Suggested Fix
Import the defining namespace and rerun the build.

### Resolution
- **Resolved**: 2026-10-08T10:36:00-07:00
- **Notes**: Added the missing `Agentweaver.Abstractions` using; the orchestrator Release build then passed with 0 warnings/errors.

### Metadata
- Reproducible: yes
- Related Files: services/orchestrator/Agentweaver.Orchestrator/MafExecutionPlanner.cs

---

## [ERR-20261008-007] workspace changes overview base ref

**Logged**: 2026-10-08T11:11:00-07:00
**Priority**: low
**Status**: resolved
**Area**: config

### Summary
The workspace changes overview could not compute the merge base for the current worktree.

### Error
```text
git merge-base a540ba511c9b75c80a4d081f385365bc252d6e1c 61820d03caf6d5b412178795a1139538afe39eaa failed
```

### Context
- `get_changes_overview` failed on the local host before returning changed-file details.
- The checkpoint identifies the intended admitted-v1 baseline as `61820d03caf6d5b412178795a1139538afe39eaa`.

### Suggested Fix
Use local `git status` and targeted diffs rather than retrying the overview when its configured merge-base cannot be resolved.

### Resolution
- **Resolved**: 2026-10-08T11:22:00-07:00
- **Notes**: Used `git status` and targeted diffs; did not retry the overview with its unresolved merge base.
- **Recurrence**: 2026-10-08T13:32:00-07:00 — `get_changes_overview` again failed because its configured `a540ba511c9b75c80a4d081f385365bc252d6e1c` merge base was unavailable against the current branch head. Continue with targeted local Git commands; do not retry the overview.
- **Recurrence**: 2026-10-08T18:51:00-07:00 — `get_changes_overview` failed against candidate base `f3cb9f3b25a2148520116731b1426d0742e442be`; continue using local `git status`/targeted diffs without retrying the overview.

### Metadata
- Reproducible: unknown
- Related Files: none

---

## [ERR-20261008-008] cost preflight patch context

**Logged**: 2026-10-08T11:18:00-07:00
**Priority**: low
**Status**: resolved
**Area**: backend

### Summary
A cost preflight patch did not match the current single-line `VerifyPinned` call.

### Error
```text
Failed to apply patch: Failed to find expected lines in NativeUsageReceiptConsumer.cs
```

### Context
- The patch split the `VerifyPinned` call across lines, but the current source keeps it on one line.
- The failed patch was rejected atomically and made no source changes.

### Suggested Fix
Use the exact current method context and apply the signature and call-site changes in smaller hunks.

### Resolution
- **Resolved**: 2026-10-08T11:22:00-07:00
- **Notes**: Applied the preflight changes against exact method context; focused Events and runtime-host tests passed.

### Metadata
- Reproducible: yes
- Related Files: services/events-and-sessions/Agentweaver.EventsAndSessions/Usage/NativeUsageReceiptConsumer.cs
- See Also: ERR-20261008-005

---

## [ERR-20261008-009] preflight method placement

**Logged**: 2026-10-08T11:20:00-07:00
**Priority**: medium
**Status**: resolved
**Area**: backend

### Summary
The Events project failed to compile because the preflight method was inserted before the receipt method's `finally` block.

### Error
```text
CS1524: Expected catch or finally
```

### Context
- The focused `NativeCostPreflight` test command caught a malformed method boundary in `NativeUsageEndpoints.cs`.
- No test executed because compilation failed.

### Suggested Fix
Keep `AppendAsync`'s `finally` adjacent to its `try`, then declare preflight as a separate class method.

### Resolution
- **Resolved**: 2026-10-08T11:23:00-07:00
- **Notes**: Restored the method boundary; the Events preflight project compiled and its 4 focused tests passed.

### Metadata
- Reproducible: yes
- Related Files: services/events-and-sessions/Agentweaver.EventsAndSessions/Usage/NativeUsageEndpoints.cs

---

## [ERR-20261008-010] preflight error mapping order

**Logged**: 2026-10-08T11:22:00-07:00
**Priority**: low
**Status**: resolved
**Area**: backend

### Summary
The focused Events build rejected a filtered `RuntimeAuthorizationException` catch after its unfiltered catch.

### Error
```text
CS0160: A previous catch clause already catches all exceptions of this or of a super type
```

### Context
- The cost-preflight-specific HTTP status mapping followed the existing broad authorization mapping.
- C# requires the more specific filtered catch to appear first.

### Suggested Fix
Place the cost-preflight filter before the generic authorization handler, then rerun the focused test.

### Resolution
- **Resolved**: 2026-10-08T11:23:00-07:00
- **Notes**: Reordered the catches; the Events preflight project compiled and its 4 focused tests passed.

### Metadata
- Reproducible: yes
- Related Files: services/events-and-sessions/Agentweaver.EventsAndSessions/Usage/NativeUsageEndpoints.cs

---

## [ERR-20261008-011] runtime host preflight test fixture

**Logged**: 2026-10-08T11:25:00-07:00
**Priority**: medium
**Status**: resolved
**Area**: tests

### Summary
RuntimeAgentHost tests failed because the owner HTTP fixture routed the new Events preflight request as a source-registration request.

### Error
```text
The JSON property 'contractVersion' could not be mapped to RuntimeSdkSourceRequest.
```

### Context
- The test fixture treated every POST under `/internal/runtime/sources/` as Orchestrator source registration.
- RuntimeAgentHost now POSTs a version-only preflight request to the Events owner on that path family.

### Suggested Fix
Route the exact cost-preflight path to a trusted priced receipt response and assert it occurs before readiness/model sends.

### Resolution
- **Resolved**: 2026-10-08T11:27:00-07:00
- **Notes**: Updated the fixture to return an owner-shaped preflight receipt and added unpriced cleanup coverage; all 25 RuntimeAgentHost tests passed.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/RuntimeAgentHostTests.cs

---

## [ERR-20261008-012] runtime registration source lookup

**Logged**: 2026-10-08T13:56:15-07:00
**Priority**: low
**Status**: resolved
**Area**: config

### Summary
The initial search targeted a nonexistent `v1` directory.

### Error
```text
Search paths do not exist: ...\\sabbour-reimagined-robot\\v1
```

### Context
- The branch stores source under repository-root `services` and `tests`, not a `v1` subdirectory.
- No source files were changed by the failed searches.

### Suggested Fix
Search the repository-root `services` and `tests` trees.

### Resolution
- **Resolved**: 2026-10-08T13:56:15-07:00
- **Notes**: Located the registration owner, store, endpoint, and persistence/integration tests under their actual root paths.

### Metadata
- Reproducible: yes
- Related Files: services/orchestrator/Agentweaver.Orchestrator/RuntimeRegistrationStore.cs

---

## [ERR-20261008-013] owner integration test patch context

**Logged**: 2026-10-08T13:57:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
A test patch did not match the exact assertion in the current integration test.

### Error
```text
Failed to apply patch: expected RuntimeRegistrationOwnerIntegrationTests.cs context was not found.
```

### Context
- The patch expected a null-forgiving operator on `ProvisionedResource` at a different assertion than the current file.
- The patch was rejected atomically and made no source changes.

### Suggested Fix
Read the exact enrollment and assertion block, then apply the test changes against current source.

### Resolution
- **Resolved**: 2026-10-08T13:57:00-07:00
- **Notes**: Read the current integration test block and will patch its exact context.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/RuntimeRegistrationOwnerIntegrationTests.cs

---

## [ERR-20261008-014] runtime registration documentation context

**Logged**: 2026-10-08T13:57:30-07:00
**Priority**: low
**Status**: resolved
**Area**: docs

### Summary
A documentation patch did not match the exact line wrap in the architecture guide.

### Error
```text
Failed to apply patch: expected sessions-and-coordination.md context was not found.
```

### Context
- The current sentence ends its line with "The endpoint rechecks the"; the patch omitted "the" from that line.
- The patch was rejected atomically and made no documentation changes.

### Suggested Fix
Use the exact current text from the architecture guide and apply the documentation update.

### Resolution
- **Resolved**: 2026-10-08T13:57:30-07:00
- **Notes**: Read the exact architecture paragraph and will patch at its current paragraph boundary.

### Metadata
- Reproducible: yes
- Related Files: docs/architecture/design/sessions-and-coordination.md

---

## [ERR-20261008-015] checkpoint append arguments

**Logged**: 2026-10-08T13:58:00-07:00
**Priority**: low
**Status**: resolved
**Area**: config

### Summary
A checkpoint append call was rejected because its arguments contained duplicate `content` fields.

### Error
```text
Invalid input: duplicate field `content`
```

### Context
- The checkpoint call was rejected before any artifact update.

### Suggested Fix
Retry with one `content` field and preserve the current checkpoint revision.

### Resolution
- **Resolved**: 2026-10-08T13:58:00-07:00
- **Notes**: Will retry the checkpoint append with a single content value.

### Metadata
- Reproducible: no
- Related Files: checkpoint.md

---

## [ERR-20261008-018] MAF readiness malformed phase mapping

**Logged**: 2026-10-08T15:22:39-07:00
**Priority**: medium
**Status**: resolved
**Area**: tests

### Summary
Focused MAF tests found malformed startup-phase JSON escapes as a constructor exception instead of a coordination error.

### Error
```text
Expected: Agentweaver.Orchestrator.CoordinationException
Actual: System.ArgumentOutOfRangeException (parameter 'phase')
```

### Context
- `dotnet test tests\Agentweaver.Orchestrator.Core.Tests\Agentweaver.Orchestrator.Core.Tests.csproj --no-restore --filter FullyQualifiedName~Maf` returned 28 passed and 1 failed.
- The exception occurs while deserializing `RuntimeHostStartupReceipt` in `CoordinationEndpoints.SendRuntimeHostAsync`, before the readiness client can classify the malformed response.

### Suggested Fix
Translate invalid host-receipt deserialization/constructor values to `CoordinationException`, preserving cancellation and unrelated transport failures.

### Resolution
- **Resolved**: 2026-10-08T15:26:34-07:00
- **Notes**: Typed host-response parsing now maps `JsonException` and `ArgumentException` to `maf_execution_host_response_invalid`; the focused MAF tests pass 29/29.

### Metadata
- Reproducible: yes
- Related Files: services/orchestrator/Agentweaver.Orchestrator/MafExecutionDispatchEndpoints.cs; tests/Agentweaver.Orchestrator.Core.Tests/MafExecutionDispatchTests.cs

---

## [ERR-20261008-019] Cost reconciliation patch context

**Logged**: 2026-10-08T15:26:34-07:00
**Priority**: low
**Status**: resolved
**Area**: backend

### Summary
A combined contract and Events consumer patch applied its contract hunk but rejected the consumer hunk.

### Error
```text
Failed to find expected lines in NativeUsageReceiptConsumer.cs.
```

### Context
- The consumer comparison did not match the exact current predicate ordering.
- The contract hunk was applied; the consumer file was not changed by the rejected hunk.

### Suggested Fix
Read the current exact consumer loop and matching predicate, then patch only those remaining hunks.

### Resolution
- **Resolved**: 2026-10-08T15:26:34-07:00
- **Notes**: Read the current consumer loop and applied the remaining unpriced-evidence handling against exact source lines.

### Metadata
- Reproducible: yes
- Related Files: packages/Agentweaver.Identity/RuntimeUsageSourceReceipt.cs; services/events-and-sessions/Agentweaver.EventsAndSessions/Usage/NativeUsageReceiptConsumer.cs

---

## [ERR-20261008-020] Events reconciliation compile

**Logged**: 2026-10-08T15:26:34-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The focused Events reconciliation test build found three compile errors in the new query and error response code.

### Error
```text
CS0103: RuntimeContractValidation does not exist in PostgresUsageLedger.cs
CS0136: meterSource local shadows the filter parameter in PostgresUsageLedger.cs
CS0173: Guid[] and ImmutableArray<Guid> conditional types do not match in NativeUsageEndpoints.cs
```

### Context
- The new filtered totals helper referenced the identity validator without its namespace import and reused the source-meter name for both the filter and a row local.
- The reconciliation error response selected incompatible array types in a conditional.

### Suggested Fix
Import the validator namespace, rename the filter parameter, and normalize the receipt-ID branch to an array.

### Resolution
- **Resolved**: 2026-10-08T15:26:34-07:00
- **Notes**: Applied the namespace, local-parameter, and immutable-array fixes; focused Events cost reconciliation tests pass 3/3.

### Metadata
- Reproducible: yes
- Related Files: services/events-and-sessions/Agentweaver.EventsAndSessions/Usage/PostgresUsageLedger.cs; services/events-and-sessions/Agentweaver.EventsAndSessions/Usage/NativeUsageEndpoints.cs

---

## [ERR-20261008-021] MAF platform executor test search path

**Logged**: 2026-10-08T15:40:35-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
A repository search used a nonexistent Orchestrator test-project directory.

### Error
```text
Search paths do not exist: tests\Agentweaver.Orchestrator.Tests
```

### Context
- The test project is `tests\Agentweaver.Orchestrator.Core.Tests`.
- Repeated the search with the existing directory and located the MAF planner and dispatch tests.

### Suggested Fix
Use the exact project directory returned by the repository file listing before searching tests.

### Resolution
- **Resolved**: 2026-10-08T15:40:35-07:00
- **Notes**: Corrected the search path; relevant tests were found.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Orchestrator.Core.Tests/MafExecutionPlannerTests.cs; tests/Agentweaver.Orchestrator.Core.Tests/MafExecutionDispatchTests.cs

---

## [ERR-20261008-022] default workflow test collection assertion

**Logged**: 2026-10-08T16:28:52-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The new built-in workflow test compared an immutable array with a collection expression using the wrong xUnit overload.

### Error
```text
Assert.Equal() Failure: Collections differ
Expected: ["implementation"]
Actual:   ["implementation"]
```

### Context
- The failure came from comparing `ImmutableArray<string>` directly to a collection expression.
- Changed the assertion to compare materialized arrays.

### Suggested Fix
Normalize immutable collections before using sequence assertions.

### Resolution
- **Resolved**: 2026-10-08T16:28:52-07:00
- **Notes**: Rerun the focused default-workflow test after the assertion correction.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Orchestrator.Core.Tests/CoordinatorWorkflowCatalogTests.cs

---

## [ERR-20261008-032] MAF dispatch replay patch context

**Logged**: 2026-10-08T18:16:00-07:00
**Priority**: low
**Status**: resolved
**Area**: backend

### Summary
A follow-up patch for exact dispatch-intent replay validation did not match the current method layout.

### Error
```text
Failed to apply patch: expected the BuildPrompt return block immediately before CompleteDispatchAsync.
```

### Context
- The intended change was to rebuild the expected deterministic dispatch intent from the checkpoint and accepted plan before acknowledging a replay.
- The patch was rejected atomically because it assumed the helper insertion point was adjacent to the dispatch local function.

### Suggested Fix
Apply smaller hunks using the current `BuildPrompt`, `CompleteDispatchAsync`, and replay-helper locations.

### Resolution
- **Resolved**: 2026-10-08T18:18:00-07:00
- **Notes**: Re-read the exact method boundaries and applied smaller source and test hunks; the original failed patch made no changes.

### Metadata
- Reproducible: no
- Related Files: services/orchestrator/Agentweaver.Orchestrator/MafExecutionDispatchEndpoints.cs

---

## [ERR-20261008-033] MAF join documentation patch context

**Logged**: 2026-10-08T18:20:00-07:00
**Priority**: low
**Status**: resolved
**Area**: docs

### Summary
A combined planner, test, and documentation patch applied its code hunks but failed to match the already-edited documentation paragraph.

### Error
```text
Failed to find expected lines in docs/architecture/design/orchestration.md.
Earlier hunks were applied and not rolled back.
```

### Context
- The documentation text had been changed earlier in this task, so the patch's original paragraph context was stale.
- The planner, dispatch endpoint, and planner test hunks applied before the documentation hunk failed.

### Suggested Fix
Inspect the applied source and patch only the current documentation paragraph and diagram.

### Metadata
- Reproducible: no
- Related Files: docs/architecture/design/orchestration.md

---

## [ERR-20261008-034] MAF planner test patch context

**Logged**: 2026-10-08T18:22:00-07:00
**Priority**: low
**Status**: in_progress
**Area**: tests

### Summary
A combined planner regression patch did not match the current test method context.

### Error
```text
Failed to apply patch: expected CompletedChildSatisfiesJoinAndUnblocksDependents.
```

### Context
- The intended test updates followed earlier planner edits and the current method text no longer matched the full patch hunk.
- The patch was rejected without modifying the test file.

### Suggested Fix
Read the exact current planner test methods and apply smaller, independent test hunks.

### Metadata
- Reproducible: no
- Related Files: tests/Agentweaver.Orchestrator.Core.Tests/MafExecutionPlannerTests.cs

---

## [ERR-20261008-031] interpolated JSON test literal

**Logged**: 2026-10-08T17:50:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
Interpolated raw JSON test literals failed compilation because JSON closing braces conflicted with interpolation delimiters.

### Error
```text
CS9007: The interpolated raw string literal does not start with enough '$' characters to allow this many consecutive closing braces as content.
```

### Context
- The focused `dotnet test` run failed compiling `CoordinatorWorkflowCatalogTests.cs`.
- The literals embedded a value between JSON object braces using `$$"""..."""`.

### Suggested Fix
Serialize the test object with `JsonSerializer.Serialize` instead of interpolating JSON braces.

### Resolution
- **Resolved**: 2026-10-08T17:51:00-07:00
- **Notes**: Replaced both literals with standard-library JSON serialization.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Orchestrator.Core.Tests/CoordinatorWorkflowCatalogTests.cs

---

## [ERR-20261008-029] delayed callback test prompt mismatch

**Logged**: 2026-10-08T16:40:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The direct SDK-session delayed-callback test sent prompts that did not match the controlled transport's fixed expected prompt.

### Error
```text
Expected: "A bounded user request."
Actual:   "first send"
```

### Context
- The controlled transport asserted its configured prompt during disposal.
- Replaced the direct-session test with a Host-level two-A2A-send test using the fixture's normal prompt.

### Resolution
- **Resolved**: 2026-10-08T16:40:00-07:00
- **Notes**: Host-level test now exercises the delayed callback with distinct A2A IDs.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/RuntimeAgentHostTests.cs

---

## [ERR-20261008-030] stale documentation patch context

**Logged**: 2026-10-08T16:40:00-07:00
**Priority**: low
**Status**: resolved
**Area**: docs

### Summary
A follow-up documentation hunk targeted the superseded timestamp-correlation wording.

### Error
```text
Failed to find expected lines in docs/architecture/agenthost.md
```

### Context
- The earlier coordinator-directed patch had already replaced that wording with fail-closed semantics.
- Inspected the file and confirmed no remaining documentation update was needed for that hunk.

### Resolution
- **Resolved**: 2026-10-08T16:40:00-07:00
- **Notes**: No documentation content was lost; the current text states callbacks stay uncorrelated until a supported binding is available.

### Metadata
- Reproducible: yes
- Related Files: docs/architecture/agenthost.md

---

## [ERR-20261008-027] timestamp-window usage ownership attempt

**Logged**: 2026-10-08T16:38:00-07:00
**Priority**: high
**Status**: wont_fix
**Area**: backend

### Summary
The attempted usage-callback correlation used SDK event timestamps to classify A2A send ownership.

### Error
```text
Focused Host/SDK session test run: Failed 2, Passed 54, Total 56.
Failed: RuntimeAgentHostTests.UnbudgetedHostedCopilotKeepsLegacyStartupAndSelectionHash
Failed: RuntimeCopilotSessionTests.LateUsageCallbacksRemainBoundToTheirOriginalA2AMessage
```

### Context
- The approach resolved callback ownership from timestamp intervals rather than a native send identity.
- Root rejected timestamp windows as positive ownership evidence because event creation time is not an authenticated send ID and may classify a prior send as current.
- The quiet test output did not include detailed failure messages; the timestamp approach is rejected regardless of those details.

### Resolution
- **Resolved**: 2026-10-08T16:39:00-07:00
- **Notes**: Removed timestamp ownership inference. Actual SDK callbacks remain uncorrelated, and Events' exact CostTurnId comparison keeps evidence/exposure held until Primary provides a supported native binding.

### Metadata
- Reproducible: yes
- Related Files: packages/Agentweaver.AgentRuntime/RuntimeCopilotSessionFactory.cs; tests/Agentweaver.Identity.Broker.Tests/RuntimeCopilotSessionTests.cs

---

## [ERR-20261008-028] error-log patch context mismatch

**Logged**: 2026-10-08T16:39:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
A combined patch applied code changes but failed to update the error log because it referenced a nonexistent trailing `See Also` line.

### Error
```text
Failed to find expected lines in .learnings/ERRORS.md
```

### Context
- The failed hunk followed successful source/test/doc hunks, which remained applied.
- Inspected the exact log tail and retried only the remaining logging change.

### Resolution
- **Resolved**: 2026-10-08T16:39:00-07:00
- **Notes**: Added entries after inspecting the actual file tail.

### Metadata
- Reproducible: yes
- Related Files: .learnings/ERRORS.md

---

## [ERR-20261008-024] focused tests missing restore assets

**Logged**: 2026-10-08T16:35:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The first focused test command used `--no-restore` when the test project's NuGet assets were absent.

### Error
```text
error NETSDK1004: Assets file '.../tests/Agentweaver.Identity.Tests/obj/project.assets.json' not found. Run a NuGet package restore to generate this file.
```

### Context
- Command: `dotnet test tests\Agentweaver.Identity.Tests\Agentweaver.Identity.Tests.csproj --no-restore --verbosity quiet --filter FullyQualifiedName~RuntimeUsageSourceReceiptTests`
- The worktree has no restored `project.assets.json` for this test project.

### Suggested Fix
Rerun the focused tests without `--no-restore` so the SDK restores dependencies.

### Resolution
- **Resolved**: 2026-10-08T16:35:10-07:00
- **Notes**: Rerunning without `--no-restore` restored dependencies and advanced to compilation.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Tests/Agentweaver.Identity.Tests.csproj

---

## [ERR-20261008-025] source receipt test missing JSON import

**Logged**: 2026-10-08T16:35:10-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The new legacy-hash assertion used `System.Text.Json` without importing its namespace.

### Error
```text
CS0103: The name 'JsonSerializer' does not exist in the current context
CS0246: The type or namespace name 'JsonSerializerOptions' could not be found
```

### Context
- The focused Identity test build failed while compiling `RuntimeUsageSourceReceiptTests.cs`.
- An attempted import patch used an assumed preamble that did not match the file; inspection confirmed the test had no JSON namespace import.

### Suggested Fix
Add `using System.Text.Json;` and rerun the focused test.

### Resolution
- **Resolved**: 2026-10-08T16:35:20-07:00
- **Notes**: Added the import; all three focused Identity receipt tests passed.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Tests/RuntimeUsageSourceReceiptTests.cs

---

## [ERR-20261008-026] turn observer correlation property placement

**Logged**: 2026-10-08T16:35:20-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The Host build found the new active A2A message property on `RuntimeCopilotSession` instead of its turn observer.

### Error
```text
CS0103: The name '_gate' does not exist in the current context
CS0103: The name '_completion' does not exist in the current context
CS0103: The name '_a2aMessageId' does not exist in the current context
CS1061: 'RuntimeCopilotTurnObserver' does not contain a definition for 'CurrentA2AMessageId'
```

### Context
- The property was inserted next to the first method-ending context in `RuntimeCopilotSession.cs`, outside `RuntimeCopilotTurnObserver`.
- Moved it into the observer, which owns the lock and active message ID.

### Suggested Fix
Rerun the Host and Events focused tests to verify compilation and callback capture.

### Resolution
- **Resolved**: 2026-10-08T16:36:00-07:00
- **Notes**: Moved the property to the observer; 29 Host tests and 3 Events reconciliation tests passed.

### Metadata
- Reproducible: yes
- Related Files: packages/Agentweaver.AgentRuntime/RuntimeCopilotSession.cs

---

## [ERR-20261008-023] PowerShell test filter pipe

**Logged**: 2026-10-08T16:28:52-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
PowerShell interpreted the `|` in a combined `dotnet test --filter` expression as a pipeline separator.

### Error
```text
The term 'FullyQualifiedName~CoordinatorWorkflowCatalogTests' is not recognized as a name of a cmdlet, function, script file, or executable program.
```

### Context
- The combined filter was passed without quotes.
- Rerun with the entire filter expression quoted.

### Suggested Fix
Quote VSTest filter expressions containing `|` in PowerShell.

### Resolution
- **Resolved**: 2026-10-08T16:28:52-07:00
- **Notes**: The filtered test rerun is pending.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Orchestrator.Core.Tests/CoordinatorWorkflowCatalogTests.cs

---

## [ERR-20261008-035] numeric runtime-policy patch context

**Logged**: 2026-10-08T18:34:00-07:00
**Priority**: low
**Status**: resolved
**Area**: backend

### Summary
A multi-file patch partially applied before its Host method context did not match.

### Error
```text
Failed to find expected lines in RuntimeAgentHost.cs; remaining Host and RuntimeActionEndpoints hunks were not attempted.
```

### Context
- OwnerContext, RuntimeBinding, policy parsing, and registration-mapping hunks applied.
- Host and action endpoint hunks remain unapplied and must be applied against current exact method context.

### Suggested Fix
Read current Host method context and apply only the missing hunks.

### Resolution
- **Resolved**: 2026-10-08T18:39:00-07:00
- **Notes**: Reapplied the Host and action-owner guards at their current method locations; focused Host and Broker builds passed.

### Metadata
- Reproducible: yes
- Related Files: packages/Agentweaver.AgentRuntime/RuntimeAgentHost.cs, services/orchestrator/Agentweaver.Orchestrator/RuntimeActionEndpoints.cs

---

## [ERR-20261008-036] parallel dotnet build output collision

**Logged**: 2026-10-08T18:35:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
Parallel test builds attempted to write the shared Abstractions assembly.

### Error
```text
CS2012: Cannot open Agentweaver.Abstractions.dll for writing; file may be locked by Microsoft Defender Antivirus Service.
```

### Context
- Three independent `dotnet test` commands ran concurrently from the same worktree and shared `obj` outputs.
- The Identity and Broker test commands passed; the Orchestrator command failed during compilation.

### Suggested Fix
Rerun remaining build/test commands serially to avoid shared intermediate-output contention.

### Resolution
- **Resolved**: 2026-10-08T18:39:00-07:00
- **Notes**: The serial Orchestrator test build and run passed 40 filtered tests.

### Metadata
- Reproducible: yes
- Related Files: packages/Agentweaver.Abstractions/Agentweaver.Abstractions.csproj

---

## [ERR-20261008-037] interpolated raw JSON test literal

**Logged**: 2026-10-08T18:37:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
A test's interpolated raw string did not allow adjacent JSON closing braces.

### Error
```text
CS9007: The interpolated raw string literal does not start with enough '$' characters to allow this many consecutive closing braces as content.
```

### Context
- The invalid literal was used to generate a test run-limit object.

### Suggested Fix
Serialize an anonymous object containing a dictionary keyed by the tested policy name.

### Resolution
- **Resolved**: 2026-10-08T18:37:00-07:00
- **Notes**: Replaced the raw interpolated literal with `JsonSerializer.Serialize`; targeted Orchestrator tests remain to be rerun.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Orchestrator.Core.Tests/CoordinatorWorkflowCatalogTests.cs

---

## [ERR-20261008-039] broad owner-context theory transport reset

**Logged**: 2026-10-08T18:42:00-07:00
**Priority**: low
**Status**: in_progress
**Area**: tests

### Summary
The broad theory filter hit a transport reset in a source-control webhook row before the new owner-context assertions.

### Error
```text
System.Net.Http.HttpRequestException: Error while copying content to a stream.
The remote host forcibly closed the transport connection.
```

### Context
- `BrokerIssuedRunTokenRegistersSessionsDeliversAtTurnBoundaryAndKeepsGatePending` was filtered by method name, selecting all four theory rows.
- The failing `sourceLoss: "lease"` row stopped at the source-control webhook helper, before runtime-owner-context propagation assertions. The process continued after the test failure and was stopped.

### Suggested Fix
Run the existing no-source-loss fact wrapper that invokes the same integration scenario through the runtime-owner-context assertions.

### Metadata
- Reproducible: unknown
- Related Files: tests/Agentweaver.Identity.Broker.Tests/CoordinationMessageIntegrationTests.cs

---

## [ERR-20261008-040] malformed ripgrep regex

**Logged**: 2026-10-08T19:02:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tooling

### Summary
A repository search failed because its regular expression contained an unclosed group.

### Error
```text
regex parse error: unclosed group
```

### Context
- The initial `rg` expression was malformed; a corrected expression returned the intended matches.

### Suggested Fix
Use a literal or escaped grouping expression, and run a narrow search before expanding its scope.

### Resolution
- **Resolved**: 2026-10-08T19:02:00-07:00
- **Notes**: Corrected the expression and completed the targeted search.

### Metadata
- Reproducible: yes
- Related Files: none

---

## [ERR-20261009-067] SDK patch controlled-runtime anchor

**Logged**: 2026-10-09T10:46:00-07:00
**Priority**: low
**Status**: pending
**Area**: tests

### Summary
The minimal SDK patch generator assumed the controlled runtime's `default` case indentation did not match the published source.

### Error
```text
tests/Agentweaver.Identity.Broker.Tests/ControlledCopilotRuntime.cs: expected 1 exact matches, got 0
```

### Context
- A read-only baseline transform prepared SDK version and lifecycle edits.
- Its insertion marker used a hard-coded indentation string; the target anchor differed. No patch file was emitted and repository source was unchanged.

### Suggested Fix
Use a minimal context-safe insertion marker derived from the exact published switch source.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/ControlledCopilotRuntime.cs

---

## [ERR-20261008-041] missing SDK RPC namespace

**Logged**: 2026-10-08T19:16:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The focused runtime authorization test build failed because the permission decision type is in the SDK RPC namespace.

### Error
```text
RuntimeCopilotToolCallAuthorization.cs(48,25): error CS0246: The type or namespace name 'PermissionDecision' could not be found
```

### Context
- The class imported `GitHub.Copilot` for SDK tool request types but omitted `GitHub.Copilot.Rpc`, where `PermissionDecision` is defined.
- Command: `dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-restore --filter "FullyQualifiedName~RuntimeActionHttpClientTests|FullyQualifiedName~RuntimeCopilotToolCallAuthorizationTests" --logger "console;verbosity=minimal"`.
- The first log append patch missed because it used bolded field text that does not match this file's established format; the corrected patch uses the exact existing metadata.

### Suggested Fix
Import the SDK RPC namespace and rerun the same focused test command.

### Metadata
- Reproducible: yes
- Related Files: packages/Agentweaver.AgentRuntime/RuntimeCopilotToolCallAuthorization.cs

### Resolution
- **Resolved**: 2026-10-08T19:20:00-07:00
- **Notes**: Imported the SDK RPC namespace containing `PermissionDecision`.

## [ERR-20261008-042] focused authorization test compilation errors

**Logged**: 2026-10-08T19:18:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The focused runtime authorization tests exposed several SDK callback integration compile errors after the missing RPC namespace was fixed.

### Error
```text
GHCP001 suppression was missing; a Guid? value was passed where Guid was required; and PermissionInvocation/HookInvocation callback contexts were confused with ToolInvocation.
RuntimeCopilotSessionFactory.cs(192,40): error CS8621: Nullability of reference types in return type of 'Task<PreToolUseHookOutput> RuntimeCopilotToolCallAuthorization.OnPreToolUseAsync(PreToolUseHookInput, HookInvocation)' doesn't match the target delegate 'Func<PreToolUseHookInput, HookInvocation, Task<PreToolUseHookOutput?>>'.
RuntimeCopilotToolCallAuthorizationTests.cs(40,15): error CS1593: Delegate 'Func<CancellationToken>' does not take 1 arguments. The same error occurred at lines 84 and 118.
RuntimeCopilotToolCallAuthorizationTests.cs(52, 90, 91, 123, 142): error CS8602: Dereference of a possibly null reference.
```

### Context
- Command: `dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-restore --filter "FullyQualifiedName~RuntimeActionHttpClientTests|FullyQualifiedName~RuntimeCopilotToolCallAuthorizationTests" --logger "console;verbosity=minimal"`.
- SDK XML documentation showed the required callback context types. The nullable hook-output signature, test cancellation lambdas, and nullable-output assertions were corrected; the test suite proceeded to execution.

### Suggested Fix
Keep the documented callback context types, use nonnullable event IDs, add the required GHCP001 suppression, and rerun the focused test command.

### Metadata
- Reproducible: yes
- Related Files: packages/Agentweaver.AgentRuntime/RuntimeCopilotToolCallAuthorization.cs; packages/Agentweaver.AgentRuntime/RuntimeActionHttpClient.cs

### Resolution
- **Resolved**: 2026-10-08T19:20:00-07:00
- **Notes**: Corrected SDK callback nullability, test token-provider lambdas, and nullable hook-output assertions.

## [ERR-20261008-044] incomplete SDK permission request fixtures

**Logged**: 2026-10-08T19:19:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The focused authorization tests ran, but SDK JSON deserialization rejected incomplete permission request fixtures.

### Error
```text
21 tests: 13 passed, 8 failed on the first run because PermissionRequestShell was missing canOfferSessionApproval, commands, and hasWriteFileRedirection; PermissionRequestRead was missing intention; PermissionRequestWrite was missing canOfferSessionApproval, diff, and intention. After adding those values, a second run had 16 passed and 5 failed because PermissionRequestShell still lacked required possiblePaths and possibleUrls.
```

### Context
- Command: `dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-restore --filter "FullyQualifiedName~RuntimeActionHttpClientTests|FullyQualifiedName~RuntimeCopilotToolCallAuthorizationTests" --logger "console;verbosity=minimal"`.
- The fixtures deserialize SDK request models from JSON; the pinned SDK marks the listed fields as required. The second run confirms the first set of values fixed the Read and Write fixtures; only the shared Shell fixture remains incomplete.

### Suggested Fix
Add empty possible-path/URL collections using the pinned SDK model's field types, then rerun the focused tests.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/RuntimeCopilotToolCallAuthorizationTests.cs

### Resolution
- **Resolved**: 2026-10-08T19:20:00-07:00
- **Notes**: Added all required SDK permission request fixture properties; focused authorization tests pass 21/21.

## [ERR-20261008-043] stale-context patch failed

**Logged**: 2026-10-08T19:18:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tooling

### Summary
A patch to replace the runtime authorization implementation failed because its context no longer matched the current file.

### Error
```text
The patch could not be applied because the expected source context was stale.
```

### Context
- The file had changed since the patch context was prepared. The current file was reread and the replacement was then applied successfully.

### Suggested Fix
Reread the target file before forming the patch and apply against its current exact content.

### Metadata
- Reproducible: no
- Related Files: packages/Agentweaver.AgentRuntime/RuntimeCopilotToolCallAuthorization.cs

## [ERR-20261008-045] broad SDK XML search timed out

**Logged**: 2026-10-08T19:20:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tooling

### Summary
A broad ripgrep search of the pinned SDK XML documentation exceeded the command timeout.

### Error
```text
rg timed out after 20 seconds. Narrow the search further with glob patterns and/or search roots for faster performance.
```

### Context
- The search targeted a few permission request property names in `GitHub.Copilot.SDK.xml`.
- A narrow `view` of the known SDK documentation range provided the needed property semantics.

### Suggested Fix
Use a narrow file range or exact XML member name instead of scanning the complete SDK documentation file.

### Metadata
- Reproducible: no
- Related Files: N/A

## [ERR-20261008-046] guessed native permission RPC method

**Logged**: 2026-10-08T22:18:00-07:00
**Priority**: low
**Status**: in_progress
**Area**: tests

### Summary
A new native permission-callback integration test used an incorrect SDK RPC method name.

### Error
```text
System.InvalidOperationException: {"code":-32601,"message":"Method not found: permission.request"}
System.InvalidOperationException: {"code":-32601,"message":"Method not found: permission.requested"}
System.Threading.Tasks.TaskCanceledException in GitHub.Copilot.Rpc.ServerSessionFsApi.SetProviderAsync
```

### Context
- Command: `dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-restore --filter FullyQualifiedName~ActualNativePermissionCallbackAuthorizesItsExactActionAndToolCall`
- The pinned Copilot SDK rejected the guessed server callback names before invoking the registered permission delegate. `permission.requested` is also a session-event type, not a registered server callback method. The event-injection retry timed out during SDK `SetProviderAsync` startup, before reaching the permission assertion, amid shared-machine contention. A separate package search command used `rg` from PowerShell, where the executable is unavailable.

### Suggested Fix
Read the pinned SDK's exact permission callback wire method/shape before changing the integration test; use `functions.rg` for workspace searches and PowerShell `Select-String` for external package files.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/RuntimeActionHttpClientTests.cs

---

## [ERR-20261008-047] SDK reflection dependency unavailable

**Logged**: 2026-10-08T22:18:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tooling

### Summary
Loading the pinned Copilot SDK assembly directly in PowerShell failed because its .NET dependency context was not configured.

### Error
```text
Unable to load one or more requested types: Microsoft.Extensions.AI.Abstractions, Version=10.2.0.0
```

### Context
- Command attempted `Assembly.LoadFrom` on the NuGet SDK DLL to inspect RPC types. The XML contract and binary strings were sufficient to identify the permission event model without reflection.

### Suggested Fix
Use the SDK XML and narrow binary string inspection first; configure a dependency-aware AssemblyLoadContext only if reflection is still necessary.

### Metadata
- Reproducible: yes
- Related Files: N/A

---

## [ERR-20261008-048] stale decision-owner patch context

**Logged**: 2026-10-08T22:18:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tooling

### Summary
The first transaction-guard patch did not match the current decision-owner source context.

### Error
```text
Failed to apply patch: expected source context was not found.
```

### Context
- The intended insertion point was `CoordinatorDecisionOwnerStore.ReadCurrentAsync`; the patch used an inaccurate nearby line sequence.
- The file was reread at exact ranges and the helper was applied successfully in narrower hunks.

### Suggested Fix
Read the exact insertion range before constructing a patch.

### Metadata
- Reproducible: no
- Related Files: services/orchestrator/Agentweaver.Orchestrator/CoordinatorDecisionOwnerStore.cs

### Resolution
- **Resolved**: 2026-10-08T22:18:00-07:00
- **Notes**: Reapplied the decision-lock helper against current source context.

---

## [ERR-20261008-049] controlled SDK permission response unhandled

**Logged**: 2026-10-08T23:10:00-07:00
**Priority**: high
**Status**: resolved
**Area**: tests

### Summary
The native permission-event integration test reaches the SDK's permission handling RPC, but the controlled runtime does not answer that SDK method.

### Error
```text
System.InvalidOperationException: Unexpected actual SDK method: session.permissions.handlePendingPermissionRequest;
params: {"requestId":"<event request id>","result":{"kind":"approve-once"},"sessionId":"<SDK session id>"}
```

### Context
- Command: `dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-restore --filter "FullyQualifiedName~RuntimeActionHttpClientTests|FullyQualifiedName~RuntimeCopilotToolCallAuthorizationTests" --logger "console;verbosity=minimal"`
- Result: 21 passed, 1 failed. The failure occurs after emitting the actual `permission.requested` session event; the test server throws on the subsequent SDK method instead of returning the native permission decision.
- Narrow retry with `FullyQualifiedName~ActualNativePermissionCallbackAuthorizesItsExactActionAndToolCall` confirmed the exact request fields: `requestId`, `result.kind = "approve-once"`, and `sessionId`.
- A whole-binary `RegexOptions.Singleline` diagnostic over the SDK assembly did not finish within 30 seconds and was stopped; avoid that unbounded scan.
- Related to ERR-20261008-046, which recorded earlier guessed RPC names and a startup timeout.

### Suggested Fix
Handle `session.permissions.handlePendingPermissionRequest` in the controlled runtime, validate request/session identity and the `approve-once` result, and return the SDK's typed `PermissionRequestResult` JSON (`success`/`applied`).

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/ControlledCopilotRuntime.cs, tests/Agentweaver.Identity.Broker.Tests/RuntimeActionHttpClientTests.cs
- See Also: ERR-20261008-046

### Resolution
- **Resolved**: 2026-10-08T23:10:00-07:00
- **Notes**: Controlled transport validates the actual SDK decision RPC and responds with `{success:true, applied:true}`. The focused native permission/action suite passed 22/22.

---

## [ERR-20261008-050] xUnit single-result filter analyzer

**Logged**: 2026-10-08T23:10:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The test build rejected filtering a request queue before calling `Assert.Single`.

### Error
```text
xUnit2031: Do not use a Where clause to filter before calling Assert.Single.
```

### Context
- Command: `dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-restore --filter "FullyQualifiedName~RuntimeActionHttpClientTests|FullyQualifiedName~RuntimeCopilotToolCallAuthorizationTests" --logger "console;verbosity=minimal"`
- Replaced `Assert.Single(collection.Where(predicate))` with xUnit's predicate overload.

### Suggested Fix
Use `Assert.Single(collection, predicate)` when asserting exactly one matching item.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/RuntimeActionHttpClientTests.cs

### Resolution
- **Resolved**: 2026-10-08T23:10:00-07:00
- **Notes**: Switched to the predicate overload of `Assert.Single`.

---

## [ERR-20261008-051] exact Git blob import script input mode

**Logged**: 2026-10-08T23:57:00-07:00
**Priority**: medium
**Status**: resolved
**Area**: infra

### Summary
The byte-preserving blob import helper failed when it passed raw bytes to a text-mode subprocess.

### Error
```text
TypeError: write() argument must be str, not bytes
```

### Context
- Command: Python helper captured `git cat-file blob`, then invoked `git hash-object --stdin` with `input=blob` and `text=True`.
- The failure occurred before the destination file was written. The running shell was stopped.

### Suggested Fix
Keep the hash-object subprocess in binary mode and decode its stdout after it returns, then retry the import and verify the final Git blob hashes.

### Metadata
- Reproducible: yes
- Related Files: services/orchestrator/Agentweaver.Orchestrator/BacklogOwnerStore.cs, services/orchestrator/Agentweaver.Orchestrator/BacklogEndpoints.cs, services/orchestrator/Agentweaver.Orchestrator/Migrations/016_project_backlog.sql

### Resolution
- **Resolved**: 2026-10-09T00:04:00-07:00
- **Notes**: Kept the hash-object subprocess in binary mode and decoded its output after completion. All three imported file hashes matched the admitted blobs exactly.

---

## [ERR-20261009-052] PowerShell Git blob type probe

**Logged**: 2026-10-09T00:23:41-07:00
**Priority**: low
**Status**: resolved
**Area**: infra

### Summary
The PowerShell wrapper failed to pass a Git revision expression with a blob type suffix.

### Error
```text
error: unknown switch `n'
```

### Context
- Command: `git cat-file -e <hash>^{blob}` followed by `git cat-file -p <hash>`.
- PowerShell/native argument parsing changed the revision-expression arguments; the blob was not read.

### Suggested Fix
Quote the complete revision expression or use `git cat-file -t <hash>` to test a known object ID.

### Metadata
- Reproducible: unknown
- Related Files: none

### Resolution
- **Resolved**: 2026-10-09T00:24:45-07:00
- **Notes**: Retried with `git cat-file -t '<hash>'`; Git returned `blob` and `git cat-file -p '<hash>'` read the contract successfully.

---

## [ERR-20261009-053] Cross-session artifact path

**Logged**: 2026-10-09T03:03:00-07:00
**Priority**: low
**Status**: resolved
**Area**: config

### Summary
The virtual session-artifact path for another project session was not addressable from this session.

### Error
```text
ENOENT: The system cannot find the path specified.
```

### Context
- Operation: `session_artifacts.read` on the exact `agents/<session-id>/notes/root71-p1-source-contract.md` path.
- Root supplied the actual backing file path and confirmed virtual `agents/...` links are not available across project sessions.

### Suggested Fix
Use the exact backing file path supplied by its owning session; do not guess an alternate contract path.

### Metadata
- Reproducible: yes
- Related Files: none

### Resolution
- **Resolved**: 2026-10-09T03:03:00-07:00
- **Notes**: Read the supplied backing artifact with `view`.

---

## [ERR-20261009-054] Workspace change overview

**Logged**: 2026-10-09T03:03:00-07:00
**Priority**: low
**Status**: resolved
**Area**: config

### Summary
The change-overview helper could not resolve its configured merge base.

### Error
```text
git merge-base a540ba511c9b75c80a4d081f385365bc252d6e1c dd20ce67adf919c77e8f49e19a2fcdc5b24fad2c failed
```

### Context
- Operation: `get_changes_overview` for the active worktree.
- The current task checkpoint records the admitted v1 baseline and exact source lease.

### Suggested Fix
Use non-destructive `git status` and targeted diffs against the explicitly recorded baseline.

### Metadata
- Reproducible: yes
- Related Files: none

### Resolution
- **Resolved**: 2026-10-09T03:04:00-07:00
- **Notes**: Used successful targeted `git status --short` and `git diff` for the owned files; no destructive or broad Git operation was needed.

---

## [ERR-20261009-055] MAF checkpoint contract namespace

**Logged**: 2026-10-09T03:04:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The extracted MAF checkpoint contract initially imported `CheckpointInfo` from the wrong namespace.

### Error
```text
CS0246: CheckpointInfo could not be found in Agentweaver.Abstractions.
```

### Context
- B70's compile-only validation used the frozen contract blob before it was corrected.
- Existing `MafExecutionCheckpointStore.cs` imports `Microsoft.Agents.AI.Workflows` for the checkpoint type.

### Suggested Fix
Reuse the existing MAF workflow checkpoint contract and namespace; do not define a parallel checkpoint type.

### Metadata
- Reproducible: yes
- Related Files: services/orchestrator/Agentweaver.Orchestrator/MafExecutionContracts.cs

### Resolution
- **Resolved**: 2026-10-09T03:04:00-07:00
- **Notes**: Updated the extracted file to import `Microsoft.Agents.AI.Workflows`; materialized superseding blob `3b25a37965cfbc4f7fd95822d0dad9dd7f99ef21`.

---

## [ERR-20261009-056] Learnings log patch context

**Logged**: 2026-10-09T03:04:00-07:00
**Priority**: low
**Status**: resolved
**Area**: config

### Summary
An append patch did not match the current learnings-log heading.

### Error
```text
Failed to find expected lines.
```

### Context
- Patch expected `**Metadata**`; the current file uses `### Metadata`.

### Suggested Fix
Use the exact heading from the current file when applying patches.

### Metadata
- Reproducible: yes
- Related Files: .learnings/ERRORS.md

### Resolution
- **Resolved**: 2026-10-09T03:04:00-07:00
- **Notes**: Re-read the exact tail and applied the update with matching context.

---

## [ERR-20261009-057] Orchestrator build blocked by pending B70 helpers

**Logged**: 2026-10-09T03:05:00-07:00
**Priority**: medium
**Status**: in_progress
**Area**: tests

### Summary
The Orchestrator build reached the extracted MAF contract but stopped on two not-yet-integrated B70 transaction helpers.

### Error
```text
BacklogOwnerStore.cs(264,47): CS1061: CoordinationOwnerStore has no AcceptRootInTransactionAsync
BacklogOwnerStore.cs(267,47): CS1061: CoordinatorDecisionOwnerStore has no InitializeRootInTransactionAsync
```

### Context
- Command: `dotnet build services\orchestrator\Agentweaver.Orchestrator\Agentweaver.Orchestrator.csproj --no-restore --configuration Release`
- The build reported no error in `MafExecutionContracts.cs`; the missing methods are owned by B70 and are required by its current reader/store changes.

### Suggested Fix
Re-run after B70's exact helper hunks are integrated; do not add substitute helpers in this owner slice.

### Metadata
- Reproducible: yes
- Related Files: services/orchestrator/Agentweaver.Orchestrator/BacklogOwnerStore.cs

---

## [ERR-20261009-058] Workspace change overview base refs

**Logged**: 2026-10-09T03:47:00-07:00
**Priority**: low
**Status**: pending
**Area**: config

### Summary
The workspace change overview could not compute a merge base for its configured refs.

### Error
```text
git merge-base a540ba511c9b75c80a4d081f385365bc252d6e1c dd20ce67adf919c77e8f49e19a2fcdc5b24fad2c failed
```

### Context
- Operation: `get_changes_overview`
- The failure did not affect the worktree; `git status` and `git diff` were used to inspect current changes.

### Suggested Fix
Refresh or repair the overview tool's configured base/head refs before retrying it.

### Metadata
- Reproducible: unknown
- Related Files: none

---

## [ERR-20261009-059] PowerShell Git object type probe

**Logged**: 2026-10-09T03:52:00-07:00
**Priority**: low
**Status**: pending
**Area**: config

### Summary
The PowerShell Git object check passed a Bash-style peeled-object suffix that PowerShell/Git parsed as an invalid switch.

### Error
```text
git cat-file: unknown switch `n'
```

### Context
- Command: `git cat-file -e <blob>^{blob}` inside PowerShell.
- The supplied object IDs are already blob IDs; the suffix is unnecessary. Use quoted revisions or omit the suffix.

### Suggested Fix
Run `git cat-file -e <object-id>` directly, then inspect the object only if it exists.

### Metadata
- Reproducible: yes
- Related Files: none

---

## [ERR-20261009-060] Invalid migration view range

**Logged**: 2026-10-09T03:53:00-07:00
**Priority**: low
**Status**: resolved
**Area**: docs

### Summary
A file view requested a line range beyond the end of the migration file.

### Error
```text
Invalid view_range: start line (330) is beyond the end of the file. The file has 280 lines.
```

### Context
- Operation: `view` on `Migrations/015_maf_execution_evidence.sql`.
- The migration was already read through line 270; the requested 330-470 range was not needed.

### Suggested Fix
Check a file's reported length or use a range bounded by the known remaining lines.

### Metadata
- Reproducible: yes
- Related Files: services/orchestrator/Agentweaver.Orchestrator/Migrations/015_maf_execution_evidence.sql
- Recurrence: 2026-10-09T10:47:00-07:00 — a second `view` requested line 130 of a 102-line file; read the valid 1–102 range instead.

### Resolution
- **Resolved**: 2026-10-09T10:47:00-07:00
- **Notes**: Bounded the second view to the actual file length and continued with targeted search for the missing claim helper.

---

## [ERR-20261009-061] Partial compatibility wrapper scope

**Logged**: 2026-10-09T04:00:00-07:00
**Priority**: medium
**Status**: resolved
**Area**: backend

### Summary
A compatibility partial class was inserted inside the checkpoint wrapper class instead of at namespace scope.

### Error
```text
MafExecutionCheckpointStore.cs(56,20): error CS1501: No overload for method 'AppendOutputWitnessInTransactionAsync' takes 13 arguments
MafExecutionCheckpointStore.cs(86,20): error CS1501: No overload for method 'ReadOutputWitnessInTransactionAsync' takes 12 arguments
```

### Context
- Command: `dotnet build services\orchestrator\Agentweaver.Orchestrator\Agentweaver.Orchestrator.csproj --no-restore --configuration Release`.
- The new partial declaration was meant to be namespace-level but was nested in `MafExecutionCheckpointStore`, so it could not see the generic overloads on the actual output-witness store.

### Suggested Fix
Confirm the enclosing class boundary before inserting a partial declaration; keep the compatibility wrapper at namespace scope.

### Metadata
- Reproducible: yes
- Related Files: services/orchestrator/Agentweaver.Orchestrator/MafExecutionCheckpointStore.cs

### Resolution
- **Resolved**: 2026-10-09T04:00:24-07:00
- **Notes**: Moved the compatibility partial after `MafExecutionCheckpointStore` closes; rerun the focused build.

---

## [ERR-20261009-062] Workspace changes overview merge-base failure

**Logged**: 2026-10-09T07:05:00-07:00
**Priority**: low
**Status**: pending
**Area**: config

### Summary
The changes-overview tool could not compute a merge base for the current worktree.

### Error
```text
git merge-base a540ba511c9b75c80a4d081f385365bc252d6e1c dd20ce67adf919c77e8f49e19a2fcdc5b24fad2c failed
```

### Context
- Operation: `get_changes_overview` in the current worktree.
- `git status` and `git diff --check` work; inspecting the required files directly is an available fallback.

### Suggested Fix
Repair or refresh the workspace's configured base/branch references before using the changes-overview tool.

### Metadata
- Reproducible: unknown
- Related Files: none

---

## [ERR-20261009-063] SDK 1.0.18 session API compatibility

**Logged**: 2026-10-09T10:18:44-07:00
**Priority**: medium
**Status**: resolved
**Area**: backend

### Summary
The SDK 1.0.18 upgrade changed a session API overload used by the runtime adapter.

### Error
```text
RuntimeCopilotSessionFactory.cs(129,72): error CS1503: Argument 2: cannot convert from
'System.Threading.CancellationToken' to 'string?'
```

### Context
- Command: `dotnet test tests/Agentweaver.Identity.Broker.Tests/Agentweaver.Identity.Broker.Tests.csproj --no-restore --configuration Release --filter "FullyQualifiedName~NativeRuntimeManifestTests|FullyQualifiedName~RuntimeCopilotToolCallAuthorizationTests" --nologo`
- Restore succeeded with `GitHub.Copilot.SDK` 1.0.18. Compilation stopped at the changed session API before running filtered tests.

### Suggested Fix
Inspect the SDK 1.0.18 API signature and make the smallest compatible change in the assigned runtime adapter, then rerun the focused build and tests.

### Metadata
- Reproducible: yes
- Related Files: packages/Agentweaver.AgentRuntime/RuntimeCopilotSessionFactory.cs

### Resolution
- **Resolved**: 2026-10-09T10:25:00-07:00
- **Notes**: Passed `null` for the new optional `selectionId` and preserved the credential as the named `gitHubToken`; focused manifest and authorization tests then passed.

---

## [ERR-20261009-064] Controlled SDK runtime rejects session detach

**Logged**: 2026-10-09T10:25:00-07:00
**Priority**: medium
**Status**: resolved
**Area**: tests

### Summary
SDK 1.0.18 session disposal sends a `session.detach` RPC that the controlled runtime test fixture does not accept.

### Error
```text
System.InvalidOperationException: Unexpected actual SDK method: session.detach
```

### Context
- Command: `dotnet test tests/Agentweaver.Identity.Broker.Tests/Agentweaver.Identity.Broker.Tests.csproj --no-restore --configuration Release --filter "FullyQualifiedName~RuntimeCopilotSessionTests" --nologo`
- The test assembly built successfully, then 15 tests failed while the controlled server handled session cleanup; 11 passed.

### Suggested Fix
Inspect SDK 1.0.18 detach semantics and update the controlled test transport to model the harmless lifecycle acknowledgment, without changing production session disposal or unrelated RPC behavior.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/ControlledCopilotRuntime.cs

### Resolution
- **Resolved**: 2026-10-09T10:30:00-07:00
- **Notes**: The controlled transport now acknowledges `session.detach` for the current session, and lifecycle assertions match SDK 1.0.18's documented preserve-on-dispose behavior. The focused set passed 48/48.

---

## [ERR-20261009-065] SDK baseline lock patch anchor

**Logged**: 2026-10-09T10:41:00-07:00
**Priority**: low
**Status**: pending
**Area**: config

### Summary
The minimal SDK patch generator stopped because an exact NuGet lock-file replacement anchor did not match the published baseline.

### Error
```text
packages/Agentweaver.AgentRuntime/packages.lock.json: expected 1 occurrences, found 0
```

### Context
- A read-only Python script compared and transformed bytes from published commit `0cbc6f14980bd46abebc34ea52a6bd88f45a59b3`.
- The first assumed whitespace/layout for the SDK entry was wrong; the script halted before emitting any patch or changing repository files.

### Suggested Fix
Inspect the exact baseline lock-file block and generate focused JSON field replacements that preserve all unrelated dependency entries.

### Metadata
- Reproducible: yes
- Related Files: packages/Agentweaver.AgentRuntime/packages.lock.json

---

## [ERR-20261009-066] SDK patch blob hash input mode

**Logged**: 2026-10-09T10:43:00-07:00
**Priority**: low
**Status**: pending
**Area**: config

### Summary
The read-only SDK patch generator supplied bytes to a text-mode subprocess.

### Error
```text
TypeError: write() argument must be str, not bytes
```

### Context
- `git hash-object --stdin` was invoked with `text=True` while receiving bytes from the proposed lock/config content.
- The worker thread stopped on the type error; the focused patch file had not been emitted and repository source was unchanged.

### Suggested Fix
Keep the hash subprocess binary-mode and decode only its returned object ID.

### Metadata
- Reproducible: yes
- Related Files: none

---

## [ERR-20261009-068] Ripgrep shell command unavailable

**Logged**: 2026-10-09T10:48:00-07:00
**Priority**: low
**Status**: pending
**Area**: config

### Summary
A PowerShell command could not find the `rg` executable.

### Error
```text
The term 'rg' is not recognized as a name of a cmdlet, function, script file, or executable program.
```

### Context
- `git apply --stat` succeeded for the generated patch, but the subsequent shell `rg` scope check failed.
- Ripgrep is available through the dedicated `functions.rg` tool; no source file was changed by the failed shell command.

### Suggested Fix
Use the dedicated ripgrep tool for repository searches, or inspect generated temporary patch files with `view`.

### Metadata
- Reproducible: yes
- Related Files: none

---

## [ERR-20261009-069] Ambiguous abbreviated Git commit

**Logged**: 2026-10-09T11:27:24-07:00
**Priority**: low
**Status**: pending
**Area**: config

### Summary
Git rejected the abbreviated `0cbc` object prefix because it matched both a commit and a blob.

### Error
```text
error: short object ID 0cbc is ambiguous
```

### Context
- Used `0cbc` in a diff command after resolving it as the immutable baseline commit.
- The command failed before producing diff output or changing repository files.

### Suggested Fix
Use the full verified commit ID `0cbc6f14980bd46abebc34ea52a6bd88f45a59b3` in subsequent Git commands.

### Metadata
- Reproducible: yes
- Related Files: none

---

## [ERR-20261010-070] Bounded patch inspection command arguments

**Logged**: 2026-10-10T05:25:00-07:00
**Priority**: low
**Status**: pending
**Area**: config

### Summary
Three inspection commands failed because of invalid path or range arguments.

### Error
```text
MSBUILD : error MSB1009: Project file does not exist.
InvalidOperation: The regular expression pattern \ is not valid.
Invalid view_range: start line (-40) must be greater than or equal to 1.
```

### Context
- The project path omitted the `orchestrator` directory.
- A PowerShell `-replace` expression did not escape its backslash.
- `view_range` does not accept a negative start line.
- None of these failures changed source files.

### Suggested Fix
Use the complete project path, `String.Replace` for literal separators, and positive line numbers with `view`.

### Metadata
- Reproducible: yes
- Related Files: none

---

## [ERR-20261010-071] Duplicate unified-diff headers

**Logged**: 2026-10-10T05:28:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The generated source patch contained duplicate file headers and failed Git's reverse-check.

### Error
```text
error: git apply: bad git-diff - inconsistent old filename on line 2
```

### Context
- A manually added `---`/`+++` pair was followed by empty headers from `difflib.unified_diff`.
- The first patch was rejected without modifying files.

### Suggested Fix
Pass explicit `fromfile` and `tofile` values to `unified_diff` and omit manual file headers.

### Metadata
- Reproducible: yes
- Related Files: temporary MAF-only patch

### Resolution
- **Resolved**: 2026-10-10T05:28:00-07:00
- **Notes**: Regenerated the patch and `git apply --check --reverse` passed.

---

## [ERR-20261010-072] temporary provenance export quoting and digest

**Logged**: 2026-10-10T09:34:00-07:00
**Priority**: low
**Status**: resolved
**Area**: config

### Summary
PowerShell export generation first failed on an apostrophe in a single-quoted string, then a normalized excerpt did not match the recorded digest.

### Error
```text
ParserError: Unexpected token 's' in expression or statement.
Excerpt digest mismatch: checkpoint=d7ee1158204717b95d3d2512f8e466de0170862d74e968a2e6d1ca9b894accf2 witness=48c0bb32710cde3c30a4c31e98624bbbbe8990c54d55b75b9822d05b34d62f56
```

### Context
- Attempted to recreate a temporary provenance note from line excerpts using PowerShell single-quoted array entries.
- A possessive apostrophe terminated one string; after correcting it, hashing LF-normalized lines did not reproduce the recorded digest.
- No source files were modified by either failed command.

### Suggested Fix
Retrieve the successful extraction logic from session history, or hash the exact source bytes rather than a reconstructed line-normalized excerpt.

### Metadata
- Reproducible: yes
- Related Files: temporary `%TEMP%\\maf-held-isboundto-context.txt`

### Resolution
- **Resolved**: 2026-10-10T09:35:00-07:00
- **Notes**: Recreated the report with a literal here-string and hashed the exact LF-joined excerpt without a trailing newline. Both excerpt hashes matched the recorded values; the final report hash was verified.

---
