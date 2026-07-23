# Agent-host token delivery — Deep Dive

AgentHost pods act on behalf of the signed-in GitHub user that owns a run. They clone, push, and call GitHub APIs with that user's token. This page describes the current warm-pool delivery model.

The A2A turn endpoint has a separate per-run bearer token. `KubernetesSandboxExecutor` sends it in `POST /configure`, `RemoteAgentProxy` uses it as `Authorization: Bearer ...` on `message:stream`, and AgentHost rejects turns without the configured token.

## Delivery model

Each authenticated user's GitHub OAuth token is stored in Azure Key Vault under a per-user secret name (`ghtok-user--{base32(userId)}`). AgentHost pods no longer mount user tokens through CSI and no longer require per-run `SecretProviderClass` objects.

The AgentHost warm pool keeps two pods running in standby. Each sandbox pod runs as a **dedicated managed identity (`agentweaver-agenthost-identity`) with no Key Vault role assignments** (issue #471), so untrusted shell/tool execution inside the pod cannot exchange its workload-identity token for a vault access token and read other users' secrets. Because the sandbox has no vault access, the run owner's token is **brokered by the API**: the API resolves it on the API side (which legitimately holds Key Vault access) and delivers it in-memory in the one-time `/configure` body.

At run launch the executor claims one warm pod and calls:

```http
POST /configure
Content-Type: application/json

{
  "runId": "...",
  "userId": "...",
  "turnBearerToken": "...",
  "gitHubAccessToken": "...",
  "kvUserSecretName": "ghtok-user--..."
}
```

`AgentHostRuntimeState` stores those values once. `KeyVaultUserTokenProvider` prefers the brokered `gitHubAccessToken` and skips Key Vault entirely. Only when no brokered token is present does it attempt a direct `SecretClient` + `DefaultAzureCredential` fetch of `KvUserSecretName` — a defense-in-depth fallback that now fails closed because the sandbox identity has no Key Vault roles. The token is cached in memory for the pod lifetime.

## End-to-end flow

```mermaid
sequenceDiagram
    participant User as User (browser sign-in)
    participant API as Agentweaver API / Executor
    participant Claim as SandboxClaim
    participant Pod as Warm AgentHost pod
    participant State as AgentHostRuntimeState
    participant KV as Azure Key Vault
    participant Agent as CopilotAIAgent

    User->>API: GitHub OAuth callback
    API->>KV: store secret ghtok-user--{base32(userId)}
    API->>Claim: bind shared agentweaver-agent-host warm pool
    Claim-->>API: Ready with pod IP
    API->>KV: resolve run owner's token (API identity)
    API->>Pod: POST /configure(runId, userId, token, gitHubAccessToken, kvUserSecretName, workingDirectory)
    Pod->>State: TryConfigure(...) one time
    Pod->>Agent: SetupAsync after configure
    Note over Pod,KV: Sandbox identity has NO KV roles; uses brokered gitHubAccessToken (no vault call)
    Pod-->>API: /healthz ready
    API->>Pod: A2A message:stream with bearer token
```

## Components

- **`AgentHostRuntimeState`** — mutable singleton populated by `/configure` or by the backward-compatible env launch path. `TryConfigure(...)` uses `Interlocked.CompareExchange` so only the first configuration wins.
- **`AgentHostStartupService`** — enters standby when no `RunId` is present at startup, logs that it is waiting for `/configure`, and runs `SetupAsync` only after `ConfigureAsync(...)` is called. Env-launched pods with `RunId` still initialize immediately.
- **`POST /configure`** — accepts `runId`, `userId`, `turnBearerToken`, optional `gitHubAccessToken` (the API-brokered token, issue #471), optional `kvUserSecretName` (fallback secret name), and optional `workingDirectory`; returns `400` for missing `runId`, `409` if already configured, and is excluded from the readiness gate. `workingDirectory` is `Run.WorktreePath`, so setup and file tools root at the same shared worktree named by the system prompt.
- **`KeyVaultUserTokenProvider` / `KeyVaultGitHubTokenStore` / `RuntimeUserScopeProvider`** — serve the configured user's token. `KeyVaultUserTokenProvider` prefers the brokered `gitHubAccessToken`; its direct Key Vault fetch is a fail-closed fallback because the sandbox identity has no vault roles.

## Security trade-off

The old CSI model provided infrastructure-layer isolation: each pod mounted a filesystem projection containing only one user's token. The warm-pool model moves that boundary to the application layer. With issue #471, the sandbox pod runs as a **dedicated managed identity (`agentweaver-agenthost-identity`) that holds no Key Vault roles**, so the pod cannot read any vault secret — even the run owner's. The run owner's token is instead resolved by the API and brokered in-memory through the one-time `/configure` call.

Compensating controls:

| Property | Detail |
|---|---|
| One-time configuration | `AgentHostRuntimeState.TryConfigure` rejects reconfiguration with `409`. |
| `/configure` reachability | Not bearer-protected by design; NetworkPolicy restricts AgentHost ingress to API/worker pods. |
| Turn auth unchanged | `POST /a2a/agent/v1/message:stream` still requires the per-run bearer token. |
| Less etcd exposure | `TurnBearerToken` is no longer written into `SandboxClaim.spec.env`; it travels over in-cluster HTTP to the claimed pod. |
| No user-token CSI | No per-run SPC, CSI volume, or mounted user-token file exists on the pod. |
| No sandbox vault access | The AgentHost identity has **no Key Vault role assignments** (issue #471); the run owner's token is brokered via `gitHubAccessToken` in `/configure`, and the residual direct-fetch fallback fails closed. |

## Configuration reference

| Config key | Default | Notes |
|---|---|---|
| `AgentHost:KeyVaultUri` | *(unset)* | Vault URI for the legacy runtime-fetch fallback. Fails closed under the KV-less sandbox identity (issue #471); the run owner's token is delivered via the brokered `gitHubAccessToken` in `/configure`. |
| `AgentHost:KvTokenMountPath` | *(unset)* | Local compatibility file path. Superseded in AKS by the brokered `/configure` token. |
| `AgentHost:UseSharedTokenStore` | `false` | Local compatibility only; production AKS does not mirror user tokens to shared storage. |

## Source

| Concern | File |
|---|---|
| Configure endpoint and runtime wiring | `apps/Agentweaver.AgentHost/Program.cs` |
| Runtime state | `apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs` |
| Standby/configure lifecycle | `apps/Agentweaver.AgentHost/AgentHostStartupService.cs` |
| KV user-token provider/store/scope | `apps/Agentweaver.AgentHost/KeyVaultUserTokenProvider.cs` |
| Executor configure call | `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs` |
| Warm pool | `k8s/base/sandbox-warmpool-agenthost.yaml` |
| AgentHost template | `k8s/base/sandbox-template-agenthost.yaml` |

## Related reading

- [Auth & Security](./auth-security.md) — overall credential model and `/configure` security.
- [Sandbox pod execution](./sandbox-pod-execution.md) — pod lifecycle, warm pool, reaper, and quota.
- [Sandbox pods reference](../reference/sandbox-pods.md) — flags, warm-pool sizing, token delivery, and security properties.
- [Infrastructure & deployment](./infra-deployment.md) — cluster topology and Key Vault setup.
