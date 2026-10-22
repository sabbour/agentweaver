# AgentSessions Sessions provider

This package is one optional implementation of the Agentweaver Sessions provider contract. It
verifies [AgentSessions](https://github.com/agent-primitives/sessions) record exports and serves
them as read-only replay through `ISessionsJournal`. The native PostgreSQL journal stays the
default Sessions provider, and Agentweaver builds and runs without this package. See
[Agent primitives proof of concept](../../docs/architecture/design/agent-primitives.md#agentsessions-as-an-optional-sessions-provider).

| Type | Purpose |
| --- | --- |
| `AgentSessionsSessionsProvider` | Provider descriptor, registration, negotiation, and run pinning. |
| `AgentSessionsRecordJournal` | Read-only `ISessionsJournal` over verified record exports. |
| `AgentSessionsRecordVerifier` | Independent hash, chain, and fork verification. |
| `AgentSessionsPin` | The pinned upstream commit and contract version. |

## Vendored contract

`Protos/agentsessions/v1/common.proto` is a byte-identical copy of the file at the same path in
[agent-primitives/sessions](https://github.com/agent-primitives/sessions) at commit
`465e82175da7815db39ec14852deb3949ff82670`. The upstream project publishes it under the MIT
License. Don't edit the file. To move to a new upstream commit, copy the file again, update
`AgentSessionsPin`, regenerate the test fixture in
`tests/Agentweaver.Providers.Sessions.AgentSessions.Tests/Fixtures/AgentSessions`, and update the
golden vectors in `AgentSessionsRecordVerifierTests`.
