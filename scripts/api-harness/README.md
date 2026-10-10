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
Retain actual owner cleanup receipts; do not clean up a pre-existing project.
