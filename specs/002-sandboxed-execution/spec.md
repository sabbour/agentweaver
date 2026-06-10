# Feature Specification: mxc-Based Sandboxed Execution

**Feature Branch**: `002-sandboxed-execution`

**Created**: 2026-06-10

**Status**: Draft

**Input**: User description: "mxc-based sandboxed execution in scaffolders — adopt Microsoft mxc for both (a) sandboxed shell/command execution and (b) optional file-tool isolation. Exploratory/spike-first; production wiring gated behind validation and a later approval, not enabled by default. Source of truth: the approved exploratory design plan (mxc Sandboxed Execution: Exploratory Design Plan)."

## Overview

Scaffolders today performs **no process or command execution**. Its governance policy categorically denies the `shell` capability (`deny-shell`), and agents may only operate path-contained file tools (read/write/edit/list) confined to a run's working directory through in-process containment.

This feature explores and specifies how scaffolders adopts the Microsoft `mxc` sandboxing engine to (a) make sandboxed shell/command execution possible and (b) optionally run file tools inside an `mxc` isolation boundary. The work is **exploratory and spike-first**: it must prove that real isolation is achievable on the target hosts, define the seam where `mxc` plugs into the existing governance and runner layers, and establish the gating mechanism — **without enabling sandboxed shell by default during the exploratory phase, before the spike-validation/approval gate (FR-028)**. Sandboxed shell becomes available only when the runtime confirms real isolation is active; otherwise the system continues to deny shell, never running commands unsandboxed silently.

Both supported host postures are in scope: (Option 2) a WSL2-as-sandbox-host path on Windows providing Linux `lxc`/`bubblewrap` isolation, and (Option 3) a local Windows-native backend using `mxc`'s default `processcontainer` backend (which works on Windows ARM64, Win11 24H2+, build 26100). The capability MUST reach both front-ends (CLI/TUI and Web UI) at parity through the authoritative API, and all execution MUST stream as observable run events.

`mxc` is an early preview and its own documentation warns that its profiles are **not yet hardened security boundaries**. Therefore `mxc` is treated as a defense-in-depth layer that augments — and never replaces — the existing in-process path containment and deny-by-default governance.

By default, file tools (read/write/list) are routed through the `mxc` sandbox executor for uniform isolation, while in-process path-containment validation is always retained as defense-in-depth. Once the spike validates real isolation and the production gate (FR-028) is passed, sandboxed shell is governed by a per-deployment configuration setting that defaults **on** — but actual execution still requires the platform-support probe to confirm real isolation; when isolation is unavailable, shell remains denied via the deny-by-default fallback regardless of the setting. The setting expresses operator intent; real-isolation gating always decides whether a command actually runs.

## Clarifications

### Session 2026-06-10

- Q: Default file-tools isolation mode — in-process containment only (A) or also route file tools through the sandbox (B)? → A: Route file tools (read/write/list) through the mxc sandbox executor by default; in-process path-containment validation always still applies as defense-in-depth.
- Q: Native binary distribution strategy — bundle the executor binaries, or require operators to provide them via an env-var-pointed directory? → A: Both — bundle `wxc-exec.exe` per-arch under `bin/<arch>` for zero-config by default, and allow the `MXC_BIN_DIR` env var to override the bundled binary.
- Q: Shell-enablement default once isolation is proven — enabled by default, opt-in per deployment, or opt-in per run? → A: A per-deployment configuration setting that is on by default and changeable through settings; sandboxed shell still requires the platform-support probe to confirm real isolation, else it falls back to deny (deny-by-default) regardless of the setting.
- Q: Network-policy posture given the Windows allowlist gap — default-block, route via WSL2/Linux, or document the gap and leave network open by configuration? → A: Document the Windows host-allowlist enforcement gap and leave network open by configuration (`defaultPolicy: allow`) as the default; operators may tighten to `defaultPolicy: block` + proxy, and the WSL2/Linux path remains available when true allowlisting is required.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Detect and report sandbox isolation availability (Priority: P1)

