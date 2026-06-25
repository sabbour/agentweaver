# Workflow Library

Agentweaver ships a library of **functional, reusable workflow definitions** that any Blueprint may
reference. Each workflow is a purpose-built pipeline named after what it does, not after the team
that runs it. A Blueprint bundles a **set** of workflow ids; the coordinator selects the right one
per task (Feature 015 US5), and the user may override the choice.

Workflow YAML files live in `packages/Agentweaver.Squad/Catalog/Resources/workflows/` and are
embedded in the `Agentweaver.Squad` assembly. They are loaded by `WorkflowRegistry` alongside the
built-in default and any project-authored workflows in `.agentweaver/workflows/`.

---

## Workflow index

| Id | Name | Default for Blueprint(s) | Trigger | Binder status |
|----|------|--------------------------|---------|---------------|
| `software-delivery` | Software Delivery | Software Development, Product & Software Delivery | Event | ✅ Runnable |
| `bug-fix` | Bug Fix | — (referenced by Software Development, Product & Software Delivery) | Event | ✅ Runnable |
| `code-review` | Code Review | — (referenced by Software Development) | Manual | ✅ Runnable |
| `content-authoring` | Content Authoring | Content Authoring, Product Management | Event | ✅ Runnable |
| `pm-discovery` | Product Management Discovery | Product Management, Product & Software Delivery | Event | ✅ Runnable |
| `agent-evaluation` | Agent Evaluation | AI Agent Engineering | Event | ⚠️ **Not runnable** — uses `fan_out`/`fan_in` |
| `incident-response` | Incident Response | Platform Reliability / SRE | Event | ✅ Runnable |

> **Binder status** reflects whether the workflow binds onto the live MAF run graph today (see
> [workflow-binder.md](workflow-binder.md)). Six of the seven library workflows bind and run: their
> `peer_review` nodes are wired (as verdict gates or plain turns) and every transition has an executor
> mapping. **`agent-evaluation` does not yet run** — its `fan_out`/`fan_in` nodes are accepted by the
> loader but not yet wired to a runtime executor, so building it throws a `WorkflowBindException` at
> build time. It remains in the catalog as the reference shape for the forthcoming parallel-dispatch
> support.

---

## `software-delivery`

**Purpose**: Full software delivery pipeline for new features and significant changes.

**When to use**: A task that requires planning, implementation, QA sign-off, RAI content safety
check, and code review before merging. Use this when quality and safety gates are non-negotiable.

**Node structure**:

```
plan (prompt)
  → implement (prompt)
    → test-gate (peer_review, agent: qa-engineer)
        when: pass  → rai-check (check, gate_kind: rai)
                          when: revise       → implement
                          when: safety-failed → terminal-safety-failed
                          when: no-changes   → scribe
                          when: review       → code-review (peer_review)
                                                 → review-gate (check, gate_kind: human-review)
                                                       when: approved        → merge → scribe → done
                                                       when: request-changes → implement
                                                       when: declined        → terminal-declined
        when: fail  → implement
```

**Key gates**: QA test gate (must pass before RAI check), RAI safety check, human review gate.

---

## `bug-fix`

**Purpose**: Lightweight pipeline for defects and patches with a quick QA verification cycle.

**When to use**: A task that is clearly a bug fix, patch, or small targeted correction. Skips the
heavyweight planning and code-review stages of `software-delivery`.

**Node structure**:

```
triage (prompt)
  → fix (prompt)
    → verify (peer_review, agent: qa-engineer)
        when: approved       → merge → scribe → done
        when: request-changes → fix
        when: declined        → terminal-declined
```

**Key characteristic**: No separate RAI or code-review gate — optimized for quick turnaround.

---

## `code-review`

**Purpose**: Standalone review that produces feedback without merging.

**When to use**: A task that asks for a review of an existing change, draft PR, or proposed approach
where the output is feedback only (no deployment or merge).

**Node structure**:

```
review (peer_review)
  → feedback (prompt)
    → scribe → done
```

**Trigger**: Manual only (user explicitly initiates).

---

## `content-authoring`

**Purpose**: Content creation pipeline for articles, documentation, and long-form written output.

**When to use**: A task that produces published text rather than merged code — e.g. a blog post,
product documentation page, release notes, or feature specification.

**Node structure**:

```
research (prompt)
  → draft (prompt)
    → edit (peer_review)
      → rai-check (check, gate_kind: rai)
            when: revise        → draft
            when: safety-failed → terminal-safety-failed
            when: no-changes    → scribe
            when: review        → publish (merge) → scribe → done
                                  when blocked    → edit
```

**Key characteristic**: `publish` is a `merge` node that delivers content. No code-review gate.

---

## `pm-discovery`

**Purpose**: Product discovery pipeline for research, synthesis, and stakeholder sign-off.

**When to use**: A task whose output is a document — requirements spec, feature definition, user
research synthesis, or prototype brief — not deployable code.

**Node structure**:

```
research (prompt)
  → synthesis (prompt)
    → review (peer_review)
      → review-gate (check, gate_kind: human-review)
            when: approved        → scribe → done
            when: request-changes → synthesis
            when: declined        → terminal-declined
```

**Key characteristic**: No `merge` stage — approved work goes directly to the scribe.

---

## `agent-evaluation`

> ⚠️ **Not runnable yet.** This workflow uses `fan_out`/`fan_in` nodes, which the loader accepts but the
> binder does not yet wire to a runtime executor. Attempting to run it throws a `WorkflowBindException`
> at build time. It is kept in the catalog as the canonical parallel-evaluation shape and as a few-shot
> example for workflow generation. See [workflow-binder.md §5](workflow-binder.md).

**Purpose**: AI agent evaluation with parallel evaluation runs and a mandatory safety gate.

**When to use**: A task that evaluates an AI agent's capabilities, safety properties, or performance.
The safety gate must clear before an evaluation report is produced.

**Node structure**:

```
eval-setup (prompt)
  → eval-run (fan_out)
    → eval-collect (fan_in, target: eval-run)
      → safety-gate (check, gate_kind: rai)
            when: revise        → eval-setup
            when: safety-failed → terminal-safety-failed
            when: no-changes    → scribe
            when: review        → report (prompt) → scribe → done
```

**Key characteristics**: `fan_out`/`fan_in` pair for parallel eval runs; safety gate blocks the
report if content safety fails.

---

## `incident-response`

**Purpose**: SRE incident response with an explicit postmortem step before the run closes.

**When to use**: A task representing a production incident, outage, or reliability event. Every run
ends with a postmortem so the incident is retrospectively documented before the scribe records it.

**Node structure**:

```
triage (prompt)
  → mitigate (prompt)
    → verify (peer_review)
      → review-gate (check, gate_kind: human-review)
            when: approved        → postmortem (prompt) → scribe → done
            when: request-changes → mitigate
            when: declined        → terminal-declined
```

**Key characteristic**: `postmortem` step is mandatory on the approval path before the scribe.

---

## Blueprint → workflow mappings

| Blueprint | Default workflow | Full workflow set |
|-----------|-----------------|-------------------|
| Software Development | `software-delivery` | `software-delivery`, `bug-fix`, `code-review` |
| Product Management | `pm-discovery` | `pm-discovery`, `content-authoring` |
| Content Authoring | `content-authoring` | `content-authoring` |
| Product & Software Delivery | `pm-discovery` | `pm-discovery`, `software-delivery`, `bug-fix` |

The built-in `default` workflow (agent → rai → review → merge → scribe) remains as a fallback
for projects that pre-date the workflow library and for inline blueprints that do not reference a
library workflow.
