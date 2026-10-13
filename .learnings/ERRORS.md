# Errors

## [ERR-20261013-013] moved-authority-helper-reference

**Logged**: 2026-10-13T04:21:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The Broker test build found one consent path still calling the moved authority helper.

### Error
```text
IdentityBrokerEndpoints.cs(228,13): error CS0103: The name 'UsesProjectsAuthority' does not exist in the current context
```

### Context
- `UsesProjectsAuthority` moved from `IdentityBrokerEndpoints` to `BrokerRunBindingAuthority`.
- The authorize path was updated, but the consent-resume path was not.

### Suggested Fix
Update all callers when moving a shared helper, then compile the full Broker test project.

### Metadata
- Reproducible: yes
- Related Files: services/identity/Agentweaver.Identity.Broker/IdentityBrokerEndpoints.cs

---

## [ERR-20261013-012] fake-owner-response-uri

**Logged**: 2026-10-13T04:15:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The fake Projects owner response omitted its originating request URI.

### Error
```text
QueryUsesTypedShortLivedTicketAndAcceptsValidOwnerReceipt expected Authorized but received Unavailable.
```

### Context
- The Broker correctly treats a response without the exact expected request URI as unavailable.
- The fake HTTP handler did not set `HttpResponseMessage.RequestMessage`.

### Suggested Fix
Set the originating request message on fake owner responses so controlled tests preserve the real transport contract.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/BrokerRunBindingAuthorityTests.cs

---

## [ERR-20261013-011] test-clock-mismatch

**Logged**: 2026-10-13T04:10:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The validation event handled only JwtSecurityToken, but JwtBearer supplied JsonWebToken.

### Error
```text
AcceptsPrivateQueryTokenWithBoundedTimeProfile expected OK but received Unauthorized.
```

### Context
- JwtBearer failed the otherwise valid request inside the custom token-validated event.
- Directly checking the fixed-time payload profile passed, isolating the failure to the middleware security-token type.

### Suggested Fix
Handle the JsonWebToken type used by the configured JwtBearer middleware and keep coverage through the middleware itself.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Projects.Config.Tests/BrokerRunBindingTokenValidationTests.cs

---

## [ERR-20261013-010] test-result-namespace-import

**Logged**: 2026-10-13T04:08:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The middleware test's minimal endpoint lacked the ASP.NET Core HTTP namespace import.

### Error
```text
CS0103: The name 'Results' does not exist in the current context.
```

### Context
- The focused test uses `Results.Ok()` in a minimal route.
- The test project does not provide the namespace as a global using.

### Suggested Fix
Import Microsoft.AspNetCore.Http in the test file.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Projects.Config.Tests/BrokerRunBindingTokenValidationTests.cs

---

## [ERR-20261013-009] test-middleware-import

**Logged**: 2026-10-13T04:07:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The test host did not compile because the ASP.NET middleware extension namespace was missing.

### Error
```text
CS1061: IApplicationBuilder has no UseRouting, UseAuthentication, UseAuthorization, or UseEndpoints method.
```

### Context
- The focused middleware test uses a TestServer pipeline without WebApplication.
- Its extension methods require the ASP.NET Core Builder namespace.

### Suggested Fix
Import Microsoft.AspNetCore.Builder in the TestServer test file.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Projects.Config.Tests/BrokerRunBindingTokenValidationTests.cs

---

## [ERR-20261013-008] test-namespace-imports

**Logged**: 2026-10-13T04:06:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The new test file was missing xUnit and hosting namespace imports.

### Error
```text
CS0246: IAsyncLifetime, IHost, Fact, and Theory could not be found.
```

### Context
- The focused test compile identified missing explicit using directives in the new test file.
- Existing projects do not enable global xUnit or hosting usings.

### Suggested Fix
Include the specific test and hosting namespace imports in each new test source file.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Projects.Config.Tests/BrokerRunBindingTokenValidationTests.cs

---

## [ERR-20261013-007] jwt-constructor-overload-mismatch

**Logged**: 2026-10-13T04:04:00-07:00
**Priority**: low
**Status**: resolved
**Area**: backend

### Summary
The installed JWT library does not provide the attempted constructor overload with an explicit issued-at argument.

### Error
```text
CS1503: cannot convert from 'System.DateTime' to 'string'
```

### Context
- The private token needs an explicit `iat` in addition to `nbf` and `exp`.
- The constructor overload was not supported by the repository's installed IdentityModel version.