As a scaffolders operator starting a run, the system must determine, before any command can execute, whether real sandbox isolation is available on this host, select the appropriate execution path, and surface that decision so I can trust what the agent is allowed to do.

**Why this priority**: Every other behavior depends on this gate. Without a reliable, observable platform-support probe and executor selection, the system cannot safely decide whether to permit shell, and operators cannot tell whether commands would run isolated or be denied. This is the foundational seam.

**Independent Test**: Start a run on each target host (Windows-native, WSL2-backed, and an unsupported host). Confirm the run reports the selected execution backend and whether it provides real isolation, and that the reported decision matches the host's actual capability.

**Acceptance Scenarios**:

1. **Given** a Windows host where the native sandbox backend is available, **When** a run starts, **Then** the run reports that a real-isolation executor was selected and names the backend.
2. **Given** a Windows host where only the WSL2 Linux backend is available, **When** a run starts, **Then** the run reports that the WSL2-backed real-isolation executor was selected.
3. **Given** a host where no isolation backend is available, **When** a run starts, **Then** the run reports that no real isolation is available and that shell execution will be denied (fallback), including the reason from the platform probe.
4. **Given** any of the above, **When** the selection is made, **Then** the selected backend, isolation status, and reason are emitted as a run event and recorded in the audit log.

---

### User Story 2 - Run a shell command inside a sandbox with streamed output (Priority: P1)

As an agent operator on a host where real isolation is confirmed, I want the agent to be able to run an approved shell command inside the sandbox and watch its output stream live, so the agent can accomplish tasks that require command execution without escaping the run's boundary.

**Why this priority**: Enabling sandboxed shell is the primary motivating capability. It is gated on Story 1's outcome and is the first capability that delivers new agent functionality, but only under confirmed isolation.

**Independent Test**: On a host with confirmed real isolation, have an agent request a benign command (e.g., listing the working directory). Confirm the command runs, its stdout/stderr stream as run events in order, and a final exit code is reported, all confined to the run's sandbox root.

**Acceptance Scenarios**:

1. **Given** a host with confirmed real isolation, **When** the agent issues an approved shell command scoped to the run's working directory, **Then** the command executes inside the sandbox and its standard output and standard error are streamed as ordered run events.
2. **Given** a streamed command, **When** the command finishes, **Then** the run reports the command's exit code as part of the result.
3. **Given** a command that attempts to read or write a path outside the run's sandbox root, **When** it executes, **Then** the access is denied (not merely warned) and the denial is observable.
4. **Given** a command that exceeds the configured time limit, **When** the limit is reached, **Then** the command is terminated and the run reports a timeout outcome.
5. **Given** command output that exceeds the captured-output cap, **When** the result is produced, **Then** the result indicates the output was truncated.

---

### User Story 3 - Deny shell safely when isolation is unavailable (Priority: P1)

As a security-conscious operator, when real isolation is not available on a host, I need the system to refuse shell execution rather than fall back to running commands unsandboxed, so a missing or broken sandbox can never silently expose the host.

**Why this priority**: This is the safety invariant that makes enabling shell acceptable at all. It must be guaranteed alongside Stories 1 and 2, never deferred.

**Independent Test**: On a host with no isolation backend, have an agent request a shell command. Confirm the request is denied with a clear, observable reason, and that no command process is ever spawned outside a sandbox.

**Acceptance Scenarios**:

1. **Given** a host with no real isolation, **When** the agent requests a shell command, **Then** the request is denied and the denial reason is surfaced as a run event and in the audit log.
2. **Given** the deny-by-default fallback, **When** a run executes, **Then** no command is ever run unsandboxed without an explicit, loudly-warned opt-in (the default is deny).
3. **Given** both governance layers, **When** a shell command is evaluated, **Then** it must pass both the governance policy allow-rule and the command-scope validation before any execution path is taken.

---

### User Story 4 - Prove isolation works on the target hosts (spike) (Priority: P1)

As the engineer responsible for this feature, before any production wiring is approved, I need a spike that proves the sandbox engine actually runs on the target Windows ARM64 host and through the WSL2 path, capturing the exact working configuration, so the design rests on verified facts rather than assumptions.

