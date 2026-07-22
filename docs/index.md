---
layout: page
title: Agentweaver
description: Put a team of AI agents on real work. You approve the plan and review the result.
pageClass: agentweaver-home
---

<script setup>
import { withBase } from 'vitepress'
</script>

<div class="aw-home">
  <section class="aw-hero" aria-labelledby="hero-heading">
    <div class="aw-hero-copy">
      <p class="aw-hero-kicker">Open-source agent orchestration</p>
      <h1 id="hero-heading">Put a team of agents on the job.</h1>
      <p class="aw-hero-lede">
        The coordinator writes a plan and assigns each task to the agent that fits it.
        You approve the plan before any agent starts, then follow the run tree as the
        team works. When the work is done, it comes back to you for review.
      </p>
      <div class="aw-hero-actions">
        <a class="aw-button aw-button-primary" href="#see-it-run">See it run</a>
        <a class="aw-button aw-button-secondary" href="./guide/getting-started">Run your first team</a>
        <a class="aw-button aw-button-secondary" href="https://github.com/sabbour/agentweaver">View on GitHub</a>
      </div>
      <div class="aw-hero-quickstart" aria-label="Quick start">
        <div class="aw-hero-quickstart-card">
          <h3>Run locally</h3>
          <pre class="aw-hero-quickstart-code"><code>git clone https://github.com/sabbour/agentweaver.git
cd agentweaver
npm run setup
npm run dev</code></pre>
        </div>
        <div class="aw-hero-quickstart-card">
          <h3>Deploy to Azure</h3>
          <pre class="aw-hero-quickstart-code"><code>git clone https://github.com/sabbour/agentweaver.git
cd agentweaver
npm run azure:provision-infra</code></pre>
          <p>
            This provisions the infrastructure <strong>and performs the initial deployment</strong>
            (build, image push, deploy, and verification). For later deployments to the same
            environment, run <code>npm run azure:deploy-from-local</code>.
          </p>
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
      <h2 id="control-title">Agree on the plan before anyone starts.</h2>
      <p>
        The coordinator writes an OutcomeSpec that states what success
        looks like, what's in scope, what it assumes, and how you'll review the result.
        Agents start after you confirm the plan.
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
        <div><strong>Define the outcome</strong><p>Make success, scope, and constraints explicit.</p></div>
      </li>
      <li>
        <span>2</span>
        <div><strong>Confirm the plan</strong><p>Your confirmation starts the run.</p></div>
      </li>
      <li>
        <span>3</span>
        <div><strong>Follow the graph</strong><p>Watch dependencies, owners, models, and live status as the run moves.</p></div>
      </li>
      <li>
        <span>4</span>
        <div><strong>Review the result</strong><p>Review the team's work as one assembled result.</p></div>
      </li>
    </ol>
  </section>

  <section class="aw-team-story" aria-labelledby="team-title">
    <div class="aw-team-copy">
      <h2 id="team-title">Cast a team for the job.</h2>
      <p>
        Start from a blueprint or build your own team. Each agent gets a role, a
        model, a charter, and the project skills it needs to do its part. The coordinator
        hands out the tasks, and you can always see which agent owns each one.
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
      <h2 id="mcp-title">Drive it from any AI assistant.</h2>
      <p>
        Agentweaver ships an MCP server, so any MCP-capable client — GitHub Copilot
        CLI, Claude, Cursor — can create projects, cast teams, and start coordinator
        runs on your behalf. Prefer to stay in-app? The built-in Assistant calls the
        exact same tools, over the same MCP surface.
      </p>
    </div>
    <div class="aw-mcp-panels">
      <div class="aw-mcp-panel">
        <div class="aw-mcp-panel-header">
          <span class="aw-mcp-dot aw-mcp-dot-red" aria-hidden="true"></span>
          <span class="aw-mcp-dot aw-mcp-dot-yellow" aria-hidden="true"></span>
          <span class="aw-mcp-dot aw-mcp-dot-green" aria-hidden="true"></span>
          <span class="aw-mcp-panel-title">GitHub Copilot CLI</span>
        </div>
        <pre class="aw-mcp-panel-body"><code><span class="aw-mcp-prompt">$</span> copilot mcp add agentweaver --transport http \
    --url https://&lt;your-host&gt;/mcp --header "Authorization: Bearer &lt;token&gt;"