### Suggested Fix
Add the issued-at value as an integer64 JWT claim and keep the supported not-before/expiry constructor overload.

### Metadata
- Reproducible: yes
- Related Files: services/identity/Agentweaver.Identity.Broker/BrokerRunBindingAuthority.cs

---

## [ERR-20261013-006] missing-nuget-assets

**Logged**: 2026-10-13T04:02:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The Broker source and test builds initially lacked NuGet assets files.

### Error
```text
NETSDK1004: Assets file .../project.assets.json not found.
```

### Context
- Focused `--no-restore` builds found absent assets for the Broker source and Broker test projects.
- Projects & Config built successfully; restoring the affected projects allowed builds and tests.

### Suggested Fix
Restore each affected project after a build reports missing assets, then repeat the focused build or test.

### Metadata
- Reproducible: yes
- Related Files: services/identity/Agentweaver.Identity.Broker/Agentweaver.Identity.Broker.csproj

---

## [ERR-20261013-005] patch-context-mismatch

**Logged**: 2026-10-13T04:01:00-07:00
**Priority**: low
**Status**: resolved
**Area**: backend

### Summary
A source patch did not apply because its context did not match the current file.

### Error
```text
Failed to find expected lines in Program.cs.
```

### Context
- The patch assumed a different ordering of existing using directives and URI configuration code.
- A focused file read established the current source context before retrying.

### Suggested Fix
Read the exact target range before preparing a context-sensitive patch.

### Metadata
- Reproducible: yes
- Related Files: services/projects-config/Agentweaver.Projects.Config/Program.cs

---

## [ERR-20261013-004] helper-path-mismatch

**Logged**: 2026-10-13T03:58:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
A helper source file was inspected at an assumed location instead of being located first.

### Error
```text
Path services/identity/Agentweaver.Identity.Broker/RuntimeOwnerHttpTransport.cs does not exist.
```

### Context
- A direct file read assumed the shared transport helper was in the Broker project.
- A repository search had already found callers but not the helper definition.

### Suggested Fix
Use repository-scoped symbol search to locate shared helper definitions before opening a guessed path.

### Metadata
- Reproducible: yes
- Related Files: services/identity/Agentweaver.Identity.Broker/BrokerRunBindingAuthority.cs

---

## [ERR-20261013-003] targeted-test-path-mismatch

**Logged**: 2026-10-13T03:40:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
An initial targeted test search used production-folder paths for test files.

### Error
```text
Search paths do not exist: services\identity\Agentweaver.Identity.Broker.Tests\IdentityBrokerEndpointTests.cs, services\projects-config\Agentweaver.Projects.Config.Tests\ProjectsConfigBrokerAuthorizationTests.cs
```

### Context
- The tests are under `tests\Agentweaver.Identity.Broker.Tests`.
- A follow-up glob located the correct files.

### Suggested Fix
Locate test files with a repo-scoped glob before targeted searches when their project path is uncertain.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/IdentityBrokerEndpointTests.cs

---

## [ERR-20261013-002] native-probe-failure-exit-code

**Logged**: 2026-10-13T01:27:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
A native probe failure changed full P1 status but left the aggregate exit code successful.

### Error
```text
Expected exitCode 1; received 0.
```

### Context
- The new controlled aggregation test supplied a failed required native probe.
- Full P1 status correctly became failed, but exit code still depended on surface status.

### Suggested Fix
Derive the aggregate exit code from `fullP1Status`.

### Metadata
- Reproducible: yes
- Related Files: scripts/harness-shared/p1-acceptance-result.mjs, scripts/harness-shared/test/p1-acceptance-result.test.mjs

### Resolution
- **Resolved**: 2026-10-13T01:27:00-07:00
- **Notes**: Exit code now returns 1 whenever full P1 status is failed.

---

## [ERR-20261013-001] stale-multifile-patch-context

**Logged**: 2026-10-13T01:26:00-07:00
**Priority**: low
**Status**: resolved
**Area**: docs

### Summary
A combined source-and-docs patch stopped when one documentation context no longer matched.

### Error
```text
Failed to find expected lines in docs/guide/testing.md; remaining hunks were not attempted.
```

### Context
- The first two file hunks were already applied before the documentation hunk failed.
- No documentation hunks were applied.

### Suggested Fix
After a partial patch failure, inspect the applied files and use smaller patches against current exact text.

