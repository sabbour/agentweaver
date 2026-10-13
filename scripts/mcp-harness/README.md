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
context, a distinct denied actor, and the selected gate or message content.
The runner sends that expected revision through the existing run-selection
request and verifies the accepted and observed owner selection revisions.
This plan value is not authority or an ownership receipt; the owner
compare-and-swap checks it against current state. The fixture retains its
project-configuration read and does not call the PlatformAdmin-only global
runtime-default endpoint. After source and deployment checks pass,
set `AGENTWEAVER_P1_SETUP_TOKEN` in the process environment and provide
`--identity-module <authorized-identity-provider.mjs>` returning already-issued
run-bound and denied-actor tokens. No sign-in, token refresh, or consent is
performed. The unfinished invocation ID is persisted so setup and idempotent
MCP actions can be replayed with the same IDs.

```powershell
npm run harness:p1 -- --surface mcp --input <manifest.json> --output <evidence.jsonl> --identity-module <authorized-identity-provider.mjs>
```

Surface results are reported separately from full P1 acceptance. Full P1 stays
blocked until the required native probes have real evidence. Controlled MCP
and runner tests use local fixtures; they do not prove a deployed Gateway,
Broker, owner, native execution, or AKS acceptance.
