# Competitive Landscape: Multi-Agent Orchestration Systems

> **Purpose.** Competitive research input for the Agentweaver PRD. Agentweaver is a
> human-led, file-native multi-agent orchestration system (its core primitives —
> **Casting**, **Scribe**, **Decisions**, **Memory/Decision Inbox** — directly mirror
> [bradygaster/squad](https://github.com/bradygaster/squad), its stated inspiration).
> This report maps the direct inspiration, adjacent products, and the broader industry
> standards that a credible multi-agent orchestrator is expected to meet.
>
> **Researched:** 2026-06-25 · **Requested by:** Ahmed Sabbour
>
> **Methodology.** Findings were gathered from primary sources — official docs, GitHub
> source, and live product sites — via live fetch/search. Where a named system could
> **not** be confirmed in any official source, it is flagged explicitly rather than
> presented as fact.

---

## Executive Summary

| System | Category | Open Source | Relevance to Agentweaver |
|---|---|---|---|
| **bradygaster/squad** | Human-led agent team over GitHub Copilot | ✅ (alpha) | **Direct inspiration** — shared primitives (casting, decisions, Scribe, Ralph) |
| **Google Agent Executor** | Distributed, harness-agnostic agent runtime | ❌ (Google Cloud) | Self-managed compute sovereignty; harness-agnostic runtime bar |
| **Google Agent Substrate** | Open-source Kubernetes abstraction for million-agent scale | ✅ (open source) | Agent-optimized compute layer; GKE Agent Sandbox (GA) |
| **Google Gemini Enterprise Agent Platform / ADK / A2A** | Managed enterprise agent platform + open SDK + interop protocol | Partial (ADK/A2A ✅) | Enterprise-grade governance, memory, interop bar |
| **OpenClaw.ai** | Self-hosted multi-channel agent gateway | ✅ (MIT) | Skills/memory file model; an *executor* others orchestrate |
| **Nous Research — Hermes Agent** | Self-improving agent w/ Kanban multi-agent queue | ✅ | **Closest peer** to Agentweaver's durable multi-agent + memory model |
| **Paperclip.ing** | Agent *orchestration* layer (org charts, budgets, governance) | ✅ (MIT) | **Closest peer** to Agentweaver's governance/accountability framing |
| **LangGraph** | Graph/state-machine orchestration runtime | ✅ (Apache 2.0) | HITL + checkpointing gold standard |
| **AutoGen (Microsoft)** | Actor-model conversable agents / group chat | ✅ (MIT) | Multi-agent team topologies + human proxy |
| **CrewAI** | Role-based crews + tasks + flows | ✅ (MIT) | Role/task model + richest built-in memory |
| **OpenAI Agents SDK (← Swarm)** | Lightweight handoff-based orchestration | ✅ (MIT) | Minimal-primitive, tool-approval HITL |
| **AWS Bedrock Multi-Agent** | Managed supervisor/sub-agent service | ❌ | No-code managed supervisor model |

---

# Part 1 — Direct Inspiration & Adjacent Products

## 1. bradygaster/squad

**Repo:** https://github.com/bradygaster/squad · **CLI:** `@bradygaster/squad-cli` · Status: **Alpha**

### Core value proposition
A **human-directed AI development team delivered through GitHub Copilot**. You describe what
you're building; Squad scaffolds a team of specialist agents (frontend, backend, tester, lead,
Scribe) that **live in your repo as files**, persist across sessions, learn your codebase, and
share decisions — explicitly positioned as a productivity amplifier, *not* a replacement for
human accountability ("Responsible AI stance — keep humans in the loop").

### What problem it solves
Lets a single human coordinate more parallel work without losing oversight: agents run in
**isolated contexts**, read only their own knowledge, and **write back what they learned** so
work stays inspectable and governance stays close to the code.

### Core concepts
| Concept | What it is |
|---|---|
| **Casting** | The team-composition model. Each member is named from a persistent **thematic cast**; `squad init` proposes a team (charters + routing rules); human confirms with "yes". Members are files under `.squad/`. |
| **Routing** | A **coordinator** routes natural-language requests (`@AgentName` or commas) to the right agent(s); independent agents launch **in parallel**. |
| **Decisions** | Every decision any agent makes is appended to **`.squad/decisions.md`** — a shared, inspectable decision ledger for the whole team. |
| **Scribe** | A dedicated logging/record-keeping member that captures everything: decisions, orchestration log, session history. |
| **Ceremonies** | Structured team workflows (setup, review/governance, streaming) demonstrated across the bundled samples (casting, governance, streaming, Docker). |
| **Ralph (Watch Mode)** | An automated polling loop (`squad watch`/`triage`/`loop`) that scans GitHub Issues, builds a context snapshot, and **delegates issue selection to the agent** (`-p context.md`), then monitors and escalates to humans. |
| **History / knowledge compounding** | Each member writes lasting learnings to its `history.md`; knowledge compounds across sessions so agents learn conventions over time. |

### Execution model
File-native state in `.squad/` (team, charters, decisions, orchestration-log, logs). Runs via
`copilot --agent squad --yolo`. Agents execute in **isolated Copilot contexts** and in parallel;
the coordinator records follow-ups and breadcrumbs. State can be **externalized** outside the
working tree to survive branch switches, exported/imported as JSON snapshots, and persisted via
**git-notes** or **orphan-branch** backends.

### Human-in-the-loop
Strong, explicit, governance-first. Human confirms casting, reviews `decisions.md`, and Ralph's
4-tier escalation (circuit-breaker reset → auth reprobe → git pull → 30-min pause) backs off to
humans on blockers. `--yolo` only suppresses per-tool approval prompts.

### Memory & state
Markdown-file memory: per-agent `history.md`, shared `decisions.md`, `orchestration-log/`,
`log/`. `squad nap` does context hygiene (compress/prune/archive). No vector store — memory is
human-readable files committed alongside code.

### Tool/skill system
Skills ship as packaged capabilities (e.g., `squad-commands`). Integrates GitHub (Issues/PRs/`gh`),
a plugin marketplace + upstream sync, and a **`Squad.Agents.AI`** preview NuGet that exposes a
Squad team as a Microsoft Agent Framework `AIAgent`.

### Notable differentiators
- **File-native, repo-resident team** — agents and their memory are inspectable files, not opaque state.
- **Built on GitHub Copilot CLI** rather than a bespoke runtime.
- **Ralph watch mode** — autonomous issue triage with agent-delegated selection + tiered escalation.
- **Governance-as-first-class** — decision ledger + responsible-AI stance baked into the product.

### Links
- README: https://github.com/bradygaster/squad/blob/dev/README.md
- CHANGELOG: https://github.com/bradygaster/squad/blob/dev/CHANGELOG.md
- .NET package: https://github.com/bradygaster/squad/blob/dev/src/Squad.Agents.AI/README.md

---

## 2. Google — Agent Executor, Agent Substrate, Gemini Enterprise Agent Platform, ADK & A2A

> **Correction (2026-06-25):** Earlier research failed to locate Agent Executor and Agent Substrate
> via documentation search. Both are confirmed products announced in 2026. See 2a and 2b below.

### 2a. Google Agent Executor (confirmed, announced 2026)

**Core value proposition.** A distributed, harness-agnostic agent runtime for running the full agentic stack on your own data plane — MCP servers, skills, sub-agents, custom isolation, and workload policy enforcement. Designed for enterprise sovereignty: no vendor lock-in, full data residency control, self-managed compute.

| Dimension | Detail |
|---|---|
| **What it is** | A distributed runtime layer you deploy on your own infrastructure |
| **Harness support** | Harness-agnostic — bring your own or use vendor-provided; supports industry-standard frameworks and protocols |
| **Compute** | Runs on your own compute with custom isolation boundaries; designed to work with Agent Substrate for Kubernetes scale |
| **Sovereignty** | Full control over data residency, cost, and compute; prevents vendor lock-in |

Link: https://cloud.google.com/blog/products/ai-machine-learning/agent-executor-googles-distributed-agent-runtime

### 2b. Google Agent Substrate (confirmed, open source, announced 2026)

**Core value proposition.** A new open-source Kubernetes abstraction layer designed for hundreds of millions of simultaneous agents at sub-second tool-call latency — the scale where standard Kubernetes control planes break down.

| Dimension | Detail |
|---|---|
| **What it is** | A minimal control plane on top of Kubernetes, optimized for the chatter of millions of sub-second tool calls |
| **Scale** | Designed for hundreds of millions of registered agents vs. thousands of long-running services |
| **Primitives** | Pairs GKE Agent Sandbox secure runtime + pod snapshotting with a lightweight scheduler |
| **Agent Sandbox (GA)** | 300 sandboxes/second per cluster, 90% allocated in <200ms; gVisor + default-deny network policy; pod snapshots for suspend/resume; 30% better price-performance on Axion vs. comparable hyperscalers |
| **Open source** | https://github.com/agent-substrate/substrate |

Links: https://cloud.google.com/blog/products/containers-kubernetes/bringing-you-agent-sandbox-on-gke-and-agent-substrate · https://docs.cloud.google.com/kubernetes-engine/docs/concepts/machine-learning/agent-sandbox

### 2c. Gemini Enterprise Agent Platform (formerly **Vertex AI Agent Engine** / "Agent Runtime")
*(The old `vertex-ai/.../agent-engine` URL now 301-redirects to the Gemini Enterprise Agent Platform.)*

**Core value proposition.** A fully managed, serverless, **stateful production runtime** for AI
agents — deploy without managing infrastructure, with built-in memory, sessions, observability,
governance, sandboxed code execution, and evaluation. Organized into **Build → Scale → Govern → Optimize**.

| Dimension | Detail |
|---|---|
| **Primitives** | Agent Runtime (sub-second cold start), Sessions (`Events`+`State`), **Memory Bank** (LLM-extracted, consolidated, cross-session, TTL), Code Execution, Agent Identity (mTLS+DPoP), **Agent Gateway**, Agent Registry, Example Store, Evaluation Service, Managed Agents API ("Antigravity" harness) |
| **Execution** | Serverless runtime for ADK agents; Managed Agents API gives each agent an isolated sandbox with allowlisted egress |
| **HITL** | No dedicated `interrupt()`/checkpoint primitive — HITL via workflow patterns + A2A status streaming; docs urge verifying critical outputs |
| **Memory/state** | Three tiers: in-context `State` → session `Events` → **Memory Bank** (evolving, LLM-consolidated cross-session memory) |
| **Tools** | MCP servers (governed by Agent Gateway + Model Armor), A2A agent-as-tool, REST/gRPC egress, sandboxed code execution, Agent Garden templates |
| **Differentiators** | The **"Govern" pillar** (Agent Identity + Gateway + Registry) is an enterprise security layer no OSS framework matches; Memory Bank's LLM-driven consolidation evolves memory over time |

Links: https://docs.cloud.google.com/gemini-enterprise-agent-platform/overview ·
/scale/sessions · /scale/memory-bank · /govern/gateways/agent-gateway-overview

### 2b. Google Agentspace → **Gemini Enterprise**
End-user-facing enterprise AI assistant/search/agent platform for **knowledge workers** (not a
developer framework): permissions-aware enterprise search, Agent Gallery, no-code workbench,
NotebookLM Enterprise, 50+ connectors. (URL `cloud.google.com/agentspace` 301s to
`cloud.google.com/gemini-enterprise`.) Links: https://cloud.google.com/gemini-enterprise

### 2c. ADK (Agent Development Kit) + A2A (Agent2Agent)
- **ADK** — open-source, model-agnostic, **4-language** (Python/TS/Go/Java) framework. Primitives:
  `LlmAgent`, `SequentialAgent`/`ParallelAgent`/`LoopAgent`, graph & collaborative workflows
  (ADK 2.0), `AgentTool`, `Session`+`State`, `Memory`, `Artifact`, `Runner`, `Callbacks`. Explicit
  context budgeting/summarization ("treats context like source code"). https://adk.dev/
- **A2A** — open cross-vendor interop protocol (JSON-RPC 2.0 over HTTP; sync / SSE streaming /
  async push). Primitives: **Agent Card**, **Task**, **Artifact**, **Message/Parts**. Designed for
  long-running, HITL tasks; preserves agent **opacity**; now under **Linux Foundation**
  (`a2aproject/A2A`). Complements MCP (agent↔agent vs. agent↔tool).
  https://github.com/a2aproject/A2A · https://developers.googleblog.com/en/a2a-a-new-era-of-agent-interoperability/

---

## 3. OpenClaw.ai

**Site:** https://openclaw.ai · **Docs:** https://docs.openclaw.ai/ · **Repo:** https://github.com/openclaw/openclaw · MIT, Node.js, very new (~weeks old as of mid-2026)

### Core value proposition
A **self-hosted, MIT-licensed personal AI gateway**: a single Node.js **Gateway** process bridges
20+ messaging channels (WhatsApp, Telegram, Discord, iMessage, Signal, Slack, Teams, …) to AI
agents, keeping context, skills, and memory **on your own machine** rather than a walled garden.

### What it solves / positioning
"Personal AI assistant undersells it" — pitched as a company/family/team assistant: proactive
(cron jobs, reminders, background tasks), persistent 24/7 memory, open-source with a growing
skills community. Best understood as a **multi-channel routing infrastructure / executor**, not an
opinionated orchestration framework.

| Dimension | Detail |
|---|---|
| **Primitives** | **Gateway** (source of truth), **Channels** (protocol adapters), **Agent** (scoped runtime: workspace+state+sessions+auth+skills), **Binding** (most-specific-wins channel→agent routing), Skills (`SKILL.md`), Memory files, **Nodes** (iOS/Android companion apps), Plugins |
| **Execution** | Single Gateway process → binding-routed per-agent runtime → LLM (35+ providers) → tool dispatch → channel reply. Inbound steering while streaming (`/queue steer\|followup\|collect\|interrupt`) |
| **HITL** | "Safer-than-YOLO" exec auto-mode; **Skill Workshop** (agent proposes skill, human approves before write); DM allowlists; queue interrupt |
| **Memory** | Plain-markdown, file-first: `MEMORY.md`, daily `memory/YYYY-MM-DD.md`, `DREAMS.md`. Pluggable backends (builtin SQLite+vector, QMD, Honcho, LanceDB, Memory-Wiki); `memory_search` hybrid |
| **Tools/skills** | `SKILL.md` on the **agentskills.io** open standard (shared with Hermes); **ClawHub** registry with trust envelopes + security scanning |
| **Differentiators** | Lowest-friction npm install; widest channel breadth (incl. iMessage/LINE/Nostr); **mobile nodes** with camera/voice/screen; Skill Workshop HITL; provenance-rich Memory-Wiki |

> **Relationship to Agentweaver:** OpenClaw is an *execution endpoint* — products like Paperclip
> explicitly **orchestrate OpenClaw bots**. It validates the file-native skills/memory model but
> is single-gateway, channel-centric rather than a governed multi-agent team.

---

## 4. Nous Research — Hermes Agent (+ Kanban)

**Repo:** https://github.com/NousResearch/hermes-agent · **Docs:** https://hermes-agent.nousresearch.com/docs/

> **Disambiguation.** "Hermes" is overloaded at Nous: **Hermes 3/4** are *LLMs* (and Hermes 4 is
> explicitly **not recommended** as the brain inside the agent), **Hermes Agent** is a separate
> open-source *agent framework*, and **Nous Portal** is an API gateway/subscription (300+ models +
> tool gateway). "Forge" was **not found** as a Nous product. "Psyche" is a model/training project,
> not an agent/Kanban component.

### Core value proposition
A **provider-agnostic, self-hosted, self-improving** terminal-native agent (comparable to Claude
Code/Codex CLI) that gets more capable the longer it runs: it **creates reusable skills from
experience**, maintains bounded long-term memory, and can orchestrate **fleets of named sub-agents
through a durable Kanban work queue** — runnable from a $5 VPS or serverless cloud, reachable via
20+ messaging platforms.

| Dimension | Detail |
|---|---|
| **Primitives** | `AIAgent` loop, **Skills** (`SKILL.md`, progressive disclosure, `/learn` auto-authoring), **Memory** (`MEMORY.md` 2.2k / `USER.md` 1.375k chars, frozen-snapshot), **Sessions** (SQLite FTS5, `session_search`), **Cron**, **Kanban board**, `delegate_task`, **Profiles** (named agent identities) |
| **Execution** | Core loop with 15+ providers; terminal backends Local/Docker/SSH/**Daytona**/**Modal**/Singularity (serverless hibernation); 20+ gateway platforms |
| **HITL** | 3-mode shell approval (`manual`/`smart`/`off`); `/yolo` toggle w/ visual warning; `cron_mode: deny`; skill-write approval gate; hardline blocklist |
| **Memory** | Bounded frozen text memory + unlimited searchable session DB + optional Honcho user modeling; pre-write prompt-injection scanning |
| **🌟 Kanban (key differentiator)** | Durable SQLite multi-agent queue: state machine `triage→todo→ready→running→blocked→done→archived`; **named profile** workers w/ persistent memory; comments as inter-agent + human protocol; parent→child links; dispatcher reclaims crashed workers; boards/tenants/worktree workspaces |
| **Differentiators** | **Closed learning loop** (autonomously creates/improves skills); **durable named-agent Kanban** vs. ephemeral `delegate_task`; RL trajectory export (Atropos); full serverless deploy; `hermes claw migrate` imports from OpenClaw |

> **Relationship to Agentweaver:** Hermes Kanban is the **closest peer to Agentweaver's durable
> multi-agent + persistent-memory model**. Its `delegate_task` vs. Kanban distinction (RPC fork/join
> vs. durable, resumable, HITL-capable, named-agent queue) is a strong reference for Agentweaver's
> decision-inbox/run model.

Kanban docs: https://hermes-agent.nousresearch.com/docs/user-guide/features/kanban ·
Memory: …/features/memory · Architecture: …/developer-guide/architecture

---

## 5. Paperclip.ing

**Site:** https://paperclip.ing · MIT, self-hosted, Node.js + embedded Postgres

### Core value proposition
An **agent orchestration layer that turns existing agents into a governed organization** — "org
charts, budgets, goals, governance, and accountability." Paperclip **does not run the model**; it
**orchestrates other agents** (Claude Code sessions, OpenClaw bots, Python scripts, shell commands,
HTTP webhooks — anything that can receive a heartbeat).

| Dimension | Detail |
|---|---|
| **Primitives** | **Companies** (isolated tenants), **Projects** (groups of work), **Agents** (platform + adapter defining who does the work), **Tasks**, **Board** (the human/governance role), **Budgets**, **Heartbeats**, **Adapters** |
| **Execution** | Agents run on **scheduled heartbeats** and/or notifications (assigned/@-mentioned); **unopinionated about runtime** via adapters; local single Node.js process w/ embedded Postgres, or remote deploy |
| **HITL / governance** | **Board approval** gates (e.g., hiring new agents gated by default); governance & control modules constrain what agents can do to Paperclip itself |
| **Budgets** | Soft warning at 80%, **auto-pause + task block at 100%**; Board can override and resume |
| **State** | Postgres-backed; tracks work checkout, sessions, costs; complete **data isolation** across companies (dozens per deployment) |
| **Differentiators** | **Bring-your-own-agent** orchestration with org-chart/budget/governance framing; multi-company isolation; coordinates *who has work checked out*, session maintenance, cost monitoring — the orchestration "subtleties" it argues a raw agent + Trello/Asana lacks |

> **Relationship to Agentweaver:** **Closest peer to Agentweaver's governance/accountability
> framing.** Paperclip orchestrates *external* runtimes via adapters and centers budgets/board
> approval; Agentweaver (like Squad) is more file-native and repo-resident, but the
> budgets + board-approval + heartbeat model is directly relevant to the PRD.

---

# Part 2 — Industry Standards: What a Multi-Agent Orchestrator Should Do

> Five reference frameworks define the expected capability bar. Details are from official docs
> (verified live, June 2026).

## 6. LangGraph (LangChain)
- **Value prop.** A **low-level orchestration runtime** for long-running, stateful agents modeled as
  directed graphs — focused on durable execution, streaming, HITL, and persistence without hiding
  prompts/architecture.
- **Primitives.** `StateGraph` + typed **State** (per-key **reducers**), **Nodes**, **Edges**,
  `Command`, `interrupt()`, **Checkpointer** (thread-scoped), **Store** (cross-thread).
- **Execution.** **Pregel-inspired super-step** message passing; parallel nodes per super-step;
  terminates when no messages in flight.
- **HITL (gold standard).** Dynamic `interrupt(payload)` at **any line in any node**; resume with
  `Command(resume=value)`; thread-ID checkpoints; **parallel interrupts**; time-travel/replay.
- **Memory/state.** Two-tier: **Checkpointer** (thread) + **Store** (cross-thread). Backends:
  InMemory, Postgres, SQLite, Redis.
- **Tools.** Tool-agnostic; plain Python functions; optional LangChain tool abstractions.
- **Differentiators.** True durable execution, time-travel debugging, fan-out/fan-in, fine-grained
  event streaming, LangSmith observability.
- **Links.** https://docs.langchain.com/oss/python/langgraph/overview · /interrupts · /persistence ·
  https://github.com/langchain-ai/langgraph

## 7. AutoGen (Microsoft)
- **Value prop.** **Event-driven, actor-model** framework for scalable, distributed multi-agent
  systems; low-level `Core` + high-level `AgentChat` preset teams.
- **Primitives.** `AssistantAgent`, **`UserProxyAgent`** (human proxy), `RoundRobinGroupChat`,
  `SelectorGroupChat`, AgentChat `Swarm` (handoffs), termination conditions, `TaskResult`.
- **Execution.** Core: async actor message passing (Python **+ .NET** interop). AgentChat: team
  iterates by policy (round-robin / selector / handoff) until a termination condition → `TaskResult`.
- **HITL.** **`UserProxyAgent`** blocks the run for human input during a turn; or between-runs
  save/restore team state (`max_turns=1`).
- **Memory/state.** Core "memory-as-a-service"; AgentChat shared conversation context; team state
  save/load for between-run HITL.
- **Tools.** Python functions auto-converted to JSON schema; `reflect_on_tool_use`.
- **Differentiators.** Actor model for distributed agents; **Python + .NET** cross-language; multiple
  team topologies; Microsoft Research pedigree.
- **Links.** https://microsoft.github.io/autogen/stable/ · …/tutorial/human-in-the-loop.html ·
  https://github.com/microsoft/autogen

## 8. CrewAI
- **Value prop.** **Role-based** multi-agent framework — "crews" of agents with `role`/`goal`/
  `backstory` executing tasks via sequential or hierarchical process; production features baked in.
- **Primitives.** **Agent**, **Task** (`expected_output`, `context`, `guardrail`, typed output),
  **Crew**, **Process** (`sequential`/`hierarchical`), **Flow** (`@start`/`@listen`/`@router` state
  machine), **Memory**, **Knowledge Sources**.
- **Execution.** Sequential (ordered, output→context) or hierarchical (manager LLM routes tasks);
  Flows add event-driven, persisted, resumable workflows. `crew.kickoff(inputs=…)`.
- **HITL.** **Task-boundary**: `human_input=True` pauses after a task; `guardrail` validators retry;
  `step_callback`.
- **Memory (richest built-in).** Unified `Memory` with **LLM-inferred hierarchical scoping** +
  **composite recall scoring** (semantic + recency + importance); memory slices; vector backends.
- **Tools.** `BaseTool` / `crewai_tools`; per-agent or per-task; sandboxed code execution;
  `allow_delegation`.
- **Differentiators.** Role-playing personas, **YAML-first** config, sophisticated memory, Flows
  layer, CrewAI AMP enterprise (visual builders, RBAC, triggers).
- **Links.** https://docs.crewai.com/ · /concepts/tasks · /concepts/memory ·
  https://github.com/crewAIInc/crewAI

## 9. OpenAI Swarm → OpenAI Agents SDK
- **Value prop.** A **minimal-primitive, Python-first** production orchestration runtime succeeding
  the experimental Swarm — Agents, Handoffs, Guardrails plus a managed loop, HITL, sessions, tracing,
  and **native MCP**.
- **Primitives.** `Agent`, `Runner`, **Handoffs / `Agent.as_tool()`**, **Guardrails**, **Sessions**
  (SQLite/Redis/SQLAlchemy/Mongo/OpenAI Conversations), `RunState`, **`ToolApprovalItem`**,
  `function_tool`, MCP servers. *(Swarm legacy: `Agent`, `Handoff`, `context_variables`, stateless
  `client.run()`.)*
- **Execution.** `Runner.run()` managed, resumable loop (history → LLM → tools w/ approval checks →
  handoffs → repeat → append to session); sync/async/streamed.
- **HITL.** **Tool-approval-centric**: `@function_tool(needs_approval=True)` → run pauses, populates
  `interruptions`; serialize via `to_state()`; `approve`/`reject`; resume `Runner.run(agent, state)`;
  sticky decisions.
- **Memory/state.** **Sessions** prepend/append conversation history; auto-compaction; encrypted
  sessions w/ TTL.
- **Tools.** `@function_tool` auto-schema; **MCP native**; `ShellTool`/`ApplyPatchTool`;
  `Agent.as_tool()` for sub-agent delegation; parallel guardrails.
- **Differentiators.** Minimum abstractions, Python-native, MCP first-mover, realtime/voice agents,
  tracing into OpenAI eval/fine-tune, sandbox agents.
- **Links.** https://openai.github.io/openai-agents-python/ · /human_in_the_loop/ · /sessions/ ·
  https://github.com/openai/swarm · https://github.com/openai/openai-agents-python

## 10. AWS Bedrock Multi-Agent Collaboration
- **Value prop.** A **fully managed, serverless supervisor/sub-agent** orchestration service on
  Amazon Bedrock Agents — specialized collaborators coordinated by a supervisor via natural-language
  role descriptions; AWS manages infra, prompts, memory, and routing.
- **Primitives.** **Supervisor Agent**, **Collaborator Agents**, **Action Groups** (OpenAPI +
  Lambda or return-control), **Knowledge Bases** (RAG), **Guardrails**, **Aliases/Versions**, Prompt
  Templates, Traces.
- **Execution.** Hierarchical **plan-and-dispatch**: supervisor reads collaborator roles → generates
  a plan → routes subtasks → collaborators run (parallel where possible) → supervisor synthesizes.
  Console/API config, not code-first.
- **HITL.** **Return-control / confirmation** at action-group level; elicit missing info; prompt-
  template approval gates; console test harness. No programmatic `interrupt()`/`resume()` equivalent.
- **Memory/state.** Fully AWS-managed in-session + optional cross-session memory; Knowledge Bases for
  long-term facts; automatic prompt engineering.
- **Tools.** **Action Groups** (named operations, OpenAPI schema, Lambda fulfillment) + Knowledge Bases.
- **Differentiators.** Fully managed/serverless, **no-code**, natural-language routing, deep AWS
  integration, enterprise compliance (encryption/VPC/IAM/guardrails), mature versioning. Least flexible
  for custom orchestration; not open source.
- **Links.** https://docs.aws.amazon.com/bedrock/latest/userguide/agents-multi-agent-collaboration.html ·
  /agents.html · /agents-action-create.html

---

## Cross-Framework Comparison Matrix

| Dimension | LangGraph | AutoGen | CrewAI | OpenAI Agents SDK | AWS Bedrock |
|---|---|---|---|---|---|
| **Paradigm** | Graph / state machine | Actor model / group chat | Role-playing crews | Lightweight loops + handoffs | Managed supervisor hierarchy |
| **Abstraction** | Low (manual wiring) | Medium (preset teams) | Med-high (YAML) | Low-med (Python-first) | High (no-code managed) |
| **Execution** | Pregel super-steps | Async message passing | Sequential / hierarchical | Agent loop + sessions | Plan-and-dispatch (managed) |
| **HITL granularity** | Any line (`interrupt()`) | Turn / mid-run (UserProxy) | Task boundary (`human_input`) | Tool approvals (`needs_approval`) | Action-group return-control |
| **State persistence** | Checkpointer + Store | Team state save/load | Crew + Flows persistence | Sessions (SQLite/Redis/PG…) | Fully AWS-managed |
| **Memory** | Two-tier | Memory-as-a-service | Unified, LLM-scoped, composite | Session backends + compaction | Managed + Knowledge Bases |
| **Tools** | Python fns | Python fns (auto-schema) | `BaseTool` + crewai_tools | `@function_tool` + MCP | Action Groups (OpenAPI+Lambda) |
| **Parallelism** | ✅ fan-out/in | ✅ async actors | ✅ hierarchical | ✅ parallel tool calls | ✅ collaborators |
| **Languages** | Python | Python + .NET | Python | Python (JS separate) | Any (via Lambda) |
| **Open source** | ✅ Apache 2.0 | ✅ MIT | ✅ MIT | ✅ MIT | ❌ |

---

## Synthesis — Implications for Agentweaver

**Table-stakes capabilities** (present across the standards bar):
1. **Named specialist agents with role/charter** (CrewAI roles, AutoGen teams, Squad casting, Hermes profiles).
2. **A durable, resumable run/task model** with a clear state machine (LangGraph checkpoints, Hermes Kanban, Bedrock plan-and-dispatch).
3. **First-class HITL** — the strongest products gate at meaningful boundaries: any-line interrupt (LangGraph), tool approval (OpenAI), task boundary (CrewAI), board approval/budget (Paperclip), decision ledger (Squad).
4. **Two-tier memory** — session/thread-scoped short-term + curated cross-session long-term (LangGraph Store, CrewAI Memory, Google Memory Bank, Hermes `MEMORY.md`+session DB).
5. **Skills/tools on an open standard** — MCP for tools (OpenAI/Google), `SKILL.md`/agentskills.io for skills (Hermes, OpenClaw).
6. **Parallel execution + coordinator routing**.
7. **Observability & audit** — traces, decision logs, orchestration logs.

**Differentiators worth claiming (where Agentweaver's Squad-lineage is strong):**
- **File-native, repo-resident, inspectable** team + memory + decisions (vs. opaque managed state).
- **Decision Inbox / decision ledger** as an explicit governance primitive — closest analogues are
  Squad's `decisions.md`, Paperclip's board approval, and Hermes Kanban comments; none combine a
  *typed, mergeable decision inbox* with a Scribe auto-merge pass the way the Agentweaver PRD does.
- **Governance + accountability framing** (Paperclip-style budgets/board approval) layered on a
  **Copilot/.NET-native runtime** (Squad lineage + `Microsoft.Agents` ecosystem).

**Gaps to consider for the PRD:**
- HITL **granularity** — decide whether to support mid-run interrupts (LangGraph-class) or only
  boundary/decision-inbox approvals (Squad/CrewAI-class).
- **Cross-agent interop** — A2A is becoming the cross-vendor standard; MCP is the tool standard.
  Consider A2A/MCP compatibility for ecosystem reach.
- **Durable multi-agent queue** — Hermes Kanban is the strongest peer; evaluate a comparable
  durable/resumable, crash-reclaiming work queue.

---

## Appendix — Confirmed vs. Unconfirmed

| Item | Status |
|---|---|
| bradygaster/squad | ✅ Confirmed (alpha) |
| **Google Agent Executor** | ✅ Confirmed (announced 2026) — https://cloud.google.com/blog/products/ai-machine-learning/agent-executor-googles-distributed-agent-runtime |
| **Google Agent Substrate** | ✅ Confirmed (open source, announced 2026) — https://github.com/agent-substrate/substrate |
| GKE Agent Sandbox | ✅ Confirmed (GA) — https://docs.cloud.google.com/kubernetes-engine/docs/concepts/machine-learning/agent-sandbox |
| Google Gemini Enterprise Agent Platform (ex-Vertex AI Agent Engine) | ✅ Confirmed (rebranded) |
| Google Gemini Enterprise (ex-Agentspace) | ✅ Confirmed (rebranded) |
| Google ADK, A2A | ✅ Confirmed (GA; A2A under Linux Foundation) |
| OpenClaw.ai | ✅ Confirmed (MIT) |
| Nous Hermes Agent + Kanban | ✅ Confirmed (MIT) |
| Paperclip.ing | ✅ Confirmed (MIT) |
| LangGraph, AutoGen, CrewAI, OpenAI Agents SDK/Swarm, AWS Bedrock Multi-Agent | ✅ Confirmed |
