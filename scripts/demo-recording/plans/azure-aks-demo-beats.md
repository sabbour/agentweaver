# Azure/AKS Live-Repo Demo — Master Beat Plan

This is the **single committed source of truth** for the second Agentweaver demo — the
"deep demo" that runs against a **real, live, public external repository**
([`Azure/AKS`](https://github.com/Azure/AKS)) rather than the synthetic sandbox used by the
blueprint demo. It is parsed by `lib/beats.mjs`'s `loadBeatPlan`, and its rendered
narration drives `cli.mjs synthesize-beats` → capture → `sync-beat` → `assemble-final`.

Each `## Beat X.Y — Title` heading starts a beat. `Narration: "..."` is the voiceover
script for that beat. Optional `Fresh navigation: true` and `Start URL: ...` metadata
lines let a beat explicitly force a scene-cut reload or declare the route it expects when
captured as the first beat in a session.

What makes this scenario different from the blueprint demo:

- It targets a **real external OSS repo** (`Azure/AKS`), with real community issues and a
  real Docusaurus blog.
- It **generates one skill through the product UI** (issue-triage), **imports one from the
  marketplace** (`conorbronsdon/avoid-ai-writing` for content-authoring), then assigns them
  to cast agents.
- It authors a **scheduled, strictly read-only** issue-triage/dedupe workflow via
  *Generate from description*, and runs it live over real issues.
- It ships a **real blog post as exactly one pull request** against `Azure/AKS`, left
  **open and never merged** — and proves the triage side never wrote back to GitHub.

## Beat 0.1 — Introduction: a real, live external repo

Narration: "Everything in this walkthrough happens against a real, live, public repository — Azure's open-source AKS repo — not a synthetic sandbox. We'll stand up a content team, generate a new issue-triage skill in the product, import a marketplace writing skill for content authoring, have the team triage and de-duplicate real community issues, and draft a real blog post that ships as a single pull request. Along the way, we'll only open that one pull request — and we won't merge it — while the triage side stays read-only and never writes anything back to GitHub."

## Beat 1.1 — Create the project from GitHub

Narration: "We start by creating a project straight from GitHub. Paste in the Azure AKS repository, and Agentweaver analyzes it and recommends a blueprint. For this story we pick the Content Authoring blueprint, which packages a small research-and-writing team and a ready-made content workflow. A few seconds later the repo is cloned and the project is live."

Fresh navigation: true

## Beat 1.2 — Meet the cast

Narration: "Here's the team the blueprint cast for this project. A casting algorithm assigned each role a named agent from a themed universe — a lead researcher, a writer, and an editor — so the team has continuity across every run. Each agent already carries default skills: source-quality research for the researcher, and writing, editing, and fact-checking for the writer and editor."

## Beat 2.1 — Generate the issue-triage skill

Narration: "Skills are reusable guidance you can attach to an agent. Instead of importing one, we'll generate a new skill from a plain description right here in the interface. We describe an issue-triage skill: classify each open issue as a feature request, bug, or question; cluster near-duplicate feature requests into one idea; and write a structured mini-spec for each distinct idea — while staying strictly read-only. Agentweaver drafts the skill, we review it, save it, and assign it to the researcher."

Fresh navigation: true

## Beat 2.2 — Author the scheduled triage workflow

Narration: "Next we author the workflow that will run that triage. In Workflows, we choose Generate from description and type what we want in plain language: every Monday at nine, read the most recent open issues, classify and de-duplicate the feature requests, and produce a summary report plus a full mini-spec for each distinct idea — read-only, with no comments or label changes on GitHub. Agentweaver drafts the workflow graph and, because we said 'every Monday,' it automatically adds a weekly Monday schedule. We review the generated steps and the schedule trigger, then save it to the project."

## Beat 3.1 — Import the content-authoring skill

Narration: "For writing quality, we don't generate a second skill. Instead, we import a purpose-built open-source skill from the marketplace: Conor Bronsdon's MIT-licensed avoid-ai-writing repo. We add the GitHub source, browse the detected skill, import it into this project's catalog, and assign it to the writer so the draft follows concrete guidance for stripping AI writing tells instead of generic vibes."

Fresh navigation: true

On screen: Open Browse marketplaces, add the `conorbronsdon/avoid-ai-writing` source, browse the detected `avoid-ai-writing` skill, import it, then assign it to Hermione in the project's skill catalog.

## Beat 3.2 — Start the content-authoring task

Narration: "Now we put the team to work. We start a content-authoring task: write a blog post titled 'Running multi-agent orchestration frameworks on AKS.' The built-in content workflow takes it from draft, through review and editing, to a publish step. Notice we don't hand the team the blog's file convention — discovering how this repo structures its posts is part of the job, and exactly what the skill we just assigned tells it to do."

## Beat 3.3 — Review the drafted post

Narration: "When the draft lands, we read it before anything ships. The team discovered the repo's real blog convention on its own — a dated directory with a markdown file and the right frontmatter — and wrote a clear, accurate, public-facing post that doesn't overstate anything about AKS or about Agentweaver."

## Beat 3.4 — Open the single pull request

Narration: "With the draft reviewed, we open a single pull request against the live Azure AKS repository on its own clearly named branch. This is the only write we make to the repo, and we leave the pull request open instead of merging it so you can see exactly what the team produced."

## Beat 4.1 — Run the triage workflow now

Narration: "Back on the triage side, the workflow is scheduled for Mondays, but we don't have to wait. We trigger it manually with Run now, bounded to the most recent open issues so the live run finishes in a reasonable time. The weekly schedule stays active for next Monday's real run."

Fresh navigation: true

## Beat 4.2 — Review the triage results

Narration: "The run produces exactly what we asked for: a summary that classifies the issues, clusters of near-duplicate feature requests collapsed into single ideas, and a full mini-spec for each distinct idea — a problem statement, a proposal, scope and non-goals, and links back to the source issues. All of it lives inside Agentweaver as run output, ready for a human to act on."

## Beat 4.3 — Verify read-only

Narration: "And critically, we confirm the guardrail held. The triage run only read issues — it never posted a comment, changed a label, or edited anything on the repository. Read-only, start to finish."

## Beat 4.4 — See the infrastructure behind it

Narration: "Before we wrap, here's the infrastructure carrying the whole demo. The Cluster page shows the live AKS health behind this staging deployment — quota headroom for agent pods, warm pool readiness, and the current sandbox claims — and it refreshes automatically so you can watch that operational picture stay current in real time."

On screen: Navigate to **Cluster** (`/projects/:projectId/cluster`), `waitFor` real rendered diagnostics like `agent_pod_quota`, `Warm pool ready`, and the sandbox-claims table, then hold long enough for the auto-refresh countdown or refreshed timestamp to visibly change while the live quota/warm-pool numbers remain on screen.

## Beat 5.1 — Outro

Narration: "That's the full loop against a real, live, external open-source repo: we cast a content team, generated an issue-triage skill in the product, imported the MIT-licensed `conorbronsdon/avoid-ai-writing` marketplace skill for the writer, authored a scheduled read-only triage workflow, de-duplicated real community issues into actionable specs, and shipped a real blog post as a single open pull request — without ever merging it or touching a single issue. Same product, same MCP-backed workspace, now proven against the messiness of the real world."

Fresh navigation: true
