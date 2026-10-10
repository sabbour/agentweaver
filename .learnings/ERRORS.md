# Errors

## [ERR-20261009-001] podman-smoke-powershell-helper

**Logged**: 2026-10-09T00:36:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The diagnostic smoke wrapper shadowed PowerShell's automatic `$args` variable, so `dotnet publish` ran without arguments.

### Error
```text
Usage: dotnet [path-to-application]
Exception: dotnet failed (-2147450751)
```

### Context
- A temporary PowerShell helper named its argument array `$args`.
- Renaming the parameter to `$Arguments` avoids collision with PowerShell's automatic variable.

### Suggested Fix
Use non-reserved names for PowerShell helper parameters.

### Metadata
- Reproducible: yes
- Related Files: none

---

## [ERR-20261009-002] vitest-worker-startup

**Logged**: 2026-10-09T00:45:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The targeted Vitest run timed out before its forked worker started while an independent .NET test-project build was running.

### Error
```text
[vitest-pool-runner]: Timeout waiting for worker to respond
Test Files  no tests
```

### Context
- Ran `vitest run src/v1/AuthContext.test.tsx --config vitest.config.ts --maxWorkers=1` in parallel with the Broker test-project build.
- The .NET build completed successfully; Vitest did not execute the test file.

### Suggested Fix
Retry the targeted Vitest command after the build has completed and the host is idle.

### Metadata
- Reproducible: unknown
- Related Files: apps/web/src/v1/AuthContext.test.tsx

### Resolution
- **Resolved**: 2026-10-09T00:47:00-07:00
- **Notes**: The retry succeeded once the concurrent build finished; all 10 AuthContext tests passed.

---

## [ERR-20261008-019] local-amd64-image-smoke-runtime

**Logged**: 2026-10-08T19:30:00-07:00
**Priority**: medium
**Status**: pending
**Area**: infra

### Summary
The documented local production-image callback test requires Docker on Linux amd64, but this Windows host only has Podman on Linux ARM64.

### Error
```text
docker: The term 'docker' is not recognized as a name of a cmdlet, function, script file, or executable program.
```

### Context
- `docker info --format '{{.OSType}}/{{.Architecture}}'`
- Podman reports `linux/arm64`, and the pinned ASP.NET runtime image is cached for `linux/arm64`.

### Suggested Fix
Run the documented Docker amd64 image check in CI; use the cached ARM64 runtime with Podman for a separate final-image smoke test where practical.

### Metadata
- Reproducible: yes
- Related Files: scripts/release/tests/web-container-callback.test.mjs, apps/web/Dockerfile

---

## [ERR-20261008-020] repo-app-browser-callback-used-source-assets

**Logged**: 2026-10-08T19:32:00-07:00
**Priority**: medium
**Status**: in_progress
**Area**: tests

### Summary
The full Broker/Gateway browser journey served untransformed TypeScript from the source tree on the production ASP.NET callback route.

### Error
```text
Repo App browser flow did not complete.
Timeout waiting for the Broker consent prompt; the callback popup remained at /auth/callback.
```

### Context
- `RepoAppConnectionCompletesInBrowserThroughBrokerAndGateway` runs the main application in Vite dev mode, but sends `/auth/callback` to the ASP.NET Web host.
- The test configured that host's web root as `apps/web`, where `index.html` points to raw `/src/main.tsx`; the popup could not load the production React callback relay and notify its opener.

### Suggested Fix
Point the callback host at the built `apps/web/dist` assets, which the v1 CI builds before the .NET integration suite.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/RepoAppBrowserIntegrationTests.cs, apps/web/index.html, apps/web/src/v1/App.tsx

---

## [ERR-20261008-018] web-app-test-expected-owner-arguments

**Logged**: 2026-10-08T19:12:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The v1 web suite exposed stale App test expectations after calls began forwarding the confirmed tenant and idempotency key.

### Error
```text
Test Files 1 failed | 8 passed (9)
Tests 10 failed | 79 passed (89)
```

