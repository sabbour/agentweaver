# Spike: AGT .NET Consumability & API Surface

**Date**: 2026-06-08
**Author**: Morpheus (Runtime Engineer)
**Status**: Complete — findings feed design revision

---

## 1. Published NuGet Packages

**CONFIRMED: Two packages exist and are published to nuget.org.**

| Package ID | Version | Published | TFM | License |
|------------|---------|-----------|-----|---------|
| `Microsoft.AgentGovernance` | 4.0.0 | 2026-05-29 | net8.0 | MIT |
| `Microsoft.AgentGovernance.Extensions.Microsoft.Agents` | 4.0.0 | 2026-05-29 | net8.0 | MIT |

- **Core** depends on: `YamlDotNet >= 18.0.0`
- **MAF Extension** depends on: `Microsoft.AgentGovernance >= 4.0.0`, `Microsoft.Agents.AI >= 1.6.2`
- Both target `net8.0`. Our project targets `net9.0` — forward-compatible (confirmed: `dotnet add` succeeded, restore reports "compatible with all the specified frameworks").
- Source repo: `github.com/microsoft/agent-governance-toolkit` (verified via `gh api`; repo pushed 2026-06-08, language: Python but .NET DLLs are published).

---

## 2. Public API Surface (Reflection-Confirmed)

### 2.1 Core: `Microsoft.AgentGovernance` 4.0.0

**Key types for our use case:**

#### `AgentGovernance.GovernanceKernel`
```csharp
// Constructor
GovernanceKernel(GovernanceOptions options)

// Properties
PolicyEngine PolicyEngine { get; }
AuditEmitter AuditEmitter { get; }
GovernanceMiddleware Middleware { get; }
RateLimiter RateLimiter { get; }
GovernanceMetrics Metrics { get; }
RingEnforcer Rings { get; }
bool AuditEnabled { get; }

// Methods
void LoadPolicy(string yamlPath);
void LoadPolicyFromYaml(string yaml);
ToolCallResult EvaluateToolCall(string agentId, string toolName, Dictionary<string, object> args);
void OnEvent(GovernanceEventType type, Action<GovernanceEvent> handler);
void OnAllEvents(Action<GovernanceEvent> handler);
void Dispose();
```

#### `AgentGovernance.Policy.PolicyEngine`
```csharp
// Parameterless ctor
PolicyEngine()

// Methods
void LoadPolicy(Policy policy);
void LoadYaml(string yaml);
void LoadJson(string json);
void LoadYamlFile(string path);
void AddExternalBackend(IExternalPolicyBackend backend);
PolicyDecision Evaluate(string agentDid, Dictionary<string, object> context);
```

#### `AgentGovernance.Policy.IExternalPolicyBackend`
```csharp
string Name { get; }
ExternalPolicyDecision Evaluate(IReadOnlyDictionary<string, object> context);
Task<ExternalPolicyDecision> EvaluateAsync(IReadOnlyDictionary<string, object> context, CancellationToken ct);
```

#### `AgentGovernance.Policy.ExternalPolicyDecision`
```csharp
string Backend { get; set; }
bool Allowed { get; set; }
string Reason { get; set; }
double EvaluationMs { get; set; }
string Error { get; set; }
Dictionary<string, object> Metadata { get; set; }
```

#### `AgentGovernance.Policy.PolicyDecision`
```csharp
bool Allowed { get; }
string Action { get; }
string MatchedRule { get; }
string PolicyName { get; }
string Reason { get; }
bool RateLimited { get; }
DateTime EvaluatedAt { get; }
double EvaluationMs { get; }
Dictionary<string, object> Metadata { get; }
```

#### `AgentGovernance.Integration.ToolCallResult`
```csharp
bool Allowed { get; }
string Reason { get; }
GovernanceEvent AuditEntry { get; }
PolicyDecision PolicyDecision { get; }
```

#### `AgentGovernance.Audit.AuditEmitter`
```csharp
AuditEmitter()  // parameterless
void On(GovernanceEventType type, Action<GovernanceEvent> handler);
void OnAll(Action<GovernanceEvent> handler);
void Emit(GovernanceEvent ev);
void Emit(GovernanceEventType type, string agentId, string sessionId, Dictionary<string, object> data, string policyName);
```

