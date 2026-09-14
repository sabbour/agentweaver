# AgentHost capability credential delivery

AgentHost receives purpose-bound provider and repository capability data through one-time configuration, not ambient user-token stores, mounted token files, or Key Vault. Credential lifetimes differ: Copilot capability expiry is checked, BYOK has no equivalent expiry field, and turn/preview credentials are bound to the run lifecycle.

The A2A turn endpoint uses a separate per-run bearer token. Production `KubernetesSandboxExecutor` mints and sends it during `POST /configure`; `RemoteAgentProxy` sends it with each turn. AgentHost enforces equality when a nonempty token is configured. See the claim/configure sequence.

## Provider delivery

`RunGitHubCapabilitySnapshotLifecycle` captures immutable, purpose-bound snapshots before launch. In GitHub Copilot mode, the API redeems a live `UnattendedCopilot` capability and passes the bounded credential to the selected pod.

BYOK is the alternative. When `byokProviderConfiguration` is supplied, AgentHost uses it instead of `copilotCredential`. A live Copilot credential is required only when the BYOK configuration is absent.

Missing, revoked, mismatched, and expired Copilot capabilities fail closed. Never log credential values. AgentHost keeps configured credentials in memory, but the server side deliberately persists preview-control credentials under a deterministic per-run secret-store key for cross-replica callbacks. That persistence and deletion on actual release/orphan cleanup are best-effort; a missing vault configuration uses an in-memory server store and cannot provide durable cross-replica recovery.

## `/configure` request body

| Field | Required | Meaning |
| --- | --- | --- |
| `runId` | Yes | The Agentweaver run this pod executes. |
| `copilotCredential` | When no BYOK configuration is supplied | Immutable, run-bound Copilot capability credential. |
| `byokProviderConfiguration` | When `copilotCredential` is absent | Run-scoped configuration for the active BYOK provider. |
| `turnBearerToken` | No | Per-run bearer token for `POST /a2a/agent/v1/message:stream`. |
| `repositoryAccessToken` | No | Separately redeemed `UnattendedRepository` capability for scoped Git operations. |
| `sharedWorkingDirectory` | No | API-visible run worktree. |
| `previewRunnerCredential` | No | Per-run bearer for pod-root control calls. |
| `mcpBrokerToken` | For `OperatorAssistant` purpose only | Purpose-restricted broker token; not a general run credential. |

## Security properties

| Property | Detail |
| --- | --- |
| Purpose binding | Provider and repository capabilities have separate purposes. |
| Run binding | A credential provider rejects a different run ID. |
| Bounded lifetime | Copilot capability expiry is enforced; BYOK, turn and preview lifetimes have different contracts. |
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

<details id="diagram-context-sandbox-pod-execution-fig6" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Bind, reach standby, configure once</td></tr>
<tr><td>takeaway</td><td>Liveness precedes configuration; production delivers run credentials out of pod specs.</td></tr>
<tr><td>Prepare launch</td><td>Prepare launch</td></tr>
<tr><td>Prepare launch</td><td>Resolve provider and run context</td></tr>
<tr><td>Prepare launch</td><td>Mint fresh turn token</td></tr>
<tr><td>Claim warm pod</td><td>Claim warm pod</td></tr>
<tr><td>Claim warm pod</td><td>Create/adopt; omit spec.env</td></tr>
<tr><td>Claim warm pod</td><td>Wait Ready + bound pod name</td></tr>
<tr><td>Resolve and register</td><td>Resolve and register</td></tr>
<tr><td>Resolve and register</td><td>Pod mapping / token registry</td></tr>
<tr><td>Resolve and register</td><td>Resolve actual pod IP</td></tr>
<tr><td>GET /healthz</td><td>GET /healthz</td></tr>
<tr><td>GET /healthz</td><td>HTTP 200 standby</td></tr>
<tr><td>GET /healthz</td><td>Listener liveness, not turn ready</td></tr>
<tr><td>POST /configure</td><td>POST /configure</td></tr>
<tr><td>POST /configure</td><td>Identity / workspace / approvals</td></tr>
<tr><td>POST /configure</td><td>Copilot capability OR BYOK</td></tr>
<tr><td>AgentHost setup</td><td>AgentHost setup</td></tr>
<tr><td>AgentHost setup</td><td>One-time configuration</td></tr>
<tr><td>AgentHost setup</td><td>Effective workspace + HOME</td></tr>
<tr><td>Configuration guards</td><td>Configuration guards</td></tr>
<tr><td>Configuration guards</td><td>Second configure: 409</td></tr>
<tr><td>Configuration guards</td><td>Other routes: 503 before ready</td></tr>
<tr><td>Register effective endpoint</td><td>Register effective endpoint</td></tr>
<tr><td>Register effective endpoint</td><td>Return effective working directory</td></tr>
<tr><td>Register effective endpoint</td><td>Shared/local/private fallback</td></tr>
<tr><td>First A2A turn</td><td>First A2A turn</td></tr>
<tr><td>First A2A turn</td><td>Production sends turn bearer</td></tr>
<tr><td>First A2A turn</td><td>Equality guard when nonempty</td></tr>
<tr><td>arrow-1</td><td>launch</td></tr>
<tr><td>arrow-2</td><td>bound</td></tr>
<tr><td>arrow-3</td><td>poll</td></tr>
<tr><td>arrow-4</td><td>reachable</td></tr>
<tr><td>arrow-5</td><td>setup</td></tr>
<tr><td>arrow-6</td><td>ready</td></tr>
<tr><td>arrow-7</td><td>invoke</td></tr>
<tr><td>note-0</td><td>Top, middle and bottom rows are successive launch stages.</td></tr>
<tr><td>note-1</td><td>Repository / preview / broker credentials have separate purposes.</td></tr>
<tr><td>note-2</td><td>Optional schema fields do not imply unconditional endpoint enforcement.</td></tr>
<tr><td>notes</td><td>Top, middle and bottom rows are successive launch stages.; Repository / preview / broker credentials have separate purposes.; Optional schema fields do not imply unconditional endpoint enforcement.</td></tr>
</tbody></table>
</details>
