# AgentHost capability credential delivery — Deep Dive

AgentHost pods consume only short-lived, immutable credentials redeemed from capability snapshots captured for a run. They do not read GitHub user-token stores, mounted token files, Key Vault, or host configuration.

The A2A turn endpoint has a separate per-run bearer token. `KubernetesSandboxExecutor` sends it in `POST /configure`, `RemoteAgentProxy` uses it as `Authorization: Bearer …` on `message:stream`, and AgentHost rejects turns without the configured token.

## Delivery model

`RunGitHubCapabilitySnapshotLifecycle` captures purpose-bound snapshots before launch and inherits fresh opaque snapshot references on retry and resume. At AgentHost launch, `RunGitHubCapabilityCredentialProvider` fences the run's `UnattendedCopilot` snapshot before and after the vault read, bounds its expiry, and supplies the resulting `GitHubCapabilitySnapshotCredential` only to the selected pod's one-time `/configure` request.

The warm AgentHost pool starts with no run identity or GitHub credential. The executor claims a pod, requires a live credential for that exact run, and sends the opaque snapshot reference, token, and expiry in memory. `AgentHostGitHubCapabilityCredentialProvider` permits the runtime to use that value only if its run ID matches the configured run and it remains unexpired. Missing, revoked, mismatched, and expired credentials fail closed. Credentials must never be logged or persisted.

## Classifier capability requirement

Copilot-backed classifiers use the same explicit run-bound Copilot capability. A connected GitHub
identity or installation scope is not classifier authorization. A non-run classifier that needs model
classification but lacks an explicitly issued capability does not create an `unbound` run, consult
ambient credentials, call a model, choose a default, or silently degrade. It returns a clear
connect-GitHub requirement instead. Marketplace auto-browse can still list a repository's directly
discoverable `SKILL.md` files; when it requires model classification, it asks the user to connect a
GitHub account with Copilot access.

## `/configure` request body

| Field | Required | Meaning |
|---|---|---|
| `runId` | Yes | The Agentweaver run this pod executes. |
| `copilotCredential` | Yes | Immutable, run-bound, unexpired Copilot snapshot credential. |
| `turnBearerToken` | No | Per-run bearer token required by `POST /a2a/agent/v1/message:stream`. |
| `repositoryAccessToken` | No | Repository capability credential supplied separately for narrowly-scoped Git/GitHub operations. |
| `sharedWorkingDirectory` | No | API-visible run worktree. |
| `previewRunnerCredential` | No | Fresh per-run bearer for authenticated pod-root control calls. |

## Security properties

| Property | Detail |
|---|---|
| Purpose binding | Copilot and repository snapshots are distinct and cannot be redeemed for the other operation. |
| Run binding | A Host credential provider rejects another run ID. |
| Snapshot fencing | The broker validates snapshot liveness before and after credential redemption. |
| Bounded lifetime | The credential expiry is capped by the broker and enforced by Host and Runtime. |
| One-time delivery | `/configure` accepts exactly one configuration per warm pod. |
| No ambient fallback | AgentHost has no Key Vault, CSI, shared-filesystem, environment-token, or user-token-store path. |

## Source

| Concern | File |
|---|---|
| Snapshot lifecycle and broker | `apps/Agentweaver.Api/Auth/RunGitHubCapabilitySnapshotLifecycle.cs`, `GitHubCapabilityBroker.cs` |
| API credential provider and configure delivery | `apps/Agentweaver.Api/Sandbox/RunGitHubCapabilityCredentialProvider.cs`, `KubernetesSandboxExecutor.cs` |
| Host credential provider and state | `apps/Agentweaver.AgentHost/AgentHostGitHubCapabilityCredentialProvider.cs`, `AgentHostRuntimeState.cs` |

## Related reading

- [Auth & Security](./auth-security.md)
- [Sandbox pod execution](./sandbox-pod-execution.md)
- [Sandbox pods reference](../reference/sandbox-pods.md)
