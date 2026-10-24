# P1 API harness adapter

This candidate reuses the released 0.x HTTP adapter pattern with the v1 Gateway contract.
It reads the live `/openapi/v1.json` document and invokes only discovered `/api/v1` operations.
It does not import the monolith's Oracle or assume retired routes.

## Contract checks

From the repository root:

```powershell
npm run test:harness:api
```

The tests use an actual loopback Node HTTP server with controlled contract and owner responses.
They do not prove a deployed Gateway, real Broker signature validation, native model execution, or AKS acceptance.

## Direct Orchestrator credit-guard read

`lib/runtime-credit-guard-contract.mjs` is a separate static OpenAPI contract for
`POST /internal/runtime/credit-guards/read`; it is not added to or discovered through
the Gateway OpenAPI document. `RuntimeCreditGuardClient` sends that one direct,
read-only owner request, requires an explicit Orchestrator base URL and an injected
actor auth provider bound to that exact origin, and sends `Cache-Control: no-store`.
It does not acquire actor credentials, issue runtime credentials, proxy through the
Gateway, or change dispatch DTOs. A `200` must echo the exact intent; non-`200`
responses such as `404 runtime_credit_guard_unknown` retain their HTTP status and
redacted response evidence rather than becoming a synthetic success.

```javascript
import { RuntimeCreditGuardClient } from './scripts/api-harness/lib/runtime-credit-guard-client.mjs';

const api = new RuntimeCreditGuardClient({
  baseUrl: approvedDirectOrchestratorOrigin,
  authProvider: alreadyAuthenticatedProjectsActor,
});
const evidence = await api.read({ authorization: observeProof, intent });
if (evidence.status !== 200) {
  // Preserve the explicit status and redacted responseBody in evidence.
}
```

The P1 API runner does not currently provision a direct Orchestrator URL and
already-authenticated actor source. Supplying both is an explicit prerequisite for
runner integration; this adapter never falls back to the Gateway. Contract tests use
controlled loopback HTTP only and do not perform live owner or credential operations.

## Runtime and Environment owner observations

`RuntimeObservationClient` consumes the discovered Gateway
`readRuntimeObservationV1` operation and the direct Environment reads for
`/sandbox/v1/lifecycle`, one operation's `/provider-attempts`, and
`/sandbox/v1/late-resource-cleanups`. These requests use the existing
already-authorized API client, send `Cache-Control: no-store`, require a
no-store response, and verify that the Environment fence remains unchanged
around the reads. The Environment ID comes from a real creation receipt's
`snapshot.fence.owner.environmentId`, not from an input pin.

The runtime response is checked against the strict V1 projection before use.
Unavailable and unknown sections stay explicit; an unknown credit guard is not
converted to zero. The Environment reader rejects foreign operations, malformed
fences, duplicate attempt or cleanup identities, unexpected resource receipts,
and credential or raw provider-outcome fields. It records owner state only and
does not create resources, dispatch work, or turn stored rows into race or cleanup
acceptance.

Controlled loopback coverage is in
`test/runtime-observation.test.mjs`; it does not prove a deployed Gateway,
Environment service, actor authorization, provider callback, or resource cleanup.

## Use a separately authorized target

Environment lifecycle and provider evidence are read directly from the
approved Environment origin. Create a separate client and auth provider for
that exact origin; do not reuse or extract authorization from the Gateway
client.