#### `AgentGovernance.Audit.GovernanceEventType` (enum)
```
PolicyCheck, PolicyViolation, ToolCallBlocked, CheckpointCreated,
DriftDetected, TrustVerified, TrustFailed, AgentRegistered
```

#### `AgentGovernance.Policy.PolicyAction` (enum)
```
Allow, Deny, Warn, RequireApproval, Log, RateLimit
```

#### `AgentGovernance.Sandbox.ISandboxProvider`
```csharp
Task<SessionHandle> CreateSessionAsync(string agentId, SandboxConfig config);
Task<SandboxResult> ExecuteCodeAsync(string agentId, string sessionId, string code);
Task DestroySessionAsync(string agentId, string sessionId);
Task<bool> IsAvailableAsync();
```
- `SandboxConfig`: TimeoutSeconds, MemoryMb, CpuLimit, NetworkEnabled, ReadOnlyFs, EnvVars.
- Only implementation: `DockerSandboxProvider` — requires Docker daemon.

### 2.2 MAF Extension: `Microsoft.AgentGovernance.Extensions.Microsoft.Agents` 4.0.0

#### `AgentFrameworkGovernanceAdapter`
```csharp
AgentFrameworkGovernanceAdapter(GovernanceKernel kernel, AgentFrameworkGovernanceOptions options)

// Implements run-level AND function-level middleware:
Task RunAsync(IEnumerable<ChatMessage> messages, AgentSession session, AgentRunOptions opts, AIAgent agent, CancellationToken ct);
IAsyncEnumerable<...> RunStreamingAsync(...);
ValueTask<object> InvokeFunctionAsync(AIAgent agent, FunctionInvocationContext context, Func<...> next, CancellationToken ct);
```

#### `AgentFrameworkGovernanceExtensions` (static extension methods)
```csharp
// The entry points for wiring:
AIAgentBuilder WithGovernance(this AIAgentBuilder builder, AgentFrameworkGovernanceAdapter adapter);
AIAgent WithGovernance(this AIAgent agent, AgentFrameworkGovernanceAdapter adapter);
AIAgent WithGovernance(this AIAgent agent, GovernanceKernel kernel, AgentFrameworkGovernanceOptions options);
```

#### `AgentFrameworkGovernanceOptions`
```csharp
bool EnableFunctionMiddleware { get; set; }       // attach function-call governance
string DefaultAgentId { get; set; }               // fallback DID
Func<...> AgentIdResolver { get; set; }           // resolve agent DID from session
Func<...> InputTextResolver { get; set; }         // extract text for run-level eval
Func<...> ToolArgumentsResolver { get; set; }     // translate args to policy context
Func<...> BlockedRunResponseFactory { get; set; } // build response on deny
Func<...> BlockedToolResultFactory { get; set; }  // build tool result on deny
```

---

## 3. Consumption Path Assessment

| Option | Feasibility | Cost |
|--------|------------|------|
| **(a) NuGet PackageReference** | ✅ FUNCTIONAL TODAY. Both packages install cleanly into net9.0 projects. `Microsoft.Agents.AI >= 1.6.2` dep is satisfied by our 1.8.0. | Zero — `dotnet add package` only. |
| **(b) Vendor source** | Possible but unnecessary — NuGet is available. | High, maintenance burden. |
| **(c) Re-implement interfaces** | Not needed — can reference NuGet directly. | Pointless duplication. |

**Verdict: Consumption path (a) — NuGet PackageReference — is functional today, zero friction.**

---

## 4. Custom Check Integration (SandboxPathValidator)

### Can the AGT policy evaluator host a CUSTOM filesystem check?

**YES, via `IExternalPolicyBackend`.**

The `PolicyEngine.AddExternalBackend(IExternalPolicyBackend backend)` method registers custom backends evaluated alongside YAML rules. The interface:

```csharp
public interface IExternalPolicyBackend {
    string Name { get; }
    ExternalPolicyDecision Evaluate(IReadOnlyDictionary<string, object> context);
    Task<ExternalPolicyDecision> EvaluateAsync(IReadOnlyDictionary<string, object> context, CancellationToken ct);
}
```

