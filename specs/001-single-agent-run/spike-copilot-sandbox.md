# Spike: Copilot SDK Sandbox Enforcement Feasibility

**Date**: 2026-06-07
**Author**: Morpheus (Runtime Engineer)
**Status**: Complete -- findings ready for design

---

## What Was Inspected

| Artifact | Location |
|----------|----------|
| GitHub.Copilot.SDK 1.0.0-beta.2 (net8.0 TFM) | `~/.nuget/packages/github.copilot.sdk/1.0.0-beta.2/lib/net8.0/` |
| GitHub.Copilot.SDK 1.0.0 (net10.0 TFM, namespace `GitHub.Copilot`) | `~/.nuget/packages/github.copilot.sdk/1.0.0/lib/net10.0/` |
| Microsoft.Agents.AI.GitHub.Copilot 1.9.0-preview.260603.1 | `~/.nuget/packages/microsoft.agents.ai.github.copilot/1.9.0-preview.260603.1/lib/net10.0/` |
| Microsoft.Agents.AI 1.8.0 (MAF core) | `~/.nuget/packages/microsoft.agents.ai/1.8.0/lib/net10.0/` |
| Microsoft.Extensions.AI 10.5.1 | `~/.nuget/packages/microsoft.extensions.ai/10.5.1/` |
| Resolved version in project.assets.json | 1.0.0-beta.2 (beta pinned in csproj) |

Inspection method: XML doc analysis + .NET reflection (`Assembly.LoadFrom`) on the SDK DLL, plus README code samples bundled with the NuGet package.

---

## Answers to the Five Gating Questions

### 1. Permission Callback Shape

**Delegate type:**
```
GitHub.Copilot.SDK.PermissionRequestHandler
  : Func<PermissionRequest, PermissionInvocation, Task<PermissionRequestResult>>
```

The callback is **asynchronous** (returns `Task<PermissionRequestResult>`).

**`PermissionRequest` argument** -- a polymorphic type with discriminator `Kind` (string). Concrete subtypes and their key properties:

| Kind (string) | Concrete type | Key data exposed |
|---------------|---------------|------------------|
| `"shell"` | `PermissionRequestShell` | `FullCommandText`, `PossiblePaths` (string[]), `PossibleUrls`, `Commands` (parsed identifiers), `HasWriteFileRedirection`, `ToolCallId` |
| `"write"` | `PermissionRequestWrite` | `FileName` (target path), `Diff`, `NewFileContents`, `ToolCallId` |
| `"read"` | `PermissionRequestRead` | `Path` (target file/directory), `ToolCallId` |
| `"mcp"` | `PermissionRequestMcp` | `ToolName`, `ServerName`, `Args`, `ReadOnly`, `ToolCallId` |
| `"custom_tool"` | `PermissionRequestCustomTool` | (tool name/args -- invokes user-registered AIFunctions) |
| `"url"` | `PermissionRequestUrl` | `Url` |
| `"memory"` | `PermissionRequestMemory` | memory action/direction |
| `"hook"` | `PermissionRequestHook` | hook confirmation data |

**`PermissionInvocation` argument** -- carries only `SessionId` (string). Minimal context but enough to correlate.

**Return type** -- `PermissionRequestResult` with property `Kind` of type `PermissionRequestResultKind`:
- `Approved` -- allow
- `DeniedByRules` -- denied by policy (our preferred deny reason)
- `DeniedInteractivelyByUser` -- user said no
- `DeniedCouldNotRequestFromUser` -- user unavailable
- `NoResult` -- leave unanswered (protocol v2 rejects this)

**Assessment**: The request payload carries **sufficient data** for a sandbox decision. For `read`/`write`, the target path is explicit (`Path`, `FileName`). For `shell`, `FullCommandText` and `PossiblePaths` give best-effort path extraction, plus we can deny shell wholesale. For `mcp`/`custom_tool`, the tool name and args are present.

---

### 2. Callback Coverage

Per the SDK README (emphasis mine):

> "Handler called before **each tool execution** to approve or deny it."

The documented `Kind` values cover all known operation categories the agent can invoke: shell, read, write, MCP, custom tool, URL fetch, memory, and hooks. The SDK documentation states the handler is "required" and fires for every tool call.

**Built-in handlers:**
- `PermissionHandler.ApproveAll` -- static property providing a handler that returns `Approved` for everything. This is the ONLY built-in. There is no `DenyAll` shipped, but writing one is trivial.