**Why this priority**: The plan is explicitly spike-first. The Windows ARM64 build is a known blocker with known mitigations that must be empirically confirmed before the seam is wired into production.

**Independent Test**: Run the spike on the target Windows ARM64 machine and through WSL2. Confirm a hello-world command runs inside isolation on both paths and the succeeding configuration is recorded.

**Acceptance Scenarios**:

1. **Given** the target Windows ARM64 host with the native executor binaries placed where the engine can discover them, **When** the executor's probe is run, **Then** it reports a usable isolation tier and a hello-world command runs to completion inside the sandbox in pipe (non-PTY) mode.
2. **Given** the same host, **When** the WSL2 Linux isolation path is exercised, **Then** a hello-world command runs to completion inside Linux isolation with host paths correctly mapped into the Linux filesystem view.
3. **Given** a successful spike, **When** results are written up, **Then** the exact working configuration (binary location, mode, schema/version pin, any one-time host preparation) is documented so it can be reproduced.

---

### User Story 5 - Isolate file tools through the sandbox by default (Priority: P3)

As a designer evaluating uniform isolation, I want file-tool operations routed through the sandbox boundary by default in addition to in-process containment, so isolation is uniform across shell and file operations, while in-process containment remains as defense-in-depth.

**Why this priority**: The "both" half of the exploration. It makes isolation uniform but carries a performance cost (process spawn per operation), and must not remove existing in-process containment, which is always retained.

**Independent Test**: With the default configuration, perform read/write/list operations and confirm they are routed through the sandbox, remain confined to the sandbox root, and that in-process containment still validates every operation; on a host without real isolation, confirm operations fall back to the in-process path while still being validated.

**Acceptance Scenarios**:

1. **Given** the default configuration on a host with real isolation, **When** file tools are used, **Then** operations are routed through the sandbox boundary while in-process containment continues to validate each operation.
2. **Given** a host without real isolation, **When** file tools are used, **Then** operations fall back to the in-process path-contained path while in-process containment continues to validate each operation.
3. **Given** either mode, **When** a file operation targets a path outside the sandbox root, **Then** it is rejected.

---

### User Story 6 - Parity across both front-ends and the API (Priority: P2)

As a user of either the CLI/TUI or the Web UI, I expect the sandbox-execution capability and its observable run events to be reachable identically from both clients through the authoritative API, so neither client is privileged over the other.

**Why this priority**: Required by the project's two-front-ends-at-parity and API-first principles. It does not block proving isolation, but the capability is not complete until both clients reach it equally.

**Independent Test**: Trigger a sandboxed command run from the CLI and from the Web UI and confirm both observe the same streamed events (backend selection, output chunks, exit/result) through the API.

**Acceptance Scenarios**:

1. **Given** a run that selects an executor and streams command output, **When** observed from the CLI, **Then** all selection and execution events are visible.
2. **Given** the same run, **When** observed from the Web UI, **Then** the same events are visible with no client-side business logic deciding execution behavior.
3. **Given** the capability, **When** exercised, **Then** it is exposed through the API first and behaves identically regardless of which client initiated it.

---

### Edge Cases

- **Engine/binary missing or wrong architecture**: The host has the SDK but the native executor binary is absent or built for the wrong architecture. The system must treat this as "no real isolation" and deny shell (Story 3), reporting the reason, rather than failing opaquely.
- **PTY-incompatible host (Windows ARM64)**: The interactive-terminal path is unavailable on the target architecture. The system must use pipe (non-PTY) mode and still stream output.
- **Schema/version drift**: The sandbox policy schema is in alpha and may change. The system must pin an explicit policy schema version so runs are reproducible, and surface a clear error if the pinned version is unsupported.
- **Network policy gap on Windows**: Network host-allowlists are not enforced on the Windows-native backend (only block/allow plus proxy). The default posture leaves network open by configuration (`defaultPolicy: allow`) with this gap documented; when network restriction matters, the operator can tighten to a `defaultPolicy: block` posture plus proxy or use the Linux/WSL2 path (per FR-036).
- **One-time host preparation not performed**: A required tier needs a one-time system preparation step. Absent that step, the probe must report reduced capability and the system must degrade to a safe outcome (lower tier or deny), not crash.
- **Output flooding**: A command emits very large or unbounded output. The system must cap captured output and mark the result as truncated rather than exhausting resources.
- **Cancellation mid-stream**: The run is cancelled while a command streams. The command must be terminated and the cancellation reflected in the run's terminal state.
- **Path mapping across WSL2 boundary**: A Windows path handed to the Linux backend must be correctly translated to the Linux filesystem view; an untranslatable path must be rejected, not silently dropped.

