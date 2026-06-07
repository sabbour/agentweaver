# Phase 0 Research: Single-Agent File-Editing Run

## Decision 1: Use Microsoft Agent Framework loop as the sole orchestrator
- **Decision**: Implement one runtime loop (`plan -> tool call -> tool result -> continue/finish`) with Microsoft Agent Framework.
- **Rationale**: Satisfies constitution and FR-001/FR-010 while keeping orchestration inspectable.
- **Alternatives considered**:
  - Custom loop engine: rejected (violates Constitution I).
  - Multi-agent orchestration: rejected (out of scope for first slice).

## Decision 2: Build the backend and CLI on .NET; Web UI on React 19 + Fluent 2
- **Decision**: Implement the authoritative API and agent runtime on .NET 9 (ASP.NET Core). Build the CLI/TUI on .NET (Spectre.Console) sharing a generated API client. Build the Web UI with React 19 + Fluent 2 (TypeScript/Vite).
- **Rationale**: Constitution Principle I mandates the Microsoft Agent Framework (.NET) and Principle II mandates the GitHub Copilot SDK (.NET); the agent loop and provider adapters must therefore run in .NET. Principle IV mandates a CLI (TUI) and a React 19 + Fluent 2 Web UI. The OpenAPI contract is the shared source of truth across both clients (Principle III).
- **Alternatives considered**:
  - TypeScript/Node backend: rejected (cannot host the mandated .NET Agent Framework and Copilot SDK in-process; would violate Constitution I/II).
  - Python backend: rejected (same runtime conflict with the mandated .NET agent stack).
  - React/TypeScript CLI: viable, but a .NET CLI keeps the agent-facing stack unified and lets both clients consume one OpenAPI-generated client.

## Decision 3: Enforce sandboxed file tools via canonical-path checks
- **Decision**: Implement read/write tool guards using `realpath`, relative-root checks, symlink resolution checks, and deny absolute/escape paths.
- **Rationale**: Directly addresses FR-006/FR-007 and SC-002.
- **Alternatives considered**:
  - String-prefix path checks only: rejected (symlink bypass risk).
  - OS-level chroot/container-only enforcement: rejected for first slice portability.

## Decision 4: Use git worktree per run with explicit lifecycle
- **Decision**: On run creation, create a dedicated worktree from originating branch; retain on decline/failure for review; clean up on policy.
- **Rationale**: Meets FR-003/FR-014/FR-016 and supports isolated concurrent runs.
- **Alternatives considered**:
  - In-branch temporary commits: rejected (risks branch contamination).
  - Full repo clone per run: rejected (heavier startup cost).

## Decision 5: Stream steps via Server-Sent Events (SSE)
- **Decision**: Expose ordered run steps on an SSE endpoint with monotonic `sequence` and reconnect support (`Last-Event-ID`).
- **Rationale**: Simple one-way live delivery fits FR-011/FR-012 and supports CLI/Web watchers consistently.
- **Alternatives considered**:
  - WebSockets: rejected for extra bidirectional complexity not needed yet.
  - Polling: rejected (worse latency and ordering guarantees).

## Decision 6: Represent model source as strict enum with adapter boundary
- **Decision**: `modelSource` enum values: `copilot_sdk`, `microsoft_foundry`; provider adapters behind one runtime interface.
- **Rationale**: Enforces Constitution II and FR-009 while allowing per-run provider selection.
- **Alternatives considered**:
  - Free-form provider string: rejected (permits unsupported sources).
  - Separate run pipelines per provider: rejected (duplicates logic).

## Decision 7: Require explicit human review action endpoint before merge
- **Decision**: Add review API that requires explicit `approve` or `decline`; only `approve` triggers merge attempt.
- **Rationale**: Guarantees FR-015/FR-016 and clear auditability of decision.
- **Alternatives considered**:
  - Auto-merge on completion: rejected (violates human-in-loop requirement).
  - Manual git-only merge outside system: rejected (breaks end-to-end flow parity).

