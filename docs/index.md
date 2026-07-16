---
layout: page
title: Agentweaver
description: Run AI agent teams without surrendering control.
pageClass: agentweaver-home
---

<script setup>
import { withBase } from 'vitepress'
</script>

<div class="aw-home">
  <section class="aw-hero">
    <div class="aw-hero-copy">
      <p class="aw-hero-kicker">Open-source agent orchestration</p>
      <h1>Run AI agent teams without surrendering control.</h1>
      <p class="aw-hero-lede">
        Turn a goal into a confirmed outcome, a specialist-owned work graph, and one
        reviewable delivery. Watch every agent work. Steer when the plan changes.
        Nothing merges until you approve it.
      </p>
      <div class="aw-hero-actions">
        <a class="aw-button aw-button-primary" href="./guide/getting-started">Run your first team</a>
        <a class="aw-button aw-button-secondary" href="https://github.com/sabbour/agentweaver">View on GitHub</a>
      </div>
      <div class="aw-hero-proof" aria-label="Agentweaver control flow">
        <span>Outcome confirmed</span>
        <span>Specialists dispatched</span>
        <span>Human review enforced</span>
      </div>
    </div>
    <div class="aw-hero-aside" aria-hidden="true">
      <div class="aw-hero-orbit aw-hero-orbit-one"></div>
      <div class="aw-hero-orbit aw-hero-orbit-two"></div>
      <div class="aw-hero-mark">
        <span class="aw-hero-mark-node"></span>
        <span class="aw-hero-mark-node"></span>
        <span class="aw-hero-mark-node"></span>
      </div>
      <p>One system from intent to merge.</p>
    </div>
  </section>

  <section class="aw-live-proof" aria-labelledby="live-proof-title">
    <div class="aw-section-heading aw-section-heading-wide">
      <div>
        <p class="aw-section-index">The workflow is the product</p>
        <h2 id="live-proof-title">See the whole delivery system while it is running.</h2>
      </div>
      <p>
        This is the production React graph engine and run-tree control, rendered with
        synthetic Northstar data. Pan, zoom, select a task, and inspect the agent,
        model, duration, and sandbox behind it.
      </p>
    </div>
    <ClientOnly>
      <WorkflowProof />
      <template #fallback>
        <img
          class="aw-proof-fallback"
          :src="withBase('/screenshots/workflow-run-graph.png')"
          alt="Agentweaver workflow graph with a running implementation task selected"
        />
      </template>
    </ClientOnly>
  </section>

  <section class="aw-control-sequence" aria-labelledby="control-title">
    <div class="aw-section-heading">
      <p class="aw-section-index">Control starts before execution</p>
      <h2 id="control-title">Agree on the outcome. Then let the team move.</h2>
      <p>
        Agentweaver does not turn a prompt loose on a repository. The coordinator writes
        an OutcomeSpec with goal, scope, assumptions, and review criteria. Dispatch stays
        blocked until a human confirms it.
      </p>
    </div>
    <figure class="aw-proof-frame aw-proof-frame-outcome">
      <img
        :src="withBase('/screenshots/outcome-plan.png')"
        alt="Confirmed Agentweaver Outcome plan beside the generated run tree"
        loading="lazy"
      />
      <figcaption>
        <strong>A contract before code.</strong>
        <span>The confirmed outcome becomes the source of truth for every downstream agent.</span>
      </figcaption>
    </figure>
    <ol class="aw-sequence-list">
      <li>
        <span>1</span>
        <div><strong>Define the outcome</strong><p>Make success, scope, and constraints explicit.</p></div>
      </li>
      <li>
        <span>2</span>
        <div><strong>Confirm the plan</strong><p>Keep dispatch behind an intentional human gate.</p></div>
      </li>
      <li>
        <span>3</span>
        <div><strong>Follow the graph</strong><p>Watch dependencies, owners, models, and live status in one place.</p></div>
      </li>
      <li>
        <span>4</span>
        <div><strong>Review once</strong><p>Inspect assembled work before merge and memory capture.</p></div>
      </li>
    </ol>
  </section>

  <section class="aw-team-story" aria-labelledby="team-title">
    <div class="aw-team-copy">
      <p class="aw-section-index">A team shaped for the work</p>
      <h2 id="team-title">Cast specialists, not generic workers.</h2>
      <p>
        Start from a blueprint or formulate a team for the scenario. Give each agent a
        role, model, charter, and the project skills it needs. The coordinator assigns
        the work; every node still has a visible owner.
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
        <figcaption><strong>Named specialists.</strong><span>Roles and capabilities stay inspectable.</span></figcaption>
      </figure>
      <figure class="aw-proof-frame aw-team-cast">
        <img
          :src="withBase('/screenshots/casting-wizard-review.png')"
          alt="Agentweaver team casting proposal ready for review"
          loading="lazy"
        />
        <figcaption><strong>Review the cast.</strong><span>Confirm the team before it becomes project state.</span></figcaption>
      </figure>
    </div>
  </section>

  <section class="aw-evidence" aria-labelledby="evidence-title">
    <div class="aw-section-heading aw-section-heading-wide">
      <div>
        <p class="aw-section-index">Operations, not theater</p>
        <h2 id="evidence-title">Every layer remains visible and steerable.</h2>
      </div>
      <p>
        The graph is the spine. Board state, skills, memory, artifacts, telemetry, and
        cost stay connected to the same project and run.
      </p>
    </div>
    <div class="aw-evidence-stage">
      <figure class="aw-proof-frame aw-evidence-board">
        <img :src="withBase('/screenshots/project-board.png')" alt="Northstar agent task board" loading="lazy" />
        <figcaption><strong>Work stays legible.</strong><span>Intake, active runs, review, and recovery share one board.</span></figcaption>
      </figure>
      <div class="aw-evidence-stack">
        <figure class="aw-proof-frame">
          <img :src="withBase('/screenshots/skills-catalog.png')" alt="Project skills catalog with agent assignments" loading="lazy" />
          <figcaption><strong>Capabilities are explicit.</strong><span>Reusable skills are assigned to the agents that need them.</span></figcaption>
        </figure>
        <figure class="aw-proof-frame">
          <img :src="withBase('/screenshots/memories-decisions.png')" alt="Team memory with accepted decisions and pending proposals" loading="lazy" />
          <figcaption><strong>Decisions survive the run.</strong><span>Accepted context and pending proposals remain reviewable.</span></figcaption>
        </figure>
      </div>
    </div>
  </section>

  <section class="aw-observability" aria-labelledby="observability-title">
    <div class="aw-observability-copy">
      <p class="aw-section-index">Know what autonomy costs</p>
      <h2 id="observability-title">Inspect the run, not just the answer.</h2>
      <p>
        Trace model calls, agent activity, token use, AI credits, latency, and outcomes.
        Reopen a run and follow the same evidence the coordinator used.
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

  <section class="aw-final-cta">
    <div>
      <p class="aw-section-index">Agent teams you can account for</p>
      <h2>Give the agents room to work. Keep the controls that matter.</h2>
    </div>
    <div class="aw-final-actions">
      <a class="aw-button aw-button-light" href="./guide/getting-started">Get started</a>
      <a class="aw-text-link aw-text-link-light" href="./guide/">Read how Agentweaver works <span aria-hidden="true">→</span></a>
    </div>
  </section>

  <aside class="aw-alpha-note">
    <strong>Alpha software.</strong>
    Agentweaver is under active development. Expect breaking changes and rough edges;
    do not rely on it for production workloads yet.
  </aside>
</div>