## Requirements *(mandatory)*

### Functional Requirements

#### Platform detection and executor selection

- **FR-001**: The system MUST probe, per run (or at startup with per-run reuse), whether real sandbox isolation is available on the current host before permitting any command execution.
- **FR-002**: The system MUST select an execution path based on the probe: a real-isolation executor when the native backend is available, a WSL2-backed Linux-isolation executor when only that path is available, or a deny-by-default fallback when no isolation is available.
- **FR-003**: The system MUST expose, on each executor, a clear indication of whether it provides real isolation, and MUST treat the fallback as not-real-isolation.
- **FR-004**: The system MUST emit the selected backend, its isolation status, and the probe's reason as a run event and record them in the audit log.

#### Execution abstraction

- **FR-005**: The system MUST define a scaffolders-owned execution abstraction that decouples both agent runners from the underlying sandbox SDK, so that runners depend on the abstraction and not on the SDK directly.
- **FR-006**: The abstraction MUST support both a buffered one-shot execution (returning exit code, captured stdout, and captured stderr) and a streaming execution (yielding ordered output chunks and a terminal exit).
- **FR-007**: The abstraction MUST accept a command, a working directory, an environment, an explicit filesystem policy, and a time limit as inputs to an execution.
- **FR-008**: The system MUST provide at least three execution implementations behind the abstraction: a Windows-native real-isolation implementation operating in pipe (non-PTY) mode, a WSL2-backed Linux-isolation implementation, and a deny-by-default fallback that never runs commands unsandboxed by default.

#### Filesystem policy mapping

- **FR-009**: The system MUST map the run's working directory / sandbox root into the sandbox's read-write path set.
- **FR-010**: The system MUST map any configured allowed repository roots into the sandbox's read-only or read-write path set according to intent.
- **FR-011**: The system MUST rely on the sandbox's default-deny behavior so that any path not explicitly granted is denied, and MUST support adding sensitive host paths to an explicit deny set.
- **FR-012**: The system MUST canonicalize and validate the path set using the existing path-validation primitives before handing the filesystem policy to the sandbox engine (defense-in-depth on the policy itself).

#### Sandboxed shell governance and routing

