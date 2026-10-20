# P1 MCP harness adapter

This candidate uses the released 0.x MCP client pattern and its exact SDK `1.29.0` pin.
It uses Streamable HTTP at the exact `/mcp` path.
It does not import remote P2 tools, persona actors, or a second protocol framework.

Restore this package's locked dependencies when they are missing:

```powershell
npm ci --prefix scripts\mcp-harness --ignore-scripts --no-audit --no-fund
npm run test:harness:mcp
```

The tests exercise contract fakes and an actual SDK connection to a controlled loopback JSON-RPC HTTP server.
That transport proof is not a deployed .NET MCP/Gateway/Broker journey or cloud acceptance.

## Use a separately authorized target

```javascript
import { McpHarnessClient } from './scripts/mcp-harness/mcp-client/client.mjs';
import { createBrokerAuthProvider } from './scripts/harness-shared/broker-auth.mjs';
import { appendRedactedJsonLine } from './scripts/harness-shared/safe-jsonl.mjs';

const authProvider = createBrokerAuthProvider({
  target: approvedMcpUrl,
  token: suppliedInMemoryBrokerToken,
  issuer,
  audience,
  actorId,
});
const mcp = await McpHarnessClient.connect({
  target: approvedMcpUrl,
  authProvider,
  tenantId,
});
try {
  await mcp.discoverTools();
  const receipt = await mcp.callTool('agentweaver_getProject', { projectId, tenantSelector: tenantId });
  await appendRedactedJsonLine(evidenceFile, receipt);
} finally {
  await mcp.close();
}
```

The token provider checks supplied metadata and expiry, not the signature.
The real Broker and servers must validate signatures and current authority.
The adapter does not acquire or refresh tokens.
It preserves normal TLS checks and rejects cross-origin requests and redirects.

Discovery reads every tools page and rejects missing, duplicate, foreign, or invalid tool identities and repeated cursors.
A failed refresh clears the previous menu.
Tool names and argument names come from live Gateway-derived schemas.
Path and query names retain their original names.
Use the advertised `body`, `tenantSelector`, and `idempotencyKey` fields where applicable.
Do not add retired `path_` or `header_` prefixes.

The result preserves `isError` and the structured `{ status, ownerResponse }` contract.
An owner denial or HTTP `202` cannot become a completed action.
Retained calls and protocol records are redacted.
Raw results are transient and non-enumerable.
The protocol array retains actual initialize, discovery, and call request/response IDs.
It is SDK message evidence, not a raw network-byte capture.

`close` closes the owned SDK client and transport.
It does not archive projects, release Environments, or clean up sessions.
Product cleanup needs separate current owner authority and observed receipts.

## Executable P1 journey

The exported `runP1McpJourney` function reads journal pages through the
discovered `agentweaver_replayRunEvents` tool. The API client still reads
owner snapshots and verifies a denied-actor `403`, but API replay does not
count as MCP evidence. Gate-answer, approval, rejection, and addressed-message
journeys call the corresponding discovered Gateway-derived MCP tool.

The shared CLI creates and verifies an invocation-owned project, grants,
selection, and sessions before it supplies those receipts to the selected
surface. It rejects caller-supplied `ownerFences` and `fixtureScope` claims.
Cleanup revokes setup grants and archives the project only when no run
selection was accepted; otherwise it reports `retainedPending` and preserves
the active grants. The runner does not invoke an Orchestrator dispatch method.

Supply the same `executionIntent: "execute"` manifest and four
`deploymentEvidence` paths documented by the API harness. `fixturePlan`
requires a positive `expectedPlatformRuntimeRevision`, the exact selection
context, and the selected gate or message content. A distinct
`fixturePlan.deniedActor` is optional. When supplied, it must remain a distinct
actor with its own token. When omitted, the `valid-denied-actor-403` step is
recorded as blocked with the explicit missing-account reason; the runner
continues other checks, but the surface and full P1 result cannot pass. Do not
substitute the signed-in actor or change any role or access grant.
The runner sends that expected revision through the existing run-selection
request and verifies the accepted and observed owner selection revisions.
This plan value is not authority or an ownership receipt; the owner
compare-and-swap checks it against current state. The fixture retains its
project-configuration read and does not call the PlatformAdmin-only global
runtime-default endpoint.

```powershell
npm run harness:p1:signin-run -- --surface mcp --input <manifest.json> --output <evidence.jsonl>
```

Use `--surface api,mcp` to run both surfaces sequentially after one sign-in.
The setup token and fresh browser session are shared. Each surface creates its
own fixture, run-bound token, and evidence file: `evidence.api.jsonl` and
`evidence.mcp.jsonl`. The unsuffixed output path is not written. A blocked
surface does not skip the next surface; the command returns the highest exit
code. See the [API harness guide](../api-harness/README.md) for the full contract.

The sign-in launcher uses the existing `agentweaver-web` public client and one
interactive TenantAdmin sign-in. It uses one fresh empty headed-Chrome profile
for the setup and run-bound authorization flows, then deletes it on success,
failure, or cancellation. It never reads/copies an existing profile, cookie
store, or credential cache. Token values stay in memory and never enter logs
or evidence. Chrome request-stage interception blocks the registered Portal
callback before the Portal loads, including server-side redirect hops. The
launcher validates the callback and exchanges its code; the SPA cannot consume
it. This one-account command requires `fixturePlan.deniedActor` to be
omitted, so the distinct denied-actor check remains blocked while other
journey steps continue. The Host-caller probe-provider gap remains blocked;
the launcher adds no Host-caller provider, and missing native probes still
block full P1 acceptance.

For advanced runs, the lower-level `npm run harness:p1` command still accepts
`--identity-module` and `AGENTWEAVER_P1_SETUP_TOKEN`. A separately authorized
actor can be supplied there when one is genuinely available; do not use the
one-account launcher to impersonate or substitute an actor. The unfinished
invocation ID is persisted so setup and idempotent MCP actions can be replayed
with the same IDs.

Surface results are reported separately from full P1 acceptance. Full P1 stays
blocked until the required native probes have real evidence. Controlled MCP
and runner tests use local fixtures; they do not prove a deployed Gateway,
Broker, owner, native execution, or AKS acceptance.