```javascript
import { AgentweaverClient } from './scripts/api-harness/lib/client.mjs';
import { RuntimeObservationClient } from './scripts/api-harness/lib/runtime-observation-client.mjs';
import { createBrokerAuthProvider } from './scripts/harness-shared/broker-auth.mjs';
import { appendRedactedJsonLine } from './scripts/harness-shared/safe-jsonl.mjs';

const authProvider = createBrokerAuthProvider({
  target: approvedGatewayOrigin,
  token: suppliedInMemoryBrokerToken,
  issuer,
  audience,
  actorId,
});
const gatewayApi = new AgentweaverClient({
  baseUrl: approvedGatewayOrigin,
  authProvider,
  tenantId,
});
const environmentAuthProvider = createBrokerAuthProvider({
  target: approvedEnvironmentOrigin,
  token: separatelyAuthorizedEnvironmentBrokerToken,
  issuer,
  audience: environmentAudience,
  actorId,
});
const environmentApi = new AgentweaverClient({
  baseUrl: approvedEnvironmentOrigin,
  authProvider: environmentAuthProvider,
  tenantId,
});
await gatewayApi.discover();
const runtimeObservation = new RuntimeObservationClient({
  api: gatewayApi,
  environmentApi,
});
const observation = await runtimeObservation.readRuntimeObservationV1({
  projectId, runId, rootSessionId, sessionId, executionFence,
});
const environmentObservation = await runtimeObservation.readEnvironmentEvidence({
  environmentCreation,
  owner: { tenantId, projectId, runId },
  operationId,
});
const receipt = await gatewayApi.invoke('getProject', {
  pathParameters: { projectId },
});
await appendRedactedJsonLine(evidenceFile, {
  kind: 'runtime-owner-observation',
  observation,
  environmentObservation,
});
await appendRedactedJsonLine(evidenceFile, receipt);
```

The provider checks supplied token metadata and expiry, not its signature.
The real Gateway and Broker must validate the signature and current authority.
No token acquisition or refresh occurs.
Cross-origin requests, auth or tenant overrides, and redirects are rejected.
Normal TLS validation remains enabled.

`invoke` uses the discovered path, query, and body contract.
A failed contract refresh clears the previous operation menu.
Transport errors remain explicit.
`calls` retains redacted request and response evidence, status, and available correlation headers.
Transient raw bodies are non-enumerable and must not be persisted separately.
HTTP `202` is acceptance, not completion.

## Source and cleanup boundaries

The SHA helper requires a full lowercase 40-character source commit.
It compares an independently observed source value; it does not observe a deployment.
The v1 Gateway has no retired `/api/version` proof route.
An expected SHA, health response, or synthetic `verified` flag is not deployment evidence.

Project creation and revisioned archive use current discovered owner operations.
`OwnedProjectFixture.create` requires an actual `201` creation receipt from the same client.
It does not adopt a pre-existing project.
Persist its creation evidence before starting a journey.
Its explicit `archive` method reads the current owner revision, archives once, and verifies the owner state.
A conflict or lost response remains failed cleanup evidence.
A later invocation can verify an already archived project without sending another update.
Changed owner name, tenant, target, or auth provider stops cleanup.
Archive is not deletion or Environment/session cleanup.
Record created resource identities and obtain current revisions and authority before cleanup.

## Executable P1 journey

The exported `runP1ApiJourney` function reads journal pages through the
discovered Gateway `replayRunEvents` operation:
`GET /api/v1/projects/{projectId}/runs/{runId}/events`. It checks the actual
cursor chain, event IDs, and project, run, and session identities. MCP replay
does not count as API evidence. Gate-answer, approval, rejection, and addressed
message journeys use the matching discovered API operation.

The shared CLI creates a new invocation-owned project named
`p1-<invocation-uuid>` through the public `createProject` operation, using the
signed-in setup token. It then calls `assignProjectRole` to grant that actor
the Owner and Orchestrator roles, checks each receipt, and accepts the
selected run configuration. It creates and reads back
the root session. A message journey also creates and reads back a child session.
Caller-supplied
`ownerFences` or `fixtureScope` are rejected; only receipts produced and checked
during this invocation can authorize a write. Cleanup revokes the
Orchestrator assignment, archives the project, and then revokes the Owner
assignment. The project is archived first because the last Owner of an active
project can't be revoked.

