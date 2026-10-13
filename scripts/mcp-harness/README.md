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
count as MCP evidence.

The CLI currently blocks before MCP access because a JSON manifest cannot
prove that this invocation created the project, run, selection, and sessions.
The input manifest must contain
`executionIntent: "execute"`, direct Environment identity metadata, pinned
runtime receipts, current owner fences, and caller-supplied creation/readback
receipt claims for the fixture.
The manifest also needs `deploymentEvidence` paths named `publicationPath`,
`manifestPath`, and `agentHostArchivePath`. Before reading bearer credentials
or connecting to MCP, the runner verifies the pinned clean Git commit/tree,
published images and AgentHost archive, then performs read-only Kubernetes and
public GHCR observations. It then returns blocked without reading credentials
or connecting to MCP. Public project, selection, root, and session endpoints
exist, but they require separately authorized actors and current role grants.
Project roles are provisioned only through a privileged source path, and
caller receipts cannot create that authority. Gateway does not expose the
Orchestrator dispatch handler to this harness.
Direct MCP gate and message journeys allow read-only tool discovery but block
before write tool calls; caller-supplied receipts do not authorize writes.

```powershell
npm run harness:p1 -- --surface mcp --input <manifest.json> --output <evidence.jsonl>
```

The example currently exits with code `2` and a blocked result. The controlled
MCP adapter tests do not prove a deployed P1 journey.
