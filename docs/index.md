---
layout: page
title: Agentweaver
description: Plan, run, and review AI agent work with Agentweaver.
pageClass: agentweaver-home
---

<script setup>
import { withBase } from 'vitepress'
</script>

<div class="aw-home">
  <section class="aw-hero" aria-labelledby="hero-heading">
    <div class="aw-hero-copy">
      <p class="aw-hero-kicker">Open-source agent orchestration</p>
      <h1 id="hero-heading">Plan and review AI agent work.</h1>
      <p class="aw-hero-lede">
        Agentweaver coordinates multi-agent runs for Git repositories. Set up a project,
        choose or define an agent team, and start a run. Review its plan, follow its
        progress, and decide on the assembled result.
      </p>
      <div class="aw-hero-actions">
        <a class="aw-button aw-button-primary" href="#see-it-run">See it run</a>
        <a class="aw-button aw-button-secondary" href="./guide/getting-started">Run your first team</a>
        <a class="aw-button aw-button-secondary" href="https://github.com/sabbour/agentweaver">View on GitHub</a>
      </div>
      <div class="aw-hero-quickstart" aria-label="Quick start">
        <div class="aw-hero-quickstart-card">
          <h3>Run locally</h3>
          <CopyButton />
          <pre class="aw-hero-quickstart-code"><code>git clone https://github.com/sabbour/agentweaver.git
cd agentweaver
npm run setup
npm run dev</code></pre>
        </div>
        <div class="aw-hero-quickstart-card">
          <h3>Provision Azure infrastructure</h3>
          <CopyButton />
          <pre class="aw-hero-quickstart-code"><code>git clone https://github.com/sabbour/agentweaver.git
cd agentweaver
npm run azure:provision-infra</code></pre>
        </div>
      </div>
      <p class="aw-hero-quickstart-note">
        See the <a href="./guide/getting-started#prerequisites">getting started guide</a>
        for prerequisites and full details.
      </p>
    </div>
  </section>

  <section id="see-it-run" class="aw-live-proof" aria-label="Interactive Agentweaver scenario runs">
    <ClientOnly>
      <WorkflowProof />
      <template #fallback>
        <img
          class="aw-proof-fallback"
          :src="withBase('/screenshots/workflow-run-graph.png')"
          alt="A still frame of an Agentweaver run. Turn on JavaScript to watch it play."
        />
      </template>
    </ClientOnly>
  </section>

  <section class="aw-control-sequence" aria-labelledby="control-title">
    <div class="aw-section-heading">
      <h2 id="control-title">Review the run plan before work begins.</h2>
      <p>
        The coordinator drafts an OutcomeSpec for each coordinator run. The plan records
        the outcome, scope, constraints, and review criteria. Confirm the plan to create
        its work plan, or request a revision.
      </p>
    </div>
    <figure class="aw-proof-frame aw-proof-frame-outcome">
      <img
        :src="withBase('/screenshots/outcome-plan.png')"
        alt="Confirmed Agentweaver Outcome plan beside the generated run tree"
        loading="lazy"
      />
      <figcaption>
        <span>The plan you confirmed, beside the run tree it produced.</span>
      </figcaption>
    </figure>
    <ol class="aw-sequence-list">
      <li>
        <span>1</span>
        <div><strong>Set the goal</strong><p>Start the run from the work that you want done.</p></div>
      </li>
      <li>
        <span>2</span>
        <div><strong>Review the plan</strong><p>Read the proposed outcome, scope, and constraints.</p></div>
      </li>
      <li>
        <span>3</span>
        <div><strong>Confirm or revise</strong><p>Confirm the plan, or send feedback for a new draft.</p></div>
      </li>
      <li>
        <span>4</span>
        <div><strong>Review the result</strong><p>Review the assembled changes before you approve them.</p></div>
      </li>
    </ol>
  </section>

  <section class="aw-team-story" aria-labelledby="team-title">
    <div class="aw-team-copy">
      <h2 id="team-title">Set up a project and its team.</h2>
      <p>
        Start from a blueprint or define a team for a project. Agent definitions can include
        a role, model, charter, and project skills. The coordinator creates a work plan with
        subtasks and dependencies for coordinator runs.
      </p>
      <a class="aw-text-link" href="./experience/team-casting-memory">Explore team casting <span aria-hidden="true">→</span></a>
    </div>
    <div class="aw-team-collage">
      <figure class="aw-proof-frame aw-team-roster">
        <img
          :src="withBase('/screenshots/team-roster.png')"
          alt="Agentweaver specialist roster with the selected architect's model and skills"
          loading="lazy"
        />
        <figcaption><span>Each agent shows its role, model, and skills.</span></figcaption>
      </figure>
      <figure class="aw-proof-frame aw-team-cast">
        <img
          :src="withBase('/screenshots/casting-wizard-review.png')"
          alt="Agentweaver team casting proposal ready for review"
          loading="lazy"
        />
        <figcaption><span>Check the proposed team before you save it.</span></figcaption>
      </figure>
    </div>
  </section>

  <section class="aw-mcp-showcase" aria-labelledby="mcp-title">
    <div class="aw-section-heading">
      <h2 id="mcp-title">Connect an MCP client.</h2>
      <p>
        Agentweaver includes an MCP server for clients that support MCP. The server exposes
        tools for projects, teams, workflows, runs, memory, and workspace operations.
        You can use protected HTTP or local stdio transport.
      </p>
    </div>
    <div class="aw-mcp-panels">
      <div class="aw-mcp-panel">
        <div class="aw-mcp-panel-header">
          <span class="aw-mcp-dot aw-mcp-dot-red" aria-hidden="true"></span>
          <span class="aw-mcp-dot aw-mcp-dot-yellow" aria-hidden="true"></span>
          <span class="aw-mcp-dot aw-mcp-dot-green" aria-hidden="true"></span>
          <span class="aw-mcp-panel-title">MCP server</span>
        </div>
        <pre class="aw-mcp-panel-body"><code><span class="aw-mcp-prompt">$</span> copilot mcp add agentweaver --transport http \
    --url https://&lt;your-host&gt;/mcp --header "Authorization: Bearer &lt;token&gt;"