<span class="aw-mcp-out">✓ agentweaver added — 96 tools discovered</span>
<span class="aw-mcp-prompt">&gt;</span> Create a "Task Tracker" project and start the coordinator on it
<span class="aw-mcp-call">● project_create({ name: "Task Tracker" })</span>
<span class="aw-mcp-ret">  → project_id: 4b1a9e…  state: active</span>
<span class="aw-mcp-call">● coordinator_start({ project_id: "4b1a9e…", goal: "…" })</span>
<span class="aw-mcp-ret">  → run_id: 9c2f31…  status: drafting</span>
<span class="aw-mcp-out">Drafted an OutcomeSpec for your review — nothing runs until you confirm it.</span></code></pre>
        <p class="aw-mcp-panel-caption">
          Auto-discovered from the repo's <code>.mcp.json</code>, or registered against a
          hosted deployment — see the <a href="./reference/mcp">MCP reference</a>.
        </p>
      </div>
      <div class="aw-mcp-panel aw-mcp-panel-chat">
        <div class="aw-mcp-panel-header">
          <span class="aw-mcp-panel-title">Assistant · Sessions</span>
        </div>
        <div class="aw-mcp-chat-body">
          <div class="aw-mcp-chat-msg aw-mcp-chat-user">
            <span class="aw-mcp-chat-label">You</span>
            What's blocking the Task Tracker run?
          </div>
          <div class="aw-mcp-chat-msg aw-mcp-chat-assistant">
            <span class="aw-mcp-chat-label">Assistant</span>
            <span class="aw-mcp-call">● run_status({ run_id: "9c2f31…" })</span>
            <span class="aw-mcp-ret">  → status: awaiting_confirmation</span>
            The plan's ready — it proposes Azure Container Apps for hosting. Want me
            to walk through the OutcomeSpec, or should I ask you to confirm it now?
          </div>
        </div>
        <p class="aw-mcp-panel-caption">
          No setup — every project has an Assistant session. See the
          <a href="./deep-dive/assistant-runtime">Assistant runtime deep dive</a>.
        </p>
      </div>
    </div>
  </section>

  <section class="aw-evidence" aria-labelledby="evidence-title">
    <div class="aw-section-heading aw-section-heading-wide">
      <div>
        <h2 id="evidence-title">Every run keeps its work in one place.</h2>
      </div>
      <p>
        Open a project and its board, runs, and results are already linked. Skills,
        memory, artifacts, telemetry, and cost all attach to the same run.
      </p>
    </div>
    <div class="aw-evidence-stage">
      <figure class="aw-proof-frame aw-evidence-board">
        <img :src="withBase('/screenshots/project-board.png')" alt="Northstar agent task board" loading="lazy" />
        <figcaption><span>Intake, active runs, review, and recovery on one board.</span></figcaption>
      </figure>
      <div class="aw-evidence-stack">
        <figure class="aw-proof-frame">
          <img :src="withBase('/screenshots/skills-catalog.png')" alt="Project skills catalog with agent assignments" loading="lazy" />
          <figcaption><span>Assign reusable skills to the agents that use them.</span></figcaption>
        </figure>
        <figure class="aw-proof-frame">
          <img :src="withBase('/screenshots/memories-decisions.png')" alt="Team memory with accepted decisions and pending proposals" loading="lazy" />
          <figcaption><span>Accepted decisions and open proposals stay with the project.</span></figcaption>
        </figure>
      </div>
    </div>
  </section>

  <section class="aw-observability" aria-labelledby="observability-title">
    <div class="aw-observability-copy">
      <h2 id="observability-title">Trace every answer back to its model calls.</h2>
      <p>
        Reopen any run to see the evidence the coordinator used.
      </p>
      <ul>
        <li>Live coordinator and child-agent streams</li>
        <li>Per-agent model and AI-credit breakdowns</li>
        <li>Persistent traces, files, decisions, and memory</li>
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
    edges. Don't use it for production work yet.
  </aside>
</div>