- **FR-013**: The system MUST replace the categorical shell-deny rule with a sandboxed-shell-allow rule that is gated on the selected executor providing real isolation; when real isolation is not present, shell MUST remain denied.
- **FR-014**: The system MUST retain its dual-layer governance: a shell command MUST pass both the governance policy allow-rule and a command-scope validation (validating the command's declared filesystem scope against the sandbox root) before any execution occurs.
- **FR-015**: When a shell request is approved, both agent runners MUST route it to the execution abstraction's streaming path instead of rejecting it, with the Copilot runner handling its shell permission request and the Foundry runner exposing an equivalent run-command tool.
- **FR-016**: The deny-by-default fallback MUST refuse shell execution by default and MUST only run a command unsandboxed behind an explicit, loudly-warned opt-in that is off by default.

#### Streaming and results

- **FR-017**: Command execution MUST stream as run events on the existing observable stream, modeling a tool call, streamed output chunks, and a terminal result or error, in order.
- **FR-018**: The system MUST surface the command's exit code in the result.
- **FR-019**: The system MUST cap captured output and mark the result as truncated when the cap is exceeded.
- **FR-020**: The system MUST enforce the run's step and wall-clock limits over sandboxed execution and MUST terminate a command that exceeds its time limit, reporting a timeout outcome.

#### File-tool isolation

- **FR-021**: The system MUST route file tools (read/write/list) through the sandbox boundary by default, and MUST continue to apply in-process path-containment validation to every file operation as defense-in-depth.
- **FR-022**: The system MUST allow file operations to fall back to the in-process path-contained path when the sandbox boundary is unavailable (e.g., no real isolation on the host), and in every mode MUST apply in-process containment validation to every operation.
- **FR-023**: Under either mode, any file operation targeting a path outside the sandbox root MUST be rejected, not warned.

#### Host postures and reproducibility

- **FR-024**: The system MUST support both host postures: a Windows-native isolation path and a WSL2-backed Linux-isolation path, mapping Windows paths into the Linux filesystem view for the latter.
- **FR-025**: The system MUST pin an explicit sandbox policy schema version for reproducibility and MUST report a clear error when the pinned version is unsupported on the host.
- **FR-026**: The system MUST locate the native executor binary deterministically — preferring the `MXC_BIN_DIR` override when set, otherwise the bundled per-architecture `bin/<arch>` binary (per FR-034) — and MUST treat a missing or wrong-architecture binary as "no real isolation" (deny), reporting the reason.
- **FR-027**: The spike deliverable MUST empirically confirm that a hello-world command runs inside isolation on the target Windows ARM64 host and through the WSL2 path, and MUST document the exact working configuration.

#### Scope, gating, and documentation

- **FR-028**: This feature MUST NOT enable sandboxed shell during the exploratory phase; production wiring MUST remain gated behind successful spike validation and a later explicit approval. Once that gate is passed, the steady-state default is governed by the per-deployment setting in FR-035 (on by default), still subject to the real-isolation requirement (shell never runs unsandboxed).
- **FR-029**: The system MUST treat the sandbox engine as a defense-in-depth layer and MUST NOT remove or weaken existing in-process path containment or deny-by-default governance.
- **FR-030**: The feature MUST produce a committed design document capturing the spike results, the design seam, the Windows ARM64 setup runbook, and the "not a hardened security boundary yet" caveat.
- **FR-031**: The same build MUST support both local-developer and hosted-cloud execution of the capability, with neither treated as a special case.
- **FR-032**: A named human MUST remain accountable for every run, and any irreversible action arising from sandboxed execution MUST require explicit human approval.

#### Resolved decisions (see Clarifications: Session 2026-06-10)

- **FR-033**: The default file-tools isolation mode MUST route file tools (read/write/list) through the `mxc` sandbox executor by default, providing uniform isolation. In-process path-containment validation MUST always still be applied to every file operation as defense-in-depth and MUST never be removed; the sandbox boundary augments, not replaces, it.
- **FR-034**: The native binary distribution strategy MUST do both: the package MUST bundle the `wxc-exec.exe` executor per architecture under `bin/<arch>` to provide zero-configuration isolation by default, AND the system MUST allow the `MXC_BIN_DIR` environment variable to override the bundled binary location when set.
- **FR-035**: Once the spike validates isolation and the production gate (FR-028) is passed, sandboxed shell MUST be governed by a per-deployment configuration setting that defaults to **on** (enabled) and is changeable through settings. This setting expresses operator intent only: actual shell execution MUST still require the platform-support probe to confirm real isolation. When real isolation is unavailable, shell MUST remain denied via the deny-by-default fallback regardless of the setting value.
- **FR-036**: The network-policy posture MUST, by default, leave network open by configuration (`defaultPolicy: allow`) while the Windows host-allowlist enforcement gap is documented. Operators MUST be able to tighten the posture to `defaultPolicy: block` plus proxy, and the WSL2/Linux path MUST remain available for runs that require true host-allowlist enforcement.

### Key Entities *(include if feature involves data)*

- **Sandbox executor**: A selectable execution path with a real-isolation indicator. Implementations: Windows-native isolation, WSL2-backed Linux isolation, deny-by-default fallback.
- **Platform support result**: The outcome of the host probe — whether isolation is supported, the available methods/tiers, and a human-readable reason — used to select an executor.
- **Sandbox command**: A unit of execution — command line, working directory, environment, filesystem policy, and time limit.
- **Filesystem policy**: The explicit set of read-write paths, read-only paths, and denied paths handed to the sandbox engine, derived from the run's sandbox root and configured repository roots.
- **Execution result**: The terminal outcome of a command — exit code, captured output (with a truncation indicator), and error/timeout status.
- **Output chunk / run event**: An ordered streamed unit (tool call, output chunk, result/error) on the run's observable event stream.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On every supported host, a run reports its selected execution backend and isolation status before any command executes, in 100% of runs.
- **SC-002**: On a host with no real isolation, shell execution is denied in 100% of attempts, and no command process is ever spawned outside a sandbox.
- **SC-003**: On a host with confirmed real isolation, a benign shell command runs to completion inside the sandbox and its output is observable as ordered run events, with the exit code reported, in 100% of attempts.
- **SC-004**: Any command attempt to access a path outside the run's sandbox root is rejected (not merely warned) in 100% of attempts, under both the shell path and the default sandbox-routed file-tools path.
- **SC-005**: The spike demonstrates a hello-world command running inside isolation on the target Windows ARM64 host and through the WSL2 path, with the working configuration documented and reproducible.
- **SC-006**: The capability and its streamed run events are reachable identically from both the CLI and the Web UI through the API, verified by observing the same events from each client for the same run.
- **SC-007**: Commands exceeding the configured time limit are terminated and reported as timeouts in 100% of such cases; output exceeding the cap is marked truncated in 100% of such cases.
- **SC-008**: Existing in-process path containment and deny-by-default governance remain in force throughout; no regression allows an operation to escape the sandbox.
- **SC-009**: Every selection decision, command execution, and denial is recorded in the audit log in enough detail to reconstruct what happened, for 100% of runs.
- **SC-010**: Production sandboxed shell remains disabled by default until the spike passes and a later approval is granted; no default-path run executes shell before that gate.

## Assumptions

- The exploratory design plan (mxc Sandboxed Execution: Exploratory Design Plan) is the authoritative input for these requirements, and its findings about the sandbox engine's behavior and the Windows ARM64 mitigations are accepted as given for this spec.
- The target local host is a Windows ARM64 machine (Win11 24H2+, build 26100 or later) where the engine's default process-container backend is expected to function; x64-only or unshipped backends are out of scope.
- The pipe (non-PTY) execution mode is the intended mode for scaffolders on all hosts, including Windows ARM64.
- The existing governance convergence point (where both runners evaluate tool calls) is the intended place to route approved shell calls to an executor.
- The existing path-validation primitives are reusable to build and validate the filesystem policy handed to the engine.
- This feature delivers the spike, the design seam, the gating mechanism, and the design document; full production rollout of sandboxed shell is a later, separately-approved phase.
- The sandbox engine is a preview that is not yet a hardened security boundary; it is adopted as defense-in-depth, not as a replacement for existing containment.

## Dependencies

- The .NET port of the sandbox engine SDK (the managed package wrapping the native executor) must be available and resolvable in the build.
- The native executor binary (`wxc-exec.exe`) is bundled per architecture under `bin/<arch>` for zero-configuration discovery; the `MXC_BIN_DIR` environment variable may override the bundled binary location (per FR-034).
- A working WSL2 environment with the Linux isolation backend is required for the WSL2-backed path.
- One-time host preparation steps may be required for certain isolation tiers on Windows.
- The capability must be wired into both existing agent runners and the authoritative API to satisfy front-end parity.

## Out of Scope

- Hardening the sandbox engine into a certified security boundary (the engine is preview; this feature treats it as defense-in-depth only).
- Enforcing network host-allowlists on the Windows-native backend (not supported by the engine on Windows; only block/allow plus proxy). The default network posture is open-by-configuration (`defaultPolicy: allow`) with this gap documented; operators may tighten to `defaultPolicy: block` + proxy or use the WSL2/Linux path for true allowlisting (per FR-036).
- Enabling sandboxed shell by default in production prior to spike validation and a later approval.
- Replacing or removing the existing in-process path-contained file tools.
- Supporting x64-only or unshipped isolation backends (hyperlight, microvm) on the ARM64 target host.
