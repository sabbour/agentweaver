# AgentHost capability credential delivery

AgentHost pods receive only short-lived credentials that are bound to a run and its execution purpose. They do not read user-token stores, mounted token files, Key Vault, or host configuration.

The A2A turn endpoint uses a separate per-run bearer token. `KubernetesSandboxExecutor` sends it during `POST /configure`. `RemoteAgentProxy` sends it with each turn. AgentHost rejects a turn without its configured token.

## Provider delivery

`RunGitHubCapabilitySnapshotLifecycle` captures immutable, purpose-bound snapshots before launch. In GitHub Copilot mode, the API redeems a live `UnattendedCopilot` capability and passes the bounded credential to the selected pod.

BYOK is the alternative. When `byokProviderConfiguration` is supplied, AgentHost uses it instead of `copilotCredential`. A live Copilot credential is required only when the BYOK configuration is absent.

Missing, revoked, mismatched, and expired credentials fail closed. Credentials must not be logged or persisted.

## `/configure` request body

| Field | Required | Meaning |
| --- | --- | --- |
| `runId` | Yes | The Agentweaver run this pod executes. |
| `copilotCredential` | When no BYOK configuration is supplied | Immutable, run-bound Copilot capability credential. |
| `byokProviderConfiguration` | When `copilotCredential` is absent | Run-scoped configuration for the active BYOK provider. |
| `turnBearerToken` | No | Per-run bearer token for `POST /a2a/agent/v1/message:stream`. |
| `repositoryAccessToken` | No | Repository capability credential for scoped Git operations. |
| `sharedWorkingDirectory` | No | API-visible run worktree. |
| `previewRunnerCredential` | No | Per-run bearer for pod-root control calls. |
| `mcpBrokerToken` | No | Broker token for MCP operations that require it. |

## Security properties

| Property | Detail |
| --- | --- |
| Purpose binding | Provider and repository capabilities have separate purposes. |
| Run binding | A credential provider rejects a different run ID. |
| Bounded lifetime | The runtime enforces credential expiry. |
| One-time delivery | `/configure` accepts one configuration per warm pod. |
| No ambient fallback | AgentHost has no Key Vault, CSI, shared-filesystem, environment-token, or user-token-store path. |

## Source

- `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs`
- `apps/Agentweaver.AgentHost/Program.cs`
- `apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs`

## Related reading

- [Auth and security](./auth-security.md)
- [Sandbox pod execution](./sandbox-pod-execution.md)
- [Sandbox pods reference](../reference/sandbox-pods.md)
