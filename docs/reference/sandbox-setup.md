# Sandbox setup

This guide covers installing and configuring sandboxed execution for operators. For the full design rationale, see [architecture/sandboxed-execution.md](../architecture/sandboxed-execution.md).

## Security caveat

`mxc` is an early preview. Its profiles are not yet hardened security boundaries. The sandbox is a defense-in-depth layer on top of scaffolders' existing path containment and deny-by-default governance. Do not rely on it as the sole security control.

When the `processcontainer` backend is active on Windows, network allowlist enforcement is not available (see [Network allowlist gap](#network-allowlist-gap)).

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

When scaffolders starts, it logs the selected executor. Look for a line like:

```
SandboxExecutorFactory: selected MxcSandboxExecutor (processcontainer)
```

If you see `falling back to PassthroughExecutor`, the binary was not found or the platform probe failed. Shell will be denied.

## Linux cloud

The Linux executor probes for `lxc-exec` at two absolute paths, in order:

1. `/usr/local/bin/lxc-exec`
2. `/usr/bin/lxc-exec`

Install `lxc-exec` at one of those paths before starting the server. No environment variable is required. If neither path exists at startup, scaffolders falls back to `passthrough-deny` and shell commands are denied.

## Configuring sandbox policy

Each project's sandbox policy lives at `.scaffolder/sandbox.yml` in the project repository root. This file is version-controlled alongside the code: changes are reviewable via PR and auditable via `git log`. When the file does not exist, default values apply.

### Example `.scaffolder/sandbox.yml`

```yaml
shell_enabled: true
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
```

The file is optional — default values apply when absent. Changes take effect on the next run; no server restart is needed.

The API endpoints `GET /api/sandbox-policy` and `PUT /api/sandbox-policy` read and write this file. After a `PUT`, the operator should `git add .scaffolder/sandbox.yml && git commit` to record the change in the project history.

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

When `true`, every `run_command` invocation pauses the run and emits a `shell.approval_required` event pending human approval, not just commands matching `destructive_command_patterns`. Note: the approval API endpoint is not yet implemented (T017-api). Setting this to `true` will pause runs indefinitely until that endpoint ships.

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

- Switch to the WSL2 path (ensure WSL2 is installed with a Linux distribution). The `wsl-lxc` backend supports network allowlisting.
- Or configure a proxy on the mxc policy (not yet exposed through the sandbox policy API — requires code changes).
- Or run on a Linux host where the `lxc-native-linux` backend is used.