### Metadata
- Reproducible: yes
- Related Files: scripts/harness-shared/run-p1.mjs, scripts/harness-shared/test/run-p1.test.mjs
- Recurrence-Count: 5
- Last-Seen: 2026-10-13

### Resolution
- **Resolved**: 2026-10-13T02:02:00-07:00
- **Notes**: Inspected partial applications, used smaller file-specific hunks, and verified the final documentation with the required build/link/diagram checks.

---

## [ERR-20261010-004] oauth-drift-regression-fixture

**Logged**: 2026-10-10T09:35:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The new callback provider-drift test fixture omitted the secret redemption fake.

### Error
```text
Expected remote_mcp_oauth_provider_unapproved; received remote_mcp_secret_store_unavailable.
```

### Context
- The callback path requires both the secret writer and redemption interfaces before provider validation.
- The test was intended to prove provider configuration drift fails before metadata requests or secret redemption.

### Suggested Fix
Pass the controlled secret fake as both writer and redeemer when constructing the callback service.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/RemoteMcpOAuthConsentPreparationTests.cs

### Resolution
- **Resolved**: 2026-10-10T09:36:00-07:00
- **Notes**: The callback fixture now supplies the existing fake for redemption; all nine drift regressions pass.

## [ERR-20261010-003] remote-mcp-secret-failure-test-expectations

**Logged**: 2026-10-10T09:05:00-07:00
**Priority**: medium
**Status**: resolved
**Area**: tests

### Summary
OAuth failure/CAS tests exposed stale expectations and an unmapped provider timeout.

### Error
```text
Secret-write failure expected credential revision +1 but persisted +2.
Refresh authority-loss expected RuntimeAuthorizationException but received remote_mcp_owner_denied.
Read-only stale-refresh recovery was denied by a fixture that granted only one permission at a time.
Refresh response loss expected a 503 management error but received TaskCanceledException.
```

### Context
- Ran the focused secret-write tests, durable refresh CAS/authority tests, then the full OAuth Broker DB test class.
- Consent start and terminal failure each advance the credential revision.
- The management service maps owner-denied responses to RemoteMcpOAuthManagementException (403).
- Provider-only cancellation persisted the indeterminate state but was rethrown without the expected management error.

### Suggested Fix
Keep the persisted CAS outcomes, correct the fixture and public error expectations, and map provider timeout to an explicit unavailable response while preserving caller cancellation.

### Metadata
- Reproducible: yes
- Related Files: tests/Agentweaver.Identity.Broker.Tests/RemoteMcpOAuthConsentPreparationTests.cs

### Resolution
- **Resolved**: 2026-10-10T09:11:00-07:00
- **Notes**: Added callback and refresh secret-write persistence coverage, corrected permission/error expectations, and mapped provider-only refresh timeout. The full OAuth Broker DB test class passed 20/20.

## [ERR-20261010-002] guessed-source-file-path

**Logged**: 2026-10-10T08:51:00-07:00
**Priority**: low
**Status**: resolved
**Area**: backend

### Summary
A file read used an assumed Broker filename that does not exist.

### Error
```text
Path services/identity/Agentweaver.Identity.Broker/RuntimeModelCredentialEndpoints.cs does not exist.
```

### Context
- The feature is implemented in the existing `RuntimeCredentialEndpoints.cs`.
- A targeted file search found the actual endpoint file.

### Suggested Fix
Locate source files by symbol search or glob before reading an assumed path.

### Metadata
- Reproducible: no
- Related Files: services/identity/Agentweaver.Identity.Broker/RuntimeCredentialEndpoints.cs, services/projects-config/Agentweaver.Projects.Config/ProjectAuthorization.cs
- Recurrence-Count: 2
- Last-Seen: 2026-10-13

### Resolution
- **Resolved**: 2026-10-13T02:02:00-07:00
- **Notes**: Symbol search located the authorization owner and scope constants in ProjectAuthorization.cs.

---

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

## [ERR-20261010-001] web-test-dependency-restore

**Logged**: 2026-10-10T15:23:00Z
**Priority**: low
**Status**: pending
**Area**: tests

### Summary
The focused Web test command could not load Vitest because `apps/web` dependencies were not installed.

### Error
```
Could not resolve 'vitest/config' or '@vitejs/plugin-react'; package 'vitest' was not found.
```

### Context
- Ran focused Vitest tests from `apps/web`.
- Then ran `npm ci` from the repository root instead of `apps/web`; it failed because the lockfile is in `apps/web/package-lock.json`.
- No package manifest or lockfile was changed.

