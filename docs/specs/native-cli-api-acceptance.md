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
  as a supported 1.x product surface.
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