### Context
- The web package test script runs all `src/v1` tests even when an additional file argument is appended.
- The failed `App.test.tsx` assertions expected omitted tenant selectors and did not account for the Repo App setup signal argument; the application calls supply the confirmed tenant and optional signal.

### Suggested Fix
Update only affected assertions to expect the current selector-aware and idempotent API signatures, then run `App.test.tsx` directly from `apps/web`.

### Metadata
- Reproducible: yes
- Related Files: apps/web/src/v1/App.tsx, apps/web/src/v1/App.test.tsx

### Resolution
- **Resolved**: 2026-10-08T19:21:00-07:00
- **Notes**: Updated App test expectations for the configured tenant selector and optional installation signal; the focused suite passes 34/34.

---

## [ERR-20261008-017] web-focused-vitest-config-path

**Logged**: 2026-10-08T19:12:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The first focused Vitest command resolved its config from the repository root instead of the web package.

### Error
```text
[UNRESOLVED_ENTRY] Cannot resolve entry module vitest.config.ts.
```

### Context
- `npm --prefix apps/web exec -- vitest run src/v1/AuthContext.test.tsx --config vitest.config.ts --maxWorkers=2`
- `npm exec` ran with the repository root as the working directory, while the config is under `apps/web`.

### Suggested Fix
Run Vitest from the web package working directory.

### Metadata
- Reproducible: yes
- Related Files: apps/web/vitest.config.ts, apps/web/src/v1/AuthContext.test.tsx

### Resolution
- **Resolved**: 2026-10-08T19:17:00-07:00
- **Notes**: Running Vitest from `apps/web` passed the isolated AuthContext test file (8/8).

---

## [ERR-20261008-016] repo-app-install-callback-integration-test

**Logged**: 2026-10-08T18:56:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The new Broker installation callback integration scenario returned HTTP 500 instead of the expected redirect.

### Error
```text
GitHubRepoAppOwnerEndpointTests.RepoAppCallbacksRefreshTheCurrentOwnerInstallationList:
Expected Found, actual InternalServerError at the installation callback assertion.
```

### Context
- `dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --configuration Release --filter "FullyQualifiedName~RepoAppCallbacksRefreshTheCurrentOwnerInstallationList"`
- Installation setup form submission and GitHub redirect succeeded; the callback path failed before current-owner status/repository refresh assertions.

### Suggested Fix
Capture the callback's actual failure and correct the test fixture or endpoint path only after identifying the cause.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/GitHubRepoAppOwnerEndpointTests.cs

### Resolution
- **Resolved**: 2026-10-08T18:56:00-07:00
- **Notes**: Replaced the intentionally unused secret-redemption stub with a guarded fake that supplies only the connected owner's access token during enabled Repo App provider flows; the real Broker installation callback integration test passes.

---

## [ERR-20261008-015] gateway-sse-test-missing-query-key

**Logged**: 2026-10-08T18:55:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The new Gateway SSE assertion indexed an optional owner query parameter that was absent from later requests.

### Error
```text
System.Collections.Generic.KeyNotFoundException: The given key 'limit' was not present in the dictionary.
```

### Context
- The targeted `GatewayDelegatesOwnerStatusesReplaysCursorsAndReauthorizesBeforeSseWrite` test includes both live SSE page requests and finite replay requests; replay requests may omit `limit`.
- The new assertion used dictionary indexers while filtering all Events owner requests.

### Suggested Fix
Use `TryGetValue` when filtering optional `limit` and `runId` query parameters.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/GatewayBrokerIntegrationTests.cs

### Resolution
- **Resolved**: 2026-10-08T18:56:00-07:00
- **Notes**: Changed the optional query filters to use `TryGetValue`; the targeted Gateway integration test now passes.

---

## [ERR-20261008-014] gateway-bff-test-unused-install-cookie

**Logged**: 2026-10-08T18:52:18-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The Broker test project build failed because an obsolete Repo App installation cookie constant remained after removing its fake Gateway route assertion.

### Error
```text
GatewayGitHubRepoAppBffTests.cs(35,22): error CS0219: The variable 'installationCookie' is assigned but its value is never used
```

