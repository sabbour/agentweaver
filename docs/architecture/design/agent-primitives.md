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

`AgentSessionsRecordVerifier` in `Agentweaver.EventsAndSessions` verifies an export without Go
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

The committed fixture in `tests/Agentweaver.EventsAndSessions.Tests/Fixtures/AgentSessions` holds
a parent session and a child session forked at seq 5. The upstream controller and echo agent
produce it with no model calls. To regenerate it, run:

```bash
cd tools/agent-primitives
go run ./cmd/agentsessions-fixture -out ../../tests/Agentweaver.EventsAndSessions.Tests/Fixtures/AgentSessions
```

Each run writes new timestamps and execution IDs, so commit both files together. CI runs `gofmt`, `go vet`, and `go test` for the module in the `go`
job, and the .NET tests verify the committed fixture.
