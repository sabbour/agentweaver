---
layout: home
hero:
  name: Agentweaver
  text: Run a team of AI agents for any scenario you can describe.
  tagline: Submit a goal. A coordinator drafts a confirmed plan. Named specialist agents work in parallel, each in their own sandbox. You watch every step live, steer mid-run, and review before anything merges.
  image:
    src: /agentweaver.png
    alt: Agentweaver
  actions:
    - theme: brand
      text: What is Agentweaver?
      link: /guide/
    - theme: alt
      text: Get started
      link: /guide/getting-started
    - theme: alt
      text: Deploy to AKS
      link: /guide/deployment-aks
    - theme: alt
      text: MCP server
      link: /reference/mcp

features:
  - title: Coordinator orchestration
    details: Submit a goal. The coordinator drafts an OutcomeSpec, you confirm it, then a squad of specialists works in parallel — each in an isolated git worktree. You steer mid-run, review once, and approve.
  - title: Full observability
    details: Every agent message, tool call, and tool result streams live over SSE and is persisted before fan-out. Nothing is opaque. Reopen any run and see the full audit trail.
  - title: Human gates that matter
    details: No code merges without your explicit approval. The OutcomeSpec gate, RAI check, and human review step are enforced by the platform — not optional.
  - title: Scenario workflows
    details: Seven built-in YAML workflows cover software delivery, bug fixing, code review, content authoring, PM discovery, incident response, and agent evaluation. Add your own or generate one.
  - title: Persistent team memory
    details: Agents build on prior work through four memory layers — decisions, core context, learnings, and open session. A Scribe records what the team learned after every run.
  - title: Works the way you do
    details: Self-hosted. Runs with GitHub Copilot or Microsoft Foundry. Drive it from the web UI or any MCP-compatible client, including Copilot CLI.
---

::: warning Alpha software
Agentweaver is **alpha software** under active development. Expect breaking changes, incomplete features, and rough edges. Do not rely on it for production workloads.
:::

## How it works

```mermaid
flowchart LR
    A[Submit goal or task] --> B{Mode}
    B -->|Single agent| C[Isolated worktree run]
    B -->|Coordinator| D[OutcomeSpec drafted]
    D --> E[You confirm spec]
    E --> F[WorkPlan: subtask DAG]
    F --> G[Parallel child runs]
    G --> H[RAI check per agent]
    H --> I[Single collective review]
    C --> J[Per-run review gate]
    I --> K[Merge + Scribe memory pass]
    J --> K
```

Agentweaver supports two submission modes. In **single-agent mode**, one named agent works in an isolated workspace — you watch live, review the result, then approve or decline. In **coordinator mode**, you submit a goal: the coordinator drafts a plan, you confirm it before any work starts, and a squad of specialists works in parallel. You review the assembled work once, behind a single gate that includes a Responsible AI check.