**Coverage gap analysis:**
- The SDK runs as a JSON-RPC client to the Copilot CLI subprocess. The permission callback is a protocol-level gate in the RPC flow. The CLI subprocess cannot execute a tool without the host returning a permission decision over the RPC channel.
- There is no documented or observable bypass path in the public API surface.
- The `skip_permission` flag on custom tool `AdditionalProperties` only applies to tools WE register (not built-in native tools).

**Conclusion**: Coverage appears comprehensive. Every sensitive native operation fires the callback. A `DeniedByRules` response is respected by the protocol.

---

### 3. Toolset Restriction

The SDK provides **two complementary mechanisms** on `SessionConfig` / `SessionConfigBase`:

1. **`AvailableTools`** (`IList<string>?`) -- allowlist. "Only these tools will be available when specified." When set, it takes precedence over `ExcludedTools`.

2. **`ExcludedTools`** (`IList<string>?`) -- blocklist. "All other tools remain available."

3. **`Tools`** (`IList<AIFunction>?`) -- custom tool functions added to the session. Can override built-in tools with `is_override = true`.

The `AvailableTools` allowlist is the strongest mechanism: if we set it to (for example) `["read_file", "write_file", "list_directory"]`, the shell/command tool and all other native tools are simply not available to the agent. The agent cannot call a tool not in the allowlist.

Additionally, the 1.0.0 stable SDK adds feature flags on `SessionConfigBase`:
- `EnableHostGitOperations` (bool) -- can disable git operations
- `EnableMcpApps` (bool) -- can disable MCP apps
- `EnableSkills` (bool) -- can disable skills
- `EnableFileHooks` (bool) -- can disable hooks

**Conclusion**: The toolset CAN be locked down via `AvailableTools` allowlist. We can restrict the agent to only file read/write/list tools.

---

### 4. Working-Directory Hard-Bounding

`WorkingDirectory` (string) on `SessionConfig` is documented simply as:

> "Working directory for the session."

There is **no** documented `Chroot`, `Jail`, `RootDirectory`, or `SandboxRoot` option anywhere in the public API surface. The `WorkingDirectory` property sets the cwd for the CLI subprocess but does NOT prevent the agent from accessing paths outside it.

There is also `CreateSessionFsProvider` / `CreateSessionFsHandler`:

> "Supplies a handler for session filesystem operations. This is used only when `CopilotClientOptions.SessionFs` is configured."

This is for session state persistence, not for sandboxing file operations issued by the agent.

**Conclusion**: `WorkingDirectory` is a hint only. The SDK does NOT provide a hard filesystem boundary. Sandbox enforcement must be implemented at the permission-handler level, NOT relied upon from `WorkingDirectory`.

---

### 5. MAF Integration Angle

The MAF bridge (`Microsoft.Agents.AI.GitHub.Copilot.GitHubCopilotAgent`) exposes:

- `AsAIAgent(CopilotClient, SessionConfig, ...)` -- wraps the SDK client as a MAF `AIAgent`. The `SessionConfig` passed here is the same SDK config with `OnPermissionRequest`, `AvailableTools`, etc.

- A second constructor overload accepts `IList<AITool>` -- these become the `SessionConfig.Tools` custom tools.

The MAF bridge delegates execution to the SDK via `RunCoreStreamingAsync`, which calls `client.CreateSessionAsync(config)` internally. The permission handler and tool restrictions configured in `SessionConfig` are respected because the bridge copies them through (`CopySessionConfig`).

**MAF-level middleware:**
- `Microsoft.Extensions.AI` provides `FunctionInvokingChatClient` with `UseFunctionInvocation` middleware, but this applies to `IChatClient`-based agents (e.g., `ChatClientAgent`), NOT to the GitHub Copilot bridge. The Copilot agent does not use `IChatClient` for tool invocation -- it uses the SDK's JSON-RPC protocol. MAF's function-invocation middleware cannot intercept SDK-native tool calls.

- MAF's `AIAgent` base class does not expose a tool-invocation filter or governance middleware hook that applies to tool calls happening inside the SDK subprocess.

**Conclusion**: There is no MAF-level middleware that can intercept SDK-native tool calls. Governance must ride on the SDK's own `OnPermissionRequest` callback and `AvailableTools` restriction. The MAF bridge faithfully passes these through, so enforcement is still achievable -- it simply lives at the SDK config level, not at a MAF middleware level.

