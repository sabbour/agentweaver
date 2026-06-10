# Implementation Plan: mxc-Based Sandboxed Execution

**Branch**: `002-sandboxed-execution` | **Date**: 2026-06-10 | **Spec**: `specs/002-sandboxed-execution/spec.md`

**Revised**: 2026-06-10 (post architecture rubber-duck + Seraph security review + M4 built-in tools addition + M5/M6/M7 reusable-tools/native-exclusion/memory-planning + RBD1-RBD6/F-BT1-F-BT5 review resolution)

**Input**: Feature specification from `/specs/002-sandboxed-execution/spec.md` + approved exploratory design plan.

---

## 1. Summary and Approach

Scaffolders today categorically denies shell execution (`deny-shell` in `SandboxGovernance.SandboxPolicyYaml`) and confines agents to in-process path-contained file tools. This plan implements sandboxed command execution using Microsoft mxc (`Sabbour.Mxc.Sdk` v0.1.1) as a defense-in-depth isolation layer, while preserving existing containment.

**Approach:** Introduce an `ISandboxExecutor` abstraction in a new `Scaffolder.SandboxExec` package with four implementations selected by a platform probe:

1. **MxcSandboxExecutor** -- Windows-native `processcontainer` backend via pipe mode (`UsePty = false`). `IsRealIsolation = true`.
2. **WslMxcSandboxExecutor** -- WSL2 path invoking `lxc-exec`/`bubblewrap` isolation for Linux containment when the Windows-native backend is unavailable. `IsRealIsolation = true`.
3. **LinuxNativeMxcSandboxExecutor** -- Native Linux host (cloud) invoking `lxc-exec`/`bubblewrap` directly (no WSL). `IsRealIsolation = true`.
4. **PassthroughExecutor** -- Deny-by-default fallback. `IsRealIsolation = false`. Never runs commands unsandboxed; returns a denied result with a diagnostic reason.

The platform probe (`MxcSdk.GetPlatformSupport()`) runs at executor construction time, selects the appropriate implementation, and emits the selection as a run event (Principle V). Sandboxed shell is gated on `IsRealIsolation == true` AND a per-deployment configuration setting (`Sandbox:ShellEnabled`, defaults `true` post-gate).

**Shell routing architecture (revised per C1):** Both runners expose shell/command execution to the agent via a custom `run_command` AIFunction registered through their respective tool mechanisms. The Copilot SDK's native `shell` tool is ALWAYS DENIED in the permission handler (defense-in-depth) -- native shell approval would execute unsandboxed in the CLI subprocess, which we cannot intercept. Instead, `run_command` is registered via `SessionConfig.Tools` and executes in our process, routing through `ISandboxExecutor.StreamAsync(...)`. The Foundry runner already uses this custom-tool model. Both runners are now symmetric.

**Reusable tool library (M5):** All custom tools (shell, file, search, memory, planning) are factored into a shared `Scaffolder.AgentTools` package with a common `ISandboxTool` contract and `SandboxToolRegistry`. Both runners consume the registry output (`IList<AIFunction>`) instead of building tool lists inline. See section 4.8.

**Native-tool exclusion (M6):** The Copilot runner sets `SessionConfig.AvailableTools` to an explicit allowlist of ONLY our custom tool names. This is the strongest restriction (confirmed in spike: `AvailableTools` is allowlist, server-side enforced). `ExcludedTools` populated with known native names as defense-in-depth. See section 4.8.

**Memory/planning tools (M7):** The `store_memory`, `vote_memory`, `update_todo`, and `report_intent` built-in tools are reimplemented as custom AIFunctions with sandbox-scoped backing stores. `exit_plan_mode` is scoped OUT (internal orchestration tool, not a model-callable function). See section 4.7.8.

**File-tool routing scope (revised per M2):** For Phase 1, file-tool sandbox routing (FR-033) is scoped to shell-only. File tools (read/write/list) continue to use the existing `SandboxedFileTools` in-process path with handle-level TOCTOU verification (`VerifyOpenedHandle`). A purpose-built in-sandbox file helper is deferred to a follow-up increment (see N3 benchmark task). Rationale: routing file ops through `cat`/`echo` shell strings corrupts binary/metachar content and loses the handle-level TOCTOU defense; mxc filesystem-policy enforcement does not yet provide equivalent handle verification.

---

## 2. Technical Context

**Language/Version**: C# / .NET 10 (net10.0)

**Primary Dependencies**: Microsoft Agent Framework (.NET 10), `Sabbour.Mxc.Sdk` v0.1.1 (NuGet, pure-managed net10.0), `AgentGovernance` toolkit

**Storage**: N/A (run events use existing SQLite append-only event store)

**Testing**: xUnit (existing), integration tests gated behind `MXC_INTEGRATION_TESTS` env var

**Target Platform**: Windows ARM64 (Win11 24H2+, build 26100) primary; WSL2 Linux secondary; x64 Windows tertiary

**Project Type**: Runtime library (shared package consumed by agent runners)

**Constraints**: mxc is preview ("not a security boundary yet") -- defense-in-depth only; existing in-proc containment MUST remain; `UsePty = false` mandatory (Porta.Pty win-arm64 gap)

---

## 3. Architecture and Component Design

### 3.1 New Package: `Scaffolder.SandboxExec`

Location: `packages/Scaffolder.SandboxExec/`

Dependencies: `Sabbour.Mxc.Sdk` (v0.1.1), `Scaffolder.SandboxFs` (for `SandboxPathValidator`), `Scaffolder.Domain` (for `RunEvent`)

The package exposes the execution abstraction that both runners consume. Neither runner depends on `Sabbour.Mxc.Sdk` directly.

### 3.2 Public Contract

```csharp
namespace Scaffolder.SandboxExec;

/// <summary>
/// Abstraction over sandboxed command execution. Implementations are selected
/// by the platform probe. Runners depend on this interface, not the SDK.
/// </summary>
public interface ISandboxExecutor
{
    /// <summary>True when this executor provides real process isolation.</summary>
    bool IsRealIsolation { get; }

    /// <summary>Human-readable backend name (e.g. "processcontainer", "lxc", "passthrough").</summary>
    string BackendName { get; }

    /// <summary>Reason string from the platform probe or selection logic.</summary>
    string SelectionReason { get; }

    /// <summary>Buffered one-shot execution.</summary>
    Task<SandboxExecResult> ExecuteAsync(SandboxCommand command, CancellationToken ct = default);

    /// <summary>Streaming execution yielding ordered output chunks and a terminal result.</summary>
    IAsyncEnumerable<SandboxOutputChunk> StreamAsync(SandboxCommand command, CancellationToken ct = default);
}

/// <summary>A command to execute inside the sandbox.</summary>
public sealed record SandboxCommand(
    string CommandLine,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment,
    SandboxFsPolicy FilesystemPolicy,
    int TimeoutMs);

/// <summary>Filesystem policy handed to the sandbox engine.</summary>
public sealed record SandboxFsPolicy(
    IReadOnlyList<string> ReadWritePaths,
    IReadOnlyList<string> ReadOnlyPaths,
    IReadOnlyList<string> DeniedPaths);

/// <summary>Terminal execution result.</summary>
public sealed record SandboxExecResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    bool OutputTruncated);

/// <summary>A streamed output chunk (stdout or stderr line/fragment).</summary>
public sealed record SandboxOutputChunk(
    SandboxOutputStream Stream,
    string Data);

public enum SandboxOutputStream { Stdout, Stderr, ExitCode }
```

### 3.3 Executor Implementations

#### MxcSandboxExecutor

- Calls `MxcSdk.GetPlatformSupport()` at construction; if unsupported, throws (factory handles fallback).
- Builds `SandboxPolicy` from `SandboxFsPolicy` using `MxcSdk.CreateConfigFromPolicy(policy, "process")`.
- Pins `SandboxPolicy.Version = "0.4.0-alpha"` for reproducibility (FR-025).
- Spawns via `MxcSdk.SpawnSandboxProcessFromConfig(config, new SandboxSpawnOptions { UsePty = false })`.
- `StreamAsync` reads `GetStdout()` and `GetStderr()` concurrently, yielding `SandboxOutputChunk` items. On `WaitForExitAsync()` completion, yields a terminal chunk with exit code.
- Enforces `TimeoutMs` via `CancellationTokenSource` linked to command timeout; kills process on expiry.
- Caps captured output at 4 MB (configurable); marks `OutputTruncated` when exceeded.
- Binary discovery (F3 hardened):
  1. `MXC_BIN_DIR` env var MUST be an absolute path (reject relative; reject if not rooted). Resolved binary path: `Path.Combine(MXC_BIN_DIR, "wxc-exec.exe")`.
  2. Fallback: bundled `bin/<arch>/wxc-exec.exe` relative to assembly location (FR-034).
  3. After resolving the binary path, perform an integrity check: verify the binary's Authenticode signature (Windows) or a pinned SHA-256 hash from a `wxc-exec.sha256` manifest file shipped alongside the binary. If verification fails, refuse to start and log a critical event.
  4. Discovery order is deterministic (env var first, then bundled). The `PATH` environment variable is NEVER consulted — this prevents env-planting hijack.

#### WslMxcSandboxExecutor

- Validates WSL2 availability (`wsl.exe --status` probe).
- Maps Windows paths to `/mnt/<drive>/...` for the Linux filesystem view.
- **Command injection prevention (F8):** The shell command is NEVER passed as a bare `wsl.exe` argument. Instead, the full command and config are serialized into the base64 config blob (`--config-base64 <b64>`). The config blob's `command` field carries the shell command. The `wsl.exe` invocation is: `wsl.exe -- lxc-exec --experimental --config-base64 <b64>` where `<b64>` is the sole user-influenced parameter, and its content is structurally validated JSON before base64-encoding.
- Parses stdout/stderr/exit from the WSL process.
- `IsRealIsolation = true` (lxc/bubblewrap provide real containment on Linux).

#### LinuxNativeMxcSandboxExecutor (M1 -- Deployment Parity)

- **Purpose:** Enables real isolation on native Linux cloud hosts (no WSL, no Windows processcontainer). Satisfies Constitution VI (Deployment Parity) and FR-031.
- Validates Linux platform (`RuntimeInformation.IsOSPlatform(OSPlatform.Linux)`).
- Probes for `lxc-exec` binary in `/usr/local/bin/lxc-exec` then `/usr/bin/lxc-exec` (absolute paths only, never PATH search).
- Invokes `lxc-exec --experimental --config-base64 <b64>` directly (same config blob format as WSL executor, without the `wsl.exe` wrapper).
- Same integrity check as MxcSandboxExecutor (pinned SHA-256 hash for the `lxc-exec` binary).
- `IsRealIsolation = true`. `BackendName = "lxc-native-linux"`.
- Filesystem path mapping is identity (no `/mnt/` translation needed).

#### PassthroughExecutor

- `IsRealIsolation = false`. `BackendName = "passthrough-deny"`.
- `ExecuteAsync` and `StreamAsync` immediately return/yield a denied result: `SandboxExecResult(ExitCode: -1, Stdout: "", Stderr: "Shell execution denied: no real isolation available.", TimedOut: false, OutputTruncated: false)`.
- Never spawns any process. This is the safety net.

### 3.4 Executor Factory and Selection

```csharp
public static class SandboxExecutorFactory
{
    public static ISandboxExecutor Create(ILogger logger)
    {
        // 1. Try Windows-native mxc (processcontainer)
        if (OperatingSystem.IsWindows())
        {
            var platform = MxcSdk.GetPlatformSupport();
            if (platform.IsSupported)
                return new MxcSandboxExecutor(platform, logger);

            // 2. Try WSL2 path (Windows host, Linux isolation)
            if (WslMxcSandboxExecutor.IsWslAvailable())
                return new WslMxcSandboxExecutor(logger);
        }

        // 3. Try native Linux (cloud host — no WSL, direct lxc-exec)
        if (OperatingSystem.IsLinux())
        {
            if (LinuxNativeMxcSandboxExecutor.IsLxcAvailable())
                return new LinuxNativeMxcSandboxExecutor(logger);
        }

        // 4. Deny-by-default fallback
        var reason = OperatingSystem.IsWindows()
            ? "No isolation backend available (processcontainer unsupported, WSL2 not found)."
            : "No isolation backend available (lxc-exec not found on this Linux host).";
        return new PassthroughExecutor(reason);
    }
}
```

### 3.5 Filesystem Policy Mapping (F2 hardened)

`SandboxFsPolicyBuilder` maps run context to `SandboxFsPolicy`. All paths are canonicalized through the FULL `SandboxPathValidator` reparse-safe chain (symlink walk + reparse-point detection), NOT bare `Path.GetFullPath`. This prevents symlink escapes in policy construction. Note: mxc `process.cwd` does NOT grant filesystem access -- every accessible path must be explicitly listed in the policy.

```csharp
public static class SandboxFsPolicyBuilder
{
    /// <summary>
    /// Builds a filesystem policy using reparse-safe path canonicalization.
    /// Throws SandboxViolationException if any input path is a symlink or
    /// contains reparse points in its ancestor chain.
    /// </summary>
    public static SandboxFsPolicy Build(string sandboxRoot, string[] allowedRepositoryRoots)
    {
        // Canonicalize sandbox root through full validator (reparse-point walk)
        var canonicalRoot = SandboxPathValidator.ValidateAbsoluteContained(
            Path.GetFullPath(sandboxRoot), Path.GetFullPath(sandboxRoot));

        var rwPaths = new List<string> { canonicalRoot };

        var roPaths = new List<string>();
        foreach (var root in allowedRepositoryRoots)
        {
            // Each allowed root is canonicalized through the full validator chain
            var resolved = SandboxPathValidator.ValidateAbsoluteContained(
                Path.GetFullPath(root), Path.GetFullPath(root));
            if (!string.Equals(resolved, rwPaths[0], StringComparison.OrdinalIgnoreCase))
                roPaths.Add(resolved);
        }

        // Denied paths: sensitive host locations (defense-in-depth)
        var deniedPaths = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            deniedPaths.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\.ssh");
            deniedPaths.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\.gnupg");
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            deniedPaths.Add(Path.Combine(home, ".ssh"));
            deniedPaths.Add(Path.Combine(home, ".gnupg"));
        }

        return new SandboxFsPolicy(rwPaths, roPaths, deniedPaths);
    }
}
```

### 3.6 Shell Command Scope and Content Validation (F4 hardened)

A new validator (registered as a second `IExternalPolicyBackend` or invoked inline) that validates a shell command's declared filesystem scope AND performs host-side content validation beyond working-directory. mxc is preview and not a security boundary -- the host MUST NOT rely solely on mxc for containment.

**Host-side validation applied before execution (F4):**
1. Working directory is within sandbox root (existing `SandboxPathValidator.ValidateAbsoluteContained`).
2. Command string length cap (configurable, default 64KB) -- prevents resource exhaustion.
3. Null-byte rejection -- command must not contain `\0` (injection vector).
4. Reject commands containing known shell escape/chaining patterns that attempt to change working directory outside the sandbox (e.g., `cd /` followed by operations). Note: this is best-effort heuristic defense-in-depth; mxc filesystem policy is the primary enforcement.