### Context
- `dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --configuration Release --filter "FullyQualifiedName~GatewayDelegatesOwnerStatusesReplaysCursorsAndReauthorizesBeforeSseWrite"`
- The install endpoint assertion and response-cookie fixture were removed, but the now-unused constant was left behind.

### Suggested Fix
Remove the obsolete constant, then rerun the targeted Broker integration test.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/GatewayGitHubRepoAppBffTests.cs

### Resolution
- **Resolved**: 2026-10-08T18:56:00-07:00
- **Notes**: Removed the unreferenced installation-cookie constant; the Broker test project compiled and the targeted Gateway integration test passed.

---

## [ERR-20261008-013] repo-app-browser-test-compile

**Logged**: 2026-10-08T17:25:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The new Repo App browser integration test did not compile because of two missing source-level requirements.

### Error
```text
RepoAppBrowserIntegrationTests.cs(260,22): error CS1061: DbSet<RepoAppConnectionRecord> does not contain a definition for AsNoTracking.
RepoAppBrowserIntegrationTests.cs(228,29): error CS8620: Dictionary<string, string> cannot be used where IDictionary<string, string?> is required.
```

### Context
- The test asserts persisted Broker connection state with EF Core.
- The OAuth callback URL is built with `QueryHelpers.AddQueryString`.

### Suggested Fix
Import the EF Core extension namespace and use a nullable-value dictionary for the callback query.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/RepoAppBrowserIntegrationTests.cs

### Resolution
- **Resolved**: 2026-10-08T18:01:43-07:00
- **Notes**: Added the EF Core namespace and nullable callback-query dictionary; the browser integration test now compiles and passes.

---

## [ERR-20261008-012] broker-mcp-test-filter

**Logged**: 2026-10-08T17:12:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The MCP integration test command selected a class name that does not match its shared partial test class.

### Error
```text
No test matches the given testcase filter `FullyQualifiedName~McpBrokerIntegrationTests`.
```

### Context
- The test file is named `McpBrokerIntegrationTests.cs`, but its methods belong to the partial `ProjectsConfigBrokerAuthorizationTests` class.

### Suggested Fix
Filter by the exact test method name, such as `BrokerTokenTraversesMcpGatewayAndKnowledgeWithCurrentPostgresAuthorization`.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/McpBrokerIntegrationTests.cs

### Resolution
- **Resolved**: 2026-10-08T17:14:00-07:00
- **Notes**: Used the exact test method filter; it passed.

---

## [ERR-20261008-011] invalid-provider-decision-seed

**Logged**: 2026-10-08T17:10:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The provider correctly rejected direct creation of a Decision while preparing a Pending Decision fixture.

### Error
```text
Knowledge record kind, type, content, importance, tags, or length is invalid.
```

### Context
- The test attempted to bypass the API's Decision-creation restriction by seeding `KnowledgeRecordKind.Decision` directly through the provider.
- Provider validation also enforces the restriction.

### Suggested Fix
Create a Decision through the supported Proposal promotion path, then use the supported restore path to reset trust to Pending.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/ProjectsConfigBrokerAuthorizationTests.cs, services/knowledge/Agentweaver.Knowledge/NativePostgresMemoryProvider.cs

### Resolution
- **Resolved**: 2026-10-08T17:10:00-07:00
- **Notes**: The browser fixture will use proposal promotion plus revision restore to obtain a valid Pending Decision.

---

## [ERR-20261008-010] browser-decision-action

**Logged**: 2026-10-08T17:08:00-07:00
**Priority**: medium
**Status**: resolved
**Area**: tests

### Summary
The integrated browser lifecycle could not find the Decision approval action for a seeded Decision.

### Error
```text
Timeout while waiting for the First decision content card's Approve Decision button.
```

### Context
- The browser test uses a helper named `CreateKnowledgeDecisionAsync` before opening the UI.
- The helper may promote a proposal to an already-approved Decision, which is not eligible for another approval action; the promoted Proposal and Decision also share content.

### Suggested Fix
Seed a genuinely Pending Decision for the approval scenario and scope the card locator to that Decision's kind/title.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/ProjectsConfigBrokerAuthorizationTests.cs, apps/web/src/v1/App.tsx

