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

For a draft-stage release manifest, the harness accepts only releases pinned in
the release deployment script. It checks each release's tag, exact source
commit, and immutable publication result before it records evidence. v1.0.9 is
pinned with an annotated tag. It was deployed by the whole-release script, so it
stays pinned for this check but can't be deployed with the pre-acceptance
bootstrap. v1.0.11 is pinned the same way, except its release tag is a
lightweight tag on the published source commit.

Deployment and service evidence stay pinned to the release commit and source
tree. Reused images keep their original build labels. The harness verifies
that build source against the publication before measuring the AgentHost
archive and carries that checked binding into each journey. Do not replace
the release source pins with an older image build source. An older source
supplied in JSON alone does not authorize reuse.

The one-account launcher cannot produce a distinct denied identity. If the
manifest requests one, the launcher stops before sign-in. Do not use the
signed-in actor as the denied actor. A missing denied identity or evidence item
must remain blocked; it is not a pass.

The launcher does not change registered clients, redirect addresses, scopes,
tenant settings, roles, or permissions. Any trust change needs separate
approval for the exact client, redirects, scopes, resources, and consequences.

## Reconcile an interrupted run

Before starting another live journey, inspect the invocation from the existing
evidence file:

```powershell
npm run harness:p1:signin-run -- --reconcile <invocation-id> --input <manifest.json> --output <reconciliation.json>
```

Use the same target manifest that identifies the approved environment and setup
actor. The command uses one interactive sign-in with the existing registered
client and setup scopes. It reads the Gateway contract, visible project list,
the actor's current authorization context, the matched project's active role
assignments, and the authority-audit records linked to the invocation.
`listProjects` has no invocation-name query, so the harness compares the exact
`p1-<invocation-id>` name in the returned list. That list is
permission-filtered: a missing project is not proof that it is gone unless the
authority context confirms tenant-wide project visibility.

The audit read is bounded to 32 records. A denied, malformed, missing,
truncated, or otherwise incomplete read produces **needs review**. If a
project is visible, the role assignments and audit events must match the
expected P1 Owner and Orchestrator setup before the result can say
**safe to clean up**. A complete audit read and tenant-wide visibility with no
matching project can establish **already clean**.

Reconciliation is an assessment only. It writes one redacted JSON evidence
file and issues only reads. It never writes a fixture or grant, performs
cleanup, starts or admits a journey, or approves a cleanup action. Even a
**safe to clean up** verdict is an assessment, not authorization
for the harness to clean up; cleanup must remain a separate reviewed action.

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
Keep the shared browser window open between setup and run-bound token requests.
The launcher closes each completed authorization page, not the shared browser.

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