The `context` dictionary receives the tool name + arguments (in our case: `["tool_name"] = "write_file"`, `["path"] = "C:\sandbox\src\file.cs"`). Our `SandboxPathValidator` integrates as:

```csharp
public class SandboxPolicyBackend : IExternalPolicyBackend
{
    private readonly string _sandboxRoot;
    public string Name => "sandbox-containment";

    public ExternalPolicyDecision Evaluate(IReadOnlyDictionary<string, object> context)
    {
        // Extract path from context, call ValidateAbsoluteContained / ValidateAndResolve
        // Return ExternalPolicyDecision { Allowed = true/false, Reason = ... }
    }
}
```

This backend fires on EVERY `PolicyEngine.Evaluate()` / `GovernanceKernel.EvaluateToolCall()` invocation. It is deterministic, synchronous, and can perform our full reparse-point ancestor walk + TOCTOU check (although TOCTOU handle-verify still requires the actual file open, which happens in `SandboxedFileTools` — the backend provides the pre-execution gate).

**Key finding:** AGT policies alone (YAML regex rules) CANNOT safely do filesystem reparse/TOCTOU protection. They can do string-match path rules (e.g., reject paths not starting with `C:\sandbox\`) but NOT reparse-point detection or open-then-verify. The `IExternalPolicyBackend` is the correct extensibility point for our `SandboxPathValidator` logic.

---

## 5. Execution-Sandbox for Copilot Subprocess

### Does AGT .NET offer OS-level process sandboxing for the SDK subprocess?

**Partially — not applicable to our use case.**

- `ISandboxProvider` / `DockerSandboxProvider` provides Docker-container-based code execution sandboxing. It is designed for **code execution** (user-submitted code, agent-generated scripts) — it creates an ephemeral container, runs code in it, returns stdout.
- It does NOT wrap an arbitrary subprocess (like the Copilot CLI binary) in a sandbox.
- `SandboxConfig` has no concept of "wrap this process" — it's a container-creation config.

**Verdict:** AGT's `ISandboxProvider` is for code execution isolation, NOT for constraining the Copilot SDK subprocess. The Copilot subprocess cannot be wrapped by AGT's Docker sandbox because:
1. It's a long-lived JSON-RPC process, not a one-shot code exec.
2. We need the host↔subprocess RPC channel to remain open for permission callbacks.
3. OS-level isolation of the subprocess (if desired) would require Job Objects (Windows) or seccomp/namespaces (Linux) — a separate future concern.

AGT's `.Use()` middleware similarly cannot reach into the subprocess — it only intercepts in-process MAF function calls. The Copilot SDK's `OnPermissionRequest` remains the enforcement point.

---

## 6. Summary Verdict

### AGT .NET is CONSUMABLE via NuGet PackageReference.

| Aspect | Status |
|--------|--------|
| Published NuGet | ✅ `Microsoft.AgentGovernance` 4.0.0 + `Microsoft.AgentGovernance.Extensions.Microsoft.Agents` 4.0.0 |
| Compatible with our TFM (net9.0) | ✅ Confirmed via successful restore |
| MAF integration via `.WithGovernance()` | ✅ Registers run + function-invocation middleware |
| Custom check for SandboxPathValidator | ✅ Via `IExternalPolicyBackend` |
| Audit (Decision BOM) | ✅ `AuditEmitter` with event subscription (implements our FR-033) |
| Copilot subprocess sandboxing | ❌ Not applicable — AGT's sandbox is Docker code-exec, not subprocess wrapping |
| Policy language | YAML rules (regex/field conditions) + external backends (OPA, Cedar, custom) |

### Integration architecture:
- **Foundry path**: `agent.AsBuilder().WithGovernance(adapter).Build()` — AGT handles both run-level and function-invocation middleware via `AgentFrameworkGovernanceAdapter`.
- **Copilot path**: AGT middleware cannot intercept SDK subprocess tool calls. Use `GovernanceKernel.EvaluateToolCall()` directly from the `OnPermissionRequest` handler. Same kernel, same policy, same audit — different invocation surface.
- **Custom sandbox enforcement**: Register `SandboxPolicyBackend : IExternalPolicyBackend` on `PolicyEngine.AddExternalBackend()`. Fires our `SandboxPathValidator` on every evaluation.

### No blockers. Proceed to design revision.
