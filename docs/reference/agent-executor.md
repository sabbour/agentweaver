---
title: Agent eXecutor (AX) Comparison
---

# Agent eXecutor (AX) Comparison

Agent eXecutor (AX) is Google's open-source distributed agent runtime. This comparison helps clarify where AX overlaps with Agentweaver, where the two systems operate at different layers, and when each approach is the better fit.

| Dimension | Agent eXecutor (AX) | Agentweaver |
| --- | --- | --- |
| What it is / Layer | Distributed agent harness runtime; orchestration substrate | Full-stack agent orchestration runtime with workspace, review, and merge flow |
| Language / Stack | Go | C#, TypeScript |
| Execution model | Single-writer controller with append-only event log, `conversationId` sessions, resumable gRPC streams | Coordinator expands an OutcomeSpec into a WorkPlan DAG and runs child tasks in parallel git worktrees on AgentHost pods |
| Isolation / Sandboxing | Compute-agnostic; can run on different substrates, but does not prescribe VM isolation | Kata VM-backed sandbox execution on AKS with layered network controls |
| Human-in-the-loop | On roadmap; approvals are not first-class today | Built-in review gates with approve, request-changes, and decline flows |
| Streaming / Observability | gRPC streaming plus durable event log and OpenTelemetry support | SSE streaming, durable `RunEvents` in PostgreSQL, live topology and run visibility |
| Git / Workspace | No built-in git workspace model | Per-run git worktree and branch lifecycle with merge serialization |
| MCP integration | No built-in MCP surface | Native MCP server that exposes runs and outcomes as tools |
| Steering | No redirect / steering concept called out in the runtime model | Mid-run coordinator steering and redirection |
| Status / License / Links | Open source, Apache 2.0, [GitHub](https://github.com/google/ax), [Google Cloud blog](https://cloud.google.com/blog/products/ai-machine-learning/agent-executor-googles-distributed-agent-runtime) | Alpha software, MIT, [GitHub](https://github.com/sabbour/agentweaver), [Docs](https://sabbour.me/agentweaver/) |

AX and Agentweaver overlap most at the orchestration layer, but they optimize for different boundaries. AX is stronger when the goal is a framework-agnostic distributed runtime that can sit over multiple compute backends at scale without owning the full developer workflow.

Agentweaver is broader in scope. It couples orchestration to sandboxed git workspaces, human review, and the run-to-merge path, while AX stays closer to the runtime substrate and eventing layer.

## Running Agentweaver on top of AX

TODO: Document what an Agentweaver-on-AX deployment model would look like, including controller boundaries, workspace ownership, and where review/merge state would live.