```csharp
internal static class ShellCommandValidator
{
    private const int MaxCommandLengthBytes = 65536;

    /// <summary>
    /// Validates working directory scope and basic command content safety.
    /// Returns (allowed, reason).
    /// </summary>
    public static (bool Allowed, string? Reason) Validate(
        string commandLine, string commandWorkingDir, string sandboxRoot)
    {
        // 1. Working directory containment
        try
        {
            SandboxPathValidator.ValidateAbsoluteContained(commandWorkingDir, sandboxRoot);
        }
        catch (SandboxViolationException ex)
        {
            return (false, $"Working directory escape: {ex.Message}");
        }

        // 2. Command length cap
        if (commandLine.Length > MaxCommandLengthBytes)
            return (false, $"Command exceeds maximum length ({MaxCommandLengthBytes} bytes).");

        // 3. Null-byte injection
        if (commandLine.Contains('\0'))
            return (false, "Command contains null byte (injection attempt).");

        return (true, null);
    }
}
```

### 3.7 Human-Approval Gate for Shell (F6 -- Constitution X)

Per Constitution Principle X, destructive or irreversible actions MUST require explicit human approval. For shell commands:

- **Gate scope:** Shell execution (`run_command` tool) requires human approval when the command matches a "destructive pattern" list (configurable). Default destructive patterns: `rm -rf`, `del /s`, `format`, `mkfs`, `dd if=`, `git push --force`, `git reset --hard`. The pattern list is extensible via `Sandbox:DestructiveCommandPatterns` configuration.
- **Integration:** The existing MAF HITL (Human-In-The-Loop) review gate in `AgentGovernance` is leveraged. When a shell command matches a destructive pattern, the governance evaluation returns `RequiresApproval` instead of `Allowed`, which triggers the HITL review flow (prompt displayed in CLI/Web UI, blocks until operator approves/denies).
- **Non-destructive commands:** Approved automatically by governance when all other gates pass (isolation confirmed, working directory valid, shell enabled). Operators can configure `Sandbox:RequireApprovalForAllShell = true` to require approval for ALL shell commands regardless of pattern.
- **Audit:** Every approval/denial decision (human or automatic) is recorded in the audit log with the full command text, decision source, and timestamp.

### 3.8 Output Redaction (F7 -- Constitution IX)

Streamed `tool.output` events (stdout/stderr from sandboxed commands) MUST pass through a redaction pipeline before reaching logs, events, or client streams:

1. **Secret pattern scanning:** A configurable regex set (default: patterns matching common secrets -- API keys, tokens, connection strings, private keys). Matches are replaced with `[REDACTED]`.
2. **PII detection:** Email addresses and IPv4/IPv6 addresses in output are redacted unless the run is configured with `Sandbox:RedactPii = false` (default true).
3. **Pipeline position:** Redaction occurs in the `StreamAsync` consumer (the runner) BEFORE writing to the `ChannelWriter<RunEvent>`. This ensures no unredacted content reaches logs or clients.
4. **Implementation:** A `SandboxOutputRedactor` class with a `Redact(string data)` method, injected into both runners. The redactor is stateless and thread-safe.

```csharp
public sealed class SandboxOutputRedactor
{
    private readonly IReadOnlyList<Regex> _secretPatterns;
    private readonly bool _redactPii;

    public SandboxOutputRedactor(IReadOnlyList<Regex> secretPatterns, bool redactPii = true)
    {
        _secretPatterns = secretPatterns;
        _redactPii = redactPii;
    }

    public string Redact(string data)
    {
        var result = data;
        foreach (var pattern in _secretPatterns)
            result = pattern.Replace(result, "[REDACTED]");
        if (_redactPii)
            result = PiiPatterns.Apply(result);
        return result;
    }
}
```

### 3.9 Network Policy Warning (F5 -- Constitution V/IX)

When the sandbox runs with `NetworkPolicy = "allow"` on Windows (where true allowlist enforcement is unavailable due to the Windows gap), the runtime MUST emit a warning event:

- **Event type:** `sandbox.warning`
- **Payload:** `{ category: "network-open", message: "Sandbox running with unrestricted network on Windows (allowlist enforcement unavailable). Data exfiltration surface is open.", backend: "<backend-name>" }`
- **Emission point:** After executor selection, when `NetworkPolicy == "allow"` AND the selected executor is `MxcSandboxExecutor` (Windows-native).
- **Audit:** The warning is recorded in the audit log and surfaced to clients via the event stream (Principle V/IX).

---

## 4. Integration Points

### 4.1 SandboxGovernance Changes (C2 resolved)

**File**: `packages/Scaffolder.AgentRuntime/SandboxGovernance.cs`

Replace the categorical `deny-shell` YAML rule with a conditional `allow-shell-sandboxed` rule:

```yaml
- name: allow-shell-sandboxed
  condition: "tool_name == 'run_command'"
  action: Allow
  description: >
    Shell execution allowed when gated by ISandboxExecutor.IsRealIsolation
    and per-deployment Sandbox:ShellEnabled setting. Actual enforcement is
    in the triple-gate evaluation (executor gate + command scope validation + HITL).
- name: deny-native-shell
  condition: "tool_name == 'shell'"
  action: Deny
  description: >
    Native shell tool ALWAYS denied (defense-in-depth). Execution is routed
    through run_command custom tool which we control. See C1 resolution.
```

**SandboxPolicyBackend extension (C2):** The existing `SandboxPolicyBackend.Evaluate` will unconditionally deny `run_command` because it has no path in `PathArgumentKeys`. Fix: add a distinct `KnownShellTools` set with its own working-directory extraction logic:

```csharp
// Added to SandboxPolicyBackend
private static readonly HashSet<string> KnownShellTools = new(StringComparer.Ordinal)
{
    "run_command"
};

// In Evaluate(), after KnownFileTools check:
if (KnownShellTools.Contains(toolName))
{
    // Shell tools carry working directory in the "directory" argument key
    // (injected by the custom run_command AIFunction / MapToToolCall).
    string? directory = null;
    if (context.TryGetValue("directory", out var d))
        directory = CoercePathValue(d);

    if (string.IsNullOrWhiteSpace(directory))
    {
        return new ExternalPolicyDecision
        {
            Backend = Name, Allowed = false,
            Reason = "Shell tool missing 'directory' argument; denied.",
            EvaluationMs = sw.Elapsed.TotalMilliseconds,
        };
    }

    // Validate working directory is within sandbox
    var resolved = SandboxPathValidator.ValidateAbsoluteContained(directory, _sandboxRoot);
    return new ExternalPolicyDecision
    {
        Backend = Name, Allowed = true,
        Reason = "Shell working directory is within sandbox boundary.",
        EvaluationMs = sw.Elapsed.TotalMilliseconds,
        Metadata = new Dictionary<string, object> { ["resolved_directory"] = resolved },
    };
}
```

The `EvaluateToolCall` method gains a third layer for shell tools: after AGT policy + SandboxPolicyBackend, it checks:
1. `ISandboxExecutor.IsRealIsolation == true`
2. `ShellCommandValidator.Validate(...)` passes (working directory + content validation)
3. Configuration setting `Sandbox:ShellEnabled` is `true`
4. If command matches destructive pattern, HITL approval gate (F6)

If any check fails, shell is denied with a specific reason.

**SandboxGovernance.Create(...)** takes additional parameters:

```csharp
internal static SandboxGovernance Create(
    string workingDirectory, string runId,
    ISandboxExecutor executor, bool shellEnabled, ILogger logger)
```

### 4.2 Foundry Runner Changes

**File**: `packages/Scaffolder.AgentRuntime/FoundryAgentRunner.cs`

- `BuildTools(SandboxedFileTools, ISandboxExecutor, SandboxOutputRedactor)` registers a new `run_command` AIFunction when `executor.IsRealIsolation && shellEnabled`:

```csharp
AIFunctionFactory.Create(
    async (
        [Description("Shell command to execute.")] string command,
        [Description("Timeout in milliseconds (default 30000).")] int? timeout_ms) =>
    {
        var fsPolicy = SandboxFsPolicyBuilder.Build(sandboxRoot, allowedRoots);
        var cmd = new SandboxCommand(command, workingDirectory, null, fsPolicy, timeout_ms ?? 30000);
        var result = await executor.ExecuteAsync(cmd, ct);
        return FormatResult(result, redactor);
    },
    "run_command", "Run a shell command inside the sandbox.")
```

- The `run_command` AIFunction injects `["directory"] = workingDirectory` into its governance-evaluation args dict so `SandboxPolicyBackend` can validate the working directory (C2 resolution).

- For streamed output, the runner uses `executor.StreamAsync(...)` and emits each `SandboxOutputChunk` through the `SandboxOutputRedactor` before writing as a run event (`tool.output` type with `stream` and `data` fields).

### 4.3 Copilot Runner Changes (C1 resolved)

**File**: `packages/Scaffolder.AgentRuntime/GitHubCopilotAgentRunner.cs`

**Architecture change (C1):** The native `PermissionRequestShell` is ALWAYS denied in `BuildPermissionHandler` (defense-in-depth). Approving it would cause the Copilot CLI subprocess to execute the command natively and unsandboxed -- the host cannot intercept post-approval. Instead, shell execution is provided via a custom `run_command` AIFunction registered through `SessionConfig.Tools`.

- In `BuildPermissionHandler`, the `PermissionRequestShell` case remains **categorically denied** (returns `PermissionRequestResultKind.Rejected`). This is defense-in-depth: even if the model somehow invokes the native shell tool, it is blocked.

- A custom `run_command` AIFunction is registered via `SessionConfig.Tools`:

```csharp
var sessionConfig = new SessionConfig
{
    OnPermissionRequest = BuildPermissionHandler(governance, runId, EmitToolCallOnce, EmitToolErrorOnce),
    WorkingDirectory = workingDirectory,
    EnableConfigDiscovery = false,
    Streaming = true,
    Tools = BuildCopilotCustomTools(executor, governance, workingDirectory, sandboxRoot,
        allowedRoots, shellEnabled, redactor, Emit, ct),
};
```

- `BuildCopilotCustomTools` registers `run_command` as an `AIFunction` with `is_override = false` (there is no native tool named `run_command` -- native shell tools are `shell`/`bash` per the bundle permission map; those are excluded via AvailableTools allowlist + ExcludedTools blocklist, not via override):

```csharp
private static IList<AIFunction> BuildCopilotCustomTools(
    ISandboxExecutor executor, SandboxGovernance governance,
    string workingDirectory, string sandboxRoot, string[] allowedRoots,
    bool shellEnabled, SandboxOutputRedactor redactor,
    Action<string, object> emit, CancellationToken ct)
{
    var tools = new List<AIFunction>();

    if (executor.IsRealIsolation && shellEnabled)
    {
        var runCommand = AIFunctionFactory.Create(
            async ([Description("Shell command to execute.")] string command,
                   [Description("Timeout in milliseconds.")] int? timeout_ms) =>
            {
                // Governance triple-gate (evaluated here because custom tools
                // do NOT fire OnPermissionRequest for PermissionRequestShell)
                var args = new Dictionary<string, object>
                {
                    ["command"] = command,
                    ["directory"] = workingDirectory,
                };
                var (allowed, reason) = governance.EvaluateToolCall(
                    agentId: $"did:mesh:scaffolder:copilot:{governance.RunId}",
                    toolName: "run_command", args: args, logger: governance.Logger);
                if (!allowed)
                    return $"Error: {reason}";

                // Content validation (F4)
                var (valid, validReason) = ShellCommandValidator.Validate(
                    command, workingDirectory, sandboxRoot);
                if (!valid)
                    return $"Error: {validReason}";

                var fsPolicy = SandboxFsPolicyBuilder.Build(sandboxRoot, allowedRoots);
                var cmd = new SandboxCommand(command, workingDirectory, null, fsPolicy,
                    timeout_ms ?? 30000);

                // Stream execution with redaction (F7)
                var sb = new StringBuilder();
                await foreach (var chunk in executor.StreamAsync(cmd, ct))
                {
                    if (chunk.Stream == SandboxOutputStream.ExitCode)
                    {
                        emit("tool.output", new { stream = "exit_code", data = chunk.Data });
                    }
                    else
                    {
                        var redacted = redactor.Redact(chunk.Data);
                        emit("tool.output", new {
                            stream = chunk.Stream.ToString().ToLowerInvariant(),
                            data = redacted });
                        sb.Append(redacted);
                    }
                }
                return sb.ToString();
            },
            "run_command", "Run a shell command inside the sandbox.");

        // is_override is NOT set -- "run_command" has no native counterpart.
        // Native shell tools (shell/bash) are excluded via AvailableTools + ExcludedTools.
        tools.Add(runCommand);
    }

    return tools;
}
```

- The `PermissionRequestShell` handler in `MapToToolCall` stays unchanged -- it maps to `("shell", ...)` which hits the `deny-native-shell` YAML rule. This provides defense-in-depth: if the SDK somehow fires a native shell request despite `run_command` being the intended tool, it is denied.

- **Symmetry with Foundry:** Both runners now use a custom `run_command` AIFunction for shell. The Copilot runner registers it via `SessionConfig.Tools`; the Foundry runner registers it via `BuildTools`. Both route through `ISandboxExecutor.StreamAsync(...)`.

### 4.4 File-Tool Routing (FR-033, revised per M2, superseded by M4)

**Original M2 decision (now superseded):** Phase 1 deferred file-tool sandbox routing because the only viable path at that time was routing through `cat`/`echo` shell strings, which corrupts binary/metachar content and loses handle-level TOCTOU defense.

**M4 supersession:** Section 4.7 introduces purpose-built sandboxed AIFunction tools that mirror the Copilot CLI's built-in tool names and schemas. These tools execute in-process through `SandboxedFileTools` (retaining handle-level TOCTOU via `VerifyOpenedHandle`) and are registered with `is_override = true` (for tools matching native names) so the model never invokes native built-ins or shells out for file operations. `run_command` is the exception (`is_override = false`; native shell excluded via AvailableTools/ExcludedTools). This POSITIVELY resolves the M2 concern:
- File ops are first-class in-proc sandboxed tools (no shell string routing).
- Handle-level TOCTOU defense is preserved (no regression vs M2 deferral).
- The model no longer needs shell for file work (shell minimization achieved).

**Remaining trade-off:** The in-process path does NOT use mxc filesystem-policy enforcement (since there is no sandbox process involved). mxc enforcement becomes relevant only when/if a future increment adds an in-sandbox file helper binary. The existing `SandboxPathValidator` reparse-safe chain is the containment mechanism, and it is strictly stronger than shell-routed file ops would have been.