### Resolution
- **Resolved**: 2026-10-08T17:24:00-07:00
- **Notes**: The fixture promotes a Proposal and restores its Decision to Pending; the integrated browser lifecycle approves and supersedes it successfully.

---
## [ERR-20261008-009] browser-restore-notice-timeout

**Logged**: 2026-10-08T17:06:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The browser test timed out waiting for the guessed restore success status.

### Error
```text
Timeout 30000ms exceeded while waiting for the assumed “Updated” restore notice.
```

### Context
- The UI restore handler has a dedicated response status that was not confirmed before adding the exact notice wait.
- Other browser operations use different success messages.

### Suggested Fix
Read the restore handler's actual notice template and enum response status before selecting the wait text.

### Metadata
- Reproducible: yes
- Related Files: apps/web/src/v1/App.tsx, tests/Agentweaver.Identity.Broker.Tests/ProjectsConfigBrokerAuthorizationTests.cs

### Resolution
- **Resolved**: 2026-10-08T17:24:00-07:00
- **Notes**: The browser test now waits for the handler's exact restore notice and passes.

---

## [ERR-20261008-008] browser-restore-assertion

**Logged**: 2026-10-08T17:04:00-07:00
**Priority**: medium
**Status**: resolved
**Area**: tests

### Summary
The browser test observed the original revision text before the asynchronous restore had completed.

### Error
```text
Assert.Equal() Failure: Expected 3; Actual 2
```

### Context
- The restore flow was triggered from the history panel.
- The next provider read still saw revision 2; the text being awaited also exists in historical revision content.

### Suggested Fix
Wait for the exact restore success notice and the updated Memory article before asserting its persisted head.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/ProjectsConfigBrokerAuthorizationTests.cs

### Resolution
- **Resolved**: 2026-10-08T17:24:00-07:00
- **Notes**: The test waits for the exact mutation notice before asserting restored revision 3 and its content.

---

## [ERR-20261008-007] revision-history-text-ambiguity

**Logged**: 2026-10-08T17:02:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The browser lifecycle's page-wide `Revision 1` text locator matched unrelated metadata and Decision options.

### Error
```text
strict mode violation: GetByText("Revision 1") resolved to 5 elements
```

### Context
- The locator ran after opening one Memory's revision history.
- Other record metadata and replacement-Decision option labels also contain “revision 1”.

### Suggested Fix
Scope the revision-heading and restore-button locators to the Memory article whose history is open.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/ProjectsConfigBrokerAuthorizationTests.cs

### Resolution
- **Resolved**: 2026-10-08T17:24:00-07:00
- **Notes**: Revision heading and restore action are scoped to the Memory card; lifecycle test passes.

---

## [ERR-20261008-006] browser-correction-assertion

**Logged**: 2026-10-08T17:00:00-07:00
**Priority**: medium
**Status**: resolved
**Area**: tests

### Summary
The integrated browser lifecycle displayed its correction text before the test observed the seeded record revision update.

### Error
```text
Assert.Equal() Failure: Expected 2; Actual 1
```

### Context
- The test clicked Save correction and waited for visible corrected text.
- A direct provider read still reported the original record at revision 1.

### Suggested Fix
Inspect whether the text locator is a premature match; wait for the exact successful mutation notice or rendered corrected record before asserting persisted state.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/ProjectsConfigBrokerAuthorizationTests.cs, apps/web/src/v1/App.tsx

### Resolution
- **Resolved**: 2026-10-08T17:24:00-07:00
- **Notes**: The test waits for the mutation notice and rendered corrected record before checking persistence.

---

## [ERR-20261008-005] postgres-test-port-collision

**Logged**: 2026-10-08T16:59:00-07:00
**Priority**: medium
**Status**: resolved
**Area**: tests

### Summary
Batching multiple database-backed Broker integration tests caused PostgreSQL fixture port/teardown collisions.

### Error
```text
Failed to connect to 127.0.0.1:34335
Only one usage of each socket address (protocol/network address/port) is normally permitted.
```

### Context
- A test invocation selected three integration tests from the shared Broker test class.
- Failures occurred during fixture initialization or disposal, so the batch did not provide a reliable code result.