A failure after a mutating setup or cleanup request is sent is indeterminate.
The runner doesn't retry it or run cleanup. It keeps only the `step`,
`operationId`, HTTP `status`, and `problemCode` in
`observations.fixtureSetup.requestFailures`; response bodies aren't kept.
Use the steps described in [Troubleshoot Agentweaver](../../docs/guide/troubleshooting.md#diagnose-an-indeterminate-p1-project-setup)
before retrying setup or cleanup. The same shared behavior applies to MCP.

Supply an `executionIntent: "execute"` JSON manifest with the direct Environment
identity metadata, pinned provider/runtime/storage/source/image receipts, and
`deploymentEvidence` paths named `publicationPath`, `manifestPath`,
`agentHostArchivePath`, and either `integrationDeploymentReceiptPath` (an
images-only integration publication) or `releaseTag` (a published release).
The integration deployment
receipt must come from `release:deploy-integration` and match the source,
manifest, publication, target namespace, image identities, and service
readbacks. For a published release, `agentHostArchivePath` is the exact
AgentHost OCI archive from that release's images artifact. The runner measures
it against the published source commit, so you can run acceptance from a newer
reviewed checkout that still contains that commit. `fixturePlan` requires a
positive `expectedPlatformRuntimeRevision` alongside the selection context and
only the gate or message content needed by the selected journey. A distinct
`fixturePlan.deniedActor` is optional. When supplied, it must remain a distinct
actor and the identity provider must return its separate token. When omitted,
the `valid-denied-actor-403` step is recorded as blocked with the explicit
missing-account reason; the runner continues the other checks but the surface
and full P1 result cannot pass. Do not substitute the signed-in actor or change
any role or access grant. The runner passes the expected revision through the
existing run-selection request and verifies the accepted and observed owner
selection revisions. The plan value is not authority or an ownership receipt;
the owner compare-and-swap checks it against current state. The fixture keeps
the project-configuration read and does not call the PlatformAdmin-only global
runtime-default endpoint.
Do not put tokens or ownership receipts in the manifest.

For a one-account operator run, use the sign-in launcher:

```powershell
npm run harness:p1:signin-run -- --surface api --input <manifest.json> --output <evidence.jsonl>
```

The [P1 operator guide](../harness-shared/P1-operator.md) lists the required
release evidence, safe dry run, one-account sign-in, and automatic fixture
cleanup steps. This is the supported operator route; it does not require a
token location or an identity-provider module.

To run API and MCP journeys after one sign-in, use `--surface api,mcp`:

```powershell
npm run harness:p1:signin-run -- --surface api,mcp --input <manifest.json> --output <evidence.jsonl>
```

The launcher runs the surfaces sequentially in the requested order. Both use
the same setup token and fresh browser session, but each creates its own
fixture and obtains its own run-bound token. Each surface keeps a separate
resumable invocation: `evidence.api.jsonl` and `evidence.mcp.jsonl`. The
unsuffixed output path is not written in a multi-surface run. A single-surface
run keeps the supplied output path unchanged. Both surfaces run even if the
first returns a blocked or failed result; the command returns the highest exit
code. Cancellation stops remaining work. UI cannot be combined with API or MCP.

The launcher uses the registered public client and one interactive sign-in.
It creates a fresh empty headed-Chrome profile and removes it on success,
failure, or cancellation. It never reads or copies an existing browser profile,
cookie store, or credential cache. The same in-memory browser session
authorizes the setup and run-bound tokens. The launcher validates the callback
and redeems its code with a PKCE verifier before the Portal can consume it.
Token values stay in memory and are not written to output, logs, or evidence.
The one-account route does not create a distinct denied actor; that check and
any missing native or recovery evidence remain blocked.

Advanced invocations may continue to use the lower-level runner with an
already-authorized identity provider. It requires the setup token in the
process environment and `--identity-module`; if a distinct denied actor is
included in the plan, that provider must return a separate token for it. The
shared runner does not sign in or refresh credentials. Both commands record
the invocation ID in JSONL evidence so an unfinished API or MCP run can replay
setup and idempotent actions with the same IDs.

```powershell
npm run harness:p1 -- --surface api --input <manifest.json> --output <evidence.jsonl> --identity-module <authorized-identity-provider.mjs>
```

The output distinguishes the selected surface result from full P1 status.
Surface actions can pass while full P1 remains blocked by required native
probes that the admitted clients cannot perform. Controlled adapter and runner
tests use local fixtures; they do not prove a deployed Gateway, Broker, owner,
native model execution, or AKS acceptance.
