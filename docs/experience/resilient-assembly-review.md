# Resilient assembly-review loop

Before v0.9.17-rc1, a coordinator run whose autonomous steering budget was exhausted during collective
assembly wrote a terminal `assembly_blocked` status and **parked permanently** — the assembled work existed
and was ready, but no human could reach it for review. This page explains what changed, what you observe
now, and how the new behavior affects your day-to-day review workflow.

For the technical design see the [deep dive](../deep-dive/resilient-assembly-review.md); for config knobs
see the [reference](../reference/resilient-assembly-review.md).

## What used to happen vs. what happens now

| Scenario | Before v0.9.17-rc1 | v0.9.17-rc1 and later |
|---|---|---|
| Assembly-gate steering budget exhausted | Run writes `assembly_blocked`, parks forever. No review card. | Run opens the **human-review gate** automatically. The assembled work is immediately reviewable. |
| Reviewer requests changes across rounds | Revising agent received only the latest feedback; earlier rounds silently discarded. | Revising agent receives **all accumulated round feedback** (structured by gate source and round). |
| Reviewer rejects an artifact | Author could be re-dispatched again on the same artifact. | Author is **locked out** for that subtask; a **different** eligible agent picks up the revision, reusing the prior committed work and the full feedback history. |
| Post-turn commit stuck on a stale `.git/index.lock` | All three retry attempts fail the same way; child run silently wedges. | Lock is cleared between retries (if stale and unowned); commit succeeds on retry. On a persistent fault the child fails with a **visible structured event** instead of a silent stream drain. |

## Human-review gate: what the UI shows now

When the autonomous steering budget is exhausted, the coordinator transitions the run to `InReview` (the
same state as the normal happy-path human-review gate) and emits a review-requested event with reason
`steering_budget_exhausted`. In the web UI:

- The **review card opens** on the run detail page, exactly as it would if the gate had been reached
  organically — the same Approve / Decline / Request changes actions are available.
- The review card includes a **"Why are you seeing this?"** context panel showing all accumulated gate
  feedback from the autonomous rounds, so you can understand why the system could not converge before
  escalating to you.
- The **preview** (if the coordinator run started a Build & Test live-preview) remains accessible from
  the review gate — preview pod binding is unaffected by the escalation.

📸 **Screenshot — `assembly-review-escalation.png`**
*Shows:* the coordinator run review card with `reason: steering_budget_exhausted`, accumulated autonomous
gate feedback, and the Approve / Decline / Request changes actions.
*Path:* let an assembly gate exhaust its steering budget on a project with a non-trivial plan → the review
card opens automatically at `/projects/:projectId/orchestrations/:runId`.

:::info Screenshot is a placeholder
This screenshot is not yet captured. The image below is a placeholder until the feature is recorded
against a live AKS environment.
:::

![Assembly-review escalation — review card opens automatically](/screenshots/assembly-review-escalation.png)

## What to expect when you review an escalated run

### Approve

Works exactly like a normal human-review approval: the coordinator merges the assembled branches, runs
the Scribe memory pass, and marks the run `assembly_complete`. The preview (if active) stays reachable
until the run is torn down.

### Decline

Works exactly like a normal decline: the run writes a terminal `assembly_declined` status. This is a
deliberate human decision — not a platform dead-end.

### Request changes

The coordinator passes your feedback to the autonomous steering loop, which receives a **fresh budget
window** (the loop's iteration counter is reset) so it can act on your specific guidance without
immediately re-exhausting. A round-trip counter is incremented; after **3 human round-trips** the
autonomy loop is paused (your gate stays open) but the run never terminates on its own — you still hold
Approve / Decline.

## Accumulated feedback across revision rounds

Each time a gate requests changes — whether from the Rubberduck reviewer, the Build & Test gate, the RAI
check, or a human reviewer — the coordinator records that feedback in a durable store keyed by gate source
and round number. On every subsequent revision dispatch the revising agent receives **all prior rounds**
of feedback, not just the most recent complaint. This eliminates the amnesia that previously caused agents
to re-violate earlier feedback they had never seen.

The accumulated feedback appears in the review card context panel and is included in the revision task
handed to the agent.

## Reviewer-rejection lockout: how author rotation works

When a reviewer issues a **rejection** (request-changes):

1. The current author is **locked out** of that specific subtask for the rest of the revision cycle.
2. The coordinator selects a **different eligible agent** from the project roster.
3. The new agent is dispatched with:
   - the prior agent's **worktree and branch** (the committed work is preserved — only authorship rotates);
   - the full **accumulated feedback** from all prior rounds;
   - a **new session identity** (the new agent never inherits the locked-out author's session).
4. A visible `coordinator.steering_decision` event records the rotation: who was locked out and who was
   selected.

**Advisory or steering feedback (not a rejection)** keeps the same agent in place — no lockout, no
rotation, just a context-carrying in-place revision.

If all eligible agents become locked out (or the lockout path's budget is exhausted), the run escalates
to the human-review gate — never to a terminal state.

## Reliable commit and visible failure

The coordinator child pipeline no longer silently drops a commit fault. When a commit fails:

1. Between retry attempts, the runtime checks whether a stale `.git/index.lock` is blocking the commit
   and removes it if it is genuinely stale (older than `Coordinator:StaleLockThresholdSeconds`, default
   15 seconds) and unowned by a live `git` process.
2. If the commit still fails after all retries, the child run terminates with a **visible structured
   `run.failed` event** (`reason=commit_failed_persistent`, plus per-attempt lock diagnostics). This
   surfaces in the run timeline instead of a silent stream-drain failure.

On a lock-contention commit fault (the common case — a prior crashed process left the lock behind), the
retry typically succeeds after the clear: the revision commits its edits on the same worktree with
context intact.

## Configuration

See the [reference](../reference/resilient-assembly-review.md#configuration) for the full config table.
The two operator-facing knobs are:

| Knob | Default | What to adjust |
|---|---|---|
| `Coordinator:StaleLockThresholdSeconds` | `15` | Increase if your CI/CD agents keep git processes alive longer than 15 s after a child turn ends; decrease if you want stale locks cleared faster. A value of `0` is not recommended — it may clear locks owned by a concurrent in-flight git operation. |
| Max human review round-trips | `3` (constant) | Hard-coded in `CoordinatorSteeringDecider.DefaultMaxHumanReviewRoundTrips`. Contact your platform operator to change this between deployments. |

## Related reading

- [Resilient assembly-review — Deep Dive](../deep-dive/resilient-assembly-review.md) — the state machine, code paths, and rationale.
- [Resilient assembly-review — Reference](../reference/resilient-assembly-review.md) — config keys, status codes, and event fields.
- [Unified autonomous steering](./unified-steering.md) — how assembly-gate feedback becomes a `SteeringSignal`.
- [Coordinator orchestration](./coordinator-orchestration.md) — the broader coordinator lifecycle.
- [Review workspace & merge](./review-workspace-merge.md) — the human-review gate UI in context.
