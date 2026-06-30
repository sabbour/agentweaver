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
  - title: Sandbox browser preview
    details: When an agent starts a dev server inside its isolated Kubernetes sandbox pod, open a live browser preview of it from the run view — a port-forward tunnel scoped to that one run's pod, with no egress widening. <a href="/docs/experience/sandbox-browser-preview">See it in action →</a>
  - title: "AI credit and token usage monitoring"
    details: "Track GitHub Copilot token consumption and AI Credits at every level — individual runs, workflow orchestrations, projects, and the entire app. Live counters during execution, dashboards for analysis."
    link: /experience/token-usage-monitoring
    linkText: "See it in action"
  - title: Works the way you do
    details: Self-hosted. Runs with GitHub Copilot or Microsoft Foundry. Drive it from the web UI or any MCP-compatible client, including Copilot CLI.
  - title: A Copilot agent in every project
    details: Each new project ships with a ready-to-use GitHub Copilot agent under <code>.github/agents/</code> that drives Agentweaver through its MCP tools. Its tool map is generated from the live tool set, so it never drifts — and it never overwrites your edits. <a href="/docs/experience/agent-definition">See it in action →</a>
---

::: warning Alpha software
Agentweaver is **alpha software** under active development. Expect breaking changes, incomplete features, and rough edges. Do not rely on it for production workloads.
:::

## How it works

```mermaid
flowchart LR
    A[Submit goal or task] --> B[Coordinator drafts OutcomeSpec]
    B --> W[Selects workflow]
    W --> C{You confirm?}
    C -- Yes --> D[WorkPlan: subtask DAG]
    C -- Revise --> B
    D --> E[Parallel child runs]
    E --> F[RAI check per agent]
    F --> G[Single collective review]
    G -- Approve --> H[Merge + Scribe memory pass]
    G -- Decline --> I[Declined]
```

Submit a goal. The coordinator drafts a plan — you confirm it before any work starts. A squad of specialists works in parallel, each in an isolated sandbox. Review the assembled work once, behind a single gate that includes a Responsible AI check.
