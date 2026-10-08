# Errors

## [ERR-20261007-001] dotnet-test-no-restore

**Logged**: 2026-10-07T18:52:52-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The focused Identity test command could not start because that test project's NuGet assets had not been restored.

### Error
```text
error NETSDK1004: Assets file '...tests\Agentweaver.Identity.Tests\obj\project.assets.json' not found. Run a NuGet package restore to generate this file.
```

### Context
- Command: `dotnet test tests\Agentweaver.Identity.Tests\Agentweaver.Identity.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~RuntimeContractTests"`
- The Environment test project had restored assets, but the Identity test project had not been built/restored in this worktree.

### Suggested Fix
Rerun the focused test without `--no-restore`; restore only the missing test-project assets.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Tests/Agentweaver.Identity.Tests.csproj

### Resolution
- **Resolved**: 2026-10-07T18:52:52-07:00
- **Notes**: Retry with normal restore enabled.

---

## [ERR-20261007-004] model-grant-contract-build

**Logged**: 2026-10-07T19:19:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The focused runtime credential integration build failed because its new shared receipt contract uses `SecretRef` without importing its abstractions namespace.

### Error
```text
RuntimeBootstrapHttpContracts.cs(49,5): error CS0246: The type or namespace name 'SecretRef' could not be found
```

### Context
- Command: focused `Agentweaver.Identity.Broker.Tests` model-session grant integration test.
- Failure occurred during compilation before the test started.

### Suggested Fix
Import `Agentweaver.Abstractions` in the shared runtime HTTP contract file, then rerun the focused test.

### Metadata
- Reproducible: yes
- Related Files: packages/Agentweaver.Identity/RuntimeBootstrapHttpContracts.cs

### Resolution
- **Resolved**: 2026-10-07T19:20:00-07:00
- **Notes**: Added the missing `Agentweaver.Abstractions` import; the focused endpoint integration test passed.

---

## [ERR-20261007-005] invalid-view-range

**Logged**: 2026-10-07T19:21:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
A source read requested lines beyond the end of the test file.

### Error
```text
Invalid view_range: start line (400) is beyond the end of the file. The file has 384 lines.
```

### Context
- Read-only inspection of `RuntimeCredentialEndpointTests.cs`.
- No source files changed; the file was re-read with a valid range.

### Suggested Fix
Use an in-range read after checking the reported file length.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/RuntimeCredentialEndpointTests.cs

### Resolution
- **Resolved**: 2026-10-07T19:21:00-07:00
- **Notes**: Re-read the requested section within lines 335–384.

---

## [ERR-20261007-006] runtime-bootstrap-receiver-build

**Logged**: 2026-10-07T19:23:00-07:00
**Priority**: low
**Status**: resolved
**Area**: backend

### Summary
Changing runtime bootstrap to redeem the model credential internally left its forwarding receiver calling the removed six-argument overload.

### Error
```text
RuntimeBootstrapReceiver.cs(111,44): error CS1501: No overload for method 'ConfigureAsync' takes 6 arguments
```

### Context
- Command: focused runtime credential endpoint integration test.
- Compilation stopped before the test started.

### Suggested Fix
Update the receiver to the five-argument `RuntimeSessionBootstrap.ConfigureAsync` contract.

### Metadata
- Reproducible: yes
- Related Files: packages/Agentweaver.AgentRuntime/RuntimeBootstrapReceiver.cs

### Resolution
- **Resolved**: 2026-10-07T19:27:00-07:00
- **Notes**: Updated the receiver and callers to use the new internal model-credential redemption path.

---

## [ERR-20261007-007] model-credential-test-expectation

**Logged**: 2026-10-07T19:25:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The focused runtime integration test now reaches the SDK with the broker-redeemed model credential, while the controlled SDK fixture still expects the old injected test credential.

### Error
```text
Expected: "external-sdk-credential"
Actual:   "sdk-model-token"
```

### Context
- Command: focused `RuntimeCredentialEndpointTests` integration test.
- The model credential redemption path succeeded; the SDK test double rejected the newly correct credential value.

### Suggested Fix
Update the controlled SDK test setup to expect the test backend's redeemed credential.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/ControlledCopilotRuntime.cs

### Resolution
- **Resolved**: 2026-10-07T19:28:00-07:00
- **Notes**: Made the controlled SDK credential expectation configurable and set it to the broker-redeemed test credential; the focused integration test passed.

---

## [ERR-20261007-008] sdk-fixture-patch-context

**Logged**: 2026-10-07T19:26:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
A test-double patch used a property ordering that does not match the actual fixture.

### Error
```text
Failed to apply patch: Failed to find expected lines
```

### Context
- Attempted to make the controlled SDK credential expectation configurable.
- The patch was rejected before any files changed.

### Suggested Fix
Read the exact fixture property block and reapply against its current source.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/ControlledCopilotRuntime.cs

### Resolution
- **Resolved**: 2026-10-07T19:27:00-07:00
- **Notes**: Re-read the exact fixture declarations and applied the targeted update; the focused integration test passed.

---

## [ERR-20261007-002] runtime-owner-context-build

**Logged**: 2026-10-07T18:52:52-07:00
**Priority**: low
**Status**: resolved
**Area**: backend

### Summary
The runtime-owner integration build missed a `System.Text.Json` import for the newly added accepted-snapshot parser.

### Error
```text
RuntimeOwnerContextEndpoints.cs(83,60): error CS0246: The type or namespace name 'JsonElement' could not be found
```

### Context
- Command: focused `Agentweaver.Identity.Broker.Tests` runtime-owner integration test.
- The build stopped before executing tests.

### Suggested Fix
Import `System.Text.Json` where `JsonElement` is used.

### Metadata
- Reproducible: yes
- Related Files: services/orchestrator/Agentweaver.Orchestrator/RuntimeOwnerContextEndpoints.cs

### Resolution
- **Resolved**: 2026-10-07T18:52:52-07:00
- **Notes**: Added the missing using directive.

---

## [ERR-20261007-003] patch-context-mismatch

**Logged**: 2026-10-07T19:18:00-07:00
**Priority**: low
**Status**: resolved
**Area**: config

### Summary
A source patch was rejected because its expected context repeated an enum declaration.

### Error
```text
Failed to apply patch: Failed to find expected lines
```

### Context
- A patch targeting `RuntimeCredentialContracts.cs` included a duplicated enum in its context.
- No repository files were changed by the rejected patch.

### Suggested Fix
Re-read the exact target block before applying a patch.

### Metadata
- Reproducible: yes
- Related Files: packages/Agentweaver.Identity/RuntimeCredentialContracts.cs

### Resolution
- **Resolved**: 2026-10-07T19:18:00-07:00
- **Notes**: Corrected the patch context before retrying.

---
