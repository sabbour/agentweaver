# Specification: Native CLI API acceptance

**Status:** Existing CLI requirements and source contracts, not a claim that the
complete live acceptance journey has passed.

For contributors maintaining the native .NET CLI, Gateway contract, Broker
authentication, and API acceptance runner. This specification consolidates the
current boundary; it introduces no new route, scope, or compatibility policy.

## Problem and goals

API acceptance must exercise the actual distributable client and current owner
authorization. A test host, an authenticated read, a raw request, or a Node
substitute cannot establish that the native executable completes the journey.
The CLI must discover supported API operations, authenticate safely, report
failures, and retain redacted evidence for the operations it actually attempts.

## Non-goals

- Add an Agentweaver product MCP server or treat retained compatibility tooling
  as a supported product surface.
- Duplicate Gateway API behavior, invent endpoints or scopes, or silently reuse
  the 0.x MCP-only registration.
- Equate CLI/API version strings with an established compatibility policy.
- Publish, deploy, acquire live credentials, or run against an unapproved target.
- Count browser acceptance, controlled tests, and native API acceptance as the
  same result.

## Intended behavior and requirements

### Discover and invoke API operations

The CLI uses the Gateway's live `/openapi/v1.json` catalog for supported API
operations. It validates operation identity, advertised parameters, media types,
and security requirements before a protected invocation. The Gateway and domain
owners remain authoritative; discovering a route does not authorize its use.
Help and version discovery do not require target configuration.

The separate `rest` command is a raw request facility, not the acceptance runner
and not proof of API operation parity. Its request and response bounds remain
separate from catalog-driven API operations.

### Authenticate without ordinary token files

Interactive login uses the Broker's registered public native-client flow with
authorization code, S256 PKCE, an exact bounded loopback callback, and validated
identity. Persist refresh credentials only in the operating system's supported
secure credential store. Target, issuer, tenant, audience, scopes, and any paired
project/run binding separate credential entries. No plaintext fallback is allowed.

Refresh rotation must be persisted before returning the new access token.
An uncertain or interrupted refresh must not blindly retry a consumed credential.
Current Projects authority remains required even when OAuth consent is reusable.
Run-bound operations also require their exact current run authorization.

### Run the native acceptance journey

The public command is `agentweaver test --input <manifest> --output <new-evidence-file>`.
The manifest supplies the target and journey. The `test` command does not accept
target flags or take its target from environment variables.

- Use the self-contained apphost published from source, or the exact extracted
  apphost when checking a distribution candidate.
- Default to noninteractive authentication. `--interactive` explicitly permits
  the browser authorization flow; it does not authorize the target's mutations.
- A successful `--dry-run` validates the manifest and proof inputs without
  network requests, sign-in, browser launch, or an evidence file, including when
  `--interactive` is present. Validation failure can write a blocked result to the
  requested output.
- Do not overwrite the manifest or supplied proof artifacts with the evidence
  output. A live invocation needs a new output file.
- Execute and report only the selected journey's supported owner operations.
  Keep denied-actor checks, current authority, fixture ownership, and cleanup
  visible rather than inferring them from setup success.

### Workspace repository read

The `workspace-repository-read` journey checks the current configured repository
without creating an Agentweaver project, run, role, or test fixture. It reads
repository data only. It does not evaluate the full P1 journey.

The manifest uses the existing environment, source, and deployment proof
inputs. Its `fixturePlan` contains only a `workspace` object with these exact
fields:

| Field | Required value |
| --- | --- |
| `projectId` | Canonical project GUID |
| `expectedProjectRevision` | Positive current project configuration revision |
| `connectionId`, `connectionRevision` | Exact configured Repo App connection and positive revision |
| `installationId`, `repositoryId`, `repositoryFullName` | Exact approved installation and repository identity |
| `refKind`, `refName` | `branch` or `tag`, and its exact valid name |
| `commitSha` | Lowercase 40-character commit SHA |
| `filePath` | Normalized repository-relative path to a regular file |
| `expectedByteLength` | File length in UTF-8 bytes, from 0 through 1 MiB |
| `contentSha256` | Lowercase SHA-256 digest of those exact bytes |

Unknown fields and selection codes are invalid. The environment's actor and
tenant identify the expected owner context. The manifest values assert what
the runner must read; they do not grant access.

Before each protected read, the native CLI asks the Broker for a new selection.
The Broker presents the exact repository and operation in the signed-in user's
browser. The review uses the existing local cookie and antiforgery checks.
Reusable OAuth consent does not approve a repository. The CLI sends the
selection only in `X-Agentweaver-Team-Sync-Selection` for that operation.
Gateway and Orchestrator still check current project authority and repository
visibility.

The runner reads the current project configuration, then lists refs with a
maximum of 100 pages and a maximum of 100 refs per page. It rejects a repeated
cursor. It reads the tree and file at the same immutable commit. It accepts
only one regular file of at most 1 MiB. It checks the UTF-8 byte length and
SHA-256 digest without newline or Unicode normalization. When the tree returns
a Git blob digest, the runner checks that digest too.

The result reports `featureStatus` and sets `fullP1Status` to
`not-evaluated`. It keeps only bounded observations, such as the approved
repository, commit, file path, byte length, and digests. It does not retain
the file text or selection code. A passed feature result is not proof of
denied-actor parity, runtime behavior, deployment, or full P1 acceptance.

