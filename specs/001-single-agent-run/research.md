# Phase 0 Research: Single-Agent File-Editing Run

## Decision 1: Use Microsoft Agent Framework loop as the sole orchestrator
- **Decision**: Implement one runtime loop (`plan -> tool call -> tool result -> continue/finish`) with Microsoft Agent Framework.
- **Rationale**: Satisfies constitution and FR-001/FR-010 while keeping orchestration inspectable.
- **Alternatives considered**:
  - Custom loop engine: rejected (violates Constitution I).
  - Multi-agent orchestration: rejected (out of scope for first slice).

## Decision 2: Standardize backend and clients on TypeScript/Node
- **Decision**: Build API and shared runtime in TypeScript (Node 22 LTS).
- **Rationale**: Enables shared types/contracts between backend, CLI, and Web; good ecosystem support for streaming and git tooling.
- **Alternatives considered**:
  - Polyglot backend/frontend: rejected (higher drift risk for first slice).
  - Python backend: rejected (less direct type sharing with React/CLI stack).

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
- **Rationale**: Resolves runaway-loop edge case and guarantees visible terminal outcome (FR-013).
- **Alternatives considered**:
  - Unlimited runs: rejected (operational and UX risk).
  - Provider-side limits only: rejected (insufficient deterministic control).
