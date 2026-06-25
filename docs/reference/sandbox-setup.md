# Sandbox setup

This guide covers installing and configuring sandboxed execution for operators. For the full design rationale, see [architecture/sandboxed-execution.md](../architecture/sandboxed-execution.md).

## Security caveat

`mxc` is an early preview. Its profiles are not yet hardened security boundaries. The sandbox is a defense-in-depth layer on top of Agentweaver's existing path containment and deny-by-default governance. Do not rely on it as the sole security control.

When the `processcontainer` backend is active on Windows, network allowlist enforcement is not available (see [Network allowlist gap](#network-allowlist-gap)).

## Sandbox backends

At run start, `SandboxExecutorFactory` selects one executor for the host platform and emits a `sandbox.selected` event (`backend`, `isRealIsolation`, `reason`). The selection order is:

| Backend (`BackendName`) | Platform | Selected when |
| --- | --- | --- |
| `processcontainer` (Mxc) | Windows | mxc binaries are present (first choice on Windows). |
| `wsl-bwrap` / `wsl-unshare` (WslMxc) | Windows | processcontainer is unavailable and WSL2 has a usable backend. `wsl-bwrap` confines the filesystem and isolates namespaces; `wsl-unshare` isolates namespaces only. |
| `linux-bwrap` (LinuxBwrap) | Linux | bubblewrap (`bwrap`) is available — the preferred Linux backend (selective mount allowlist). |
| `lxc-native-linux` (LinuxNativeMxc) | Linux | bubblewrap is unavailable but `lxc-exec` is present. |
| `kubernetes-sandbox-claim` (K8s) | In-cluster | The API runs inside Kubernetes (`KUBERNETES_SERVICE_HOST` is set). See [Kubernetes (in-cluster)](#kubernetes-in-cluster). |
| `direct` (Passthrough) | Any | No isolation backend is available. Commands run **directly on the host** with no isolation layer, relying on deployment-level isolation (e.g. a container). It is not deny-by-default — shell still executes. |

`IsRealIsolation` is `true` for every real backend and `false` for `direct`. Shell execution requires either `IsRealIsolation == true` **or** the `direct` backend; any other non-isolating executor denies `run_command` at the governance gate.

## Windows ARM64

### 1. Download binaries

Download `mxc-release-binaries.zip` from https://github.com/microsoft/mxc/releases. Validated against v0.6.1.

```powershell
Expand-Archive mxc-release-binaries.zip -DestinationPath C:\mxc-bin
```

### 2. Set MXC_BIN_DIR

```powershell
[System.Environment]::SetEnvironmentVariable("MXC_BIN_DIR", "C:\mxc-bin", "Machine")
```

Restart any terminals or services that need to pick up the variable.

### 3. Verify

```powershell
& "C:\mxc-bin\arm64\wxc-exec.exe" --probe
```

A response containing `"tier": "base-container"` or any tier other than an error confirms the binary is working. If the command fails, check that the path `C:\mxc-bin\arm64\wxc-exec.exe` exists.

When Agentweaver starts, it logs the selected executor. Look for a line like:

```
SandboxExecutorFactory: selected MxcSandboxExecutor (processcontainer)
```

If you see `falling back to PassthroughExecutor`, the binary was not found or the platform probe failed. The `direct` backend then runs commands on the host with **no isolation** (it does not deny shell); rely on deployment-level isolation instead.

## Linux cloud

On Linux the factory prefers **bubblewrap** (`linux-bwrap`): if `bwrap` is available on `PATH`, it is selected and uses a selective mount allowlist confined to the worktree. No environment variable is required.

When bubblewrap is unavailable, the factory falls back to the `lxc-native-linux` backend, which probes for `lxc-exec` at two absolute paths, in order:

1. `/usr/local/bin/lxc-exec`
2. `/usr/bin/lxc-exec`

Install `bwrap` (or `lxc-exec` at one of those paths) before starting the server. If neither a bubblewrap nor an lxc backend is found at startup, Agentweaver falls back to the `direct` (passthrough) executor, which runs commands on the host with no isolation — it does not deny shell.

## Kubernetes (in-cluster)

When the API runs inside a Kubernetes cluster, `SandboxExecutorFactory.IsInCluster` (detected via the `KUBERNETES_SERVICE_HOST` environment variable) is `true` and the API overrides the platform factory with the `kubernetes-sandbox-claim` backend (`KubernetesSandboxExecutor`). This backend provides real isolation (Kata VM) and NetworkPolicy egress restriction, so `HasNetworkWarning` is `false`.

Each shell command runs inside a pre-warmed pod obtained through a **`SandboxClaim`** custom resource:

1. The executor creates a `SandboxClaim` (`apiVersion: extensions.agents.x-k8s.io/v1alpha1`, plural `sandboxclaims`), which adopts a warm pod from the pool.
2. It polls until the claim reaches `phase: Bound` and reports a pod name.
3. It runs the command via pod-exec (the Kubernetes WebSocket exec API) against the `agentweaver-sandbox` container.
4. It deletes the claim on completion; the controller GC cleans up the pod and service.

Configuration is bound from the `Sandbox:Kubernetes` section:

| Option | Default | Notes |
| --- | --- | --- |
| `Namespace` | `agentweaver` | Namespace the claims and pods live in. |
| `TemplateRef` | `agentweaver-sandbox` | The SandboxTemplate the warm pool is built from. |
| `TimeoutSeconds` | `600` | Per-command timeout. |

### The `agentweaver-sandbox` image

The warm pods run the image built from `apps/agentweaver-sandbox/Dockerfile`. It is based on `ubuntu:24.04` and ships the language runtimes agent workloads need: `git`, Python 3, Node.js/npm, and the .NET SDK 9.0. The container runs as non-root (uid/gid 1000) to match the SandboxTemplate `securityContext`. `readOnlyRootFilesystem` is enforced by the template; writable `emptyDir` mounts are provided at `/workspace` (agent work) and `/tmp` (tool scratch). The entrypoint is `sleep infinity` — the pod stays alive for pod-exec sessions and the agent drives all execution via exec rather than a long-running server process.

### Port-forwarding to a sandbox pod

A run's sandbox pod can be reached for preview/debugging via the run port-forward endpoints, which start a `kubectl port-forward` to the pod: `POST /api/runs/{runId}/sandbox/port-forward` (body `{ "target_port": <int> }`), `GET /api/runs/{runId}/sandbox/port-forward` to list active sessions, and `DELETE /api/runs/{runId}/sandbox/port-forward/{sessionId}` to stop one. See the [API reference](./api.md).

## Configuring sandbox policy

Each project's sandbox policy lives at `.agentweaver/settings.yml` in the project repository root. This file is version-controlled alongside the code: changes are reviewable via PR and auditable via `git log`. When the file does not exist, default values apply.

### Example `.agentweaver/settings.yml`

```yaml
# Settings are organized by group. Each group is independent — adding a new
# group does not affect existing ones.

sandbox:
  shell_enabled: true
  # Allow outbound network access inside the sandbox.
  # Default: false. Agentweaver defaults to blocked for security;
  # Copilot CLI defaults to true. Set to true only when agents need
  # external package downloads (e.g. npm install, go get).
  network_enabled: false
  allowed_repository_roots: []
  destructive_command_patterns:
    - rm -rf
    - del /s
    - "format "
    - mkfs
    - dd if=
    - git push --force
    - git reset --hard
  require_approval_for_all_shell: false
  redact_pii: true
  max_output_bytes: 4194304

# Other groups can be added here in the future, e.g.:
# review:
#   require_approval: true
```

The file is optional — default values apply when absent. Changes take effect on the next run; no server restart is needed.

The API endpoints `GET /api/sandbox-policy` and `PUT /api/sandbox-policy` read and write this file. After a `PUT`, the operator should `git add .agentweaver/settings.yml && git commit` to record the change in the project history.

### Read the current policy

```http
GET /api/sandbox-policy?repository_path=C:/path/to/repo
Authorization: Bearer <api-key>
```

Returns the current policy, or the default policy if none has been set.

### Update the policy

```http
PUT /api/sandbox-policy
Authorization: Bearer <api-key>
Content-Type: application/json

{
  "repository_path": "C:/path/to/repo",
  "shell_enabled": true,
  "network_enabled": false,
  "require_approval_for_all_shell": false,
  "destructive_command_patterns": ["rm -rf", "del /s", "format ", "mkfs", "dd if="],
  "allowed_repository_roots": [],
  "redact_pii": true,
  "max_output_bytes": 4194304
}
```

All fields except `repository_path` are optional in the request body; omitted fields keep their current values.

### Disabling shell

To prevent shell execution for a project:

```json
{
  "repository_path": "C:/path/to/repo",
  "shell_enabled": false
}
```

With `shell_enabled: false`, the `run_command` tool is removed from the model's tool list for that project's runs and the governance gate denies it regardless of isolation state. This takes effect on the next run start (policies are read at run creation time).

### Requiring approval for all shell commands

```json
{
  "repository_path": "C:/path/to/repo",
  "require_approval_for_all_shell": true
}
```

When `true`, every `run_command` invocation pauses the run and emits a `shell.approval_required` event pending human approval, not just commands matching `destructive_command_patterns`. The operator approves or denies via `POST /api/runs/{id}/shell-approvals` and `POST /api/runs/{id}/shell-denials` (see the [API reference](./api.md)); the run resumes once a decision arrives.

## Network allowlist gap

On Windows, the `processcontainer` backend runs with unrestricted outbound network access. There is no per-host allowlist. When this backend is selected, runs emit a `sandbox.warning` event:

```json
{
  "category": "network-unrestricted",
  "message": "Sandbox running with unrestricted network on Windows (allowlist enforcement unavailable). Data exfiltration surface is open.",
  "backend": "processcontainer"
}
```

If network restriction is required:

- Switch to the WSL2 path (ensure WSL2 is installed with a Linux distribution). The `wsl-bwrap` backend confines the filesystem to the worktree.
- Or configure a proxy on the mxc policy (not yet exposed through the sandbox policy API — requires code changes).
- Or run on a Linux host where the `lxc-native-linux` backend is used.