---

## Recommended Phase 3 Enforcement Mechanism

The recommended mechanism is a **two-layer defense-in-depth** approach, both configured through `SessionConfig`:

### Layer 1: Toolset Allowlist (attack surface reduction)

Set `SessionConfig.AvailableTools` to an explicit allowlist of ONLY the tools that can be sandbox-validated:
- `read_file` / `list_directory` -- file reads, path in request
- `write_file` / `edit_file` -- file writes, path in request

This removes `shell` (command execution), `url` (network), `memory`, and any other tool that cannot be confined to the artifact directory. The agent simply cannot invoke a tool not in the allowlist.

### Layer 2: Deny-by-Default Permission Handler (path validation)

Replace `PermissionHandler.ApproveAll` with a custom `PermissionRequestHandler` that:

1. For `Kind == "read"`: extracts `Path`, passes to `SandboxPathValidator.ValidateAndResolve(path, artifactRoot)`. If validation throws `SandboxViolationException`, returns `DeniedByRules`. Otherwise returns `Approved`.

2. For `Kind == "write"`: extracts `FileName`, same validation logic.

3. For `Kind == "shell"`: returns `DeniedByRules` unconditionally (shell is excluded by Layer 1, but defense-in-depth).

4. For ANY other `Kind`: returns `DeniedByRules` unconditionally (default-deny).

5. Logs every decision (allow + deny) to the operational record per FR-033 / SC-012.

### Layer 3 (future): OS-level Isolation

Acknowledged as out-of-scope for this slice per the spec clarification. The permission handler + tool allowlist provide software-level confinement. OS isolation (container, job object, restricted token) is a planned follow-up that adds defense against SDK bugs or protocol bypasses.

---

## Gating Verdict

**CAN guarantee confinement** at the SDK API level, subject to the following trust assumptions:

1. **The SDK RPC protocol is faithful**: the CLI subprocess does not execute a tool without receiving an `Approved` response from the host over the RPC channel. This is architecturally sound (the host is the RPC server; the CLI is the client requesting permission) and there is no documented bypass.

2. **`AvailableTools` is enforced server-side**: when set, tools not in the list are not offered to the model and cannot be called. The SDK validates this at the wire protocol level (`ResolveToolFilterOptions`).

3. **No SDK bug allows tool execution without permission callback**: this is the residual risk that OS-level isolation would cover in a later slice.

Given assumptions (1) and (2), the combination of `AvailableTools` allowlist + deny-by-default `OnPermissionRequest` handler backed by `SandboxPathValidator` **guarantees that no provider-native operation escapes the artifact directory** within the SDK's documented contract. The permission handler's path-validation logic (lexical prefix, segment scan, reparse-point walk, TOCTOU handle verification) is already robust and tested.

**No re-confirmation with the user is needed for this slice** -- the SDK provides sufficient hooks for confinement without OS-level isolation.

---

## Open Risks for Phase 4 (Microsoft Foundry)

1. The Foundry runner (`packages/Scaffolder.AgentRuntime/Providers/`) uses a different execution path (likely `IChatClient` via Azure.AI.OpenAI + M.E.AI `FunctionInvokingChatClient`). Its sandbox enforcement MUST go through M.E.AI's `UseFunctionInvocation` middleware or by validating paths inside the `AIFunction` implementations themselves. The Foundry provider does NOT have a protocol-level permission gate like the Copilot SDK.

2. For Foundry, the agent's toolset is entirely under our control (we supply the `IList<AITool>`), so there is no risk of "unknown native tools." The risk is that tool implementations themselves must enforce path validation -- the shared `SandboxedFileTools` already does this, so the gap is smaller.

3. The shared governance policy (FR-032) means the same `SandboxPathValidator` must be wired into both providers. For Copilot this rides on `OnPermissionRequest`; for Foundry it rides on the `AIFunction` wrappers in `SandboxedFileTools`. Both are satisfiable.

---

## Summary

The GitHub Copilot SDK provides both a **toolset allowlist** (`AvailableTools`) and a **deny-by-default permission callback** (`OnPermissionRequest`) with rich per-operation metadata (file paths, command text, tool names). These two mechanisms, combined, are sufficient to guarantee sandbox confinement at the API level. No MAF middleware intercept is available or needed -- the SDK's own hooks are the correct enforcement point for this provider.
