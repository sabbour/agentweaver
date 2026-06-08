# Design: Sandbox Enforcement (FR-030 / FR-031 / FR-032 / FR-033)

**Date**: 2026-06-08 (Revision 4)
**Author**: Morpheus (Runtime Engineer)
**Status**: Design-complete — ready for implementation
**Revision 4 — gate findings closed**: Resolves all blocking + advisory findings from
the rubber-duck and Seraph pre-implementation reviews of Rev 3. Changes:

1. **BLOCKING — `read` ↔ `list_directory` mapping** (rubber-duck #1): Explicit design
   decision — `MapToToolCall` inspects whether the path targets a directory (trailing
   separator OR `Directory.Exists`) and maps to `list_directory`; otherwise `read_file`.
   YAML rules consolidated into a unified `allow-file-read-or-list` rule so
   `SandboxPolicyBackend` handles containment regardless. No silent functional break.
2. **HIGH — unconditional containment fallback** (rubber-duck #2 / Seraph §2):
   `BuildPermissionHandler` now calls `SandboxPolicyBackend.Evaluate(...)` DIRECTLY,
   independently of `GovernanceKernel.EvaluateToolCall()`. Deny if EITHER denies.
   Enforcement never depends solely on AGT invoking our backend.
3. **Advisory — sync-over-async** (rubber-duck #3 / Seraph): Prefer `EvaluateAsync` when
   available; document bounded thread-pool risk.
4. **Advisory — unrecognized tool default** (Seraph Y-1): `SandboxPolicyBackend.Evaluate`
   now returns `Allowed = false` for any tool name not in the known-file-tools set.
   Construction-time assertion that YAML `defaultAction` is `Deny`.
5. **Advisory — argument key contract** (Seraph Y-2): Design rule that ALL sandboxed
   file-tool functions MUST use `"path"` as argument name. Resolver checks known keys.
   AGT audit entry always records the resolved path.
6. **Advisory — durable audit health-check** (Seraph Y-3 / rubber-duck #6): Startup
   health-check logs WARNING (or fails fast in prod) if no durable `AuditEmitter` sink.
7. **Advisory — GovernanceKernel lifecycle** (rubber-duck #5): Explicit `using`/`Dispose()`
   in both runners.
8. **Advisory — version pinning** (rubber-duck #6): PackageReference pinned to
   `[4.0.0,5.0.0)`.

Prior revision: Rev 3 replaced custom middleware with AGT's official .NET MAF extension.
Rev 2 replaced inline-call with middleware-first.

---

## 1. Problem Statement

Both agent runners currently allow escape from the artifact directory:

- **Copilot runner** (`GitHubCopilotAgentRunner.cs:48`): uses `PermissionHandler.ApproveAll` — every native tool (shell, file read/write, URL, MCP) is approved unconditionally.
- **Foundry runner** (`FoundryAgentRunner.cs:147-151`): uses `ResolveSandboxedPath` which has a prefix bug (`C:\work` matches `C:\work-evil` because no trailing separator) and performs no symlink/reparse-point protection.

The spec demands a single shared governance mechanism (FR-032) that enforces deny-by-default sandbox confinement for EVERY provider-native operation (FR-030), restricts toolsets to validatable operations (FR-031), and records every decision in the operational record (FR-033 / SC-010 / SC-012).

---

## 2. Component Diagram

```
+---------------------------------------------------------------------+
|  Scaffolder.SandboxFs                                               |
|                                                                     |
|  SandboxPathValidator (existing)                                    |
|    .ValidateAndResolve(relative, root)                              |
|    .ValidateAbsoluteContained(absolute, root)          <-- NEW      |
|    .VerifyOpenedHandle(handle, root, orig)                          |
|                                                                     |
|  SandboxPolicyBackend : IExternalPolicyBackend         <-- NEW      |
|    (AGT custom backend — delegates to ValidateAbsoluteContained     |
|     or ValidateAndResolve; returns ExternalPolicyDecision)          |
|                                                                     |
|  SandboxedFileTools (existing)                                      |
+---------------------------------------------------------------------+
               |                                    |
               v                                    v
+-------------------------------+   +-----------------------------------+
| Copilot Provider              |   | Foundry Provider                  |
| (SDK JSON-RPC subprocess)     |   | (IChatClient + MAF middleware)    |
|                               |   |                                   |
| SessionConfig:                |   | AIAgentBuilder pipeline:          |
|  .AvailableTools (allowlist)  |   |  agent.AsBuilder()                |
|  .OnPermissionRequest         |   |   .WithGovernance(adapter)        |
|    -> kernel.EvaluateToolCall  |   |   .UseLogging(...)                |
|                               |   |   .Build()                        |
| WHY NOT MAF middleware:       |   |                                   |
|  SDK subprocess executes      |   | AgentFrameworkGovernanceAdapter:  |
|  tools over JSON-RPC;         |   |  InvokeFunctionAsync — calls      |
|  MAF function-invocation      |   |  kernel.EvaluateToolCall() BEFORE |
|  middleware cannot intercept  |   |  calling next(); denies without   |
|  out-of-process tool calls.   |   |  executing the tool.              |
|                               |   |                                   |
| SAME GovernanceKernel +       |   | Defense-in-depth:                 |
| SAME policy + SAME audit      |   |  SandboxedFileTools still         |
| satisfies FR-032.             |   |  validates inside the tool impl.  |
+-------------------------------+   +-----------------------------------+
               |                                    |
               v                                    v
+---------------------------------------------------------------------+
|  AGT AuditEmitter (Decision BOM)                                    |
|    .OnAll(handler) — routes GovernanceEvent entries to ILogger       |
|    structured pipeline (category: Scaffolder.Governance.Sandbox)     |
|    Satisfies FR-033 (record every allow/deny) and FR-028             |
|    (compliance-grade operational record, durable when configured     |
|    with OpenTelemetry/file sink).                                    |
|                                                                     |
|  NOTE: Production MUST configure a durable audit sink (OTLP,        |
|  file, or database). In-process ILogger alone is acceptable for     |
|  dev/test only.                                                     |
+---------------------------------------------------------------------+
```

---

## 3. The Absolute-Path Containment Decision

### The Problem

The Copilot SDK's `PermissionRequestRead.Path` and `PermissionRequestWrite.FileName` carry ABSOLUTE paths (e.g. `C:\Users\dev\project\src\file.cs`). But `SandboxPathValidator.ValidateAndResolve` rejects absolute paths at its first check (`Path.IsPathRooted` → throw). We cannot feed SDK-supplied paths into it.

### Decision: `ValidateAbsoluteContained`

A new static method on `SandboxPathValidator` that accepts an absolute path and validates it is contained within the sandbox root.

### Method Signature

```csharp
/// <summary>
/// Validates that <paramref name="absolutePath"/> (an absolute path from an
/// external system such as the Copilot SDK permission request) resolves to a
/// location strictly inside <paramref name="sandboxRoot"/>.
/// Throws <see cref="SandboxViolationException"/> on any escape.
/// </summary>
/// <remarks>
/// Unlike <see cref="ValidateAndResolve"/>, this method EXPECTS an absolute
/// path and does NOT reject it for being rooted. It performs:
/// 1. Null/empty check.
/// 2. Early-reject device paths (\\?\, \\.\) and UNC (\\server\share).
/// 3. Early-reject drive-relative paths (e.g. C:foo — no separator after colon).
/// 4. IsPathRooted assertion (must be absolute; relative input is a caller bug).
/// 5. GetFullPath normalization (resolves ., .., trailing separators).
/// 6. Lexical prefix check: normalized path must start with
///    (normalizedRoot + DirectorySeparatorChar) OR equal normalizedRoot exactly
///    (for directory-listing operations targeting the root itself).
/// 7. Reparse-point ancestor walk (same as ValidateAndResolve).
/// </remarks>
public static string ValidateAbsoluteContained(string absolutePath, string sandboxRoot)
```

### Early-Reject Rules (Rubber-Duck Finding 1, 3)

Before the main prefix check:
- Reject paths starting with `\\?\` or `\\.\` (device namespace — cannot be safely normalized).
- Reject UNC paths `\\server\share` (network access — never sandbox-local).
- Reject drive-relative paths like `C:foo` (no `\` after colon) — semantics depend on per-drive cwd, unsafe.

### Why It Is Safe

1. **Trailing-separator bug fixed**: Root normalized via `Path.GetFullPath(sandboxRoot).TrimEnd(sep) + sep`. Target must start with this value (strict children) OR equal `root.TrimEnd(sep)` exactly (root-is-target listings).
2. **Case-insensitive on Windows**: `StringComparison.OrdinalIgnoreCase`.
3. **Normalization**: `Path.GetFullPath(absolutePath)` collapses `..`, `.`, double separators.
4. **Reparse-point walk**: Same `ValidateNoReparsePointsInAncestors` call.
5. **Non-existent targets**: Walk covers ancestors up to first non-existent component.
6. **Root-is-target**: Explicitly allowed for `list_directory` on sandbox root.

---

## 4. AGT Integration Architecture

### 4.1 GovernanceKernel Configuration (per-run)

```csharp
// Constructed once per run (sandbox root + run context known at start)
var options = new GovernanceOptions
{
    EnableAudit = true,
    EnableMetrics = true,
    EnableRings = false,
    EnablePromptInjectionDetection = false,
    EnableCircuitBreaker = false,
};

using var kernel = new GovernanceKernel(options);

// Load the sandbox-containment policy (YAML — see §4.2)
kernel.LoadPolicyFromYaml(SandboxPolicyYaml);

// Seraph Y-1: assert default-deny at construction time
// (implementation validates after load — see §4.2 assertion)

// Register our custom filesystem-aware backend
var sandboxBackend = new SandboxPolicyBackend(sandboxRoot);
kernel.PolicyEngine.AddExternalBackend(sandboxBackend);

// Subscribe audit events → ILogger structured pipeline
kernel.AuditEmitter.OnAll(ev => auditLogger.LogGovernanceEvent(ev, runId));
```

#### Sync-over-Async Trade-Off (Rubber-Duck #3 / Seraph Advisory)

`GovernanceKernel.EvaluateToolCall` and `SandboxPolicyBackend.Evaluate` are synchronous.
The Copilot `OnPermissionRequest` callback is `async Task<...>`. AGT exposes
`IExternalPolicyBackend.EvaluateAsync` — **prefer it** when a future AGT release
surfaces an async `EvaluateToolCallAsync` on the kernel. Until then:

- `EvaluateToolCall` is called synchronously within the async handler (no `Task.Run`
  wrapper — the overhead of a thread-pool hop is worse than blocking briefly).
- **Bounded risk:** Runs are sequential (one agent loop per run, one permission request
  at a time). There is no parallel permission evaluation that could starve the pool.
- **When to revisit:** If the architecture ever supports concurrent runs sharing a
  thread pool, wrap in `Task.Run` or require AGT to expose async kernel evaluation.

### 4.2 Sandbox Policy (YAML)

One YAML policy governs BOTH providers:

```yaml
apiVersion: governance/v1
name: sandbox-containment
description: Deny-by-default sandbox confinement for all agent tool calls.
defaultAction: Deny
rules:
  - name: allow-file-read-or-list
    condition: "tool_name == 'read_file' OR tool_name == 'list_directory'"
    action: Allow
    description: >
      Unified rule — both read_file and list_directory pass tool-name gating.
      Actual path containment is enforced by SandboxPolicyBackend regardless
      of which tool name was resolved. This consolidation ensures that the
      Copilot read→list_directory mapping (§6.1) cannot fall into a gap
      between separate YAML rules.
  - name: allow-file-write
    condition: "tool_name == 'write_file' OR tool_name == 'edit_file'"
    action: Allow
    description: Allowed if SandboxPolicyBackend passes path check.
  - name: deny-shell
    condition: "tool_name == 'shell'"
    action: Deny
    description: Shell execution categorically denied.
  - name: deny-all-other
    condition: "true"
    action: Deny
    description: Default deny — unknown tools blocked.
```

The YAML `Allow` rules only pass tool-name gating. The actual path-containment check lives in `SandboxPolicyBackend` (`IExternalPolicyBackend`), which fires on every `PolicyEngine.Evaluate()` and can return `Allowed = false` even when the YAML rule says Allow. External backends act as additional guards — if ANY backend denies, the final decision is deny.

#### Construction-Time YAML Assertion (Seraph Y-1)

On `GovernanceKernel` initialization, assert programmatically that the loaded policy's
`defaultAction` is `Deny`. If the assertion fails, throw `InvalidOperationException`
at startup — never silently fall into an Allow-by-default posture:

```csharp
// After kernel.LoadPolicyFromYaml(yaml):
Debug.Assert(kernel.PolicyEngine./* inspected policy */.DefaultAction == PolicyAction.Deny,
    "Sandbox policy MUST be deny-by-default.");
// In production, throw if violated:
if (loadedDefaultAction != PolicyAction.Deny)
    throw new InvalidOperationException(
        "Sandbox policy defaultAction must be Deny. Refusing to start.");
```

### 4.3 `SandboxPolicyBackend` — Custom `IExternalPolicyBackend`

```csharp
namespace Scaffolder.SandboxFs;

/// <summary>
/// AGT external policy backend that performs filesystem-aware sandbox
/// containment validation. Plugs into PolicyEngine.AddExternalBackend().
/// </summary>
public sealed class SandboxPolicyBackend : IExternalPolicyBackend
{
    private readonly string _sandboxRoot;

    /// <summary>Known file-tool names that require path validation.</summary>
    private static readonly HashSet<string> KnownFileTools = new(StringComparer.Ordinal)
    {
        "read_file", "write_file", "edit_file", "list_directory"
    };

    /// <summary>
    /// Known argument keys that may carry a filesystem path.
    /// Design rule (Seraph Y-2): ALL sandboxed file-tool functions MUST use
    /// one of these keys. If a tool exposes a path under a different key,
    /// the containment check will not fire and default-deny applies.
    /// </summary>
    private static readonly string[] PathArgumentKeys = { "path", "file_path", "directory" };

    public SandboxPolicyBackend(string sandboxRoot)
    {
        _sandboxRoot = Path.GetFullPath(sandboxRoot);
    }

    public string Name => "sandbox-path-containment";

    public ExternalPolicyDecision Evaluate(IReadOnlyDictionary<string, object> context)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var toolName = context.TryGetValue("tool_name", out var tn)
                ? tn?.ToString() : null;

            // Seraph Y-1: UNRECOGNIZED tool names get Allowed = false
            // (belt-and-suspenders — YAML default-deny should also catch,
            // but we don't defer to it for unknown tools).
            if (toolName is null || !KnownFileTools.Contains(toolName))
            {
                return new ExternalPolicyDecision
                {
                    Backend = Name,
                    Allowed = false,
                    Reason = $"Unrecognized or null tool name '{toolName}'; denied by sandbox backend.",
                    EvaluationMs = sw.Elapsed.TotalMilliseconds,
                };
            }

            // Seraph Y-2: resolve path from known argument keys
            string? path = null;
            foreach (var key in PathArgumentKeys)
            {
                if (context.TryGetValue(key, out var p) && p is string s
                    && !string.IsNullOrWhiteSpace(s))
                {
                    path = s;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return new ExternalPolicyDecision
                {
                    Backend = Name,
                    Allowed = false,
                    Reason = "No path argument found in known keys; denied.",
                    EvaluationMs = sw.Elapsed.TotalMilliseconds,
                };
            }

            // Dispatch to correct validator based on whether path is absolute
            if (Path.IsPathRooted(path))
                SandboxPathValidator.ValidateAbsoluteContained(path, _sandboxRoot);
            else
                SandboxPathValidator.ValidateAndResolve(path, _sandboxRoot);

            return new ExternalPolicyDecision
            {
                Backend = Name,
                Allowed = true,
                Reason = "Path is within sandbox boundary.",
                EvaluationMs = sw.Elapsed.TotalMilliseconds,
                // Seraph Y-2 / FR-033: record resolved path in metadata for audit
                Metadata = new Dictionary<string, object>
                {
                    ["resolved_path"] = Path.GetFullPath(
                        Path.IsPathRooted(path) ? path : Path.Combine(_sandboxRoot, path)),
                },
            };
        }
        catch (SandboxViolationException ex)
        {
            return new ExternalPolicyDecision
            {
                Backend = Name,
                Allowed = false,
                Reason = ex.Message,
                EvaluationMs = sw.Elapsed.TotalMilliseconds,
            };
        }
        catch (Exception ex)
        {
            // Seraph Finding 3: fail-closed on ANY internal exception
            return new ExternalPolicyDecision
            {
                Backend = Name,
                Allowed = false,
                Reason = $"Internal error (fail-closed): {ex.GetType().Name}",
                EvaluationMs = sw.Elapsed.TotalMilliseconds,
            };
        }
    }

    public Task<ExternalPolicyDecision> EvaluateAsync(
        IReadOnlyDictionary<string, object> context, CancellationToken ct)
        => Task.FromResult(Evaluate(context));
}
```

#### Design Rule: `"path"` Argument Name Contract (Seraph Y-2)

**All sandboxed file-tool function definitions (Foundry `AIFunctionFactory.Create` and
Copilot `AvailableTools` mappings) MUST use `"path"` as the primary argument name for
file/directory targets.** The resolver checks `PathArgumentKeys` (`path`, `file_path`,
`directory`) to cover SDK variations, but canonical Scaffolder tools use `"path"`.

The AGT `GovernanceEvent.Data` dictionary MUST always include `["resolved_path"]` (the
fully-qualified, normalized path that was validated). This satisfies FR-033 completeness
— the audit entry shows exactly which filesystem location was allowed/denied.

### 4.4 AGT `AuditEmitter` → Structured Logging (FR-033 / FR-028)

AGT replaces our hand-rolled `ISandboxAuditSink` / `LoggerAuditSink`:

```csharp
kernel.AuditEmitter.OnAll(ev =>
{
    // Seraph Finding 4: redact PII — log paths relative to sandbox root
    var target = ev.Data?.TryGetValue("path", out var p) == true
        ? RedactToRelative(p?.ToString(), sandboxRoot)
        : "[none]";

    // Seraph Y-2: always include resolved path in audit
    var resolvedPath = ev.Data?.TryGetValue("resolved_path", out var rp) == true
        ? RedactToRelative(rp?.ToString(), sandboxRoot)
        : target;

    logger.LogInformation(
        "AGT decision={Decision} type={EventType} agent={AgentId} " +
        "tool={ToolName} target={Target} resolved={ResolvedPath} " +
        "policy={Policy} reason={Reason}",
        ev.Data?.GetValueOrDefault("allowed"),
        ev.Type,
        ev.AgentId,
        ev.Data?.GetValueOrDefault("tool_name"),
        target,
        resolvedPath,
        ev.PolicyName,
        ev.Data?.GetValueOrDefault("reason"));
});
```

How this satisfies requirements:
- **FR-033**: Every `EvaluateToolCall()` (allow or deny) fires a `GovernanceEvent` through `AuditEmitter`. Event includes tool name, arguments, decision, matched rule, timing.
- **FR-028**: `AuditEmitter.OnAll` handler writes to same `ILogger` structured pipeline as operational record. In production, flows to OTLP/durable storage.
- **Compliance-grade**: AGT's `GovernanceEvent` includes `EvaluatedAt`, `PolicyName`, `MatchedRule`, `EvaluationMs` — richer than prior custom `SandboxAuditEntry`.

### 4.5 Durable Audit Health-Check (Seraph Y-3 / Rubber-Duck #6)

**Production MUST configure a durable audit sink** (OTLP exporter, file sink, or
database). In-process `ILogger` with console sink alone is acceptable only for dev/test.

At application startup, perform a health-check:

```csharp
// In Program.cs / host startup:
var auditSinkConfigured = builder.Configuration
    .GetSection("Governance:DurableAuditSink").Exists();

if (!auditSinkConfigured)
{
    if (builder.Environment.IsProduction())
    {
        // Fail fast — refuse to start without durable audit in production
        throw new InvalidOperationException(
            "Production requires a durable audit sink (Governance:DurableAuditSink). " +
            "Configure OTLP, file, or database exporter.");
    }
    else
    {
        logger.LogWarning(
            "No durable audit sink configured (Governance:DurableAuditSink). " +
            "Audit events will only go to console ILogger. " +
            "This is acceptable for dev/test but NOT for production.");
    }
}
```

**Deployment runbook gate:** The CI/CD pipeline MUST verify that the production
configuration includes `Governance:DurableAuditSink` before promoting to production.
This is a hard deployment gate — not advisory.

---

## 5. Foundry Wire-In — AGT via `.WithGovernance()`

### Refactored Architecture

```csharp
// In FoundryAgentRunner.ExecuteAsync (sketch)
var chatClient = _factory.CreateChatClient();
var sandboxedFileTools = new SandboxedFileTools(workingDirectory);
var tools = BuildTools(sandboxedFileTools, ct);

var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "scaffolder-foundry",
    Description = "File-editing agent",
    Instructions = SystemPrompt,
    Tools = tools,
});

// AGT governance — single kernel for both providers
var adapter = new AgentFrameworkGovernanceAdapter(kernel,
    new AgentFrameworkGovernanceOptions
{
    EnableFunctionMiddleware = true,
    DefaultAgentId = $"scaffolder:foundry:{runId}",
    ToolArgumentsResolver = (context) =>
    {
        // Seraph Y-2: all sandboxed tools use "path" as the argument key
        var dict = new Dictionary<string, object>
        {
            ["tool_name"] = context.Function.Name,
        };
        if (context.Arguments.TryGetValue("path", out var p))
        {
            var pathStr = p?.ToString() ?? "";
            dict["path"] = pathStr;
            // Record resolved absolute path for FR-033 audit completeness
            if (!string.IsNullOrEmpty(pathStr))
                dict["resolved_path"] = Path.IsPathRooted(pathStr)
                    ? Path.GetFullPath(pathStr)
                    : Path.GetFullPath(Path.Combine(workingDirectory, pathStr));
        }
        return dict;
    },
    BlockedToolResultFactory = (context, decision) =>
        $"Error: operation denied by sandbox policy. {decision.Reason}",
});

// Wire governance into MAF pipeline
var governedAgent = agent.AsBuilder()
    .WithGovernance(adapter)
    .UseLogging(_loggerFactory)
    .Build();

// Run the agent (MAF handles the tool loop)
var session = await governedAgent.CreateSessionAsync(ct);
await foreach (var update in governedAgent.RunStreamingAsync(
    task, session, runOptions, ct))
{
    // emit RunEvents to channel
}
```

### How AGT Denies Before Execution

`AgentFrameworkGovernanceAdapter.InvokeFunctionAsync`:
1. Extracts tool name + arguments via `ToolArgumentsResolver`.
2. Calls `kernel.EvaluateToolCall(agentId, toolName, args)`.
3. If `ToolCallResult.Allowed == false`: returns blocked result via `BlockedToolResultFactory` WITHOUT calling `next`. Tool function never executes.
4. If allowed: calls `next(context, ct)`.

SC-011 satisfied: out-of-sandbox operations denied BEFORE execution.

### Defense-in-Depth: `SandboxedFileTools` Remains

Even though AGT middleware prevents out-of-sandbox calls from reaching the tool, tool implementations still use `SandboxedFileTools` internally. Defense-in-depth against middleware misconfiguration.

### Tool Implementation (no inline policy checks)

```csharp
private static List<AITool> BuildTools(SandboxedFileTools fileTools, CancellationToken ct) =>
[
    AIFunctionFactory.Create(
        async ([Description("File path relative to working directory.")] string path) =>
        {
            var (content, failure) = await fileTools.ReadFileAsync(path, ct);
            return failure is not null ? $"Error: {failure.Message}" : content!;
        },
        "read_file", "Read the contents of a file."),

    AIFunctionFactory.Create(
        async (
            [Description("File path relative to working directory.")] string path,
            [Description("Content to write.")] string content) =>
        {
            var (_, failure) = await fileTools.WriteFileAsync(path, content, ct);
            return failure is not null ? $"Error: {failure.Message}" : "ok";
        },
        "write_file", "Write content to a file, creating it if it does not exist."),
];
```

### `ResolveSandboxedPath` Bug Fixed by Deletion

The existing `ResolveSandboxedPath` is deleted entirely. Its trailing-separator bug (`C:\work` matches `C:\work-evil`) is eliminated — AGT's `SandboxPolicyBackend` uses `SandboxPathValidator.ValidateAbsoluteContained` which includes correct trailing-separator normalization.

---

## 6. Copilot SDK Wire-In — Same Kernel, Different Entry Point

### The Asymmetry (Documented per Seraph Finding 1)

AGT's `.WithGovernance()` MAF middleware CANNOT reach the Copilot SDK subprocess's native tool calls. The SDK subprocess executes tools over JSON-RPC entirely out-of-process. MAF function-invocation middleware only intercepts in-process `AIFunction` dispatches.

**Therefore:** The Copilot path calls `GovernanceKernel.EvaluateToolCall()` directly from the `OnPermissionRequest` handler. This uses the SAME kernel, SAME `PolicyEngine`, SAME `SandboxPolicyBackend`, SAME `AuditEmitter`. FR-032 (single shared governance mechanism) is satisfied.

### Security Assumptions for Copilot Path (Seraph Finding 1)

The following assumptions MUST hold for the Copilot sandbox to be sound:

1. **No symlink-creation tool**: `AvailableTools` restricts to `["read_file", "write_file", "list_directory", "edit_file"]`. No shell, no `create_symlink`, no MCP tool can create filesystem links.
2. **Shell denied categorically**: Both by `AvailableTools` (not listed) and by the permission handler (deny unconditionally).
3. **Per-run fresh directory**: Each run gets a newly-created working directory. No pre-existing symlinks.
4. **TOCTOU residual risk accepted**: Between `OnPermissionRequest` path check and actual file I/O by the subprocess, a race window exists. Mitigation: assumptions 1-3 eliminate the agent's ability to create reparse points during the race. External actors creating symlinks during a run is accepted as residual risk mitigated by OS-level isolation (future phase).
5. **SDK RPC protocol is faithful**: The subprocess does not execute a tool without receiving `Approved`.
6. **`AvailableTools` enforced at protocol level**: Tools not in the list are not offered to the model.

### `SessionConfig` Changes

```csharp
var sandboxBackend = new SandboxPolicyBackend(workingDirectory);
// (sandboxBackend also registered on kernel.PolicyEngine — see §4.1)

var sessionConfig = new SessionConfig
{
    WorkingDirectory = workingDirectory,
    Streaming = true,
    AvailableTools = new List<string>
        { "read_file", "write_file", "list_directory", "edit_file" },
    OnPermissionRequest = BuildPermissionHandler(kernel, sandboxBackend, runId),
};
```

### Permission Handler (Unconditional Containment — Rubber-Duck #2 / Seraph §2)

**Authoritative containment guarantee:** The `BuildPermissionHandler` calls
`SandboxPolicyBackend.Evaluate(...)` DIRECTLY — independently of the
`GovernanceKernel.EvaluateToolCall()` result. Deny if EITHER denies. This ensures
enforcement NEVER depends solely on AGT calling our backend (which was inferred via
reflection, not proven). AGT provides policy-rule + audit; our validator is
unconditionally consulted for path containment on the Copilot path.

```csharp
private static PermissionRequestHandler BuildPermissionHandler(
    GovernanceKernel kernel,
    SandboxPolicyBackend sandboxBackend,
    string runId)
{
    return async (request, invocation) =>
    {
        try
        {
            var (toolName, args) = MapToToolCall(request);

            // --- Layer A: AGT policy + audit (may or may not call our backend) ---
            ToolCallResult agtResult;
            // Prefer async path if available (rubber-duck #3 / Seraph advisory)
            agtResult = kernel.EvaluateToolCall(
                agentId: $"scaffolder:copilot:{runId}",
                toolName: toolName,
                arguments: args);

            // --- Layer B: UNCONDITIONAL direct containment check ---
            // Even if AGT says Allow (or even if AGT silently skipped our
            // backend), we enforce containment ourselves. This is the
            // authoritative path-containment guarantee for Copilot.
            var directCheck = sandboxBackend.Evaluate(
                new Dictionary<string, object>(args)
                {
                    ["tool_name"] = toolName,
                });

            var allowed = agtResult.Allowed && directCheck.Allowed;

            return allowed
                ? new PermissionRequestResult
                    { Kind = PermissionRequestResultKind.Approved }
                : new PermissionRequestResult
                    { Kind = PermissionRequestResultKind.DeniedByRules };
        }
        catch (Exception)
        {
            // Seraph Finding 3: fail-closed on ANY internal exception
            return new PermissionRequestResult
                { Kind = PermissionRequestResultKind.DeniedByRules };
        }
    };
}
```

### 6.1 `MapToToolCall` — Explicit Read ↔ List Directory Disambiguation (BLOCKING Fix)

**Design Decision (rubber-duck #1):** The Copilot SDK's `PermissionRequestRead` carries
a single `Path` property for both file reads and directory listings. The tool name in
`AvailableTools` is either `"read_file"` or `"list_directory"` — but the SDK permission
callback only sees `Kind == "read"`. If we always map to `"read_file"`, the YAML rule
for `list_directory` never matches and AGT denies the call (default-deny). This is a
functional break, not just a test gap.

**Chosen approach:** Inspect `PermissionRequestRead.Path` at mapping time:
- If the path has a trailing directory separator (`/` or `\`), OR
- If `Directory.Exists(path)` returns true,

→ map to `"list_directory"`. Otherwise → `"read_file"`.

**Justification:** This heuristic matches the Copilot subprocess behaviour — when it
lists a directory, it sends the directory path. `Directory.Exists` is a cheap syscall
and is evaluated against the already-existing sandbox directory. False positives (a
file named without extension that happens to be a directory) are safe: both
`read_file` and `list_directory` are in the same unified YAML Allow rule
(`allow-file-read-or-list`), so path containment is evaluated regardless. The
functional risk is only a wrong audit label, never an escape.

**Why consolidating the YAML rule covers both:** The YAML `allow-file-read-or-list`
rule accepts BOTH `read_file` and `list_directory` tool names in a single condition.
Even if the heuristic mis-classifies, the call is still Allow-gated and
`SandboxPolicyBackend` validates containment. No silent deny.

```csharp
private static (string toolName, Dictionary<string, object> args) MapToToolCall(
    PermissionRequest request)
{
    return request.Kind switch
    {
        "read" => MapReadRequest((PermissionRequestRead)request),
        "write" => ("write_file", new Dictionary<string, object>
        {
            ["path"] = ((PermissionRequestWrite)request).FileName ?? "",
        }),
        "shell" => ("shell", new Dictionary<string, object>
        {
            ["command"] = ((PermissionRequestShell)request).FullCommandText ?? "",
        }),
        "mcp" => ("mcp", new Dictionary<string, object>
        {
            ["tool"] = ((PermissionRequestMcp)request).ToolName ?? "",
        }),
        _ => (request.Kind ?? "unknown", new Dictionary<string, object>()),
    };
}

private static (string toolName, Dictionary<string, object> args) MapReadRequest(
    PermissionRequestRead request)
{
    var path = request.Path ?? "";
    var args = new Dictionary<string, object> { ["path"] = path };

    // Heuristic: trailing separator OR existing directory → list_directory
    if (path.Length > 0 &&
        (path[^1] == Path.DirectorySeparatorChar ||
         path[^1] == Path.AltDirectorySeparatorChar ||
         Directory.Exists(path)))
    {
        return ("list_directory", args);
    }

    return ("read_file", args);
}
```

**Integration test gate (SC-011 addendum):** A test MUST assert that a
`PermissionRequestRead` with a directory path maps to `"list_directory"` and is
allowed (not denied by default-deny mismatch).

### Defense-in-Depth Layers (Copilot)

1. **Layer 1 — Toolset allowlist**: `AvailableTools` removes shell, URL, MCP, memory, hooks.
2. **Layer 2a — Permission handler → AGT `EvaluateToolCall`**: Even if Layer 1 fails, AGT policy denies everything not in the approved tool list AND validates path containment via `SandboxPolicyBackend` (if AGT invokes our backend).
3. **Layer 2b — Permission handler → direct `SandboxPolicyBackend.Evaluate`** (authoritative containment): Called UNCONDITIONALLY regardless of Layer 2a result. Ensures containment even if AGT silently skips external backends. Both 2a and 2b must allow for the call to proceed.
4. **Layer 3 (future)**: OS-level process isolation.

---

## 7. PII Redaction (Seraph Finding 4)

All audit logging redacts full paths to relative-to-sandbox:

```csharp
private static string RedactToRelative(string? fullPath, string sandboxRoot)
{
    if (string.IsNullOrEmpty(fullPath)) return "[none]";
    var root = Path.GetFullPath(sandboxRoot).TrimEnd(Path.DirectorySeparatorChar)
        + Path.DirectorySeparatorChar;
    if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        return fullPath[root.Length..];
    // Outside sandbox — redact entirely (don't log C:\Users\<name>\...)
    return "[outside-sandbox-redacted]";
}
```

---

## 8. `edit_file` Secondary Path Validation (Seraph Finding 2, Rubber-Duck Finding 5)

The `edit_file` Copilot SDK tool surfaces as `PermissionRequestWrite` with `.FileName`. We MUST validate in the first integration test that:

1. `PermissionRequestWrite.FileName` is the ONLY path argument (no hidden secondary path in diff metadata).
2. The SDK does not have a separate "target path" embedded in the diff content that could target a different file.
3. `edit_file` permissions fire `Kind == "write"` (not a separate kind).

If any of these assumptions fail, the permission handler must be updated. This is a mandatory first-integration-test validation item.

---

## 9. DI / Lifetime

### Per-Run Construction

```
IServiceCollection (application-level singletons):
  ILoggerFactory (standard M.E.Logging)

Per-run construction (in the runner or a factory):
  new GovernanceKernel(options)
  kernel.LoadPolicyFromYaml(yaml)
  // Assert defaultAction == Deny (Seraph Y-1)
  kernel.PolicyEngine.AddExternalBackend(new SandboxPolicyBackend(sandboxRoot))
  kernel.AuditEmitter.OnAll(handler)
  new SandboxedFileTools(sandboxRoot)  // Foundry only
```

### Runner Changes

**`GitHubCopilotAgentRunner`**: Constructs `GovernanceKernel` + `SandboxPolicyBackend` per-run. Kernel AND backend called from `OnPermissionRequest` handler (dual-path, §6). Kernel disposed at run completion.

**`FoundryAgentRunner`**: Constructs `GovernanceKernel` + `AgentFrameworkGovernanceAdapter`. Wires via `.WithGovernance(adapter)` on `AIAgentBuilder`. Kernel disposed at run completion.

### GovernanceKernel Lifecycle (Rubber-Duck #5)

`GovernanceKernel` implements `IDisposable`. Both runners MUST dispose it at run
completion to release audit subscription handles and metrics timers:

```csharp
// Copilot runner
using var kernel = new GovernanceKernel(options);
kernel.LoadPolicyFromYaml(SandboxPolicyYaml);
kernel.PolicyEngine.AddExternalBackend(sandboxBackend);
kernel.AuditEmitter.OnAll(auditHandler);
// ... run ...
// kernel.Dispose() called by using-block at run completion or on exception

// Foundry runner
using var kernel = new GovernanceKernel(options);
kernel.LoadPolicyFromYaml(SandboxPolicyYaml);
kernel.PolicyEngine.AddExternalBackend(sandboxBackend);
kernel.AuditEmitter.OnAll(auditHandler);
var adapter = new AgentFrameworkGovernanceAdapter(kernel, adapterOptions);
// ... run ...
// kernel.Dispose() called by using-block
```

If `using` is not feasible (e.g., the kernel outlives the method scope), a
`try`/`finally` block MUST call `kernel.Dispose()`.

### Run ID

**Decision**: Add `RunId` as parameter to `IAgentRunner.ExecuteAsync`. Used for AGT agent-DID construction and audit correlation.

### PackageReference Version Pinning (Rubber-Duck #6)

Both AGT packages MUST be pinned to a major-version range to prevent silent breaking
changes on `dotnet restore`:

```xml
<PackageReference Include="Microsoft.AgentGovernance"
                  Version="[4.0.0,5.0.0)" />
<PackageReference Include="Microsoft.AgentGovernance.Extensions.Microsoft.Agents"
                  Version="[4.0.0,5.0.0)" />
```

This allows patch/minor updates within 4.x but rejects 5.0+ (which may change
`IExternalPolicyBackend` contract or evaluation semantics).

---

## 10. Phase 4b: Mandatory Follow-Up (Rubber-Duck Finding 4)

Phase 4a wraps existing tools using `GovernanceKernel.EvaluateToolCall` as an intermediate step before full `ChatClientAgent` adoption. Phase 4b migrates Foundry to `ChatClientAgent` + `.WithGovernance()` full pipeline.

**Phase 4b is a mandatory non-deferrable follow-up.** The Phase 4a window (where governance is a thin call-wrapper rather than the full AGT middleware pipeline) is acceptable only until 4b ships. Once `ChatClientAgent` is adopted, `AgentFrameworkGovernanceAdapter` intercepts ALL function calls at the MAF pipeline level — no tool can bypass it.

---

## 11. Test Plan Outline

### SC-011 Tests (100% out-of-sandbox ops denied before execution)

| Test case | Provider | Operation | Expected |
|-----------|----------|-----------|----------|
| Read file inside sandbox (relative path) | Both | ReadFile("src/file.txt") | Allow |
| Read file inside sandbox (absolute path) | Copilot | ReadFile("C:\sandbox\src\file.txt") | Allow |
| Read file OUTSIDE sandbox (absolute path) | Copilot | ReadFile("C:\Windows\system32\cmd.exe") | Deny |
| Read with `..` traversal | Both | ReadFile("../../../etc/passwd") | Deny |
| Write file outside via absolute path | Copilot | WriteFile("D:\evil\payload.txt") | Deny |
| Write with prefix-collision | Both | path resolving to `{root}-evil\file.txt` | Deny |
| Shell command (any) | Copilot | Shell("dir C:\") | Deny |
| MCP tool call | Copilot | Mcp("dangerous_tool") | Deny |
| URL fetch | Copilot | Url("http://evil.com") | Deny |
| List sandbox root itself | Copilot | ReadFile(sandboxRoot) | Allow |
| Symlink inside sandbox → outside | Both | ReadFile("link/secret.txt") | Deny |
| Device path `\\?\C:\...` | Both | Any | Deny (early reject) |
| UNC path `\\server\share\...` | Both | Any | Deny (early reject) |
| Drive-relative `C:foo` | Both | Any | Deny (early reject) |
| Empty path | Both | ReadFile("") | Deny |
| Internal exception in validator | Both | (simulated) | Deny (fail-closed) |

### SC-012 Tests (audit entries)

| Test case | Verification |
|-----------|-------------|
| Denied read outside | `AuditEmitter` fires `ToolCallBlocked`, redacted path |
| Denied shell | Event type `ToolCallBlocked`, tool name "shell" |
| Allowed read inside | `AuditEmitter` fires `PolicyCheck` with Allowed=true |
| Multiple ops same run | All events share same agentId |

### Foundry-Specific

1. `ResolveSandboxedPath` deleted.
2. `.WithGovernance(adapter)` registered.
3. Denied tool never executes (mock body, assert not called).
4. Unknown tool name → Deny from YAML `defaultAction: Deny`.

### Copilot-Specific

1. `PermissionHandler.ApproveAll` replaced.
2. Handler calls `kernel.EvaluateToolCall()` AND direct `sandboxBackend.Evaluate()`.
3. `AvailableTools` only lists file tools.
4. `edit_file` → `Kind == "write"` with `FileName` as path (Rubber-Duck Finding 5).
5. Exception in handler → `DeniedByRules` (fail-closed).
6. **SC-011 addendum (BLOCKING fix):** `PermissionRequestRead` with directory path → maps to `list_directory` → allowed (not default-denied).
7. **SC-011 addendum (HIGH fallback):** Allowed tool name + out-of-sandbox absolute path → denied even if AGT `EvaluateToolCall` is mocked to return `Allowed = true` (proves direct containment check is authoritative). This is a HARD GATE test.
8. Unrecognized tool name → `SandboxPolicyBackend` returns `Allowed = false` (Seraph Y-1).
9. `GovernanceKernel.Dispose()` called at run completion (assert no leaked subscriptions).

---

## 12. Assumptions and Residual Risks

### Assumptions

1. Copilot SDK `AvailableTools` enforced at protocol level in beta.2.
2. `PermissionRequestRead.Path` / `PermissionRequestWrite.FileName` always absolute.
3. Foundry tool list entirely under our control.
4. ~~AGT `IExternalPolicyBackend.Evaluate` fires on every `PolicyEngine.Evaluate()`.~~ **No longer a critical assumption (Rev 4):** The unconditional direct-call fallback in `BuildPermissionHandler` (§6) ensures containment even if AGT does not invoke our backend.
5. AGT `AgentFrameworkGovernanceAdapter.InvokeFunctionAsync` fires before `next()` for all calls.

### Residual Risks

| Risk | Severity | Mitigation |
|------|----------|------------|
| SDK bug bypasses `AvailableTools` or `OnPermissionRequest` | High | Permission handler + AGT + direct containment check is triple defense-in-depth; OS isolation (future) |
| AGT doesn't call external backends for Allow-ruled tools | ~~Medium~~ **Low (Rev 4)** | Direct `SandboxPolicyBackend.Evaluate` call in permission handler (§6) eliminates dependency on AGT calling it |
| TOCTOU between permission check and SDK file I/O | Medium | Agent has no symlink tool, shell denied, fresh dir |
| AGT NuGet targets net8.0, we target net9.0 | Low | Confirmed compatible |
| `AuditEmitter` events lost on crash | Medium | Production MUST configure durable sink; startup health-check enforces (§4.5) |
| `edit_file` has undocumented secondary path arg | Low | Validated in first integration test |
| AGT 5.0 breaks `IExternalPolicyBackend` contract | Low | PackageReference pinned to `[4.0.0,5.0.0)` (§9) |

---

## 13. Implementation Phases

1. **Add `ValidateAbsoluteContained`** to `SandboxPathValidator`. Early-reject device/UNC/drive-relative.
2. **Add `SandboxPolicyBackend : IExternalPolicyBackend`** to `Scaffolder.SandboxFs`. PackageRef: `Microsoft.AgentGovernance [4.0.0,5.0.0)`. Include default-deny assertion and unrecognized-tool deny (Seraph Y-1).
3. **Wire Copilot runner** — replace `PermissionHandler.ApproveAll` with handler calling BOTH `GovernanceKernel.EvaluateToolCall` AND direct `SandboxPolicyBackend.Evaluate` (unconditional fallback). Add `MapReadRequest` directory heuristic. Add `AvailableTools`. Add `using` on kernel.
4. **Wire Foundry runner (Phase 4a)** — wrap tools with `kernel.EvaluateToolCall` before delegation. Delete `ResolveSandboxedPath`. Use `SandboxedFileTools`. Add `using` on kernel.
5. **Wire Foundry runner (Phase 4b, mandatory)** — migrate to `ChatClientAgent` + `.WithGovernance(adapter)`.
6. **Add `RunId` to `IAgentRunner.ExecuteAsync`**.
7. **Durable audit health-check** — add startup validation per §4.5.
8. **Tests** — per §11, including SC-011 addenda (directory mapping, unconditional containment hard gate).

---

## 14. What This Design Does NOT Cover

- OS-level process isolation (container, job object) — future slice.
- Content of files read/written (content safety — FR-025).
- Network isolation beyond tool-level denial.
- `edit_file` diff-level semantic validation (treated as write for sandbox).
- AGT features beyond sandbox enforcement (rate limiting, rings, prompt injection) — may adopt later.
- Concurrent-run thread-pool considerations (revisit sync-over-async if architecture changes).

---

## 15. References

- Spike: `specs/001-single-agent-run/spike-agt-dotnet.md` (AGT API surface, confirmed types)
- Rubber-duck review #1–#6 (2026-06-08, pre-implementation gate on Rev 3)
- Seraph pre-implementation review §2 + Y-1/Y-2/Y-3 advisories (2026-06-08)
