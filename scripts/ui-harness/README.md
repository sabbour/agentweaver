# P1 UI harness adapter

This candidate adapts the retained v1 browser client. It does not redesign the UI.
Supply an already authorized Playwright-compatible page on the exact web origin.
The adapter does not start a browser, copy a profile, sign in, acquire tokens, or seed browser storage.

The Gateway URL must end in `/api/v1`.
Supply the exact tenant selector and expected Broker issuer, audience, and actor.
The adapter checks observed token metadata, not its signature.
The Broker, Gateway, and owners must validate signatures and current authority.

## Contract checks

From the repository root:

```powershell
npm run test:harness:ui
```

These tests use a controlled injected-page contract.
They do not run a browser or prove live Gateway, Broker, owner, or AKS acceptance.

## Use an authorized page

```javascript
import { UiHarnessClient } from './scripts/ui-harness/lib/client.mjs';
import { appendRedactedJsonLine } from './scripts/harness-shared/safe-jsonl.mjs';

const ui = new UiHarnessClient({
  page,
  baseUrl: approvedWebOrigin,
  gatewayUrl: approvedGatewayUrl,
  tenantId,
  issuer,
  audience,
  actorId,
});
try {
  await ui.openRun({ projectId, runId });
  const receipt = await ui.gate({
    sessionId,
    requestId,
    action: 'answer',
    answer: exactAllowedAnswer,
  });
  await appendRedactedJsonLine(evidenceFile, receipt);
} finally {
  await ui.close();
}
```

Use current owner identifiers, not identifiers from another run or an old screenshot.
Gate actions refresh run, session, and decision snapshots.
They require the exact request, actor, execution fence, state version, and current choice or freeform permission.
The adapter checks the actual request body and the typed owner decision receipt, including its ID, version, fence, and issues.
The receipt must match the snapshot fence and advance its state version by exactly one, including an owner denial.
`approve` uses **Approve**; `reject` uses **Deny**.
Owner `accepted: false` and HTTP errors remain denials.
Acceptance does not prove gate completion.

`sendMessage` requires two existing active sessions in the current owner tree.
It checks the actual addressed-message body and receipt.
A browser echo does not prove delivery or durable transcript storage.

`replayJournal` reloads the selected Activity view.
It checks the actual replay cursor chain, scoped event IDs, positions, duplicate consistency, and reverse DOM order.
Object references are not recovered transcript content.
The adapter does not parse or claim a complete SSE wire journal.

## Evidence and ownership

`calls` retains redacted requests, responses, status, and available correlation headers.
Non-enumerable transient bodies support exact checks; do not persist those bodies separately.
Use the shared JSONL writer for retained evidence.

Network observation is passive. It is not a browser firewall.
Redirects, changed identity, transport failures, and navigation to another run fail the adapter.
The adapter rechecks the exact page scope after snapshot waits, before final action clicks, and before returning replay proof.
They do not undo a request that the browser already sent.

`close` removes only this adapter's listeners and drains its observations.
It does not close the borrowed page, context, or browser.
Run, session, Environment, and project cleanup require separate current owner authority and verified receipts.
Closing the adapter is not product-resource cleanup.