<span class="aw-mcp-out">agentweaver MCP server connected</span>
<span class="aw-mcp-prompt">&gt;</span> Create a "Task Tracker" project and start the coordinator on it
<span class="aw-mcp-call">● project_create({ name: "Task Tracker" })</span>
<span class="aw-mcp-ret">  → project_id: 4b1a9e…  state: active</span>
<span class="aw-mcp-call">● coordinator_start({ project_id: "4b1a9e…", goal: "…" })</span>
<span class="aw-mcp-ret">  → run_id: 9c2f31…  status: drafting</span>
<span class="aw-mcp-out">Drafted an OutcomeSpec for your review — nothing runs until you confirm it.</span></code></pre>
        <p class="aw-mcp-panel-caption">
          Read the <a href="./reference/mcp">MCP reference</a> for connection and
          authentication details.
        </p>
      </div>
      <div class="aw-mcp-panel aw-mcp-panel-chat">
        <div class="aw-mcp-panel-header">
          <span class="aw-mcp-panel-title">Assistant conversations</span>
        </div>
        <div class="aw-mcp-chat-body">
          <div class="aw-mcp-chat-msg aw-mcp-chat-user">
            <span class="aw-mcp-chat-label">You</span>
            Show the status of this project run.
          </div>
          <div class="aw-mcp-chat-msg aw-mcp-chat-assistant">
            <span class="aw-mcp-chat-label">Assistant</span>
            The Assistant can start and continue a conversation with project context.
            Each conversation has a run ID and a streamed transcript.
          </div>
        </div>
        <p class="aw-mcp-panel-caption">
          Start an Assistant conversation with an optional project ID. See the
          <a href="./deep-dive/assistant-runtime">Assistant runtime deep dive</a>.
        </p>
      </div>
    </div>
  </section>

  <section class="aw-evidence" aria-labelledby="evidence-title">
    <div class="aw-section-heading aw-section-heading-wide">
      <div>
        <h2 id="evidence-title">Keep project context with the work.</h2>
      </div>
      <p>
        Projects include a board, runs, teams, skills, memory, and decisions. Use them to
        keep the team context close to its work.
      </p>
    </div>
    <div class="aw-evidence-stage">
      <figure class="aw-proof-frame aw-evidence-board">
        <img :src="withBase('/screenshots/project-board.png')" alt="Northstar agent task board" loading="lazy" />
        <figcaption><span>Track project work on the board.</span></figcaption>
      </figure>
      <div class="aw-evidence-stack">
        <figure class="aw-proof-frame">
          <img :src="withBase('/screenshots/skills-catalog.png')" alt="Project skills catalog with agent assignments" loading="lazy" />
          <figcaption><span>Assign reusable skills to the agents that use them.</span></figcaption>
        </figure>
        <figure class="aw-proof-frame">
          <img :src="withBase('/screenshots/memories-decisions.png')" alt="Team memory with accepted decisions and pending proposals" loading="lazy" />
          <figcaption><span>Keep decisions and proposals with the project.</span></figcaption>
        </figure>
      </div>
    </div>
  </section>

  <section class="aw-observability" aria-labelledby="observability-title">
    <div class="aw-observability-copy">
      <h2 id="observability-title">See run activity and usage details.</h2>
      <p>
        The run views show coordinator and agent activity. Observability views show recent
        coordinator runs and transaction traces.
      </p>
      <ul>
        <li>Coordinator and child-agent run streams</li>
        <li>Per-agent model and AI-credit breakdowns</li>
        <li>Transaction traces for coordinator runs</li>
      </ul>
      <a class="aw-text-link" href="./experience/token-usage-monitoring">See observability and cost <span aria-hidden="true">→</span></a>
    </div>
    <figure class="aw-proof-frame aw-observability-shot">
      <img
        :src="withBase('/screenshots/observability-overview.png')"
        alt="Agentweaver observability view with model mix and AI-credit usage"
        loading="lazy"
      />
    </figure>
  </section>

  <section class="aw-final-cta" aria-labelledby="final-cta-heading">
    <div>
      <h2 id="final-cta-heading">Ready to run your first team?</h2>
    </div>
    <div class="aw-final-actions">
      <a class="aw-button aw-button-light" href="./guide/getting-started">Get started</a>
      <a class="aw-text-link aw-text-link-light" href="./guide/">Read how Agentweaver works <span aria-hidden="true">→</span></a>
    </div>
  </section>

  <aside class="aw-alpha-note">
    <strong>Alpha software.</strong>
    Agentweaver is under active development, so expect breaking changes and rough
    edges. Do not use it for production work yet.
  </aside>
</div>