The Workspace journey requires explicit `--interactive` permission for a
fresh browser review. `InteractionPolicy.Never` blocks before authentication,
Gateway discovery, or browser work; a cached bearer is not a fresh selection.
A successful `--dry-run` validates the manifest and proof files, then returns
before authentication, network access, listener or browser launch, reservation,
or evidence creation. The journey does not create, change, or delete GitHub
repository data. A live review can create a short-lived Broker selection
record, and normal sign-in can update the operating system's credential store.

### Journal fixture

The `journal` journey may omit `fixturePlan.selectionContext`. The CLI then
uses `createProjectRun` with the observed project configuration revision and a
stable invocation key. Projects creates the selection from current owner-held
configuration. The CLI uses the returned run and root-session IDs, reads back
the exact selection and expected platform runtime revision, then obtains
run-bound authorization before root admission. It does not construct raw
context from an effective selection.

After it reads back the accepted root, the CLI sends one real
`sendProjectRunMessage` seed and replays the journal through the owner API.
Replay requires nonempty pages, exact project/run identity, unique ordered
positions, and advancing bounded cursors. The seed does not start a workflow
or dispatch a model. Explicit `selectionContext` keeps the existing raw-context
path.

Cleanup cancels the owned run before it removes authority. It checks the
cancellation receipt and independently reads the cancelled run, root session,
and session tree at the advanced fence. It then revokes Orchestrator, archives
the project, revokes the creator's Owner assignment last, and independently
reads the archived project and empty assignment list. Missing receipts or
failed readbacks remain indeterminate; the runner does not retry uncertain
writes or report cleanup as complete.

### Session-history metric fixture

The `session-history-metric` journey may also omit
`fixturePlan.selectionContext`. For both journeys, explicit `null` or a
malformed selection context remains invalid. The CLI uses an idempotency key
for `createProjectRun`, accepts only the returned run and root-session IDs,
reads the effective selection, and admits the root with its run-bound
Orchestrator client. Its benign first message does not start the run or dispatch
work. Root admission retains normal runtime and budget checks; it is not a
zero-cost or admission-free path. Supplying a selection context keeps the
existing explicit selection path.

Both paths verify the archived-inclusive, detached-exclusive metric, then cancel
the accepted run through an unbound Orchestrator client. Before removing project
authority, the CLI independently verifies the cancelled run, retained root and
archived child, and detached child at the advanced execution fence. It revokes
the Orchestrator assignment, archives the project, revokes the creator's
verified Owner assignment last, and
independently reads back the archived project and empty assignment list.
Uncertain creation, selection, cancellation, or terminal readback remains
indeterminate and blocks authority removal; a cancellation receipt alone does
not prove cleanup.

Project setup reads the creator's active Owner assignment from
`listProjectRoleAssignments`; it does not assign Owner a second time.
Orchestrator is granted separately for the fixture.

## Contracts and boundaries

The native runner uses the existing Gateway client and Broker authentication.
The Node API harness entrypoint is compatibility delegation only: when used,
`AGENTWEAVER_CLI_PATH` identifies the exact native executable. There is no fallback
to a Node API runner.

The self-contained executable carries its .NET runtime; building or publishing it
from source still needs the .NET SDK. Installation and packaging instructions
belong in the [CLI guide](../guide/cli.md).

Live tests create and clean up API fixtures. They require a separately approved
non-production target, actors, permissions, effect scope, and cleanup scope.
Credentials must not enter ordinary files, arguments, logs, or JSONL evidence.

## Failure and recovery

Return a nonzero result for an unsuccessful journey. Retain the actual owner
error, failed or blocked verification, and cleanup outcome in redacted evidence.
Missing native runtime, authority, required proof, or supported consumer behavior
is blocked or failed as defined by the runner, not passed.

Do not treat an uncertain remote write as absent. Reconcile the existing
invocation-owned resource through its owner before retrying or cleaning it up.
Do not broaden cleanup to resources merely named in input.

The [session-material scenario](../architecture/design/session-material-acceptance.md)
has a missing accepted-invocation-to-authorized-readback consumer boundary.
Its blocked result prevents full acceptance success; narrower surface results
remain separate. This specification does not declare that gap resolved.

## Acceptance and validation

| Criterion | How to verify | Evidence boundary |
| --- | --- | --- |
| API changes cannot silently leave catalog-driven CLI operations stale | Gateway/CLI OpenAPI contract and deliberate mismatch tests | Contract alignment is not a version-window policy |
| Login, callback validation, credential isolation, refresh rotation, and uncertainty handling fail safely | CLI auth, credential-lock, vault, and Broker native-consent tests | Controlled credentials and transports are not live sign-in |
| `test` preserves explicit interaction policy and safe output handling | CLI command and manifest tests, including dry-run and overwrite rejection | Successful dry-run performs no journey |
| The exact executable completes the selected API journey and owned cleanup | Authorized native apphost execution with its manifest and redacted result | Not established by this documentation change |

Use the [testing guide](../guide/testing.md) for canonical validation commands.
Do not substitute targeted `rest` requests or unit tests for the final native
journey.

## Rollout and compatibility

Keep CLI operation discovery tied to the actual Gateway contract and retain the
existing Broker/native-client checks. Source merge, distributable publication,
workload deployment, and live acceptance are separate states. This document
neither publishes an executable nor authorizes a live run.

## Open questions and decisions

The CLI/API version-compatibility policy remains undecided. A maintainer must
establish its exact rule and applicability before a specification can require a
version window or negotiation mechanism. The proposed internal N-1 service
contract in [ADR 0001](../architecture/decisions/0001-platform-architecture.md)
does not settle that client policy.