**N3 benchmark task** (Phase 0, T004a) remains relevant for future mxc-routed file tooling but is no longer blocking for file-tool functionality.

### 4.5 Run Event Streaming (Principle V, N2 resolved)

New event types on the existing `RunEvent` stream:

| Event Type | Payload | When |
|---|---|---|
| `sandbox.selected` | `{ backend, isRealIsolation, reason }` | Run start, after executor selection |
| `sandbox.warning` | `{ category, message, backend }` | Run start, when network is open on Windows (F5) |
| `tool.output` | `{ callId, stream: "stdout"\|"stderr", data }` | Each output chunk during streaming exec |
| `tool.exec_result` | `{ callId, exitCode, timedOut, truncated }` | Command completion |
| `tool.error` | `{ callId, errorMessage }` | Command denied or failed (existing) |

**Event schema decision (N2):** A distinct `tool.exec_result` event type is used for sandbox command completion (rather than extending the existing `tool.result`). Rationale: `tool.result` carries a `content` string (the tool's return value to the model). `tool.exec_result` carries structured execution metadata (`exitCode`, `timedOut`, `truncated`). Using a distinct type preserves backward compatibility -- existing clients that handle `tool.result` are unaffected. New clients opt into `tool.exec_result` handling.

Events flow through the existing `ChannelWriter<RunEvent>` in both runners, consumed by the SSE fan-out broadcaster unchanged.

### 4.6 Configuration Surface

**appsettings.json** addition:

```json
{
  "Sandbox": {
    "ShellEnabled": true,
    "TimeoutMs": 30000,
    "MaxOutputBytes": 4194304,
    "NetworkPolicy": "allow",
    "PolicyVersion": "0.4.0-alpha",
    "RequireApprovalForAllShell": false,
    "DestructiveCommandPatterns": ["rm -rf", "del /s", "format", "mkfs", "dd if=", "git push --force", "git reset --hard"],
    "RedactPii": true,
    "SecretPatterns": []
  }
}
```

Exposed through the API as read-only run metadata (Principle III); both CLI and Web UI display the sandbox status on run detail views (Principle IV).

### 4.7 Built-In Sandboxed Tools (Shell Minimization)

**Goal:** Eliminate shell usage for file read/search/edit operations by providing purpose-built, sandboxed AIFunction tools that mirror the Copilot CLI's built-in tool names and argument schemas. The model's tool-calling behavior is unchanged -- only the implementation is ours and sandboxed.

#### 4.7.1 Confirmed Tool Schemas (from Copilot CLI bundle v1.0.61 static analysis)

| Tool Name | Type | Args (name / type / required) | Description (abridged) | Bundle Evidence |
|---|---|---|---|---|
| `read_file` | function | `filePath` (string, R), `startLine` (number, R), `endLine` (number, R) | Read file contents for a specified line range. Lines 1-indexed. | Line ~2178, `$sn()` |
| `grep_search` | function | `query` (string, R), `isRegexp` (boolean, R), `includePattern` (string, opt), `maxResults` (number, opt) | Fast text/regex search in workspace. Case-insensitive. | Line ~2176, `Dsn()` |
| `file_search` | function | `query` (string, R -- glob pattern), `maxResults` (number, opt) | Search files by glob pattern. Returns paths only. | Line ~2172, `xsn()` |
| `str_replace_editor` | function | `command` (enum: "view"\|"create"\|"str_replace"\|"insert", R), `path` (string, R), plus command-specific: `view_range` (int[], opt), `forceReadLargeFiles` (bool, opt), `file_text` (string -- create), `old_str` (string -- str_replace), `new_str` (string -- str_replace/insert), `insert_line` (int -- insert) | Multi-command file editor: view/create/str_replace/insert. | Line ~2329-2330, `XYt()` |
| `apply_patch` | custom (freeform) | Single string input (patch text). Accepts via `input` or `patch` property or raw string. | Freeform diff-like patching (Add File, Delete File, Update File hunks in a custom grammar). | Line ~5619, `FDn()` |
| `create` | function | `path` (string, R -- absolute), `file_text` (string, R) | Create a new file (fails if exists). | Line ~2296, via `ZYt()` |
| `edit` | function | `path` (string, R -- absolute), `old_str` (string, opt), `new_str` (string, opt) | String replacement in a file (single occurrence). | Line ~2297, via `ZYt()` |
| `semantic_search` | function | `query` (string, R) | Natural language code search via GitHub embeddings API. | Line ~2187, `zsn()` |

**Compatibility group map** (bundle line ~2330, `_fn` object): `edit`/`MultiEdit`/`Write` resolve to `["apply_patch", "str_replace_editor", "create", "edit"]`. `Grep`/`Glob` resolve to `["search"]` (internally mapped to `grep_search`/`file_search`). `read` resolves to the `str_replace_editor` view command.

**Scope decision:**
- IN SCOPE: `read_file`, `grep_search`, `file_search`, `str_replace_editor`, `apply_patch`, `create`, `edit`.
- OUT OF SCOPE: `semantic_search` -- requires a GitHub embeddings API backend (`/embeddings/code/search`) that is not available in our local/self-hosted deployment. Per Constitution VII, this is scoped out rather than stubbed.

#### 4.7.2 Implementation Architecture

Each in-scope tool is implemented as a custom `AIFunction` registered in BOTH runners (Constitution IV parity):

- **Copilot runner:** Registered via `SessionConfig.Tools` (extending `BuildCopilotCustomTools` from section 4.3). Each file/search/memory/planning tool is marked `is_override = true` so it supersedes the native built-in of the same name. `run_command` is marked `is_override = false` (no native tool of that name exists -- native shell is excluded via AvailableTools/ExcludedTools). The native equivalents are additionally excluded via the permission handler as defense-in-depth.
- **Foundry runner:** Registered via `BuildTools(...)` in `FoundryAgentRunner.cs`.

All tools share the same internal implementation layer -- a new `BuiltInToolImplementations` static class in `Scaffolder.SandboxExec` (or `Scaffolder.SandboxFs`) that provides the sandboxed operation bodies.

**Note (M5 supersession):** Section 4.8 supersedes this inline registration pattern. Phase 4a (T047/T048) initially registers tools inline in each runner; Phase 4b (T057/T063) refactors them into named `ISandboxTool` classes in `packages/Scaffolder.AgentTools/` and both runners consume `SandboxToolRegistry.Build(context)` instead. The implementation layer (SandboxedFileTools, SandboxedSearchTools) remains unchanged.

#### 4.7.3 File Read/Edit Tools -- Mapping to SandboxedFileTools

All file operations route through the existing `packages/Scaffolder.SandboxFs/SandboxedFileTools.cs` which already provides handle-level TOCTOU verification via `SandboxPathValidator.VerifyOpenedHandle`. No shell, no mocks.

| Copilot Tool | Operation | SandboxedFileTools Method | New Method Needed? |
|---|---|---|---|
| `read_file` | Read lines startLine..endLine | `ReadFileAsync` + line-range slicing | Yes: `ReadFileRangeAsync(path, startLine, endLine)` -- reads full file via existing handle-verified path, then returns the requested line slice. |
| `str_replace_editor` (view) | Read file or list directory | `ReadFileAsync` / `ListDirectoryAsync` | No (existing). Line-range and large-file truncation applied post-read. |
| `str_replace_editor` (create) | Create new file | `WriteFileAsync` (with create-only mode) | Yes: `CreateFileAsync(path, content)` -- wraps `WriteFileAsync` with existence pre-check (fail if exists). |
| `str_replace_editor` (str_replace) | Replace old_str with new_str | N/A | Yes: `StrReplaceAsync(path, oldStr, newStr)` -- reads via handle-verified path, performs exact single-occurrence replacement, writes back via handle-verified path. |
| `str_replace_editor` (insert) | Insert at line | N/A | Yes: `InsertAtLineAsync(path, insertLine, newStr)` -- reads, inserts after specified line, writes back. |
| `apply_patch` | Multi-hunk patch application | N/A | Yes: `ApplyPatchAsync(path, patchText)` -- TWO-PHASE apply. Phase 1 (parse + validate): parse the custom patch grammar, collect ALL paths -- every Add File target, Delete File target, Update File target, AND every "Move to" destination (rename target from `*** Move to: <path>` lines) -- and validate EACH through the full `SandboxPathValidator` reparse-safe chain; if ANY path fails validation, reject the entire patch with zero writes (no partial mutation). Phase 2 (apply): only after all paths pass, apply changes sequentially. Each file touched goes through handle-verified I/O. |
| `create` | Create file (absolute path) | Same as str_replace_editor create | No (reuses `CreateFileAsync`). |
| `edit` | String replacement | Same as str_replace_editor str_replace | No (reuses `StrReplaceAsync`). |

**New `SandboxedFileTools` method signatures:**

```csharp
// All methods reuse SandboxPathValidator.ValidateAbsoluteContained + VerifyOpenedHandle

public async Task<(string? Content, SandboxReadFailure? Failure)> ReadFileRangeAsync(
    string requestedPath, int startLine, int endLine, CancellationToken ct = default);

public async Task<(long BytesWritten, SandboxWriteFailure? Failure)> CreateFileAsync(
    string requestedPath, string content, CancellationToken ct = default);

public async Task<(bool Success, string? FailureReason)> StrReplaceAsync(
    string requestedPath, string oldStr, string newStr, CancellationToken ct = default);

public async Task<(bool Success, string? FailureReason)> InsertAtLineAsync(
    string requestedPath, int insertLine, string newStr, CancellationToken ct = default);

public async Task<ApplyPatchResult> ApplyPatchAsync(
    string patchText, CancellationToken ct = default);
```

`ApplyPatchResult` captures: files added/modified/deleted, per-hunk success/failure, validated paths (from Phase 1), and the two-phase outcome (rejected-at-validation vs applied).

#### 4.7.4 Search Tools -- In-Process Implementation

Search tools execute in-process constrained to validated sandbox/allowed roots. NO shell spawning.

**`grep_search` implementation:**
- Uses `System.IO.Enumeration.FileSystemEnumerable<T>` (or recursive `Directory.EnumerateFiles`) constrained to paths that pass `SandboxPathValidator.ValidateAbsoluteContained`.
- Performs line-by-line regex or literal (case-insensitive) matching using `System.Text.RegularExpressions.Regex` with `RegexOptions.IgnoreCase | RegexOptions.Compiled`.
- `includePattern` (glob) is evaluated via a .NET glob matcher (e.g., `Microsoft.Extensions.FileSystemGlobbing`) to filter enumerated files.
- Respects the same exclusion directories as the Copilot CLI (`.git`, `node_modules`, `__pycache__`, `venv`, `.venv`, `build`, `dist`).
- Returns results capped at `maxResults` (default 20) with file path, line number, and content snippet.
- Every enumerated path is validated through `SandboxPathValidator` before content access.

**`file_search` implementation:**
- Uses `Microsoft.Extensions.FileSystemGlobbing.Matcher` against the sandbox root.
- Enumerates matching files, validates each path through `SandboxPathValidator`.
- Returns relative paths only, capped at `maxResults` (default 20).
- Rejects glob patterns that would traverse outside the working directory (same check as Copilot CLI: `hnt()` function rejects absolute or `..`-containing patterns).

**`semantic_search`:** OUT OF SCOPE. Requires GitHub embeddings API infrastructure not available in self-hosted deployments. This tool is NOT registered in either runner and is NOT listed in `AvailableTools` -- the model cannot invoke it. It is excluded from `SandboxToolRegistry` entirely (no stub, no error handler). If a future SDK version attempts to dispatch to `semantic_search` despite the AvailableTools allowlist, the native tool is blocked by `ExcludedTools` (which lists `semantic_search`) and the permission handler (tertiary enforcement). This is a deliberate scope boundary per Constitution VII, not a deferred implementation.

#### 4.7.5 Governance Integration

These tools flow through the existing dual-layer governance: `SandboxGovernance.EvaluateToolCall` + `SandboxPolicyBackend`.

**PathArgumentKeys coverage:**

| Tool | Path-bearing arg | Currently in PathArgumentKeys? | Action |
|---|---|---|---|
| `read_file` | `filePath` | NO (`"file_path"` is listed but not `"filePath"`) | Add `"filePath"` to `PathArgumentKeys` |
| `str_replace_editor` | `path` | YES | None |
| `create` | `path` | YES | None |
| `edit` | `path` | YES | None |
| `apply_patch` | None (paths embedded in patch text) | N/A | Special handling: parse patch text to extract ALL file paths (Add/Delete/Update targets AND "Move to" rename destinations), validate each through `SandboxPathValidator` before application (two-phase: validate-all-then-apply) |
| `grep_search` | None (operates from working dir) | N/A | Validate working-dir containment; if `includePattern` is provided, validate it does not escape |
| `file_search` | None (glob from working dir) | N/A | Validate working-dir containment; reject traversal patterns |

**Required changes to `SandboxPolicyBackend` (three distinct code branches -- RBD6):**

1. **`KnownFileTools` set (path-arg validation):** Add `"filePath"` to `PathArgumentKeys` array. Add all path-bearing tool names to `KnownFileTools`: `"read_file"`, `"str_replace_editor"`, `"apply_patch"`, `"create"`, `"edit"` (some already present; add missing ones). For `apply_patch`: the backend validates that the tool is recognized (allows it through the known-tool check), but per-path validation is deferred to the tool implementation itself (which validates each path extracted from the patch text via the two-phase approach). The backend trusts the tool to call `SandboxPathValidator` per-path.

2. **`KnownSearchTools` set (working-directory-containment-only validation):** A NEW set containing `"grep_search"` and `"file_search"`. These tools have no path argument -- they operate from working directory. The backend validates working-directory containment only (these tools are implicitly scoped to `workingDirectory` which is already validated at run start). If `includePattern` is provided (grep_search), the tool implementation validates it does not escape.

3. **`KnownInternalTools` set (allow-without-path):** A NEW set containing `"update_todo"` and `"report_intent"`. These tools have no filesystem paths and operate on in-memory/event-only state. The backend unconditionally allows them (returns `Allowed` immediately with reason "Internal tool, no filesystem access required").

All three branches are distinct `if` checks in `Evaluate()`, evaluated in order: KnownFileTools (path validation) -> KnownShellTools (directory validation) -> KnownSearchTools (working-dir containment) -> KnownInternalTools (allow). Unknown tool names fall through to the existing deny-by-default.

**Output redaction (return-value path -- F-BT2/F-BT3):** Every tool AIFunction MUST run its return value (the string/content handed back to the LLM framework) through `SandboxOutputRedactor.Redact()` BEFORE returning -- not only at `tool.output` event emission. This applies to: `read_file` content, `grep_search`/`file_search` results, `run_command` output, `store_memory`/`vote_memory` confirmation echoes, and `RecallAsync` content surfaced to the model. This closes the secret-amplification vector where the model could echo redacted event content from an unredacted return value. The redaction wraps the RETURN path of every tool; event-level redaction (existing F7 pipeline) is defense-in-depth on top.

#### 4.7.6 Events (Principle V)

- File-edit tools (`str_replace_editor`, `apply_patch`, `create`, `edit`) emit `tool.result` events on completion (existing schema: `{ callId, content }`).
- Read/search tools (`read_file`, `grep_search`, `file_search`) stream results via `tool.result` events. For large results, content is truncated per the tool's `maxResults`/line-cap logic before event emission.
- Failed operations emit `tool.error` events (existing schema: `{ callId, errorMessage }`).
- Schema is consistent with section 4.5 event definitions. No new event types needed for file tools.

#### 4.7.7 Copilot Runner: Native Tool Exclusion (Defense-in-Depth)

In `BuildPermissionHandler`, the following native tool permission requests are DENIED in addition to `PermissionRequestShell`:
- `PermissionRequestFile` (or equivalent) for file read/write operations.
- Any native tool whose name matches the in-scope tools (`read_file`, `grep_search`, `file_search`, `str_replace_editor`, `apply_patch`, `create`, `edit`).

This ensures that even if the model somehow invokes a native built-in instead of our override, it is blocked. The custom AIFunction tools registered with `is_override = true` are the ONLY execution path.

#### 4.7.8 Memory and Planning Tools (M7)

**Goal:** Reimplement the remaining Copilot CLI built-in tool families (memory, planning) as custom sandboxed AIFunctions, ensuring the model cannot invoke native implementations that operate outside our control.

##### 4.7.8.1 Confirmed Tool Schemas (from bundle v1.0.61 static analysis)

**Memory tools** (permission kind: `"memory"`):

| Tool Name | Args (name / type / required) | Description | Bundle Evidence |
|---|---|---|---|
| `store_memory` | `subject` (string, R -- 1-2 words topic), `fact` (string, R -- <200 chars), `citations` (string, R -- file:line refs or user quote), `reason` (string, R -- 2-3 sentences), `scope` (enum: "repository"\|"user", R) | Store a fact about the codebase for future tasks. | Line ~1714 (`DS="store_memory"`), schema at line ~1785 (`Vbe=Z.object(...)`) |
| `vote_memory` | `fact` (string, R -- exact fact text to vote on), `direction` (enum: "upvote"\|"downvote", R), `reason` (string, R -- 2-3 sentences), `scope` (enum: "repository"\|"user", opt) | Vote on an existing memory to indicate agreement or disagreement. | Line ~1714 (`MY="vote_memory"`), schema at line ~1785 (`nqt=Z.object(...)`) |

**Planning tools** (internal category: `"think"`):

| Tool Name | Args (name / type / required) | Description | Bundle Evidence |
|---|---|---|---|
| `update_todo` | `todos` (string, R -- markdown checklist) | Update the TODO checklist showing completed and pending tasks. | Line ~5422 (`aK="update_todo"`), schema: `ees=Z.object({todos:Z.string()})` |
| `report_intent` | `intent` (string, R -- current activity description) | Report what the agent is currently doing or planning to do. | Line ~1139 (`Jm="report_intent"`), schema: `Rbi=Z.object({intent:Z.string()})` |

**Scoped OUT tools (not reimplemented):**

| Tool Name | Reason for Exclusion |
|---|---|
| `exit_plan_mode` | Internal SDK orchestration tool. Not a model-callable function in the standard sense -- it triggers a UI-level plan review flow (`exit_plan_mode.requested` event) that is specific to the Copilot CLI session lifecycle. Our runners have their own run lifecycle management. Scoped OUT per Constitution VII (no mocks of infrastructure we do not provide). |
| `task` | Explicitly excluded per user direction. Subagent orchestration is out of scope. |
| `notebook` | Explicitly excluded per user direction. Not relevant to scaffolder runtime. |
| `web_fetch` | MCP server tool (`github-mcp-server-web_search` / `web_fetch`). Not a built-in -- delivered via GitHub's MCP server. Network access is not needed in sandboxed scaffolder runs. Scoped OUT. |
| `web_search` | Same as `web_fetch` -- MCP-delivered. Scoped OUT. |
| `sql` / `session_store_sql` | Session-local SQLite database tools for the Copilot CLI's own session management (checkpoints, turn history). Not a scaffolder concern -- we have our own event store. Scoped OUT. |

##### 4.7.8.2 Implementation: Memory Tools

Memory tools are reimplemented as custom AIFunctions backed by a **sandbox-scoped JSON store** persisted within the run's artifact directory. This is NOT a mock -- it is a real persistent store that survives the run and can be consumed by subsequent runs against the same repository.

**Backing store:** `{sandboxRoot}/.scaffolder/memory.json` -- a simple JSON file containing an array of memory objects:

```csharp
// packages/Scaffolder.AgentTools/Stores/SandboxMemoryStore.cs
namespace Scaffolder.AgentTools.Stores;

public sealed class SandboxMemoryStore
{
    private readonly string _storePath;
    private readonly SandboxPathValidator _validator;
    private readonly string _sandboxRoot;

    public SandboxMemoryStore(string sandboxRoot, SandboxPathValidator validator)
    {
        _sandboxRoot = sandboxRoot;
        _validator = validator;
        _storePath = Path.Combine(sandboxRoot, ".scaffolder", "memory.json");
    }

    public async Task<StoreMemoryResult> StoreAsync(MemoryEntry entry, CancellationToken ct);
    public async Task<VoteMemoryResult> VoteAsync(string fact, string direction, string reason, CancellationToken ct);
    public async Task<IReadOnlyList<MemoryEntry>> RecallAsync(CancellationToken ct);
}

public sealed record MemoryEntry(
    string Subject, string Fact, string Citations,
    string Reason, string Scope, DateTimeOffset StoredAt);
```

The store path is validated through `SandboxPathValidator.ValidateAbsoluteContained` (contained within sandbox root). File I/O uses the same handle-verified path as `SandboxedFileTools`. Concurrency: file-level lock via `FileStream` with `FileShare.None` during writes.

**`store_memory` AIFunction:** Validates all required fields, appends to the JSON array, returns confirmation text. Enforces `fact` max 200 chars, `subject` max 50 chars. Return value is passed through `SandboxOutputRedactor.Redact()` before being handed to the LLM framework (F-BT2: prevents secret amplification if citations or reason contain sensitive content).

**`vote_memory` AIFunction:** Finds the matching `fact` entry in the store, appends a vote record (direction + reason + timestamp). If fact not found, returns an error message (not an exception -- the model should see the failure). Return value is redacted before return (F-BT2).

##### 4.7.8.3 Implementation: Planning Tools

Planning tools are reimplemented as custom AIFunctions backed by **in-memory state** scoped to the current run. These tools have no persistence requirement beyond the run (the model uses them for self-organization during execution).

**`update_todo` AIFunction:** Accepts a markdown checklist string. Stores it in a `RunTodoState` object held by the tool registry (scoped to the run). Emits a `tool.result` event with the parsed counts (total/completed/pending). The stored checklist is available to the model on subsequent calls.

```csharp
// packages/Scaffolder.AgentTools/Stores/RunTodoState.cs
namespace Scaffolder.AgentTools.Stores;

public sealed class RunTodoState
{
    public string CurrentChecklist { get; private set; } = "";
    public int TotalItems { get; private set; }
    public int CompletedItems { get; private set; }

    public (int Total, int Completed, int Pending) Update(string todos)
    {
        CurrentChecklist = todos;
        // Parse markdown checklist: count lines matching "- [x]" and "- [ ]"
        var lines = todos.Split('\n');
        CompletedItems = lines.Count(l => l.TrimStart().StartsWith("- [x]", StringComparison.OrdinalIgnoreCase));
        TotalItems = CompletedItems + lines.Count(l => l.TrimStart().StartsWith("- [ ]"));
        return (TotalItems, CompletedItems, TotalItems - CompletedItems);
    }
}
```

**`report_intent` AIFunction:** Accepts an intent string. Emits it as a run event (`agent.intent` type with `{ intent }` payload) for observability (Principle V). Returns "Intent logged" to the model. This feeds the live run-status display in CLI/Web UI.

##### 4.7.8.4 Governance Integration (Memory/Planning)

- `store_memory` and `vote_memory`: Evaluated by `SandboxPolicyBackend` -- the store path (`{sandboxRoot}/.scaffolder/memory.json`) is within the sandbox read-write zone (already covered by `SandboxFsPolicy.ReadWritePaths`). No new governance rules needed.
- `update_todo` and `report_intent`: No filesystem paths involved. These tools are unconditionally allowed through governance (they operate on in-memory/event-only state). Added to a new `KnownInternalTools` set in `SandboxPolicyBackend` that always returns `Allowed`.
- Output from all four tools passes through `SandboxOutputRedactor` before event emission (consistent with Constitution IX). Additionally, the RETURN VALUE of each tool (the string handed back to the LLM framework) is redacted through the same `SandboxOutputRedactor.Redact()` call before return (F-BT2/F-BT3: closes secret-amplification via model echo of unredacted return content).

### 4.8 Reusable Tool Library and Native-Tool Exclusion (M5/M6)

#### 4.8.1 Shared Tool Package: `Scaffolder.AgentTools`

**Location:** `packages/Scaffolder.AgentTools/`

**Justification for new package (not a folder in existing packages):**
- `Scaffolder.SandboxFs` is filesystem-specific (path validation, file tools). Adding shell, memory, and planning tools there violates single-responsibility.
- `Scaffolder.SandboxExec` is execution-specific (mxc SDK, executor abstraction). It provides `ISandboxExecutor` but should not own tool registration.
- `Scaffolder.AgentRuntime` contains runners -- tools should be a dependency OF runners, not embedded in them (prevents circular deps and enables independent testing).
- A dedicated `Scaffolder.AgentTools` package owns the tool contract, all tool class implementations, the registry, and the backing stores. Both runners add a `<PackageReference>` to it.

**Dependencies:** `Scaffolder.SandboxFs` (for `SandboxedFileTools`, `SandboxPathValidator`, `SandboxedSearchTools`), `Scaffolder.SandboxExec` (for `ISandboxExecutor`, `SandboxOutputRedactor`, `SandboxFsPolicyBuilder`), `Scaffolder.Domain` (for `RunEvent`), `Microsoft.Extensions.AI` (for `AIFunction`, `AIFunctionFactory`). Note: NO dependency on `Scaffolder.AgentRuntime` -- governance is injected as a delegate (see RBD1 resolution in section 4.8.2).

#### 4.8.2 ISandboxTool Contract

```csharp
// packages/Scaffolder.AgentTools/ISandboxTool.cs
namespace Scaffolder.AgentTools;

/// <summary>
/// Contract for a sandboxed tool that can be registered in both runners.
/// Each implementation is a single tool class exposing metadata and producing
/// an AIFunction bound to injected dependencies.
/// </summary>
public interface ISandboxTool
{
    /// <summary>Tool name as seen by the model (e.g. "read_file", "run_command").</summary>
    string Name { get; }

    /// <summary>Human-readable description passed to the model.</summary>
    string Description { get; }

    /// <summary>
    /// Whether this tool should supersede a native built-in of the same name.
    /// When true, the produced AIFunction has is_override = true metadata.
    /// RULE: IsOverride = true ONLY when Name matches an actual native Copilot CLI
    /// tool name from the bundle (copilot-builtin-tools.md evidence artifact).
    /// run_command -> false (no native "run_command"; native shell is "shell"/"bash").
    /// read_file, grep_search, file_search, str_replace_editor, apply_patch,
    /// create, edit, store_memory, vote_memory, update_todo, report_intent -> true.
    /// </summary>
    bool IsOverride { get; }

    /// <summary>
    /// Produces an AIFunction (via AIFunctionFactory.Create) bound to the provided
    /// dependencies. Called once per run at tool-registration time.
    /// </summary>
    AIFunction CreateFunction(SandboxToolContext context);
}

/// <summary>
/// Dependencies injected into each tool at creation time.
/// NOTE (RBD1 resolution): Governance is exposed as a delegate, NOT a concrete type.
/// SandboxGovernance is internal sealed in Scaffolder.AgentRuntime. Using it here
/// would create a circular dependency (AgentRuntime -> AgentTools AND AgentTools ->
/// AgentRuntime). Instead, the runner binds the delegate at construction:
///   (toolName, args) => governance.EvaluateToolCall(agentId, toolName, args, logger)
/// This keeps the dependency graph acyclic:
///   AgentTools -> {SandboxFs, SandboxExec, Domain}
///   AgentRuntime -> {AgentTools, SandboxFs, SandboxExec, Domain}
/// </summary>
public sealed record SandboxToolContext(
    ISandboxExecutor Executor,
    Func<string, IReadOnlyDictionary<string, object>, (bool Allowed, string? Reason)> EvaluateToolCall,
    SandboxedFileTools FileTools,
    SandboxedSearchTools SearchTools,
    SandboxPathValidator PathValidator,
    SandboxOutputRedactor Redactor,
    SandboxMemoryStore MemoryStore,
    RunTodoState TodoState,
    string WorkingDirectory,
    string SandboxRoot,
    string[] AllowedRoots,
    bool ShellEnabled,
    Action<string, object> EmitEvent,
    CancellationToken RunCancellation);
```

#### 4.8.3 Tool Implementations (class-per-tool)

Each tool is its own class in `packages/Scaffolder.AgentTools/Tools/`:

```text
packages/Scaffolder.AgentTools/
+-- Scaffolder.AgentTools.csproj
+-- ISandboxTool.cs
+-- SandboxToolContext.cs
+-- SandboxToolRegistry.cs
+-- Tools/
|   +-- RunCommandTool.cs
|   +-- ReadFileTool.cs
|   +-- GrepSearchTool.cs
|   +-- FileSearchTool.cs
|   +-- StrReplaceEditorTool.cs
|   +-- ApplyPatchTool.cs
|   +-- CreateFileTool.cs
|   +-- EditFileTool.cs
|   +-- StoreMemoryTool.cs
|   +-- VoteMemoryTool.cs
|   +-- UpdateTodoTool.cs
|   +-- ReportIntentTool.cs
+-- Stores/
    +-- SandboxMemoryStore.cs
    +-- RunTodoState.cs
```

Each class implements `ISandboxTool` and routes through the EXISTING in-proc primitives:
- File tools -> `SandboxedFileTools` methods (TOCTOU-verified via `VerifyOpenedHandle`)
- Search tools -> `SandboxedSearchTools` methods (path-validated enumeration)
- Shell tool -> `ISandboxExecutor.StreamAsync(...)` (mxc-isolated)
- Memory tools -> `SandboxMemoryStore` (file I/O through validated path)
- Planning tools -> `RunTodoState` (in-memory) / event emission

No tool reimplements behavior -- the refactor is structural (move inline lambda logic from `BuildCopilotCustomTools` / `BuildTools` into named classes).

#### 4.8.4 SandboxToolRegistry

```csharp
// packages/Scaffolder.AgentTools/SandboxToolRegistry.cs
namespace Scaffolder.AgentTools;

/// <summary>
/// Assembles the complete list of AIFunctions from registered tool classes.
/// Both runners call this instead of building tools inline.
/// </summary>
public sealed class SandboxToolRegistry
{
    private readonly IReadOnlyList<ISandboxTool> _tools;

    public SandboxToolRegistry(IEnumerable<ISandboxTool>? additionalTools = null)
    {
        // Default tool set: all built-in sandboxed tools
        var tools = new List<ISandboxTool>
        {
            new RunCommandTool(),
            new ReadFileTool(),
            new GrepSearchTool(),
            new FileSearchTool(),
            new StrReplaceEditorTool(),
            new ApplyPatchTool(),
            new CreateFileTool(),
            new EditFileTool(),
            new StoreMemoryTool(),
            new VoteMemoryTool(),
            new UpdateTodoTool(),
            new ReportIntentTool(),
        };
        if (additionalTools is not null)
            tools.AddRange(additionalTools);
        _tools = tools;
    }

    /// <summary>
    /// Produces the AIFunction list bound to a specific run's context.
    /// Shell tool is conditionally included (requires IsRealIsolation + ShellEnabled).
    /// </summary>
    public IList<AIFunction> Build(SandboxToolContext context)
    {
        var functions = new List<AIFunction>();
        foreach (var tool in _tools)
        {
            // Gate: RunCommandTool requires real isolation + shell enabled
            if (tool is RunCommandTool && !(context.Executor.IsRealIsolation && context.ShellEnabled))
                continue;

            var fn = tool.CreateFunction(context);
            // is_override rule (RBD2/F-BT4): set ONLY when the tool's Name matches
            // an actual native Copilot CLI tool name from the bundle. run_command has
            // no native counterpart (native shell is "shell"/"bash"), so IsOverride=false.
            // All file/search/memory/planning tools DO match native names and get true.
            if (tool.IsOverride)
                fn.AdditionalProperties["is_override"] = true;
            functions.Add(fn);
        }
        return functions;
    }

    /// <summary>
    /// Returns the list of tool names for AvailableTools allowlist construction.
    /// When includeDisabled is true, returns ALL tool names regardless of gating
    /// (used by canonical-name unit test T068 for source-of-truth validation).
    /// </summary>
    public IReadOnlyList<string> GetToolNames(bool includeShell = false, bool includeDisabled = false)
    {
        if (includeDisabled)
            return _tools.Select(t => t.Name).ToList();
        return _tools
            .Where(t => includeShell || t is not RunCommandTool)
            .Select(t => t.Name)
            .ToList();
    }
}
```

#### 4.8.5 Runner Integration (M5 -- DRY)

Both runners replace their inline tool-building code with a call to `SandboxToolRegistry`:

**Copilot runner (`GitHubCopilotAgentRunner.cs`):**
```csharp
var registry = new SandboxToolRegistry();
// RBD1: bind governance as a delegate to avoid circular package dependency
Func<string, IReadOnlyDictionary<string, object>, (bool, string?)> evalToolCall =
    (toolName, args) => governance.EvaluateToolCall(agentId, toolName, args, logger);

var context = new SandboxToolContext(executor, evalToolCall, fileTools, searchTools,
    pathValidator, redactor, memoryStore, todoState,
    workingDirectory, sandboxRoot, allowedRoots, shellEnabled, Emit, ct);

var sessionConfig = new SessionConfig
{
    Tools = registry.Build(context),
    AvailableTools = registry.GetToolNames(includeShell: executor.IsRealIsolation && shellEnabled),
    ExcludedTools = NativeToolExclusion.GetExcludedToolNames(),
    // ... existing config
};
```

**Foundry runner (`FoundryAgentRunner.cs`):**
```csharp
var registry = new SandboxToolRegistry();
Func<string, IReadOnlyDictionary<string, object>, (bool, string?)> evalToolCall =
    (toolName, args) => governance.EvaluateToolCall(agentId, toolName, args, logger);

var context = new SandboxToolContext(executor, evalToolCall, fileTools, searchTools,
    pathValidator, redactor, memoryStore, todoState,
    workingDirectory, sandboxRoot, allowedRoots, shellEnabled, Emit, ct);

var tools = registry.Build(context);
// Pass to BuildTools or directly to agent configuration
```

#### 4.8.6 Native-Tool Exclusion via AvailableTools Allowlist (M6)

**Strategy:** The model MUST see ONLY our custom sandboxed tools -- zero native Copilot CLI tools.

**Primary enforcement -- `SessionConfig.AvailableTools` (allowlist):**

Set `AvailableTools` to an explicit list containing EXACTLY our custom tool names:
- `run_command` (conditional on shell enabled)
- `read_file`, `grep_search`, `file_search`, `str_replace_editor`, `apply_patch`, `create`, `edit`
- `store_memory`, `vote_memory`
- `update_todo`, `report_intent`

Per the spike evidence (`specs/001-single-agent-run/spike-copilot-sandbox.md`, line 86-92): "`AvailableTools` is an allowlist. When set, it takes precedence over `ExcludedTools`." And line 179: "`AvailableTools` is enforced server-side: when set, tools not in the list are not offered to the model and cannot be called." This is the STRONGEST restriction mechanism.

**Secondary enforcement -- `SessionConfig.ExcludedTools` (defense-in-depth, belt-and-suspenders):**

Additionally populate `ExcludedTools` with known native tool names as defense-in-depth:

```csharp
// packages/Scaffolder.AgentTools/NativeToolExclusion.cs
namespace Scaffolder.AgentTools;

/// <summary>
/// Defense-in-depth: explicit blocklist of known native Copilot CLI tool names.
/// Redundant when AvailableTools is set (allowlist takes precedence), but provides
/// an extra layer if AvailableTools enforcement has a bug.
/// </summary>
public static class NativeToolExclusion
{
    // From bundle v1.0.61 permission map (line ~1315, srn object):
    // bash, shell, write, edit, create, memory, store_memory, vote_memory,
    // read, view, glob, grep, ls, task, webfetch, web_fetch, websearch, web_search
    private static readonly string[] KnownNativeTools =
    [
        "bash", "shell",                          // shell execution (native)
        "write", "read", "view", "ls",            // native file ops
        "glob", "grep",                           // native search
        "task",                                   // subagent orchestration
        "webfetch", "web_fetch",                  // network access
        "websearch", "web_search",                // network search
        "memory",                                 // legacy memory alias
        "semantic_search",                        // requires GitHub embeddings API
    ];

    public static IList<string> GetExcludedToolNames() => KnownNativeTools.ToList();
}
```

**Tertiary enforcement -- Permission handler (existing):**

The `BuildPermissionHandler` continues to deny `PermissionRequestShell`, `PermissionRequestFile`, and all native tool permission requests. This covers the case where the SDK somehow bypasses both `AvailableTools` and `ExcludedTools`.

**Forward-compatibility guarantee:**

If a future Copilot CLI version adds a new native tool not in our allowlist, `AvailableTools` already excludes it by design (allowlist is safe-by-default -- anything not listed is invisible to the model). The `ExcludedTools` blocklist is updated periodically as new native tool names are discovered, but is NOT the primary enforcement boundary. This makes the architecture future-proof without code changes.

**Verification:** A new integration test (`T061`) asserts that `SessionConfig.AvailableTools` contains exactly the tool names produced by `SandboxToolRegistry.GetToolNames()` and no others.

#### 4.8.7 Unit Testing Strategy (M5)

Individual tool classes are testable in isolation without a runner:

```csharp
// Example: testing ReadFileTool directly
[Fact]
public async Task ReadFileTool_ReturnsContentForValidRange()
{
    var tool = new ReadFileTool();
    var context = CreateTestContext(sandboxRoot: testDir);
    var fn = tool.CreateFunction(context);

    var result = await fn.InvokeAsync(new { filePath = testFile, startLine = 1, endLine = 5 });
    Assert.Contains("expected content", result?.ToString());
}
```

This is strictly easier than testing through a runner (no SDK/Foundry session setup needed). The `SandboxToolContext` can be constructed with test doubles for `ISandboxExecutor` (using `PassthroughExecutor`) and real instances of `SandboxedFileTools`/`SandboxPathValidator` pointed at a temp directory.

---

## 5. Constitution Compliance

| Principle | Obligation | How Satisfied |
|---|---|---|
| I -- Agent Runtime | Use MAF exclusively | Executor wires into MAF governance; tool dispatch via MAF `AIFunction` / `SessionConfig.Tools` |
| II -- Model Sources | Copilot + Foundry only | No change; sandbox is orthogonal to model source |
| III -- API-First | API is authoritative | Sandbox settings/status exposed via API; clients read from API |
| IV -- Two Front-Ends at Parity | CLI and Web equal | Both runners consume `SandboxToolRegistry` (M5 parity). Same API surface for sandbox status/events |
| V -- Observable Runs | Stream steps live | `sandbox.selected` + `sandbox.warning` + `tool.output` + `tool.exec_result` + `agent.intent` events stream to all clients |
| VI -- Deployment Parity | Same build local+cloud | Four-executor factory: Windows-native, WSL2, Linux-native, Passthrough. Cloud hosts get `LinuxNativeMxcSandboxExecutor` |
| VII -- No Mocks/Fakes/Placeholders | Functional from commit one | PassthroughExecutor is a real deny-by-default implementation, not a mock. Memory store is real persistent JSON. `exit_plan_mode` scoped OUT (not stubbed). |
| IX -- Responsible AI | Human accountable, transparent | All sandbox decisions auditable; HITL approval gate for destructive commands (F6); output redaction for secrets/PII (F7) |
| X -- Safe Execution | Enforced sandbox boundary | Defense-in-depth: mxc + in-proc path containment + AGT deny-by-default + executor IsRealIsolation gate + AvailableTools allowlist (M6) + human-approval gate for destructive commands |
| XI -- Agent Governance Toolkit | MAF governance enforces policy | Shell allow/deny via AGT policy YAML + external backend + HITL; no ad hoc gates |

### Complexity Tracking

| Added Complexity | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| New package `Scaffolder.SandboxExec` | Decouples runners from mxc SDK (FR-005) | Referencing SDK directly from runners creates tight coupling and violates the abstraction requirement |
| Four executor implementations | Platform heterogeneity (Windows native, WSL2, Linux native, fallback) per FR-008 + Principle VI | Three implementations omit Linux-native cloud hosts, violating Deployment Parity |
| Custom `run_command` tool in both runners | Native shell approval executes unsandboxed in CLI subprocess (C1) -- only a custom tool executes in-process where we can route to mxc | Approving native shell and "intercepting" is architecturally impossible per the SDK protocol |
| Distinct `KnownShellTools` path in SandboxPolicyBackend | Shell args have no PathArgumentKeys -- unconditional deny (C2) | Stuffing a fake "path" into shell args is fragile and lies about semantics |
| SandboxOutputRedactor pipeline | Constitution IX requires secrets/PII not in logs/events (F7) | Omitting redaction violates privacy requirements |
| Seven built-in tool AIFunctions (M4) | Shell minimization: model must not shell out for file read/search/edit (M4) | Relying on native built-ins means uncontrolled execution; relying on shell strings corrupts content and loses TOCTOU |
| In-process search tools (`SandboxedSearchTools`) | `grep_search`/`file_search` must not spawn `rg`/`grep` processes | Shell-spawned search bypasses sandbox path validation and introduces injection surface |
| New package `Scaffolder.AgentTools` (M5) | DRY tool library shared by both runners (Constitution IV parity) | Inline lambdas in each runner duplicates logic, complicates testing, violates single-responsibility |
| `AvailableTools` allowlist (M6) | Model must see ONLY sandboxed tools -- zero native tools | `ExcludedTools` alone is a blocklist (new tools slip through); `is_override` alone still exposes native tools the model could attempt to call. `is_override` is set only for tools matching a real native name (not `run_command`) |
| Memory/planning tool AIFunctions (M7) | Complete native-tool elimination; model needs memory/planning capabilities | Leaving native memory/planning runs outside our control; stubbing violates Constitution VII |

---

## 6. Project Structure

### Documentation (this feature)

```text
specs/002-sandboxed-execution/
+-- spec.md              # Feature specification
+-- plan.md              # This file
+-- spike-results.md     # Phase 0 spike output
```

### Source Code

```text
packages/
+-- Scaffolder.SandboxExec/
|   +-- Scaffolder.SandboxExec.csproj
|   +-- ISandboxExecutor.cs
|   +-- SandboxCommand.cs
|   +-- SandboxExecResult.cs
|   +-- SandboxOutputChunk.cs
|   +-- SandboxFsPolicy.cs
|   +-- SandboxFsPolicyBuilder.cs
|   +-- SandboxExecutorFactory.cs
|   +-- ShellCommandValidator.cs
|   +-- SandboxOutputRedactor.cs
|   +-- MxcSandboxExecutor.cs
|   +-- WslMxcSandboxExecutor.cs
|   +-- LinuxNativeMxcSandboxExecutor.cs
|   +-- PassthroughExecutor.cs
|   +-- bin/
|       +-- arm64/               # Bundled wxc-exec.exe (ARM64)
|       +-- x64/                 # Bundled wxc-exec.exe (x64)
|       +-- wxc-exec.sha256     # Integrity manifest (F3)
|       +-- NOTICE              # Attribution/license (N1)
+-- Scaffolder.AgentRuntime/     # Modified: governance + runner integration (uses SandboxToolRegistry)
+-- Scaffolder.AgentTools/       # NEW (M5): reusable tool library
|   +-- Scaffolder.AgentTools.csproj
|   +-- ISandboxTool.cs
|   +-- SandboxToolContext.cs
|   +-- SandboxToolRegistry.cs
|   +-- NativeToolExclusion.cs
|   +-- Tools/
|   |   +-- RunCommandTool.cs
|   |   +-- ReadFileTool.cs
|   |   +-- GrepSearchTool.cs
|   |   +-- FileSearchTool.cs
|   |   +-- StrReplaceEditorTool.cs
|   |   +-- ApplyPatchTool.cs
|   |   +-- CreateFileTool.cs
|   |   +-- EditFileTool.cs
|   |   +-- StoreMemoryTool.cs
|   |   +-- VoteMemoryTool.cs
|   |   +-- UpdateTodoTool.cs
|   |   +-- ReportIntentTool.cs
|   +-- Stores/
|       +-- SandboxMemoryStore.cs
|       +-- RunTodoState.cs
+-- Scaffolder.SandboxFs/        # Modified: KnownShellTools in SandboxPolicyBackend; new SandboxedSearchTools; extended SandboxedFileTools
+-- Scaffolder.Domain/           # Modified: new event type constants (agent.intent)

tests/
+-- Scaffolder.Tests/
    +-- SandboxExec/
        +-- MxcSandboxExecutorTests.cs
        +-- WslMxcSandboxExecutorTests.cs
        +-- LinuxNativeMxcSandboxExecutorTests.cs
        +-- PassthroughExecutorTests.cs
        +-- SandboxFsPolicyBuilderTests.cs
        +-- ShellCommandValidatorTests.cs
        +-- SandboxOutputRedactorTests.cs
        +-- SandboxGovernanceShellTests.cs
    +-- AgentTools/
        +-- ReadFileToolTests.cs
        +-- GrepSearchToolTests.cs
        +-- FileSearchToolTests.cs
        +-- StrReplaceEditorToolTests.cs
        +-- ApplyPatchToolTests.cs
        +-- CreateFileToolTests.cs
        +-- EditFileToolTests.cs
        +-- StoreMemoryToolTests.cs
        +-- VoteMemoryToolTests.cs
        +-- UpdateTodoToolTests.cs
        +-- ReportIntentToolTests.cs
        +-- SandboxToolRegistryTests.cs
        +-- NativeToolExclusionTests.cs
    +-- Integration/
        +-- MxcIntegrationTests.cs          # Gated: MXC_INTEGRATION_TESTS
        +-- WslIntegrationTests.cs          # Gated: MXC_INTEGRATION_TESTS
        +-- LinuxNativeIntegrationTests.cs  # Gated: MXC_INTEGRATION_TESTS
        +-- BuiltInToolsParityTests.cs      # Phase 4a: end-to-end parity (T053)
        +-- AvailableToolsAllowlistTests.cs # Phase 4b: M6 verification (T061)
```

---

## 7. Phased Task Breakdown

### Phase 0: Local Spike (Validate mxc on Target Host)

> Prerequisite: Prove real isolation before any production wiring (FR-027, FR-028).

| ID | Task | Owner | Description | Depends On |
|---|---|---|---|---|
| T001 | Acquire and place mxc binaries | Link | Download `mxc-release-binaries.zip` v0.6.1; extract `arm64/wxc-exec.exe` to a local path; set `MXC_BIN_DIR`. Verify `wxc-exec.exe --probe` returns a valid JSON tier object on this Windows ARM64 host. | -- |
| T002 | Spike: pipe-mode hello-world (Windows native) | Morpheus | Create a throwaway .NET 10 console project referencing `Sabbour.Mxc.Sdk` v0.1.1. Call `MxcSdk.GetPlatformSupport()`, then `SpawnSandboxProcessFromConfig` with `UsePty=false`, `SandboxPolicy.Version="0.4.0-alpha"`, containment `"process"`. Run `cmd /c echo hello` inside the sandbox. Capture stdout, stderr, exit code. Document working config. | T001 |
| T003 | Spike: WSL2/lxc-exec hello-world | Link | In the same spike, invoke `wsl.exe -- lxc-exec --experimental --config-base64 <b64>` with a Linux echo command. Verify path mapping (`/mnt/c/...`) and capture working config. Verify command is inside base64 blob, not bare argument (F8). | T001 |
| T004 | Document spike results | Morpheus | Write `specs/002-sandboxed-execution/spike-results.md` with exact working configuration, binary location, version pins, any one-time host preparation required. | T002, T003 |
| T004a | Benchmark: file-tool sandbox routing latency (N3) | Morpheus | In the spike project, measure per-op latency of executing `cat <file>` via mxc vs in-proc `File.ReadAllText`. Document P50/P95 for 1KB and 100KB files. Results feed the M2 decision on future file-tool routing. | T002 |

**Gate**: Phase 0 results reviewed by Seraph. If `processcontainer` or WSL2 path fails, the plan adapts before proceeding.

---

### Phase 1: Core Abstraction and Implementations

| ID | Task | Owner | Description | Depends On |
|---|---|---|---|---|
| T005 | Create `Scaffolder.SandboxExec` package | Morpheus | New .csproj (net10.0), add to solution. Reference `Sabbour.Mxc.Sdk` v0.1.1 NuGet. Define `ISandboxExecutor`, `SandboxCommand`, `SandboxExecResult`, `SandboxOutputChunk`, `SandboxFsPolicy` public types exactly as specified in section 3.2. | T004 |
| T006 | Implement `SandboxFsPolicyBuilder` (F2 hardened) | Morpheus | Maps sandbox root + `Runs:AllowedRepositoryRoots` into `SandboxFsPolicy`. Canonicalizes ALL paths through the full `SandboxPathValidator` reparse-safe chain (ValidateAbsoluteContained, including symlink/junction walk) -- NOT bare `Path.GetFullPath`. Adds sensitive-path deny list. Acceptance: symlink pointing to sandbox root is rejected; reparse-point ancestor is rejected. | T005 |
| T007 | Implement `ShellCommandValidator` (F4 hardened) | Morpheus | Validates: (1) command working directory within sandbox root via `SandboxPathValidator.ValidateAbsoluteContained`, (2) command length cap, (3) null-byte rejection. Does NOT rely solely on mxc for containment. | T005 |
| T007a | Implement `SandboxOutputRedactor` (F7) | Morpheus | Redacts secrets and PII from streamed output before it reaches events/logs. Configurable regex patterns for secrets; built-in patterns for email/IP. Thread-safe, stateless. Acceptance: known secret patterns are replaced with `[REDACTED]` in output. | T005 |
| T008 | Implement `MxcSandboxExecutor` (F3 hardened) | Morpheus | Wraps `Sabbour.Mxc.Sdk` pipe-mode spawn. Builds `SandboxPolicy` from `SandboxFsPolicy`. Implements `ExecuteAsync` (buffered) and `StreamAsync` (yields chunks). Binary discovery: absolute `MXC_BIN_DIR` only (reject relative), then bundled path. Integrity check (Authenticode/SHA-256 manifest). PATH never consulted. Handles timeout + output cap. | T005, T006 |
| T009 | Implement `WslMxcSandboxExecutor` (F8 hardened) | Link | WSL2 path: maps Windows paths to `/mnt/` Linux paths, serializes command INSIDE base64 config blob (never as bare wsl.exe argument). Validates config JSON structure before base64 encoding. | T005 |
| T009a | Implement `LinuxNativeMxcSandboxExecutor` (M1) | Morpheus | Native Linux cloud host executor. Probes for `lxc-exec` at fixed absolute paths. Same config-blob approach as WSL executor. Integrity check on `lxc-exec` binary. `IsRealIsolation = true`. | T005 |
| T010 | Implement `PassthroughExecutor` | Morpheus | Deny-by-default: `IsRealIsolation = false`, immediately returns denied result. No process spawn. | T005 |
| T011 | Implement `SandboxExecutorFactory` | Morpheus | Four-tier probe: Windows-native -> WSL2-on-Windows -> Linux-native -> Passthrough. Logs selection decision. Emits `sandbox.warning` event when network is open on Windows (F5). | T008, T009, T009a, T010 |
| T012 | Bundle native binaries (N1 prereq) | Link | **Prerequisite:** Verify mxc binary redistribution license; generate `NOTICE` file with attribution. Add `bin/arm64/wxc-exec.exe`, `bin/x64/wxc-exec.exe`, `wxc-exec.sha256` integrity manifest, and `NOTICE` to the `Scaffolder.SandboxExec` package as content files (CopyToOutputDirectory). | T001 |

---

### Phase 2: Governance and Runner Integration

| ID | Task | Owner | Description | Depends On |
|---|---|---|---|---|
| T013 | Update SandboxGovernance YAML and Create() | Morpheus | Replace `deny-shell` rule with `allow-shell-sandboxed` (for `run_command`) + `deny-native-shell` (for `shell`). Update `SandboxGovernance.Create(...)` to accept `ISandboxExecutor` + `shellEnabled` config. Add third-layer shell gate in `EvaluateToolCall`: executor real-isolation check + command scope validation + config check + HITL gate for destructive commands (F6). | T007, T011 |
| T014 | Extend SandboxPolicyBackend for shell tools (C2) | Morpheus | Add `KnownShellTools` set (`"run_command"`). For shell tools, extract working directory from `["directory"]` argument key (injected by the custom tool / MapToToolCall). Validate directory containment. Return allowed/denied with reason. Acceptance: `run_command` with valid `directory` passes; without `directory` or with escaping directory, fails. | T013 |
| T015 | Integrate executor into FoundryAgentRunner | Morpheus | Inject `ISandboxExecutor` + `SandboxOutputRedactor` via constructor. `BuildTools` registers `run_command` AIFunction when shell is enabled and executor provides real isolation. Route execution through `StreamAsync`, apply redaction (F7), emit `tool.output` events per chunk and `tool.exec_result` on completion. `run_command` tool injects `["directory"] = workingDirectory` in its governance args dict (C2). | T011, T013, T007a |
| T016 | Integrate executor into GitHubCopilotAgentRunner (C1) | Morpheus | Keep `PermissionRequestShell` categorically DENIED in `BuildPermissionHandler` (defense-in-depth). Register custom `run_command` AIFunction via `SessionConfig.Tools` with `is_override = false` (no native tool named `run_command` exists; native shell excluded via AvailableTools + ExcludedTools). The custom tool performs governance eval inline, validates via `ShellCommandValidator`, builds `SandboxFsPolicy`, routes through `StreamAsync`, applies redaction to return value and events (F7/F-BT2), emits streamed events. Symmetric with Foundry runner. | T011, T013, T007a |
| T017 | HITL approval gate for destructive shell (F6) | Morpheus | Integrate MAF HITL review gate. When `run_command` matches a destructive pattern (from `Sandbox:DestructiveCommandPatterns`), governance returns `RequiresApproval`. HITL flow blocks until operator approves/denies. Configurable `RequireApprovalForAllShell` forces approval for all shell. Audit every decision. | T013 |
| T018 | Configuration surface (`Sandbox:*` settings) | Tank | Add all Sandbox configuration keys to `appsettings.json`. Wire via `IConfiguration` into `SandboxGovernance.Create`, executor construction, and `SandboxOutputRedactor`. Include `DestructiveCommandPatterns`, `RequireApprovalForAllShell`, `RedactPii`, `SecretPatterns`. | T013 |

---

### Phase 3: API, Streaming, and Front-End Parity

| ID | Task | Owner | Description | Depends On |
|---|---|---|---|---|
| T019 | Emit `sandbox.selected` + `sandbox.warning` run events | Morpheus | Both runners emit `sandbox.selected` at run start. Emit `sandbox.warning` when network is open on Windows (F5). Recorded in audit log. | T015, T016 |
| T020 | API: expose sandbox status on run metadata | Tank | `GET /api/runs/{id}` includes `sandbox: { backend, isRealIsolation }` in the response. | T018, T019 |
| T021 | API: sandbox configuration endpoint | Tank | `GET /api/settings/sandbox` returns full sandbox config (read-only). | T018 |
| T022 | CLI: display sandbox status | Trinity | `scaffolder run watch` displays `sandbox.selected` and `sandbox.warning` events. `scaffolder settings sandbox` shows current sandbox configuration. | T020, T021 |
| T023 | Web UI: display sandbox status | Trinity | Run detail view shows sandbox backend selection and warnings. Settings page shows sandbox configuration. Both read from API. | T020, T021 |
| T024 | CLI: display streamed command output | Trinity | `tool.output` events rendered inline during `scaffolder run watch` with stdout/stderr differentiation. `tool.exec_result` shows exit code. | T019 |
| T025 | Web UI: display streamed command output | Trinity | `tool.output` events rendered in the run timeline with terminal-style formatting. `tool.exec_result` shows structured completion. | T019 |

---

### Phase 4: Testing

| ID | Task | Owner | Description | Depends On |
|---|---|---|---|---|
| T026 | Unit tests: PassthroughExecutor | Smith | Verify `IsRealIsolation = false`, `ExecuteAsync` returns denied, `StreamAsync` yields denied. | T010 |
| T027 | Unit tests: SandboxFsPolicyBuilder (F2) | Smith | Verify path mapping uses full `SandboxPathValidator` chain: sandbox root to RW, allowed roots to RO, sensitive paths to denied. Edge cases: symlink root rejected, reparse-point ancestor rejected, empty roots, overlapping paths. | T006 |
| T028 | Unit tests: ShellCommandValidator (F4) | Smith | Verify valid paths pass, paths outside sandbox rejected, null-byte rejected, overlength rejected, edge cases (empty, UNC, relative). | T007 |
| T028a | Unit tests: SandboxOutputRedactor (F7) | Smith | Verify secret patterns redacted, PII redacted when enabled, no false positives on normal output, thread safety. | T007a |
| T029 | Unit tests: SandboxGovernance shell gate (C1/C2) | Smith | Verify `run_command` allowed when executor is real-isolation + config enabled + directory valid; denied when any is false. Verify native `shell` tool ALWAYS denied. Verify `KnownShellTools` path in backend validates directory. | T013, T014 |
| T030 | Unit tests: Executor factory selection (M1) | Smith | Verify factory selects MxcSandboxExecutor when Windows + platform supported, WslMxcSandboxExecutor when Windows + WSL available, LinuxNativeMxcSandboxExecutor when Linux + lxc available, PassthroughExecutor otherwise. | T011 |
| T030a | Unit tests: Binary integrity check (F3) | Smith | Verify executor refuses to start when SHA-256 hash does not match manifest. Verify relative `MXC_BIN_DIR` is rejected. | T008 |
| T031 | Integration tests: MxcSandboxExecutor (gated) | Smith | Behind `MXC_INTEGRATION_TESTS` env var. Verify hello-world command runs, stdout captured, exit code correct, path-escape denied by filesystem policy, timeout enforced. Requires real mxc binaries. | T008, T012 |
| T032 | Integration tests: WslMxcSandboxExecutor (gated) | Smith | Behind `MXC_INTEGRATION_TESTS`. Verify WSL2 hello-world, path mapping, exit code. Verify command is in base64 blob (F8). Requires WSL2. | T009 |
| T032a | Integration tests: LinuxNativeMxcSandboxExecutor (gated) | Smith | Behind `MXC_INTEGRATION_TESTS`. Verify lxc-exec hello-world on Linux, exit code, path-escape denied. Requires Linux host with lxc-exec. | T009a |
| T033 | Integration tests: end-to-end run with shell (C1 verified) | Smith | Start a run via API, confirm `sandbox.selected` event, execute a `run_command` (via custom tool, not native shell), verify streamed `tool.output` events are redacted (F7) and `tool.exec_result` with exit code. Verify native shell is denied. Gated. | T019, T020 |
| T034 | Regression tests: file-tool containment (M2 verified) | Smith | Verify existing path-escape tests still pass. Verify file tools work through existing in-proc path with handle-level TOCTOU verification. No sandbox routing for file tools in this phase. | T015, T016 |
| T034a | Unit tests: HITL destructive command gate (F6) | Smith | Verify destructive patterns trigger approval requirement. Verify non-destructive commands auto-approve. Verify `RequireApprovalForAllShell` forces approval for all. | T017 |

---

### Phase 4a: Built-In Sandboxed Tools (Shell Minimization -- M4)

> Prerequisite: Phase 2 governance integration complete. These tools plug into the same `BuildCopilotCustomTools` / `BuildTools` extension points.

| ID | Task | Owner | Description | Depends On |
|---|---|---|---|---|
| T042 | Static-analysis evidence artifact | Morpheus | Create `specs/002-sandboxed-execution/copilot-builtin-tools.md` documenting confirmed tool schemas from Copilot CLI bundle v1.0.61 static analysis: tool name, arg schema, description, bundle line reference. This is the grounding reference for implementation. | -- |
| T043 | Extend `SandboxedFileTools` with new methods | Morpheus | Add to `packages/Scaffolder.SandboxFs/SandboxedFileTools.cs`: `ReadFileRangeAsync(path, startLine, endLine)`, `CreateFileAsync(path, content)` (fail-if-exists), `StrReplaceAsync(path, oldStr, newStr)` (single-occurrence exact match), `InsertAtLineAsync(path, insertLine, newStr)`. All reuse `SandboxPathValidator.ValidateAbsoluteContained` + `VerifyOpenedHandle` for TOCTOU safety. | T005 |
| T044 | Implement `ApplyPatchAsync` in SandboxedFileTools | Morpheus | Parse the Copilot CLI custom patch grammar (Begin Patch / End Patch, Add File / Delete File / Update File hunks with context/add/remove lines, and "Move to" rename destinations). Implement TWO-PHASE apply: Phase 1 parses the entire patch and collects ALL paths -- every Add File target, Delete File target, Update File target, AND every `*** Move to: <path>` rename destination -- then validates EACH through the full `SandboxPathValidator` reparse-safe chain; if ANY path fails, the entire patch is rejected with zero writes (no partial mutation). Phase 2 applies hunks sequentially only after all paths pass. Return `ApplyPatchResult` with per-hunk outcomes. Test cases MUST include: (a) hunk with `*** Move to: ../escape` is rejected with zero mutation, (b) hunk with `*** Move to: /etc/passwd` (absolute escape) is rejected with zero mutation, (c) valid rename within sandbox succeeds, (d) mixed patch where one hunk has a valid path and another has an escaping "Move to" -- entire patch rejected, zero files modified. | T043 |
| T045 | Implement in-process `grep_search` tool body | Morpheus | New internal class `SandboxedSearchTools` in `Scaffolder.SandboxFs`. Enumerates files under validated sandbox root using `Directory.EnumerateFiles` + `FileSystemGlobbing` for `includePattern`. Excludes `.git`, `node_modules`, etc. Performs line-by-line regex/literal matching (case-insensitive). Caps results at `maxResults`. Every path validated through `SandboxPathValidator`. | T005 |
| T046 | Implement in-process `file_search` tool body | Morpheus | In `SandboxedSearchTools`: glob matching via `Microsoft.Extensions.FileSystemGlobbing.Matcher` constrained to sandbox root. Rejects traversal patterns. Returns relative paths capped at `maxResults`. | T045 |
| T047 | Register file/search AIFunctions in Copilot runner | Morpheus | Extend `BuildCopilotCustomTools` (section 4.3) to register `read_file`, `grep_search`, `file_search`, `str_replace_editor`, `apply_patch`, `create`, `edit` as AIFunctions. Each marked `is_override = true`. Each performs governance eval inline (same pattern as `run_command`). Deny native equivalents in permission handler. | T043, T044, T045, T046, T016 |
| T048 | Register file/search AIFunctions in Foundry runner | Morpheus | Extend `BuildTools(...)` in `FoundryAgentRunner.cs` to register the same seven tools with identical schemas and implementation bodies. Symmetric with Copilot runner (Constitution IV). | T047, T015 |
| T049 | Update `SandboxPolicyBackend` for new tools (three branches) | Morpheus | THREE distinct code changes in `SandboxPolicyBackend.Evaluate()`: (1) KnownFileTools set -- add `"filePath"` to `PathArgumentKeys`; add `"read_file"`, `"str_replace_editor"`, `"apply_patch"`, `"create"`, `"edit"` to `KnownFileTools` (keeping existing entries); path-arg validation applies. For `apply_patch`, per-path validation is deferred to the two-phase tool implementation. (2) KnownSearchTools set (NEW) -- add `"grep_search"` and `"file_search"`; validate working-directory containment only (no path arg to validate); if `includePattern` provided, tool implementation validates no escape. (3) KnownInternalTools set (NEW) -- add `"update_todo"` and `"report_intent"`; allow without path (unconditional allow, reason: "Internal tool, no filesystem access required"). Evaluation order: KnownFileTools -> KnownShellTools -> KnownSearchTools -> KnownInternalTools -> deny-by-default. | T014 |
| T050 | Unit tests: SandboxedFileTools extensions | Smith | Test `ReadFileRangeAsync` (line range, out-of-bounds, empty file), `CreateFileAsync` (success, fail-if-exists, path escape), `StrReplaceAsync` (unique match, non-unique rejected, not-found), `InsertAtLineAsync` (valid line, beyond EOF, negative). All with TOCTOU handle verification. | T043 |
| T051 | Unit tests: ApplyPatchAsync (two-phase + Move-to) | Smith | Test Add File, Delete File, Update File hunks. Test two-phase validation: (a) `*** Move to: ../escape` rejected with zero mutation, (b) `*** Move to: /etc/passwd` rejected with zero mutation, (c) valid rename within sandbox succeeds, (d) mixed patch with one valid hunk and one escaping "Move to" -- entire patch rejected with zero files modified. Test partial paths: absolute path in "Add File:" rejected. Test binary-safety (content with special chars preserved). | T044 |
| T052 | Unit tests: SandboxedSearchTools (grep + file search) | Smith | Test regex/literal matching, includePattern glob, maxResults cap, exclusion directories, path-escape rejected, empty results. | T045, T046 |
| T053 | Integration tests: built-in tools end-to-end (parity) | Smith | Run a scaffolder agent session that uses `read_file`, `edit`, `grep_search` -- verify results match expected, verify no shell processes spawned, verify events emitted, verify output redacted. Test both Copilot and Foundry runners for parity. | T047, T048 |
| T054 | Update documentation: built-in tools reference | Link | Document the sandboxed built-in tools in `docs/architecture/sandboxed-execution.md`: tool names, schemas, scope-out rationale for `semantic_search` (not registered, not stubbed -- excluded via AvailableTools and ExcludedTools enforcement), shell-minimization architecture. Note the is_override rule: true only for tools whose Name matches a real native bundle name. | T047 |

---

### Phase 4b: Reusable Tool Library, Native Exclusion, and Memory/Planning (M5/M6/M7)

> Prerequisite: Phase 4a tool implementations complete. This phase refactors them into the shared library, wires AvailableTools exclusion, and adds memory/planning tools.

| ID | Task | Owner | Description | Depends On |
|---|---|---|---|---|
| T055 | Create `Scaffolder.AgentTools` package | Morpheus | New .csproj (net10.0), add to solution. References: `Scaffolder.SandboxFs`, `Scaffolder.SandboxExec`, `Scaffolder.Domain`, `Microsoft.Extensions.AI`. NO reference to `Scaffolder.AgentRuntime` (RBD1 resolution). Define `ISandboxTool` interface and `SandboxToolContext` record as specified in section 4.8.2 -- governance exposed as `Func<string, IReadOnlyDictionary<string, object>, (bool Allowed, string? Reason)> EvaluateToolCall` delegate, NOT the concrete `SandboxGovernance` type. Verify acyclic dependency graph: AgentTools -> {SandboxFs, SandboxExec, Domain}; AgentRuntime -> {AgentTools, SandboxFs, SandboxExec, Domain}. | T005 |
| T056 | Implement `SandboxToolRegistry` | Morpheus | Assembles `IList<AIFunction>` from registered `ISandboxTool` classes. Conditionally includes `RunCommandTool` (gated on IsRealIsolation + ShellEnabled). Exposes `GetToolNames()` for `AvailableTools` construction. See section 4.8.4. | T055 |
| T057 | Refactor existing tools into `ISandboxTool` classes | Morpheus | Move inline lambda logic from `BuildCopilotCustomTools` and `BuildTools` into named classes: `RunCommandTool`, `ReadFileTool`, `GrepSearchTool`, `FileSearchTool`, `StrReplaceEditorTool`, `ApplyPatchTool`, `CreateFileTool`, `EditFileTool`. Each implements `ISandboxTool`, routes through existing primitives (no behavior change). | T055, T047, T048 |
| T058 | Implement `SandboxMemoryStore` | Morpheus | JSON-backed persistent store at `{sandboxRoot}/.scaffolder/memory.json`. Methods: `StoreAsync`, `VoteAsync`, `RecallAsync`. File I/O through `SandboxPathValidator`-verified path. File-level lock for concurrency. All return values from store/vote/recall MUST pass through `SandboxOutputRedactor.Redact()` before being returned to the LLM framework (F-BT2: prevents secret amplification if stored facts contain sensitive content). See section 4.7.8.2. | T055 |
| T059 | Implement `StoreMemoryTool` and `VoteMemoryTool` | Morpheus | `ISandboxTool` classes backed by `SandboxMemoryStore`. `store_memory`: validates fields (fact <200 chars, subject <50 chars), appends entry, returns confirmation. `vote_memory`: finds matching fact, appends vote record, returns result or error. Both marked `IsOverride = true` (native `store_memory`/`vote_memory` exist in bundle). Return values pass through `SandboxOutputRedactor.Redact()` before return to model (F-BT2). | T058 |
| T060 | Implement `RunTodoState`, `UpdateTodoTool`, `ReportIntentTool` | Morpheus | `RunTodoState`: in-memory markdown checklist parser (counts `- [x]` / `- [ ]` lines). `UpdateTodoTool`: stores checklist, returns counts, `IsOverride = true` (native `update_todo` exists). `ReportIntentTool`: emits `agent.intent` event, returns "Intent logged", `IsOverride = true` (native `report_intent` exists). Return values pass through `SandboxOutputRedactor.Redact()` (F-BT2). See section 4.7.8.3. | T055 |
| T061 | Wire `AvailableTools` allowlist in Copilot runner | Morpheus | Set `SessionConfig.AvailableTools` to `registry.GetToolNames(includeShell)`. Set `SessionConfig.ExcludedTools` to `NativeToolExclusion.GetExcludedToolNames()`. Verify: model sees ONLY custom tools. Cite: spike doc line 86-92 confirms allowlist semantics. See section 4.8.6. | T056, T057, T059, T060 |
| T062 | Implement `NativeToolExclusion` | Morpheus | Static class listing known native tool names from bundle permission map (line ~1315): `bash`, `shell`, `write`, `read`, `view`, `ls`, `glob`, `grep`, `task`, `webfetch`, `web_fetch`, `websearch`, `web_search`, `memory`, `semantic_search`. Returns `IList<string>` for `ExcludedTools`. | T055 |
| T063 | Refactor runners to use `SandboxToolRegistry` | Morpheus | Replace `BuildCopilotCustomTools` and `BuildTools` inline tool construction in both runners with `registry.Build(context)`. Remove duplicated lambda code. Verify identical behavior (no functional change). Constitution IV parity confirmed. | T056, T057, T059, T060, T061, T062 |
| T064 | Add `agent.intent` event type to Domain | Morpheus | Add `"agent.intent"` to `EventTypes.cs` in `Scaffolder.Domain`. Payload: `{ intent: string }`. Emitted by `ReportIntentTool`. Consumed by CLI/Web UI run-status display. | T060 |
| T064a | CLI + Web UI render agent.intent as live status indicator | Trinity | Render `agent.intent` events as a live status line in `scaffolder run watch` (CLI) and as a real-time status badge/text in the run detail view (Web UI). Updates on each new `agent.intent` event. Depends on T064 (event exists), T022 (CLI run watch infrastructure), T023 (Web UI run detail infrastructure). Constitution V: no dead events -- every emitted event must have a consumer. | T064, T022, T023 |
| T065 | Unit tests: `SandboxMemoryStore` | Smith | Test store (append, field validation, max-length enforcement), vote (found/not-found, upvote/downvote), recall (returns all entries), concurrent writes (file lock), path-escape on store path (rejected). | T058 |
| T066 | Unit tests: `StoreMemoryTool` and `VoteMemoryTool` | Smith | Test via `ISandboxTool.CreateFunction` -- valid store, invalid fields rejected, vote on existing/missing fact. Verify `is_override = true` metadata. | T059 |
| T067 | Unit tests: `UpdateTodoTool` and `ReportIntentTool` | Smith | Test checklist parsing (mixed completed/pending), empty input, intent event emission. Verify `is_override = true` metadata. | T060 |
| T068 | Unit tests: `SandboxToolRegistry` (canonical names) | Smith | Test `Build` returns all tools with correct names. Test `RunCommandTool` excluded when shell disabled. Test `GetToolNames` matches `Build` output names. Test `is_override` set correctly: true for all tools EXCEPT `RunCommandTool` (which has `IsOverride = false`). CANONICAL NAME TEST (RBD4): assert that `registry.GetToolNames(includeDisabled: true)` equals a hardcoded constant array exactly matching the bundle names: `["run_command", "read_file", "grep_search", "file_search", "str_replace_editor", "apply_patch", "create", "edit", "store_memory", "vote_memory", "update_todo", "report_intent"]`. This array is the single source of truth, cross-referenced to `copilot-builtin-tools.md`. A name typo (e.g. "readFile" vs "read_file") fails the test and prevents silent allowlist lockout. | T056 |
| T069 | Unit tests: `NativeToolExclusion` | Smith | Test `GetExcludedToolNames` returns known native names. Test no overlap with custom tool names from registry. | T062 |
| T070 | Integration test: AvailableTools allowlist enforcement | Smith | Start a Copilot runner session. Verify `SessionConfig.AvailableTools` contains exactly our custom tool names. Attempt to invoke a native tool name (e.g. `bash`) -- verify it is rejected/invisible. Verify forward-compat: a fabricated tool name not in allowlist is also invisible. | T061, T063 |
| T071 | Update copilot-builtin-tools.md evidence artifact | Morpheus | Add memory tool schemas (`store_memory`, `vote_memory`) and planning tool schemas (`update_todo`, `report_intent`) to the evidence artifact. Document `exit_plan_mode` as scoped-out (internal orchestration). | T042 |
| T072 | Update documentation: memory/planning tools + exclusion strategy | Link | Extend `docs/architecture/sandboxed-execution.md` with: reusable tool library architecture, AvailableTools/ExcludedTools exclusion strategy, memory/planning tool specifications, scope-out rationale for `exit_plan_mode`/`task`/`notebook`/`web_*`/`sql`. | T063 |

---

### Phase 5: Documentation and Security Review

| ID | Task | Owner | Description | Depends On |
|---|---|---|---|---|
| T035 | User documentation: sandbox setup | Link | Document Windows ARM64 setup runbook (binary placement, env var, ViVeTool keys if needed). Document WSL2 path setup. Document Linux-native cloud setup. Document the "not a security boundary yet" caveat. | T004, T012 |
| T036 | API documentation: sandbox endpoints/events | Tank | Update `docs/reference/api.md` with `sandbox.selected`, `sandbox.warning`, `tool.output`, `tool.exec_result` event schemas. Document backward-compat: `tool.exec_result` is new (N2), does not replace `tool.result`. `GET /api/settings/sandbox` endpoint. | T020, T021 |
| T037 | CLI documentation | Trinity | Update CLI help text and `docs/reference/cli.md` with `scaffolder settings sandbox` command. | T022 |
| T038 | Design document (FR-030) | Morpheus | Committed design doc at `docs/architecture/sandboxed-execution.md` capturing spike results, design seam, all review resolutions, win-arm64 runbook, linux-native runbook, mxc caveat. | T004, all Phase 2 |
| T039 | Pre-implementation security review | Seraph | Review the plan, abstraction contract, and governance changes for security gaps. Gate before Phase 2 integration begins. | T005-T012 |
| T040 | Post-implementation security review | Seraph | Review the complete implementation for sandbox escapes, governance bypasses, redaction completeness, binary integrity, and audit completeness. Gate before merge. | All Phase 2-4 |
| T041 | RAI review | Rai | Verify responsible-AI obligations: human accountability preserved (F6 HITL gate), all actions auditable, output redaction (F7) effective, no harmful content generation path introduced. | T040 |

---

## 8. Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| mxc `processcontainer` fails on target host | Medium | Blocks Windows-native path | Phase 0 spike validates first; WSL2 path as fallback; LinuxNative for cloud; PassthroughExecutor ensures safety |
| mxc schema/API breaks (preview churn) | Medium | Breaks executor | Pin `SandboxPolicy.Version`; abstraction isolates runners from SDK changes |
| No real isolation on Linux cloud host | Low | Blocks cloud shell (was High before M1) | `LinuxNativeMxcSandboxExecutor` probes for `lxc-exec`; cloud images can pre-install it |
| WSL2 not available on all target hosts | Low | Reduces coverage | Windows-native path is primary; WSL2 is secondary; Linux-native for cloud; PassthroughExecutor is always safe |
| Binary distribution size/complexity | Low | Packaging overhead | Bundled per-arch with `MXC_BIN_DIR` escape hatch; integrity manifest (F3) |
| Network allowlist gap on Windows | Certain | Cannot restrict per-host network | Documented; default open; runtime warning event emitted (F5); operators can tighten to block+proxy; Linux paths for true allowlisting |
| mxc binary hijack via PATH/env | Low | Arbitrary code execution | Binary discovery is absolute-path-only, never PATH; integrity check required (F3) |
| Secret/PII leakage in streamed output | Medium | Privacy violation | `SandboxOutputRedactor` pipeline (F7); configurable patterns |
| File-tool sandbox routing deferred | Low (mitigated by M4) | Reduced isolation for file ops | In-proc TOCTOU verification remains via M4 built-in tools; shell no longer needed for file ops; mxc policy enforcement available for future increment (M2 original concern superseded) |

---

## 9. Rollback and Feature-Flag Strategy

- **Configuration gate**: `Sandbox:ShellEnabled` (default `true` post-gate, but FR-028 keeps it gated during exploratory phase). Setting to `false` reverts to categorical shell deny.
- **Executor fallback**: If mxc fails at runtime (binary missing, probe fails, spawn throws, integrity check fails), the factory returns `PassthroughExecutor` which denies everything safely.
- **Package isolation**: `Scaffolder.SandboxExec` is a separate package. Removing the reference from runners reverts to pre-feature behavior.
- **YAML policy rollback**: Reverting the `allow-shell-sandboxed` rule to `deny-shell` in `SandboxGovernance.SandboxPolicyYaml` restores categorical deny.
- **Custom tool removal**: Removing `run_command` from `SessionConfig.Tools` (Copilot) or `BuildTools` (Foundry) removes shell capability entirely.
- **Redaction bypass**: `Sandbox:RedactPii = false` disables PII redaction for debugging (secrets are always redacted).

---

## 10. Open Questions

None blocking. All spec clarifications (FR-033 through FR-036) are resolved. Implementation proceeds per the resolved decisions.

**Advisory notes for the coordinator:**
- The exact `SandboxPolicy.Version` pin ("0.4.0-alpha") should be confirmed in the Phase 0 spike against the actual binary version deployed.
- File-tool routing through mxc (FR-033) is deferred to a future increment pending a purpose-built file helper (M2). The N3 benchmark (T004a) provides data to inform that decision.
- The WSL2 path (T003, T009) is secondary priority and can be deferred to a follow-up if the Windows-native path succeeds.
- The `LinuxNativeMxcSandboxExecutor` (T009a) requires `lxc-exec` to be available on the cloud host image. Cloud deployment documentation should specify this as a prerequisite.

---

## 11. Review Resolutions

| Finding ID | Summary | Resolution |
|---|---|---|
| **C1/F1** | Copilot runner shell routing architecturally wrong -- approving native shell executes unsandboxed | Native `PermissionRequestShell` kept ALWAYS DENIED. Custom `run_command` AIFunction registered via `SessionConfig.Tools` (with `is_override = false` -- no native tool named `run_command` exists) executes in-process, routes through `ISandboxExecutor.StreamAsync(...)`. Native shell excluded via AvailableTools + ExcludedTools. Both runners now symmetric. See sections 1, 4.3, task T016. |
| **C2** | SandboxPolicyBackend unconditionally denies shell (no PathArgumentKeys match for `["command"]`) | Added `KnownShellTools` set in backend with its own `["directory"]` extraction. Custom `run_command` tool injects `["directory"] = workingDirectory` into its governance args dict. See sections 4.1, 4.2, task T014. |
| **F2** | FS-policy symlink escape -- `SandboxFsPolicyBuilder` used bare `Path.GetFullPath` | Builder now canonicalizes ALL paths through full `SandboxPathValidator` reparse-safe chain (including symlink/junction walk, reparse-point ancestor check). See section 3.5, task T006. |
| **M1** | No Linux-native executor -- cloud hosts fall to PassthroughExecutor | Added fourth executor `LinuxNativeMxcSandboxExecutor` invoking `lxc-exec` directly. Factory probe order: Windows-native -> WSL2 -> Linux-native -> Passthrough. See sections 3.3, 3.4, tasks T009a, T011, T032a. |
| **M2** | File-tool routing fragility -- cat/echo corrupts content, loses TOCTOU defense | FR-033 file-tool routing DEFERRED to future increment. Shell-only for Phase 1. File tools remain in-proc with handle-level TOCTOU verification. Trade-off documented. See section 4.4, task T034. |
| **F3** | MXC_BIN_DIR requires absolute path + integrity check | Binary discovery requires absolute path (relative rejected), PATH never consulted. Resolved binary undergoes Authenticode/SHA-256 manifest integrity check. See section 3.3 (MxcSandboxExecutor), tasks T008, T030a. |
| **F4** | Command-content validation beyond working-directory | `ShellCommandValidator` performs: working-directory containment, command length cap, null-byte rejection. Host-side validation does not rely solely on mxc. See section 3.6, tasks T007, T028. |
| **F5** | Network open on Windows -- exfiltration surface invisible | Runtime emits `sandbox.warning` event when sandbox runs with open network on Windows. Event is auditable and visible to clients. See section 3.9, tasks T011, T019. |
| **F6** | Human-approval gate for shell (Constitution X) | Destructive/irreversible commands require HITL approval via MAF governance gate. Configurable patterns + `RequireApprovalForAllShell` option. See section 3.7, task T017. |
| **F7** | Streamed output must pass through secret/PII redaction | `SandboxOutputRedactor` pipeline applied before events hit logs/stream. Configurable secret patterns + built-in PII patterns. See section 3.8, tasks T007a, T028a. |
| **F8** | WSL executor command injection via bare wsl.exe arguments | Command is serialized INSIDE the base64 config blob, never as a bare `wsl.exe` argument. Config JSON structure validated before encoding. See section 3.3 (WslMxcSandboxExecutor), task T009. |
| **N1** | Binary licensing -- redistribution license verification | T012 now has prerequisite: verify mxc redistribution license + generate `NOTICE` attribution file. See task T012. |
| **N2** | Event schema -- `tool.exec_result` vs extended `tool.result` | Distinct `tool.exec_result` event type chosen (preserves backward compat). See section 4.5, task T036. |
| **N3** | Perf benchmark for file-tool sandbox routing | Added Phase 0 subtask T004a to benchmark per-op latency (mxc cat vs in-proc). Feeds M2 decision for future increment. See task T004a. |
| **M4** | Built-in sandboxed tools supersede shell for file ops | Purpose-built AIFunction tools mirror Copilot CLI built-in names/schemas (`read_file`, `grep_search`, `file_search`, `str_replace_editor`, `apply_patch`, `create`, `edit`). Execute in-process via `SandboxedFileTools` (TOCTOU preserved) and `SandboxedSearchTools`. File/search/memory/planning tools registered with `is_override = true` (matching native names); `run_command` with `is_override = false` (no native counterpart). Native built-ins denied via AvailableTools allowlist as primary enforcement. `semantic_search` scoped OUT (requires GitHub embeddings API, not registered). Supersedes M2 deferral positively. See section 4.7, Phase 4a tasks T042-T054. |
| **M5** | Reusable tool library -- DRY refactor (Constitution IV) | All custom tools refactored into `packages/Scaffolder.AgentTools/` with `ISandboxTool` contract and `SandboxToolRegistry`. Both runners consume registry output instead of building tools inline. Each tool is its own class, independently testable. See section 4.8, Phase 4b tasks T055-T057, T063. |
| **M6** | Native-tool exclusion via AvailableTools allowlist | `SessionConfig.AvailableTools` set to explicit allowlist (ONLY our custom tool names). Confirmed allowlist semantics from spike doc (line 86-92, 179). `ExcludedTools` populated with known native names as defense-in-depth. Forward-compat guaranteed: new native tools auto-excluded by allowlist. See section 4.8.6, tasks T061, T062, T070. |
| **M7** | Memory/planning tools reimplemented as custom AIFunctions | `store_memory`, `vote_memory` backed by sandbox-scoped JSON store (`{sandboxRoot}/.scaffolder/memory.json`). `update_todo` backed by in-memory `RunTodoState`. `report_intent` emits `agent.intent` event. `exit_plan_mode` scoped OUT (internal SDK orchestration, not model-callable). `task`/`notebook`/`web_*`/`sql` scoped OUT per user direction or irrelevance. See section 4.7.8, tasks T058-T060, T064-T067, T071. |
| **RBD1** | Circular package dependency: AgentTools references SandboxGovernance (internal in AgentRuntime) creating bidirectional dep | Replaced `SandboxGovernance Governance` field in `SandboxToolContext` with a delegate: `Func<string, IReadOnlyDictionary<string, object>, (bool Allowed, string? Reason)> EvaluateToolCall`. Runner binds at construction: `(toolName, args) => governance.EvaluateToolCall(agentId, toolName, args, logger)`. Post-fix dependency graph (acyclic): AgentTools -> {SandboxFs, SandboxExec, Domain}; AgentRuntime -> {AgentTools, SandboxFs, SandboxExec, Domain}; SandboxExec -> {SandboxFs, Domain}; SandboxFs -> {Domain}. No other type in SandboxToolContext lives in AgentRuntime. See section 4.8.2, task T055. |
| **RBD2 / F-BT4** | `is_override=true` on run_command is incorrect -- no native tool named `run_command` exists | `RunCommandTool.IsOverride = false`. Native shell tools (`shell`/`bash`) are excluded via AvailableTools (not listed in allowlist) + ExcludedTools (blocklist). Rule: `IsOverride = true` ONLY for custom tools whose Name matches an actual native tool name in the bundle (verified against copilot-builtin-tools.md evidence artifact). All other tools (read_file, grep_search, file_search, str_replace_editor, apply_patch, create, edit, store_memory, vote_memory, update_todo, report_intent) DO match native names and retain `IsOverride = true`. See sections 4.3, 4.8.2, 4.8.4, task T068. |
| **RBD3 / F-BT1** | apply_patch path validation missing "Move to" rename destinations; partial-mutation-then-escape possible | TWO-PHASE apply: Phase 1 parses entire patch, collects ALL paths (Add/Delete/Update targets AND every `*** Move to: <path>` destination), validates EACH through full SandboxPathValidator chain; if ANY fails, reject entire patch with zero writes. Phase 2 applies hunks only after all paths pass. T044 and T051 explicitly name "Move to" as a validated path and include escape tests (`*** Move to: ../escape` and absolute-path escapes rejected with zero mutation). See sections 4.7.3, 4.7.5, tasks T044, T051. |
| **F-BT2 / F-BT3** | Tool RETURN values (to LLM framework) not redacted -- only events were redacted; enables secret amplification via model echo | Every tool AIFunction runs its return value through `SandboxOutputRedactor.Redact()` BEFORE returning to the LLM framework. Applies to: read_file content, grep_search/file_search results, run_command output, store_memory/vote_memory echoes, RecallAsync content. Event-level redaction (F7) remains as defense-in-depth on top. See sections 4.7.5, 4.7.8.2, 4.7.8.4, tasks T058, T059, T060. |
| **RBD4** | T068 self-referentially checks names so a typo passes tests but locks model out via allowlist | T068 adds a test asserting `registry.GetToolNames(includeDisabled: true)` equals a hardcoded canonical constant array: `["run_command", "read_file", "grep_search", "file_search", "str_replace_editor", "apply_patch", "create", "edit", "store_memory", "vote_memory", "update_todo", "report_intent"]`. This array is the single source of truth, cross-referenced to copilot-builtin-tools.md. A typo fails the test. See section 4.8.4, task T068. |
| **RBD5** | No UI consumer task for `agent.intent` event -- dead event violates Constitution V | Added task T064a: "CLI + Web UI render agent.intent as a live status indicator in run watch / run detail". Depends on T064, T022, T023. Constitution V requires every emitted event to have a consumer. See task T064a. |
| **RBD6** | T049 must enumerate three distinct backend branches -- without all three, implementer only does (1) | T049 description explicitly enumerates THREE code changes: (1) `KnownFileTools` with path-arg validation (add `"filePath"` to PathArgumentKeys), (2) `KnownSearchTools` set for grep_search/file_search with working-directory-containment-only validation, (3) `KnownInternalTools` set for update_todo/report_intent with allow-without-path. Evaluation order specified. See section 4.7.5, task T049. |
| **F-BT5** | semantic_search "if invoked" wording implies a stub/error-handler exists | Clarified: semantic_search is NOT registered, NOT stubbed. It is excluded from SandboxToolRegistry entirely. The model cannot invoke it (AvailableTools allowlist does not list it). If a future SDK version bypasses the allowlist, ExcludedTools and permission handler provide tertiary enforcement. See section 4.7.4, task T054. |
