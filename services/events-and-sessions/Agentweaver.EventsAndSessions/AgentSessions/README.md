# AgentSessions record verifier

This folder holds the independent .NET verifier for AgentSessions record exports. See
[Agent primitives proof of concept](../../../../docs/architecture/design/agent-primitives.md).

## Vendored contract

`Protos/agentsessions/v1/common.proto` is a byte-identical copy of the file at the same path in
[agent-primitives/sessions](https://github.com/agent-primitives/sessions) at commit
`465e82175da7815db39ec14852deb3949ff82670`. The upstream project publishes it under the MIT
License. Don't edit the file. To move to a new upstream commit, copy the file again, update
`AgentSessionsPin`, regenerate the test fixture, and update the golden vectors in
`AgentSessionsRecordVerifierTests`.
