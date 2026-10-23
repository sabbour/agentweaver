# Run the P1 acceptance harness

Use this guide after the repaired release is deployed to an approved target.
The harness checks the supplied release evidence, signs in once, runs the
selected journey, and records a redacted result.

## Before you run

Prepare one manifest for the exact target. It must include:

- The exact release and source revision.
- The publication record and AgentHost image archive with their measurements.
- Current provider, runtime, and storage receipts.
- The current platform selection revision.
- Independently produced native and recovery evidence for checks that need it.

The one-account launcher cannot produce a distinct denied identity. If the
manifest requests one, the launcher stops before sign-in. Do not use the
signed-in actor as the denied actor. A missing denied identity or evidence item
must remain blocked; it is not a pass.

The launcher does not change registered clients, redirect addresses, scopes,
tenant settings, roles, or permissions. Any trust change needs separate
approval for the exact client, redirects, scopes, resources, and consequences.

## Check the manifest without contacting the target

```powershell
npm run harness:p1:signin-run -- --surface api --input <manifest.json> --output <evidence.jsonl> --dry-run
```

The dry run checks the manifest shape and prints the operator steps. It does
not open a browser, contact the target, write evidence, create a project, or
create role assignments. It does not prove that the release evidence is live
or current.

## Run a journey

Choose one command:

```powershell
npm run harness:p1:signin-run -- --surface api --input <manifest.json> --output <evidence.jsonl>
npm run harness:p1:signin-run -- --surface mcp --input <manifest.json> --output <evidence.jsonl>
npm run harness:p1:signin-run -- --surface api,mcp --input <manifest.json> --output <evidence.jsonl>
npm run harness:p1:signin-run -- --surface ui --input <manifest.json> --output <evidence.jsonl>
```

Complete one interactive sign-in in the headed Chrome window with the approved
account. Review the requested scopes and approve only the registered client
request. The API, MCP, and UI flows reuse this browser session. Do not sign in
again, use another account, or paste a token into a prompt.

For the UI flow, the launcher starts the app's silent authorization with the
same Broker session. It does not ask you to sign in again.

The launcher needs no token file, token environment variable, identity module,
or page module. It keeps Broker tokens in memory. It does not read or copy an
existing browser profile or read browser token storage. Its temporary Chrome
profile is removed when the run finishes, fails, or is cancelled.

The runner uses the public Projects API to create an invocation-owned project
and assign the signed-in actor Owner and then Orchestrator. It runs only the
selected journey. Cleanup revokes Orchestrator, archives the project, and then
revokes Owner. The evidence records the receipts for setup and cleanup.

## Review the result

Keep the redacted JSONL output. It contains no token values. A blocked or
failed cleanup is not a successful cleanup. If the output says a mutation may
have completed without a verified receipt, stop and follow its diagnosis.
Do not retry the mutation or remove unrelated resources.

A passed surface journey is not full P1 acceptance. Missing native or recovery
evidence remains blocked. A one-account run also cannot pass a check that
requires a distinct denied actor. Controlled tests do not prove a deployed
service or a live journey.
