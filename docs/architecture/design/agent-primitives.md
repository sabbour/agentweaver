# Agent primitives proof of concept

> Part of [ADR 0001: Agentweaver 1.0 platform architecture](../decisions/0001-platform-architecture.md). **Status:** Proposed.

This page describes how Agentweaver uses the public
[agent-primitives/sessions](https://github.com/agent-primitives/sessions) (AgentSessions) and
[agent-primitives/suite](https://github.com/agent-primitives/suite) (AgentSuite) contracts in the
P2 proof of concept tracked by epic [#2111](https://github.com/sabbour/agentweaver/issues/2111).

## Pinned upstream sources

| Contract | Repository | Commit | Contract version |
| --- | --- | --- | --- |
| AgentSessions | `agent-primitives/sessions` | `465e82175da7815db39ec14852deb3949ff82670` | `0.2.0-draft` |
| AgentSuite | `agent-primitives/suite` | `a09950a75359cdc7de4d3361694582613f24c732` | `1.0.0-draft` |

The Go reference tools in `tools/agent-primitives` pin these commits in `go.mod`. The .NET
verifier pins the AgentSessions commit in `AgentSessionsPin` and rejects exports from any other
commit or contract version with `unsupported_source`.

## AgentSessions as an optional Sessions provider

AgentSessions is one optional implementation of the Sessions provider contract. The native
PostgreSQL journal stays the default Sessions provider. Agentweaver builds, tests, and runs with
the AgentSessions package absent, and no service, contract, or `Agentweaver.Abstractions` type
references an AgentSessions type.

The `Agentweaver.Providers.Sessions.AgentSessions` package holds all AgentSessions code:

| Type | Purpose |
| --- | --- |
| `AgentSessionsSessionsProvider` | Provider descriptor, registration, negotiation, and run pinning. Provider ID `agentsessions.records`, Sessions adapter contract `1.0.0`, options schema `1`, in-process hosting. |
| `AgentSessionsRecordJournal` | Read-only `ISessionsJournal` over verified record exports for one pinned run binding. |
| `AgentSessionsRecordVerifier` | Independent hash, chain, and fork verification. |
| `AgentSessionsPin` | The pinned upstream commit and contract version. |

### Registration and selection

Register the provider in the host's provider catalog next to the native provider. Keep
`postgres.native-sessions` as the Sessions default, and allow AgentSessions as a project
override. The standard `ProviderResolver` then selects a provider for each run:

- A run with no project override resolves the native PostgreSQL journal.
- A run with a permitted `agentsessions.records` override resolves AgentSessions. The resolver
  matches the adapter version exactly, so AgentSessions reports Sessions adapter contract
  `1.0.0`, the same version the host requests for the native provider.
- `ResolveNegotiateAndPin` validates the options, negotiates the required capabilities, and pins
  the run. `ToSessionBinding` converts the pin to a `SessionProviderBinding`.

AgentSessions offers the `Replay`, `Subscribe`, `ObjectReferences`, and `ToolCalls`
capabilities. It doesn't offer `Append` or `Fork`. Negotiation fails when a run requires
either one.

### Journal behavior

`AgentSessionsRecordJournal.Create` verifies every export before it serves an event. It also
runs `VerifyFork` for each child whose parent export is in the same set. A failed export throws
`AgentSessionsVerificationException` with the session uid and the verifier's failure code.

The journal maps each verified record to a `SessionEventEnvelope`:

| Record kind | Sessions payload |
| --- | --- |
| Message | `TurnSessionPayload`. The role defaults to `user`. |
| Tool call | `ToolCallSessionPayload` with state `requested`. |
| Tool result | `ToolCallSessionPayload` with state `completed` or `failed`. The tool name comes from the matching call, or `unknown`. |
| Any other kind | `ArtifactReferenceSessionPayload`. |

- Every payload references the record by object key `agentsessions/{uid}/{seq}` with purpose
  `agentsessions.record`.
- Positions run across the whole run in export order. Cursors have the form
  `agentsessions:{position}`, and a page holds 1 to 1,000 events.
- Event IDs are the first 16 bytes of SHA-256 over the session uid and the record content hash. A
  fork child repeats its parent's records, so the uid keeps the IDs unique.
- Reads check the caller's `sub`, `project_id`, and `run_id` claims against the binding and
  throw `SessionAccessDeniedException` on a mismatch.
- `CreateSessionAsync` and `AppendAsync` throw `NotSupportedException`. `ForkFromExplicitEventAsync` throws
  `SessionForkUnsupportedException`. `SubscribeAsync` and `SubscribeRunAsync` return the stored events and end.

### Contract gaps

The shared Sessions contract stays provider-neutral. These gaps limit the AgentSessions provider
today:

1. The `SessionEventEnvelope` constructor is internal to Events & Sessions. An external
   provider builds envelopes from the public JSON wire form.
1. `ISessionsProviderBinder` and the Events & Sessions host bind one `ISessionsJournal`, the
   native PostgreSQL journal, and the native journal rejects other provider IDs. Serving
   AgentSessions for a run needs a provider-neutral journal router in the host.
1. The run-scope claim rules are internal to Events & Sessions, so the provider repeats the check.
1. Sessions payloads hold content by `SessionObjectReference`, and AgentSessions events are
   inline. The typed mapping covers messages and tool calls. Other kinds, such as model calls,
   usage, approvals, and lifecycle events, become artifact references.
1. The contract has no equivalent of the AgentSessions expected-head and fence append rules, so the
   provider is read-only and doesn't fork.
1. The resolver requires an exact adapter-version match for each request.

## AgentSessions record export

The Go exporter in `tools/agent-primitives/sessionsexport` reads stored rows from the upstream
SQLite event log and writes one JSON document per session. It copies `seq`, `prev_hash`,
`hash`, `fence`, and the raw protobuf `Event` bytes as stored. It never recomputes a hash, so the
export carries the upstream producer's exact output.

```json
{
  "format": "agentweaver.agentsessions.export.v1",
  "upstream": { "repository": "...", "commit": "...", "contract": "0.2.0-draft" },
  "session": {
    "uid": "child-session",
    "parent": { "uid": "parent-session", "fork_seq": "5" },
    "origin": { "source": "agentweaver", "subject": "project-id" }
  },
  "producer": { "system": "agentweaver", "references": { "runId": "..." } },
  "source_head": { "seq": "11", "hash": "..." },
  "records": [
    { "seq": "1", "prev_hash": "", "content_hash": "...", "fence": "1", "event": "<base64 protobuf>" }
  ]
}
```

- `records` hold the only hashed data.
- `session.origin` is the upstream session origin. `producer` names the system that produced the
  export and its references, such as an Agentweaver project or run ID. The verifier never mixes
  the two, and neither one is part of a record hash.
- All 64-bit integers are decimal strings, so values above 2^53 stay exact.
- `fence` is the writer fence at append time. It is not hashed, and a fork child carries a
  different fence than its parent for the same copied records.

### Independent verification

`AgentSessionsRecordVerifier` in the optional `Agentweaver.Providers.Sessions.AgentSessions` package
verifies an export without Go
and without the upstream code. It uses only the vendored `agentsessions/v1/common.proto`, the
Google.Protobuf runtime, and the shared RFC 8785 implementation in
`Agentweaver.Abstractions.CanonicalJson`. For each record it:

1. Checks that `seq` runs from 1 with no gap, duplicate, or reordering.
1. Checks that `prev_hash` equals the previous record's `content_hash`, or is empty for seq 1.
1. Decodes `event` as protobuf and rejects transport-only `EVENT_PARKED` events. It also rejects
   event bytes that carry a field the pinned schema doesn't define, at any nesting depth. The
   proto3 JSON form omits unknown fields, so the content hash doesn't cover them.
1. Computes `hex(SHA-256(JCS({"event": proto3JSON(event), "prev_hash": prev, "seq": "N"})))` and
   compares it with `content_hash`. The proto3 JSON form keeps proto field names, omits default
   values, and keeps presence for `optional` fields, so `input_count: 0` and an absent
   `input_count` hash differently.
1. For a fork child, checks the `LIFECYCLE_FORK` marker at `fork_seq + 1` with detail
   `parent@fork_seq`. `VerifyFork` also checks that records 1 through `fork_seq` equal the
   parent's records, ignoring `fence`.

The verifier stops at the first failure and reports one stable code: `unsupported_format`,
`unsupported_source`, `invalid_document`, `empty`, `too_large`, `invalid_encoding`, `unknown_field`,
`sequence`, `link`, `transport_event`, `content_hash`, `fork_marker`, `fork_lineage`, `fork_prefix`, or
`head_mismatch`.

The tests reproduce every golden vector in the pinned upstream `hack/verify_chain.py` and an
exact canonical byte literal for a delete record.

### Limits of an unkeyed chain

The record chain uses plain SHA-256 with no key or signature. Verification proves that the
records are internally consistent. It doesn't prove who wrote them, and it doesn't detect a
consistent truncation of the tail: an export cut after record N, with `source_head` rewritten to
record N, verifies. To detect truncation or a rewritten history, compare the verified head hash
with a head that you received through a separate trusted channel. `source_head` in the export is
metadata from the exporter, not an external anchor.

### Regenerate the reference fixture

The committed fixture in `tests/Agentweaver.Providers.Sessions.AgentSessions.Tests/Fixtures/AgentSessions` holds
a parent session and a child session forked at seq 5. The upstream controller and echo agent
produce it with no model calls. To regenerate it, run:

```bash
cd tools/agent-primitives
go run ./cmd/agentsessions-fixture -out ../../tests/Agentweaver.Providers.Sessions.AgentSessions.Tests/Fixtures/AgentSessions
```

Each run writes new timestamps and execution IDs, so commit both files together. CI runs `gofmt`, `go vet`, and `go test` for the module in the `go`
job, and the .NET tests verify the committed fixture.
