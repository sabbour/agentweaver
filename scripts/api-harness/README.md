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

## Use a separately authorized target

```javascript
import { AgentweaverClient } from './scripts/api-harness/lib/client.mjs';
import { createBrokerAuthProvider } from './scripts/harness-shared/broker-auth.mjs';
import { appendRedactedJsonLine } from './scripts/harness-shared/safe-jsonl.mjs';

const authProvider = createBrokerAuthProvider({
  target: approvedGatewayOrigin,
  token: suppliedInMemoryBrokerToken,
  issuer,
  audience,
  actorId,
});
const api = new AgentweaverClient({
  baseUrl: approvedGatewayOrigin,
  authProvider,
  tenantId,
});
await api.discover();
const receipt = await api.invoke('getProject', {
  pathParameters: { projectId },
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

The shared CLI creates a new invocation-owned project through the privileged
Projects Config setup command, reads back the current Owner and Orchestrator
grants, accepts the selected run configuration, and creates and reads back the
root session (and a child session for a message journey). Caller-supplied
`ownerFences` or `fixtureScope` are rejected; only receipts produced and checked
during this invocation can authorize a write. Cleanup revokes the setup grants
and archives the project only if no run selection was accepted. An accepted
selection is retained as `retainedPending` with its grants still active.

Supply an `executionIntent: "execute"` JSON manifest with the direct Environment
identity metadata, pinned provider/runtime/storage/source/image receipts, and
`deploymentEvidence` paths named `publicationPath`, `manifestPath`,
`agentHostArchivePath`, and `integrationDeploymentReceiptPath`. The deployment
receipt must come from `release:deploy-integration` and match the source,
manifest, publication, target namespace, image identities, and service
readbacks. `fixturePlan` requires a positive
`expectedPlatformRuntimeRevision` alongside the selection context, a distinct
denied actor, and only the gate or message content needed by the selected
journey. The runner passes the expected revision through the existing
run-selection request and verifies the accepted and observed owner selection
revisions. The plan value is not authority or an ownership receipt; the owner
compare-and-swap checks it against current state. The fixture keeps the
project-configuration read and does not call the PlatformAdmin-only global
runtime-default endpoint.
Do not put tokens or ownership receipts in the manifest.

After read-only source and deployment checks pass, set
`AGENTWEAVER_P1_SETUP_TOKEN` in the process environment and provide an
`--identity-module` that returns already-issued run-bound and denied-actor
tokens. The runner does not sign in, refresh credentials, or request consent.
These credentials are read only after source evidence passes. The command
records the invocation ID in its JSONL output so an unfinished API or MCP run
can replay setup and idempotent actions with the same IDs.

```powershell
npm run harness:p1 -- --surface api --input <manifest.json> --output <evidence.jsonl> --identity-module <authorized-identity-provider.mjs>
```

The output distinguishes the selected surface result from full P1 status.
Surface actions can pass while full P1 remains blocked by required native
probes that the admitted clients cannot perform. Controlled adapter and runner
tests use local fixtures; they do not prove a deployed Gateway, Broker, owner,
native model execution, or AKS acceptance.