### Suggested Fix
Run `npm ci` from `apps/web`, then rerun the focused Web tests and typecheck.

### Metadata
- Reproducible: yes
- Related Files: apps/web/package.json, apps/web/package-lock.json
- Tags: web, vitest, dependency-restore

---

## [ERR-20261013-003] p1-cli-output-path-validation-test

**Logged**: 2026-10-13T01:41:00-07:00
**Priority**: medium
**Status**: resolved
**Area**: tests

### Summary
The P1 CLI stopped writing safe blocked evidence when the input manifest failed validation.

### Error
```
ENOENT: no such file or directory, open '<temporary-directory>\evidence.jsonl'
```

### Context
- Moved output-path assignment after full manifest validation to prevent collision errors from overwriting protected input artifacts.
- The existing non-execute-input test then found no evidence output, although that destination did not collide with a protected file.

### Suggested Fix
Retain the requested path after the exact input collision check, then clear it before rejecting any collision with a deployment artifact.

### Metadata
- Reproducible: yes
- Related Files: scripts/harness-shared/run-p1.mjs, scripts/harness-shared/test/run-p1.test.mjs

### Resolution
- **Resolved**: 2026-10-13T01:43:00-07:00
- **Notes**: Retained safe output destinations after input-collision validation, cleared artifact collisions, and reran focused tests successfully.

---

## [ERR-20261013-004] dependency-merge-unrelated-ancestry

**Logged**: 2026-10-13T02:22:21-07:00
**Priority**: medium
**Status**: pending
**Area**: config

### Summary
The approved dependency commit's ancestry included unrelated branch changes and caused a conflict during integration.

### Error
```
CONFLICT (content): Merge conflict in .learnings/ERRORS.md
```

### Context
- Started an ordinary no-fast-forward merge of the approved registry helper commit from a clean worktree.
- Git also auto-merged `docs/guide/testing.md` and `package.json`, showing the source commit's ancestry carried unrelated changes.
- Aborted the merge without resolving files; the original branch head and clean worktree were restored.

### Suggested Fix
Before merging a dependency commit, inspect the full merge-base-to-commit diff, not only the commit's own file list. If unrelated ownership areas are included, stop and ask for an isolated approved source bundle or branch.

### Metadata
- Reproducible: yes
- Related Files: scripts/release/registry.mjs, scripts/release/tests/registry.test.mjs, .learnings/ERRORS.md
- Tags: git, merge, branch-ownership

---

## [ERR-20261013-005] projects-config-build-assets-missing

**Logged**: 2026-10-13T02:46:00-07:00
**Priority**: low
**Status**: pending
**Area**: tests

### Summary
The focused Projects Config build could not start because its NuGet assets file was absent.

### Error
```
NETSDK1004: Assets file '...\services\projects-config\Agentweaver.Projects.Config\obj\project.assets.json' not found. Run a NuGet package restore to generate this file.
```

### Context
- Ran `dotnet build services\projects-config\Agentweaver.Projects.Config\Agentweaver.Projects.Config.csproj --no-restore --verbosity quiet`.
- The worktree has no generated `obj\project.assets.json` for this project.

### Suggested Fix
Restore the existing locked dependencies, then rerun the focused build.

### Metadata
- Reproducible: yes
- Related Files: services/projects-config/Agentweaver.Projects.Config/Agentweaver.Projects.Config.csproj
- Tags: dotnet, restore, build

---

## [ERR-20261013-006] source-review-redacted-context

**Logged**: 2026-10-13T03:22:00-07:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
A source edit failed because the displayed review context was masked and did not match the file.

### Error
```
Failed to find expected lines in PrivilegedProjectSetupCommand.cs.
```

### Context
- A view displayed the authorization-header expression as masked text.
- A patch based on that display did not apply; no source edit was made.
- Two follow-up inspection requests also used an invalid source path and a negative line number.
- The first documentation replacement also used a wider context than the exact file lines; a narrower patch succeeded.

### Suggested Fix
Do not edit source using masked display text. Confirm the actual changed line with a safe, non-sensitive code view before applying a patch.

### Metadata
- Reproducible: yes
- Related Files: services/projects-config/Agentweaver.Projects.Config/PrivilegedProjectSetupCommand.cs
- Tags: source-view, patch-context

### Resolution
- **Resolved**: 2026-10-13T03:22:00-07:00
- **Notes**: Re-read source and documentation ranges, confirmed no source edit was made, then applied the documentation change with exact context.

---
