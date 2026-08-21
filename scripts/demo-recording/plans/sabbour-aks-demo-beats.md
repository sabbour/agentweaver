# sabbour/AKS Live-Repo Demo — Master Beat Plan

This is the **single committed source of truth** for the second Agentweaver demo — the
"deep demo" that runs against a **real, live, public external repository**
([`sabbour/AKS`](https://github.com/sabbour/AKS)) rather than the synthetic sandbox used by the
blueprint demo. It is parsed by `lib/beats.mjs`'s `loadBeatPlan`.

Each `## Beat X.Y — Title` heading starts a beat. `Narration: "..."` is the voiceover
script for that beat. Optional `Fresh navigation: true` and `Start URL: ...` metadata
lines let a beat explicitly force a scene-cut reload or declare the route it expects when
captured as the first beat in a session.

Every beat below is intended to be captured **live** against the deployed staging app.
**Critical continuity rule:** each beat continues from the live UI state left by the
previous beat unless its `On screen:` line explicitly calls for a navigation. Do not
restart the same flow from scratch just to reach a later screen.

### Capture handoff — 2026-08-17

Staging is running project-clone fix commit `12dcf52a29d0499035c70fe54da4903c61d29582`
(deployment tag `12dcf52`); health checks and image provenance passed.

**Exact sabbour/AKS retry:** in the already-open GitHub project dialog, select `sabbour/AKS`,
keep the fixture name `Agentweaver Demo — sabbour/AKS`, select the generated blueprint,
then choose **Create project once** and wait for the project page to load before doing
anything else. Do not click Create again while it is pending and do not delete a shared
staging project. If that fixture already exists, open it rather than creating another.

Beat ids below preserve the coordinator's final locked numbering, so some retired beats
remain folded rather than renumbered.

What makes this scenario different from the blueprint demo:

- It targets a **real external OSS repo** (`sabbour/AKS`) that carries an engineering blog,
  release notes, issue tracking, and product-roadmap work.
- It generates a **custom blueprint** for roadmap and content operations instead of
  starting from a prebuilt preset.
- It imports a set of **AKS PM skills** plus the marketplace writing skill
  `conorbronsdon/avoid-ai-writing`.
- It generates an **issue-triage workflow** that can run both on a weekly cadence and from
  a GitHub label event.
- It keeps the triage side local and read-only, and only shows the blog post's
  approve-to-open-PR gate rather than actually opening anything on `sabbour/AKS`.

## Beat 0.0 — Hand off to secure sign-in

Narration: "Agentweaver is an AI agent platform for engineering teams. It takes the recurring work — issue triage, blog posts, release notes, roadmap analysis — and runs it automatically, in the context of your actual repo and your team. Sign-in goes through your company's Microsoft Entra identity."

Fresh navigation: true

On screen: Show only the Agentweaver **Sign in with Microsoft Entra ID** handoff dialog
and hold for the narration. An agent may click Agentweaver's own button to start the
redirect; cached SSO may complete it. Cut as soon as Microsoft Entra is reached. Do not
select an account, type credentials, interact with MFA or consent, or access tokens,
cookies, profile data, or sensitive account content. A human completes any unfinished
sign-in privately and off camera before the authenticated beats begin.

## Beat 0.1 — Introduce the repo and the job to automate

Narration: "This repo runs an engineering blog, ships release notes, tracks issues, and owns a product roadmap. A lot of that work is manual right now. We're going to automate some of it."

On screen: Start on the live `sabbour/AKS` repository context inside Agentweaver and orient the viewer before continuing into project creation.

## Beat 1.1 — Generate the right blueprint

Narration: "I'll point this at the AKS repo. Instead of picking a pre-built blueprint, I'll generate one for what we actually need — managing issues and roadmap work, plus running the blog. Agentweaver reads the repo's issues, PRs, and content history to build the right team and workflows from scratch."

Fresh navigation: true

On screen: In the repo-to-project flow, point Agentweaver at `sabbour/AKS`, choose the blueprint-generation path, describe the need for issue triage, roadmap work, and blog/content management, then create the project from that generated blueprint.

## Beat 1.2 — Meet the cast and import PM skills

Narration: "Here's the team, and the skills they're starting with. Agentweaver ships a marketplace of ready-made skills — and you can import custom ones straight from any GitHub repo. This same cast sticks around for every run, so the team builds real memory over time instead of starting cold each week."

On screen: From the project you just created, open **Skills**, show the team and open the import dialog to demonstrate the capability. Hover over the dialog briefly, then close it without importing. The skill import itself runs off-camera.

<!-- Coordinator-final numbering preserved: beat 2.1 was folded away during final consolidation. -->
## Beat 2.2 — Generate the content pipeline workflow

Narration: "This workflow does the real work on the content side: watch merged PRs and releases week over week, draft a blog post on what changed, and queue it for team review before anything touches the repo. I'll generate it from a description, check the graph it built, then set it on a weekly cadence."

Fresh navigation: true

On screen: Generate the workflow from a natural-language description of a weekly content pipeline (blog post from merged PRs/releases), inspect the visual editor graph it produces, then save with a weekly schedule trigger.

## Beat 2.3 — Add the writing skill

Narration: "For the writing itself, I'll pull in a skill built to strip out AI writing tells and assign it to the writer."

Fresh navigation: true

On screen: From the same project, open **Browse marketplaces**, import `avoid-ai-writing`, and assign it to the writer role.

## Beat 3.1 — Run triage now

Narration: "I'm not waiting until Monday. I'll run triage now."

Fresh navigation: true

On screen: Trigger **Run now** on the issue-triage workflow, stay on the coordinator page, show the topology view, any reviews or requested edits, confirm with **split into subtasks**, follow the run through local completion, and end on the triage report linked to its generated PRDs. Nothing in this beat should write back to `sabbour/AKS`.

## Beat 3.2 — Draft the blog post locally

Narration: "This one's not tied to the specs we just generated — it's a standalone post on multi-agent orchestration on AKS. I'll give it a read, and if it's ready, approving right here opens a pull request directly against the repo."

On screen: Start the content-authoring task from the same project context, let the draft complete, and show the real approve-to-open-PR notification or banner without clicking through.

## Beat 4.4 — Check cluster health

Narration: "Before we wrap, here's the infrastructure behind all of this — live cluster health, quota headroom, warm pool readiness, sandbox claims, refreshing in real time. Every agent run draws from this shared pool, so the team always has capacity when work comes in — and you can see exactly how much headroom is available before it's needed."

On screen: Navigate to **Cluster** (`/projects/:projectId/cluster`), wait for cluster diagnostics to render, and hold on the health overview.

## Beat 5.1 — Outro

Narration: "You can drive the exact same workflows from your own tools. In Settings, grab the MCP server URL, then connect clients like Claude Desktop, VS Code, or Copilot CLI. Everything you saw here — generating a blueprint against a live repo, importing skills from GitHub and the marketplace, running scheduled and webhook-triggered triage, producing PRDs and blog drafts, and opening the PR workflow for review — is available through that same MCP server, in the same workspace and team context you've been using throughout this demo."

Fresh navigation: true