## Decision 8: Bound run execution with configurable limits
- **Decision**: Enforce max step count and max wall-clock duration; terminal state becomes `bounded`.
- **Rationale**: Resolves runaway-loop edge case and guarantees visible terminal outcome (FR-013, FR-029).
- **Alternatives considered**:
  - Unlimited runs: rejected (operational and UX risk).
  - Provider-side limits only: rejected (insufficient deterministic control).

## Decision 9: Apply content-safety checks before relaying model output to clients
- **Decision**: Intercept every model-generated output in the Agent Framework governance layer and apply the active provider's content-safety API before the output is written to the event log or relayed to any client. Content that fails the check is withheld; the run is ended with a `run.failed` lifecycle event whose payload records the failure reason; the failure event is persisted in the append-only event log.
- **Rationale**: Satisfies FR-025 and SC-008 (100% of content-safety-failing outputs withheld from clients and recorded). Placing the check in the governance layer (Principle X) ensures it cannot be bypassed by any client or tool.
- **Alternatives considered**:
  - Client-side filtering only: rejected (clients are thin by Principle III; this leaks content to the event log before filtering).
  - No content-safety check: rejected (violates Principle VIII and FR-025).

## Decision 10: Exclude secrets and personal data from event log payloads and operational records
- **Decision**: Define a scrubbing layer in the persistence path that prevents raw secrets, credentials, and personal data from being written into Event log payloads, client-facing outputs, or OperationalRecord fields. Data forwarded to the model provider is limited to what that provider requires for the active run. Scrubbing rules are enforced by the governance layer (Principle X), not by individual clients.
- **Rationale**: Satisfies FR-026 and SC-009. Centralizing in the governance layer ensures uniform enforcement regardless of client or code path.
- **Alternatives considered**:
  - Per-client scrubbing: rejected (violates Principle III; cannot guarantee consistency across clients).
  - No scrubbing (rely on users not sending secrets): rejected (violates FR-026 and Principle VIII).

## Decision 11: Centralize all governance policy enforcement in Agent/Governance/
- **Decision**: Create a dedicated `Governance/` module inside the Agent Framework host (`backend/Scaffolder.Api/Agent/Governance/`) that owns: the tool allowlist check, the model-source enum guard, the sandbox boundary enforcement, the human-approval gate, the run-limit policy (max steps and max duration), and the content-safety intercept (Decision 9). No client or tool may grant itself permissions outside what this module allows.
- **Rationale**: Satisfies FR-027 and SC-010. A single governance module is easier to audit, test, and reason about than scattered per-client policy checks. Aligns with Principle X (governance must be enforced by the Agent Framework layer).
- **Alternatives considered**:
  - Inline policy checks per tool or endpoint: rejected (scattered enforcement, hard to audit, easily bypassed).
  - Governance in client code: rejected (violates Principles III and X).

## Decision 12: Separate OperationalRecord from the per-run Event log
- **Decision**: Introduce an `OperationalRecord` entity, distinct from the append-only Event log, that captures per-run operational metadata: submitting user identity (`submittedBy`, FR-024), selected model source, run start time, step count, outcome, end time, and a trace of every governance policy decision reached during the run (SC-010). This record is written by the governance and persistence layers, not by the event-log path, and is the primary artifact for debugging, compliance review, and capacity analysis (FR-028).
- **Rationale**: Satisfies FR-024, FR-028, and SC-010. Keeping the OperationalRecord separate from the event log allows each to be optimised independently: the event log is append-only and ordered for streaming/replay; the OperationalRecord is structured for operational queries and compliance reporting.
- **Alternatives considered**:
  - Derive operational data from event log queries: rejected (event log is optimised for streaming, not ad-hoc operational queries; conflates two distinct consumer audiences).
  - Include submittedBy in every event payload: rejected (redundant and adds noise to the streaming path; one structured record per run is sufficient for compliance).
