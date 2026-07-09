# Unified autonomous steering

Unified steering makes correction feedback visible and predictable. Whether feedback comes from you, RAI, Rubberduck, Build & Test, another agent, the coordinator, or a workflow step, it goes to the coordinator first. The coordinator decides what to do and the timeline shows both the signal and the decision.

For the implementation details, see the [deep dive](../deep-dive/unified-steering.md). For event and route contracts, see the [reference](../reference/unified-steering.md).

## What you see

Every steering flow has two timeline beats:

1. **Steering received** — shows the source, severity, scope, and feedback.
2. **Steering decision** — shows what the coordinator chose and why.

The labels match the actual effect:

| Timeline label | Meaning |
| --- | --- |
| **Steered in place** | The existing child session resumes with context preserved. |
| **Fresh dispatch** | The coordinator deliberately reset and dispatched fresh work. This is never hidden. |
| **Proceeded** | The coordinator decided to continue toward review or terminal handling. |
| **Advisory noted** | The feedback was recorded, but no corrective action was taken. |

## When it happens

Unified steering handles correction feedback from:

- human review request-changes;
- RAI feedback;
- Rubberduck critique;
- Build & Test gate feedback;
- coordinator or workflow-step feedback;
- agent-originated steering signals.

The separate Assembly Gate correction route is gone. Assembly gate feedback now uses the same coordinator steering path.

## Why it matters

Previously, a request-changes gate could look like a glitch: subtasks reset and new pods appeared without a visible reason. Now fresh dispatch is an explicit coordinator decision with a rationale. When possible, the coordinator steers in place so the same child run keeps its context and worktree.

## What to expect

- **Same behavior from every source.** Feedback source changes the `source` label, not the routing mechanism.
- **Coordinator is the decider.** The coordinator chooses in-place steering, fresh dispatch, proceed/terminal, or advisory no-op.
- **Fresh dispatch is loud.** If a subtask is reset, the timeline says so before it happens.
- **Loops are bounded.** A subtask can be recovered at most three times; a plan can steer at most six times.
- **Human review stays understandable.** Review request-changes becomes a steering signal instead of a hidden assembly reset.

## Step by step

1. Open a coordinator run.
2. Watch for **steering received** events in the timeline when a gate or person supplies feedback.
3. Read the following **steering decision** event to see the coordinator's chosen action and rationale.
4. If it says **steered in place**, expect the same child session to resume.
5. If it says **fresh dispatch**, expect new child work because the coordinator explicitly chose that path.
6. If budget is exhausted, expect the run to proceed or surface a blocked terminal reason rather than loop forever.

## Related reading

- [Unified autonomous steering — Deep Dive](../deep-dive/unified-steering.md)
- [Unified autonomous steering — Reference](../reference/unified-steering.md)
- [Coordinator orchestration](./coordinator-orchestration.md)
- [Events reference](../reference/events.md)
