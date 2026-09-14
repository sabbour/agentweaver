# Sandboxed execution

This document describes the design of mxc-based sandboxed command execution in Agentweaver — how the sandbox engine plugs into the existing governance and runner layers, how executor selection works, and the security model behind it.

## Overview

Agentweaver originally permitted no shell execution. The governance policy categorically denied any `shell` tool call, and agents were restricted to path-contained in-process file operations.

This feature adds sandboxed shell execution by adopting Microsoft's `mxc` sandboxing engine. The key constraint is that `mxc` is an early preview and its own documentation says its profiles are not yet hardened security boundaries. It is therefore adopted as a **defense-in-depth layer only** — it augments the existing in-process path containment and deny-by-default governance, and never replaces them.

Live `run_command` registration requires `ShellEnabled` and either real isolation or `direct`. The local factory may fall back to direct automatically, not only through explicit opt-in. Direct is not isolation and requires a trusted/disposable surrounding environment. Native shell stays denied.

Once a backend is selected (see the [sandbox deep dive](./sandbox.md)), a `run_command` invocation flows through the triple-layer governance gate, into the chosen executor, and back out as redacted output events:

## Executor selection

Executor selection happens at startup via `SandboxExecutorFactory.Create`. The factory probes the host in order and returns the first available executor:

| Order | Backend name | Platform condition |
| --- | --- | --- |
| 1 | `processcontainer` | Windows + `wxc-exec.exe` found + `MxcSdk.GetPlatformSupport().IsSupported` |
| 2 | `wsl-bwrap` / `wsl-unshare` | Windows + WSL2 available (`wsl-bwrap` when bubblewrap is present in the distro, otherwise `wsl-unshare`) |
| 3 | `linux-bwrap` | Linux + bubblewrap (`bwrap`) available — preferred Linux backend (selective mount allowlist) |
| 4 | `lxc-native-linux` | Linux + `lxc-exec` found at `/usr/local/bin/lxc-exec` or `/usr/bin/lxc-exec` (only when bwrap is unavailable) |
| 5 | `direct` | Fallback when no isolation backend is available, **or** selected explicitly via `direct: true` in `.agentweaver/settings.yml` |

The API router first chooses Kubernetes versus local from `Sandbox:Backend` and cluster detection. Explicit `local` bypasses cluster selection; selected Kubernetes initialization fails closed. Only then does the local path invoke the factory. See the host selector.

The selected executor is injected into the per-run governance context and GitHub
Copilot SDK runner. Its key properties are:

- **`IsRealIsolation`** — `true` for `processcontainer`, `wsl-bwrap`, `linux-bwrap`, `lxc-native-linux`, and `kubernetes-sandbox-claim`; `false` for `wsl-unshare` and the `direct` fallback. Shell execution requires this to be `true`, **except** for the `direct` backend, which can be an explicit choice or automatic fallback (see [Layer C](#layer-c-executor-gate)).
- **`HasNetworkWarning`** — `true` for the Windows `processcontainer` and `wsl-unshare` tiers, whose backends cannot enforce a network allowlist. When set, the runner emits a `sandbox.warning` event (see [Limitations](#limitations)).

The executor selection decision, backend name, and probe reason are emitted as a `sandbox.selected` event when a run starts.

## Command timeouts

`run_command` uses a five-minute timeout when the caller does not provide `timeout_ms`.
An explicit caller timeout is used as requested unless the active runtime policy has a
minimum or maximum limit. The controlled Build/Test shell keeps its ten-minute minimum
and maximum limit.

The GitHub Copilot SDK runner adds a separate five-minute grace period after the
effective command timeout. The executor cancels the command first and returns
`timed_out: true`. Its streaming watchdog stops the agent turn only when the process
still has not exited after that grace period and emits shell-progress heartbeats while
waiting. This extra time is important for Kata VM pods, where process termination can
take longer while the Kata agent relays the signal.

## Tool architecture

This table is the canonical tool catalog, not a fixed live count. The runner selects intent/outcome, optional question and controlled-command tools; native file tools avoid duplicate custom exposure. AgentHost adds purpose-appropriate preview tools.

| Tool | Purpose | Conditional |
| --- | --- | --- |
| `run_command` | Run a shell command in the sandbox | `ShellEnabled && (IsRealIsolation || BackendName == "direct")` |
| `read_file` | Read a file inside the sandbox root | No |
| `grep_search` | Search file contents with a regex | No |
| `file_search` | Find files by name pattern | No |
| `str_replace_editor` | Make targeted line or text replacements | No |
| `apply_patch` | Apply a unified-diff patch | No |
| `create_file` | Create a new file | No |
| `write_file` | Write a file inside the sandbox root | No |
| `report_intent` | Emit an `agent.intent` event for UI display | No |
| `report_outcome` | Report the completed outcome | No |
| `ask_question` | Request a user answer through the run gate | No |

### Permission-gated native tools

The Copilot runner intentionally does not set the SDK `AvailableTools` allowlist: the SDK's native file tool names differ from Agentweaver's logical tool names, and setting the logical allowlist would hide usable file tools from the model. Instead, the deny-by-default `OnPermissionRequest` handler is the authoritative gate for every native file, shell, MCP, or other tool request.

When shell access is not allowed, the permission handler denies shell-like requests before execution.

### Why native shell is always denied

The YAML policy loaded into the governance kernel has an explicit `deny-native-shell` rule that matches `tool_name == 'shell'` with action `Deny`. This fires before the executor gate and before any path check, unconditionally. Shell execution must go through the `run_command` custom tool, which routes through `ISandboxExecutor` under the triple-layer evaluation described in the security model.

## Sandbox policy

Each project stores its `SandboxPolicy` as `.agentweaver/settings.yml` in the project repository root (GitOps). The file is version-controlled alongside the code: policy changes are reviewable via PR and auditable via `git log`. When the file does not exist, `SandboxPolicy.Default(repositoryPath)` applies automatically.

The API (`GET /api/sandbox-policy`, `PUT /api/sandbox-policy`) reads and writes this file. `PUT` writes the YAML file; the operator should commit and push the change to record it in the project history. No database row is required.

| Field | Type | Default | Purpose |
| --- | --- | --- | --- |
| `RepositoryPath` | `string` | — | Lookup key |
| `ShellEnabled` | `bool` | `true` | Whether `run_command` is permitted at all. `false` disables shell regardless of isolation state. |
| `AllowedRepositoryRoots` | `string[]` | `[]` | Additional paths mounted read-only inside the sandbox. If empty, only the run's working directory is accessible. |
| `DestructiveCommandPatterns` | `string[]` | `["rm -rf", "del /s", "format ", ...]` | Patterns that trigger human approval before execution. |
| `RequireApprovalForAllShell` | `bool` | `false` | When `true`, every shell command requires human approval, not just destructive ones. |
| `RedactPii` | `bool` | `true` | Whether to redact emails and IP addresses from command output in addition to secrets. |
| `MaxOutputBytes` | `int` | `4194304` (4 MB) | Output cap. Results exceeding this are truncated and marked with `OutputTruncated: true`. |

The policy is read through `ISandboxPolicyStore.GetPolicyAsync` and is configurable via the API at `GET /api/sandbox-policy` and `PUT /api/sandbox-policy`. See [sandbox-setup.md](../reference/sandbox-setup.md) for operator instructions.
When the settings file has no `sandbox` section, or its `sandbox` section omits
`destructive_command_patterns`, the canonical default approval patterns apply. An explicit list,
including `[]`, is an intentional override.

The API sends one short-lived installation credential for the selected repository and run. When
that credential is used, the sandbox parses the command and starts the approved `git` or `gh`
executable directly instead of placing the credential in a shell environment. Credential-bearing
Git is limited to argument-free `git status`; all other Git subcommands and flags are rejected.
This prevents repository configuration from selecting external diffs, signing programs, filters,
merge drivers, hooks, aliases, helpers, or remote helpers. The direct Git process disables hooks,
credential helpers, filesystem monitors, and recursive submodules. Its GitHub authorization
header is scoped to that process; it is never supplied to a child process.

Credential-bearing `gh` invocations must also match a narrow parsed allowlist. It permits direct
`gh api` and `gh status` calls; explicitly named repository list, view, and fork calls; and
explicitly repository-scoped issue, pull-request, and workflow forms that do not launch another
program. `gh issue develop` is limited to its `--list` form. Repository forks that clone or alter
remotes, pull-request creation and checkout, branch-deleting close or merge commands, and
browser/editor forms are rejected even after operator approval, so they never receive `GH_TOKEN`.
`gh config set` is not allowlisted: editor and pager settings persist and can make a later command
start an external program. Likewise, `gh gist edit` and every other editor-facing gist form remain
outside the allowlist.

The approval policy gates `git push`, remote changes, `gh pr` changes, `gh repo` changes,
`gh workflow run`, `gh api`, `gh secret set`, and `gh auth` commands (including `gh auth token`).
The API does not inspect or proxy these commands.

The system keeps this credential out of pod specs, files, logs, events, annotations, shared environments, and credential-helper files. Normal release and orphan cleanup log failed revocations. The
registry retains failed revocations independently of their SandboxClaim, and each reaper sweep retries
due revocations with capped exponential backoff until they succeed or the credential actually expires.

## Security model

A `run_command` invocation passes three layers before the sandbox engine sees it.

### Layer A — YAML policy

The `GovernanceKernel` evaluates the tool call against the embedded YAML policy. The policy allows `run_command` and all custom file tools by name; everything else is denied by the default `Deny` action. The policy also has an explicit `deny-native-shell` rule for `tool_name == 'shell'` that fires before the default.

At kernel construction, the runtime asserts that the loaded policy's `defaultAction` is `Deny`. If it is not, the run is refused.

### Layer B — path containment backend

`SandboxPolicyBackend` runs unconditionally and independently of layer A. It validates that every file path argument is contained within the sandbox root, and denies any call with no identifiable path argument. For `run_command`, it validates that the `directory` argument resolves inside the sandbox root. Layer B cannot be bypassed by layer A passing.

### Layer C — executor gate

Only for `run_command`, after both A and B allow. `SandboxGovernance.EvaluateToolCall` checks:
1. `executor.IsRealIsolation` — must be `true`.
2. `policy.ShellEnabled` — must be `true`.

These governance checks deny execution except for direct, which bypasses them. Direct can be explicit or an automatic local fallback. Live registration still requires `ShellEnabled`, including direct; approval cannot override policy denial.

Any exception in any layer produces a deny result (fail-closed).

### TOCTOU mitigations

The sandbox root is validated as a non-reparse-point at construction time (`SandboxedFileTools` asserts this). For file operations, the runtime performs an open-then-verify pass: after opening a file handle, it resolves the final path from the handle (`GetFinalPathNameByHandle` on Windows, `/proc/self/fd/<fd>` on Linux) and checks it against the sandbox root again. A handle resolving outside the root is rejected before any bytes are read or written.

### Output redaction

`SandboxOutputRedactor` runs on stdout and stderr before the content reaches log or event streams. It always removes secrets (Bearer tokens, AWS access key IDs, GitHub PATs, PEM private key headers, connection string passwords, generic API key patterns). When `SandboxPolicy.RedactPii` is `true`, it also removes email addresses and IPv4/IPv6 addresses.

### Destructive command detection

Before a `run_command` invocation reaches the executor, the runner checks `SandboxPolicy.DestructiveCommandPatterns` against the command line. If any pattern matches, or if `RequireApprovalForAllShell` is set, the runner computes a deterministic SHA-256 hash (first 16 hex characters, lowercase) of the command and checks whether the operator has already approved it for this run.

- **Not yet approved** — the runner emits a `shell.approval_required` event and returns a message to the model with the approval endpoint (`POST /api/runs/{id}/shell-approvals`) and the `command_hash` to submit. The model retries the same command on the next turn; because the hash is deterministic, it will find the approval.
- **Already approved** — the runner logs the approval and falls through to execution immediately without re-emitting the event.

Approvals are scoped by `(runId, commandHash)`. Local runtime defaults may use an in-memory store; API-hosted runs register durable approval implementations. Not every live approval is process-local or universally cleared at completion.

## Deployment parity

The API router chooses Kubernetes or local first. Local tiers differ; `wsl-unshare` and direct do not report real isolation:

| Target | Executor backend | Notes |
| --- | --- | --- |
| Windows ARM64 (developer) | `processcontainer` | Requires `wxc-exec.exe` on PATH or `MXC_BIN_DIR` set |
| Windows with WSL2 | `wsl-bwrap` / `wsl-unshare` | Falls through to this when processcontainer is unavailable; `wsl-bwrap` when bubblewrap is installed in the distro |
| Linux cloud | `linux-bwrap` | Preferred Linux backend; requires `bwrap` (bubblewrap) |
| Linux cloud (no bwrap) | `lxc-native-linux` | Used when bwrap is unavailable; requires `lxc-exec` at a known absolute path |
| AKS / in-cluster | `kubernetes-sandbox-claim` | Auto-selected when `KUBERNETES_SERVICE_HOST` is set; per-run Kata VM pod from a warm pool |

Both clients (MCP server and Web UI) reach the same endpoints. Executor selection happens in the backend, not the client. There is no client-side isolation logic.

## Windows ARM64 runbook

This covers local developer setup and CI pipelines on Windows ARM64.

### Download and extract binaries

Download `mxc-release-binaries.zip` from https://github.com/microsoft/mxc/releases. The spike was validated against v0.6.1.

```powershell
Expand-Archive mxc-release-binaries.zip -DestinationPath C:\mxc-bin
```

The zip extracts to `arm64\wxc-exec.exe` (and equivalent per-arch subdirectories).

### Set MXC_BIN_DIR

Set the environment variable system-wide or in your CI pipeline:

```powershell
[System.Environment]::SetEnvironmentVariable("MXC_BIN_DIR", "C:\mxc-bin", "Machine")
```

The SDK resolves the binary at `%MXC_BIN_DIR%\arm64\wxc-exec.exe` (where `arm64` matches the process architecture). This variable takes priority over the assembly-adjacent `bin\<arch>\wxc-exec.exe` path.

### Verify

```powershell
& "C:\mxc-bin\arm64\wxc-exec.exe" --probe
```

Expected output:

```json
{
  "tier": "base-container",
  "needsDaclAugmentation": false,
  "warnings": [],
  "probes": {
    "baseContainerApiPresent": true,
    "bfscfgPresent": false,
    "bfsCompiledIn": false
  }
}
```

### Policy version

`MxcSandboxExecutor` pins `SandboxPolicy.Version = "0.5.0-alpha"` when calling `MxcSdk.SpawnSandboxAsync`. This schema provides improved path normalization over `0.4.0-alpha` while still routing to the AppContainer fallback tier. `ClearPolicyOnExit = true` is always set so AppContainer ACL grants are cleaned up on process exit.

At executor construction (once per process start), `SandboxPolicyEnrichment.BuildForWindows()` calls three `PolicyDiscovery` helpers from the mxc SDK:
- `GetAvailableToolsPolicy` — minimal PATH-based tool directories (excludes paths already accessible to ALL_APPLICATION_PACKAGES)
- `GetUserProfilePolicy` — safe user-profile directories (LocalAppData\Programs subdirectories, not the full home directory)
- `GetTemporaryFilesPolicy` — the platform temp directory as read-write

These enrichment paths are merged into the sandbox filesystem policy on every command, replacing the need for broad mounts.

### SDK execution path

`MxcSandboxExecutor` uses `MxcSdk.SpawnSandboxAsync(script, policy, options)` with `SandboxSpawnOptions { UsePty = false }`. This returns a `SandboxProcessResult` with public `Stdout`, `Stderr`, and `ExitCode`. The alternative `SpawnSandboxProcessFromConfig` path is not used because `ProcessConnection.GetStdout()` and `GetStderr()` are declared `internal` in SDK v0.1.1.

### Optional: DACL augmentation

For the `appcontainer-dacl` isolation tier (higher than `0.4.0-alpha`), run elevated once:

```powershell
& "C:\mxc-bin\arm64\wxc-host-prep.exe" prepare-system-drive   # one-time
& "C:\mxc-bin\arm64\wxc-host-prep.exe" prepare-null-device    # per-boot
```

This is not required for the default `0.4.0-alpha` AppContainer path.

## Linux cloud runbook

On Linux hosts, `SandboxExecutorFactory` prefers bubblewrap. `LinuxBwrapExecutor` is selected when `bwrap` is available (the SDK platform probe reports the Bubblewrap backend, or `bwrap` is found on PATH); it applies a selective mount allowlist scoped to the run's working directory.

Only when bubblewrap is unavailable does the factory fall back to `LinuxNativeMxcSandboxExecutor`, which probes the following absolute paths in order:

1. `/usr/local/bin/lxc-exec`
2. `/usr/bin/lxc-exec`

PATH is never consulted for `lxc-exec`. If neither isolation backend exists, the factory automatically falls back to direct. Live registration still requires `ShellEnabled`; explicit `direct: true` is not required for fallback.

## Limitations

### mxc is not a hardened security boundary

The mxc project's own documentation states that profiles are not yet hardened security boundaries. Agentweaver adopts mxc as a defense-in-depth layer on top of the existing path containment and deny-by-default governance. mxc is not and must not be presented as the primary security layer.

### Network allowlist enforcement on Windows

The Windows AppContainer backend cannot enforce a per-host network allowlist. The default network posture for the `processcontainer` backend is unrestricted outbound. `MxcSandboxExecutor.HasNetworkWarning` returns `true` on Windows, which causes the runner to emit a `sandbox.warning` event with `category: "network-unrestricted"`. Operators who need network restriction must either configure a proxy (set `Network.Proxy` on the mxc policy) or use the WSL2/Linux path, which supports real allowlisting.

### hyperlight and microvm unavailable on ARM64

`hyperlight` is x86-64 only. `microvm` requires the `nanvixd` daemon, which is not distributed in the public release zip. Neither backend is in scope for ARM64.

### Executor path artifact in SDK stdout

SDK v0.1.1 dev-artifacts builds append the executor binary path as a trailing line on stdout. `MxcSandboxExecutor.StripExecutorArtifact` removes any trailing line that ends in `.exe` and contains a path separator. This workaround is in place until the upstream artifact is confirmed fixed or a clean release binary is used.

## Known gaps

| ID | Description | Status |
| --- | --- | --- |
| T012 | Binary bundling. The spec (FR-034) calls for bundling `wxc-exec.exe` per-arch under `bin/<arch>` for zero-configuration discovery. This is blocked pending redistribution license review for the mxc binaries. Until resolved, operators must set `MXC_BIN_DIR` manually. | Open |

<details id="diagram-context-canonical-sandbox-boundary" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Several checks contain each action</td></tr>
<tr><td>takeaway</td><td>Native shell is denied; governed tools combine AGT policy, direct containment and execution isolation.</td></tr>
<tr><td>group-title0</td><td>TOOL SELECTION / POLICY</td></tr>
<tr><td>group-title1</td><td>POINT-OF-USE CONTAINMENT</td></tr>
<tr><td>Model tool request</td><td>Model tool request</td></tr>
<tr><td>Model tool request</td><td>Permission dispatch</td></tr>
<tr><td>Model tool request</td><td>Native shell: always denied</td></tr>
<tr><td>Model tool request</td><td>URL approvals handled apart</td></tr>
<tr><td>Model tool request</td><td>Custom reporting bypass</td></tr>
<tr><td>Governance</td><td>Governance</td></tr>
<tr><td>Governance</td><td>Deny-by-default policy</td></tr>
<tr><td>Governance</td><td>AGT policy must allow</td></tr>
<tr><td>Governance</td><td>Direct backend must allow</td></tr>
<tr><td>Governance</td><td>Both checks, not either</td></tr>
<tr><td>Registered tools</td><td>Registered tools</td></tr>
<tr><td>Registered tools</td><td>Explicit capability surface</td></tr>
<tr><td>Registered tools</td><td>Files revalidate at use</td></tr>
<tr><td>Registered tools</td><td>run_command gates shell</td></tr>
<tr><td>Registered tools</td><td>Unknown tools denied</td></tr>
<tr><td>Workspace boundary</td><td>Workspace boundary</td></tr>
<tr><td>Workspace boundary</td><td>Sandbox filesystem</td></tr>
<tr><td>Workspace boundary</td><td>Lexical + real-path checks</td></tr>
<tr><td>Workspace boundary</td><td>Reject symlink escapes</td></tr>
<tr><td>Workspace boundary</td><td>Bounded / redacted output</td></tr>
<tr><td>Execution boundary</td><td>Execution boundary</td></tr>
<tr><td>Execution boundary</td><td>Selected isolation backend</td></tr>
<tr><td>Execution boundary</td><td>Shell policy + approval</td></tr>
<tr><td>Execution boundary</td><td>Kata pod in AKS</td></tr>
<tr><td>Execution boundary</td><td>Direct mode is opt-in</td></tr>
<tr><td>Credential handling</td><td>Credential handling</td></tr>
<tr><td>Credential handling</td><td>Current implementation</td></tr>
<tr><td>Credential handling</td><td>Host + tool options hold token</td></tr>
<tr><td>Credential handling</td><td>Direct git status / allowed gh</td></tr>
<tr><td>Credential handling</td><td>No blanket shell injection</td></tr>
<tr><td>relation-0</td><td>1 governed calls</td></tr>
<tr><td>relation-1</td><td>2 both allow</td></tr>
<tr><td>relation-2</td><td>3 file operation</td></tr>
<tr><td>relation-3</td><td>4 run_command</td></tr>
<tr><td>relation-4</td><td>5 eligible git / gh</td></tr>
<tr><td>assurance</td><td>Current code delivers repository credentials into Host/tool options; the normative no-credential contract is NOT met.</td></tr>
<tr><td>assurance-0-label</td><td>Dispatch exceptions</td></tr>
<tr><td>assurance-0-fact</td><td>Native shell denied; URL path separate.</td></tr>
<tr><td>assurance-0-source</td><td>CopilotAIAgent.cs</td></tr>
<tr><td>assurance-1-label</td><td>Execution isolation</td></tr>
<tr><td>assurance-1-fact</td><td>Sidecar: separate PID namespace.</td></tr>
<tr><td>assurance-1-source</td><td>sandbox-template-agenthost.yaml</td></tr>
<tr><td>assurance-2-label</td><td>Credential reality</td></tr>
<tr><td>assurance-2-fact</td><td>No blanket shell credential inheritance.</td></tr>
<tr><td>assurance-2-source</td><td>RunCommandTool.cs</td></tr>
<tr><td>n0</td><td>Native shell: always denied; URL approvals handled apart</td></tr>
<tr><td>n1</td><td>AGT policy must allow; Direct backend must allow</td></tr>
<tr><td>n2</td><td>Files revalidate at use; run_command gates shell</td></tr>
<tr><td>n3</td><td>Lexical + real-path checks; Reject symlink escapes</td></tr>
<tr><td>n4</td><td>Shell policy + approval; Kata pod in AKS</td></tr>
<tr><td>n5</td><td>Host + tool options hold token; Direct git status / allowed gh</td></tr>
<tr><td>groups</td><td>TOOL SELECTION / POLICY; POINT-OF-USE CONTAINMENT</td></tr>
</tbody></table>
</details>

<details id="diagram-context-sandbox-fig2" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Choose Kubernetes before local probing</td></tr>
<tr><td>takeaway</td><td>The API router owns cluster selection; the local factory can fall back to direct.</td></tr>
<tr><td>API executor router</td><td>API executor router</td></tr>
<tr><td>API executor router</td><td>Backend override / cluster detect</td></tr>
<tr><td>API executor router</td><td>Explicit local bypasses cluster</td></tr>
<tr><td>Kubernetes selected</td><td>Kubernetes selected</td></tr>
<tr><td>Kubernetes selected</td><td>Initialize claim executor</td></tr>
<tr><td>Kubernetes selected</td><td>Failure throws; no local fallback</td></tr>
<tr><td>Claim backend</td><td>Claim backend</td></tr>
<tr><td>Claim backend</td><td>Bound pod command contract</td></tr>
<tr><td>Claim backend</td><td>Not the in-pod PodExec client</td></tr>
<tr><td>Local factory</td><td>Local factory</td></tr>
<tr><td>Local factory</td><td>Only when router selects local</td></tr>
<tr><td>Local factory</td><td>Probe host-supported backends</td></tr>
<tr><td>Windows ladder</td><td>Windows ladder</td></tr>
<tr><td>Windows ladder</td><td>processcontainer -&gt; WSL</td></tr>
<tr><td>Windows ladder</td><td>wsl-bwrap real; unshare not real</td></tr>
<tr><td>Linux ladder</td><td>Linux ladder</td></tr>
<tr><td>Linux ladder</td><td>bubblewrap -&gt; LXC</td></tr>
<tr><td>Linux ladder</td><td>Host tools must be available</td></tr>
<tr><td>No usable isolation</td><td>No usable isolation</td></tr>
<tr><td>No usable isolation</td><td>Automatic local fallback</td></tr>
<tr><td>No usable isolation</td><td>Also explicit direct option</td></tr>
<tr><td>Direct passthrough</td><td>Direct passthrough</td></tr>
<tr><td>Direct passthrough</td><td>IsRealIsolation = false</td></tr>
<tr><td>Direct passthrough</td><td>Trusted/disposable host only</td></tr>
<tr><td>Command registration</td><td>Command registration</td></tr>
<tr><td>Command registration</td><td>ShellEnabled AND real or direct</td></tr>
<tr><td>Command registration</td><td>wsl-unshare: no controlled shell</td></tr>
<tr><td>arrow-1</td><td>select</td></tr>
<tr><td>arrow-2</td><td>ready</td></tr>
<tr><td>arrow-3</td><td>local</td></tr>
<tr><td>arrow-4</td><td>Windows</td></tr>
<tr><td>arrow-5</td><td>Linux</td></tr>
<tr><td>arrow-6</td><td>unavailable</td></tr>
<tr><td>arrow-8</td><td>warn</td></tr>
<tr><td>arrow-9</td><td>gate</td></tr>
<tr><td>note-0</td><td>The local platform ladders are alternatives, not a Windows-to-Linux chain.</td></tr>
<tr><td>note-1</td><td>Kubernetes failure never silently descends into the local ladder.</td></tr>
<tr><td>note-2</td><td>Runtime emits sandbox.selected; factory choice is not an isolation guarantee.</td></tr>
<tr><td>notes</td><td>The local platform ladders are alternatives, not a Windows-to-Linux chain.; Kubernetes failure never silently descends into the local ladder.; Runtime emits sandbox.selected; factory choice is not an isolation guarantee.</td></tr>
</tbody></table>
</details>