### Suggested Fix
Run the heavyweight integration methods individually and avoid overlapping PostgreSQL container lifecycle work.

### Metadata
- Reproducible: unknown
- Related Files: tests/Agentweaver.Identity.Broker.Tests/PostgresContainerFixture.cs

### Resolution
- **Resolved**: 2026-10-08T17:24:00-07:00
- **Notes**: Ran the affected Broker browser and MCP PostgreSQL tests individually; both passed without fixture port collisions.

---

## [ERR-20261008-003] playwright-label-ambiguity

**Logged**: 2026-10-08T16:55:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The integrated Knowledge browser test used a partial label locator that also matched the main landmark.

### Error
```text
strict mode violation: GetByLabel("Content") resolved to 2 elements
```

### Context
- The accessible name `Main content` contains the label text `Content`.
- Playwright correctly rejected the ambiguous locator before the UI lifecycle continued.

### Suggested Fix
Use an exact textbox role/name locator for the Content field.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/ProjectsConfigBrokerAuthorizationTests.cs

### Resolution
- **Resolved**: 2026-10-08T16:55:00-07:00
- **Notes**: Replaced the partial label match with exact textbox name matching.

---

## [ERR-20261008-004] browser-record-assertion

**Logged**: 2026-10-08T16:57:00-07:00
**Priority**: medium
**Status**: resolved
**Area**: tests

### Summary
The integrated browser lifecycle did not find its newly created Memory record in the provider search used by the test.

### Error
```text
Assert.Single() Failure: The collection did not contain any matching items
```

### Context
- Run-bound authorization and two sibling authorization tests passed.
- The browser reached the create flow and observed the content text, but the subsequent exact provider search did not return it.

### Suggested Fix
Inspect the submit/result sequence and test search query before changing UI or persistence behavior.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/ProjectsConfigBrokerAuthorizationTests.cs

### Resolution
- **Resolved**: 2026-10-08T17:24:00-07:00
- **Notes**: The browser test waits for the create notice and card before querying the provider; the exact record is found and the complete lifecycle passes.

---

## [ERR-20261008-001] apply_patch-context

**Logged**: 2026-10-08T16:51:00-07:00
**Priority**: low
**Status**: resolved
**Area**: docs

### Summary
A multi-file patch stopped at the architecture-document hunk after earlier test hunks had applied.

### Error
```text
Failed to find expected lines in docs/architecture/knowledge-memory.md.
```

### Context
- A single patch bundled test, docs, diagram, and changeset edits.
- The tool reported the earlier test hunks were applied and later hunks were skipped.
- I recorded the partial application, inspected the exact documentation context, and reapplied only the remaining docs/diagram/changeset hunks.

### Suggested Fix
Split broad patches at file boundaries when several independent context-sensitive documentation hunks are involved.

### Metadata
- Reproducible: no
- Related Files: docs/architecture/knowledge-memory.md, docs/reference/contracts.md, docs/diagrams/src/flagship/v1-knowledge-memory.json

### Resolution
- **Resolved**: 2026-10-08T16:52:00-07:00
- **Notes**: Applied the remaining documentation hunks individually using inspected contexts.

---

## [ERR-20261008-002] broker-consent-test

**Logged**: 2026-10-08T16:53:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The focused authorization test failed before reaching the browser flow because a second token issuance hit an existing Broker consent redirect.

### Error
```text
Expected a consent_required JSON response but got Found
```

### Context
- Added a second `IssueTokenAsync` call to explicitly assert run-bound Owner permissions in the Projects authorization context.
- The same client and upstream subject had already established consent, so the test driver received a redirect instead of its expected consent-required response.

### Suggested Fix
Avoid a duplicate consent-driven token flow; reuse the run-bound token from the integrated browser journey or add the assertion through an existing test helper path.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/ProjectsConfigBrokerAuthorizationTests.cs

### Resolution
- **Resolved**: 2026-10-08T17:24:00-07:00
- **Notes**: Removed the duplicate consent token issuance and verified Broker consent/redemption through the integrated browser flow.

---
