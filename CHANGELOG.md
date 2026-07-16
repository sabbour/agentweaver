# Changelog

All notable changes to Agentweaver are documented in this file, generated from the repository's git tag/commit history (`v0.7.0` through `v0.9.60`).

Format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/). Entries are grouped by release tag (newest first) and bucketed by commit-message prefix (`fix`, `feat`, `refactor`/`chore`, `docs`, `test`); merge commits and routine `chore(squad)` state-sync commits are omitted for readability. Regenerate with `python scripts/gen-changelog.py` if the history needs to be rebuilt.


## [v0.9.66] - 2026-07-16

### Added
- feat(web): promote Sessions to a global top-level nav item with its own collapsible section and a New Session button, no longer scoped to a project or gated behind a feature flag; adds the `/sessions` route (#346 follow-up)
- feat(workflows): wire a real GitHub webhook receiver (`POST /api/webhooks/github`, HMAC-SHA256 signature verified) as the first live external event source for the scheduled/event workflow triggers feature (#53 follow-up)
- test(aks): add Pester regression coverage for the two release-script bugs found during the v0.9.65 ship — job-state misdetection in image builds and provenance verifier pod-selector scope (#351)

### Fixed
- fix(api-harness): don't crash schema validation on null `adapterVersion`/`personaCoreVersion` for structural (non-persona) seam scenarios

### Changed
- chore: retire the dead legacy Console/Operator-dock backend (`ConsoleEndpoints`, `ConsoleTurnService`, `CopilotConsoleFacadeAgent`) — zero live callers remained after #346
- chore(release): bump version to v0.9.66


## [v0.9.65] - 2026-07-16

### Fixed
- Fix #350: tear down AgentHost pods on every cancel/fail transition, including watch-loop failures, steering stop, and cancel/delete endpoints
- Fix #348: reconcile dirty/stale-index checked-out branches after worktree merges instead of surfacing false staged deletions or silently corrupting state
- Fix #342: make provenance verification tolerate variable live pod counts and exclude Pending/Terminating pods
- fix(release): port `release.sh` to `release.ps1` and delegate image builds to `20-build-push-images.ps1` for correct provenance stamping (#340)
- fix(auth): remove the unnecessary `read:org` OAuth scope from GitHub login flows and rely on the existing public-members org fallback
- Fix #336: force inline assigned skill instructions for coordinator-dispatched pod-per-run implementation children instead of dangling materialize pointers
- chore(web): remove the legacy Operator dock and redirect its orphaned route to `/assistant` (#346)

### Added
- feat(workflows): add the `open_pull_request` workflow node with templated title/body support and draft PR creation (#49)
- feat(workflows): add `daily`/`weekly`/`monthly` schedule triggers, named events, a scheduler service, and a manual event-fire endpoint (#53)
- feat(prd): add opt-in PRD story promotion to independent backlog tasks with tracked `BacklogTaskDependency` edges (#285)

### Changed
- chore(release): bump version to v0.9.65


## [v0.9.60] - 2026-07-15

### Fixed
- fix(mcp-harness): pass raw target URL to StreamableHTTPClientTransport, not assertTargetAllowed's void return
- fix: distinguish team-workspace 404 from project-not-found in MCP error mapping
- fix(mcp): make team_cast goal/confirm_proposal_id optional in inputSchema (Fixes #344)

### Changed
- chore(release): bump version to v0.9.60


## [v0.9.59] - 2026-07-15

### Fixed
- fix(mcp): emit proper object schema for run_task's `run` property (Fixes #341)
- fix(aks): don't treat accumulated prov tags on an unchanged digest as ambiguous

### Changed
- chore(release): bump version to v0.9.59


## [v0.9.58] - 2026-07-15

### Fixed
- Fix MCP run-workflow tool schemas and error surfacing

### Changed
- chore(release): bump version to v0.9.58
- chore: append MCP stress-test harness learnings

### Other
- MCP harness: dynamic persona parity with the API harness


## [v0.9.57] - 2026-07-15

### Fixed
- Fix #336: deliver per-turn skills/memory/identity to pod-per-run agents
- fix(coordinator): bound the reply-classifier model turn and default it to a fast model (#272)
- fix(skills): make assigned-skill delivery observable in agent system prompt (#336)
- Fix agent memory tool injection for warm-pool orchestration runs (Fixes #335)
- fix(coordinator): recognize multi-clause affirmations at the outcome-spec gate (#272)
- fix(coordinator): drain orphaned outcome-spec confirm/revise deferrals (#272)

### Changed
- chore(release): bump version to v0.9.57
- chore: persist squad state and harness transcript updates
- refactor(coordinator): classify outcome-spec chat replies with the LLM, not a regex (#272)
- chore: persist harness transcripts and squad state updates


## [v0.9.56] - 2026-07-14

### Fixed
- fix(sandbox): give agents scratch space outside the worktree (#224)

### Added
- feat(coordinator): allow confirming/revising outcome spec via chat message (#272)

### Changed
- chore(release): bump version to v0.9.56
- chore(mcp): harden driver instructions, actionable errors, run_task tool (#128, #129, #130)

### Other
- Add agent memory and session list views


## [v0.9.55] - 2026-07-14

### Fixed
- fix(preview): register start_preview so observe_bound_port's hint is reachable (#334)
- fix: collapse chatty timeline micro-steps
- Fix build-subtask terminal-emission gap: recover verified child work instead of failing (#331)
- fix(coordinator): retry resumes from failure point instead of restarting lifecycle (#332)
- fix(projects): make working_directory optional when workspace provider auto-assigns paths (#333)
- fix: live-refresh orchestration artifacts and md preview default
- fix(aks): enforce provenance stamp failures
- fix(k8s): route OpenAPI through staging gateway

### Changed
- chore(release): bump version to v0.9.55

### Other
- persona-actor: cap response.body at ~1.5KB, move reasoning into thought
- harness: fix live tail being invisible -- background output must be polled and relayed
- harness: add timing-only performance summary derived from transcript ts field
- harness: reformat live tail as parsed TURN/THOUGHT lines, not raw JSONL
- harness: auto-start a live tail of the transcript for operator visibility


## [v0.9.54] - 2026-07-14

### Fixed
- fix(orchestration): surface + durably persist assembly_blocked ineligible-subtask detail (#97)
- fix(notifications): emit reserved tool_approval notification type (#321)
- fix(#319): add notification type badge to notification center dropdown
- fix(#251): wire post-deploy image provenance verification into deploy pipeline
- fix(ui): coordinator timeline UI bugs - varied step labels, outcome-spec markdown, work-plan topology thumbnail
- fix: stamp server-side UTC timestamp on every RunEvent
- fix(rai): stop raw JSON responses from leaking into the RAI verdict rationale
- fix(mcp-harness): stop applying target-guard URL validation to stdio transport; document quickstart contract
- fix(harness): route execution through discoverable skills first
- fix(harness): use canonical 'execute' tool alias instead of non-canonical 'bash'
- fix(ui-harness): enforce scoped approval execution

### Added
- feat(api-harness): drop fixed persona scenarios and curated subcommands for a dynamic, curl+OpenAPI-guided driver
- feat(harness): add persistent learnings + persona catalog memory
- feat(harness): add selectable orchestration agent
- feat(api-harness): add Copilot CLI skill
- feat: add MCP protocol test harness
- feat(ui-harness): add Playwright persona evidence driver
- feat(api-harness): support request-changes gate decisions

### Changed
- chore(release): bump version to v0.9.54

### Docs
- docs(mcp-harness): add /mcp endpoint suffix and OAuth token requirement to quickstart
- docs(harness): add target resolution + usage examples; scribe: merge fleet-mode wave decisions
- docs(harness): sharpen skill triggers
- docs(ui-harness): add Copilot CLI skill contract
- docs(mcp-harness): add Copilot CLI skill

### Other
- harness: delete orphaned approval-gate library (approvals.mjs/approval-judge.mjs)
- harness: generalize goal-statement resolution/injection out of persona-core files
- harness: record learning for drive.mjs deletion pivot
- harness: delete drive.mjs, replace with a documented curl+YAML-spec contract
- Refine Oracle core tone
- Generalize Oracle core brief
- Remove Oracle journey hints
- Thin Oracle adapter guidance
- Remove Oracle adapter tool references
- Make Oracle adapter spec-driven
- harness: drive.mjs spec prefers YAML OpenAPI by default; reinforce spec-first resolution in PersonaActor
- Add Oracle persona brief
- Enrich API OpenAPI metadata
- harness: dispatch persona driving to a fresh PersonaActor sub-agent
- harness: verify drive.mjs against Tank's live /openapi/v1.json, document operationId gap
- harness: add spec-resolved operationId dynamic client to drive.mjs call
- Add OpenAPI spec generation to Agentweaver.Api for api-harness
- harness-judge: agent-native default judge via Judge subagent (tools: [])
- Add generate-blueprint and validate-blueprint tools to API harness driver
- Add combined harness launcher skill
- Migrate persona harness to API harness


## [v0.9.53] - 2026-07-14

### Fixed
- fix(release): build image when retag source tag is absent from ACR
- fix(a2a): emit structured terminal on pod turn abort to avoid bare "Received: None" (#267)
- Fix #240: adopt durably-completed children on coordinator recovery instead of re-running them
- Fix #317: re-check durable event log before declaring agent_stall_timeout
- Fix false-positive stall: require agent.turn.end before A2A turn success
- fix(tests): derive DataMigratorTests fixture schema from real SqliteDb

### Added
- feat(harness): add shared judge package
- feat(personas): add shared persona briefs package

### Changed
- chore(release): bump version to v0.9.53

### Docs
- docs(ui-harness): add Evidence integrity & governance to Harness Agent (Seraph 4 & 5)
- docs(api-harness): clarify Finding 1 scope — allowlist is target-deployment, not in-sandbox action denial
- docs(mcp-harness): clarify Finding 1 is a host/environment allowlist, not a sandboxed-action denier
- docs(api-harness): fold Seraph Pre-Implementation security review into spec
- docs(mcp-harness): fold Seraph blocking security findings (target-host allowlist + prompt-injection threat model)
- docs(ui-harness): fold in Seraph blocking security findings
- docs(mcp-harness): mark request-changes as a hard blocking prerequisite for deep gate-review
- docs(shared): align Harness spec with canonical join-key and reproManifest
- docs(api-harness): distinguish frustration not_assessed from none
- docs(api-harness): close 5 blocking gaps from rubber-duck review
- docs(mcp-harness): split frustration 'none' from 'not_assessed' to fix aggregate math
- docs(mcp-harness): add required-capabilities contract as smoke/acceptance regression tripwire
- docs(shared): add free-text Harness invocation mode, clarify sync dispatch, fix frustration schema
- docs(shared): correct Harness Agent division of labor
- docs(shared): add Harness Agent top-level orchestrator spec
- docs(mcp-harness): driver discovers tool surface via live tools/list, never hardcodes tool names
- docs(mcp-harness): reconcile shared-package naming with API/UI specs
- docs(ui-harness): fix remaining stale scripts/persona-harness refs missed by earlier rename pass
- docs(shared): add Combined Launcher Skill spec subsection
- docs(api-harness): spec Copilot CLI skill (two-file discoverable design)
- docs(api-harness): apply three amendments to API test harness plan
- docs(mcp-harness): add GitHub Copilot CLI Skill spec section
- docs(ui-harness): add GitHub Copilot CLI Skill spec section
- docs: apply harness rename convention ({surface}-persona-harness -> {surface}-harness)
- docs(ui-harness): persona reviews/approves gates like a real operator (functional, not quality-grading)
- docs: add API test harness plan as sibling to UI/MCP harness specs
- docs(mcp): add persona-realistic gate review (validate before approving, request-changes) with scope boundary
- docs(ui-harness): state driver-acts-as-persona-only boundary (no diagnosis/interpretation)
- docs(ui-harness): parallel/autonomous driver model + explicit 4-source judge evidence
- docs(mcp): state driver-vs-judge boundary explicitly — driver simulates persona, never diagnoses
- docs(ui-harness): self-improvement loop, LLM-generated personas, frustration verdict dimension
- docs(mcp): add parallel/headless driver model + broaden judge evidence sources (AppInsights/kubectl)
- docs(mcp): bake in self-improvement loop framing, LLM-generated personas, frustration verdict dimension
- docs(ui-harness): add Cross-Harness Shared Layer (shared personas + one judge core)
- docs(mcp): add MCP test harness design spec (epic #295)
- docs: add parallel Playwright UI test harness design spec (#1 UI track)

### Tests
- test(coordinator): regression coverage for stale ineligible_subtasks redirect re-arm

### Other
- Preserve established requirements across outcome-spec revisions
- tank: history entry for persona-harness judge-gated approval driving (#1)
- persona-harness: drive approval gates via the API after judging (#1)


## [v0.9.52] - 2026-07-14

### Fixed
- fix: preserve coordinator assembly files after completion
- fix(release): push tags before GitHub release
- fix: skip malformed verdict findings
- fix: judge-automation round 2 - full transcript evidence + verdict schema validation

### Added
- feat: assemble dynamic persona brief prompts

### Changed
- chore(release): bump version to v0.9.52
- chore: ignore .worktrees/ (git worktree checkouts, not repo content)
- chore(harness): WIP safety checkpoint for persona-harness (untracked -> git-recoverable)


## [v0.9.51] - 2026-07-14

### Fixed
- fix(ui): declutter Human Review gate, add warning-tinted background
- fix: v0.9.50-rc1 batch - path-traversal hardening, pagination, notifications, backlog metrics (#261 #108 #312 #313 #208 #247 #200 #310 #302 #246 #282 #311)
- fix: batch v0.9.49-rc1 candidate - steering scope, assembly recovery, edge occlusion, scratch dirs, approval scoping (#227 #309 #308 #306 #224 #216 #278 #303)
- fix(k8s): right-size agent-host pod requests to stop MemoryPressure eviction churn (#307)
- fix(preview): retain private-session port attribution
- fix: Kata-aware bwrap passthrough (#269), steering revision-child branch mismatch (#305)
- Fix gate-scoped activity UX
- fix(coordinator-ui): UI polish for #249/#262/#277/#279
- Fix coordinator message timeline seeding
- fix(agent-runtime): only advertise team-coordination tools when registered (#268)
- fix(coordinator-ui): stale coordinator_status badge on terminal/cancelled runs (#304)
- fix(#269): install bubblewrap in AgentHost image
- fix(#256): default Sandbox:PodLocalWorkspace:ImplementationEnabled to false
- fix: fence timed-out shell and drain A2A turns (#254)
- fix: harden A2A transport recovery (#259 #267 #219)
- fix(coordinator): recover assembly RAI gates (#232 #209)
- fix: enable pod-local implementation execution (#243 #252 #253 #255 #300)
- fix(runtime): batch execution agent tool turns
- fix(web): bound session cache and stop child polling
- fix: scope tool denials to owning run (#281)
- fix(web): live-update Changes/Files, session switch cache, coordinator narration, tool status (#280, #287, #286, #299)
- fix(ui): restore approval and coordinator indicators (#274 #275 #276)
- fix: scope tool approvals to owning run (#281)
- fix(web): correct AppCard as-prop typing in TileGrid
- fix(aks): run frontend dist prebuild synchronously before parallel image builds
- fix(aks): remove frontend node_modules before ACR build
- fix(aks): harden frontend auth and image waits
- fix(frontend): prebuild dist before ACR build
- fix(frontend): use ACR secret build arg for npm auth
- fix(web): enable BuildKit Dockerfile frontend (#265)
- Fix Copilot permission rejection decisions
- Fix FlowPage agent cards rendering as unbounded raw text dumps
- Fix preview timeout cancellation handling
- Fix #257: structured declared_output_paths for coordinator conflict detection
- Fix #260: bounded auto-retry with backoff for retryable infra failures
- Fix #258: allow preview lifecycle tools through sandbox policy backend
- Fix topology node click closing panel instead of zooming
- fix(RunTimeline): stop clamping long activity step headers to one line
- fix(web): use onToggle not onOpenChange for FluentUI Accordion; support 1JS npm auth via BuildKit secret
- fix(deploy): use --legacy-peer-deps in frontend Dockerfile npm ci
- fix(timeline): open activity steps as they stream in, not just at first mount
- fix(#255): collapse package caches into sandbox home
- fix(#253): preserve repos in pruned paths (Seraph 5th re-review)
- fix(#253): bound nested-repo scan with cancellation + ignored-path pruning (Seraph 4th re-review)
- fix(#253): discover nested-repo gitlinks from filesystem, flatten deepest-first, reject residual gitlinks (Seraph 2nd re-review)
- fix(#254): worker idle backstop looser than in-pod 15m idle + eliminate per-update timer leak (Seraph re-review)
- fix: harden agent turn resiliency
- fix: make agent turns resilient to long shell commands
- Fix pod-local cache and preview path propagation
- Fix assembly gates on SMB workspaces
- fix(tests): update 13 failing tests for UI coherence migration

### Added
- feat: migrate workflow editor/graph components off copilot-fluent-system kit
- feat: migrate chat/agent thread components off copilot-fluent-system kit
- feat(board): migrate BOARD cluster off copilot-fluent-system
- feat(ui): migrate project-core pages to shared UI kit
- feat: migrate ops/system pages to shared UI kit
- feat: migrate shell cluster pages to shared UI kit
- feat: nestable AgentStep children + Composer readOnly mode
- feat: rebuild copilot surface mirroring @1js component anatomy natively
- feat: replace @1js copilot with native FluentUI chat surface
- feat: agentic progress components and @1js/fluentai copilot wiring

### Changed
- chore(release): bump version to v0.9.51
- chore(release): normalize VERSION to strict semver (0.9.50-rc1 -> 0.9.50)
- chore: bump VERSION to 0.9.45-rc1
- chore: bump VERSION to 0.9.44-rc1
- chore: bump version to v0.9.43-rc1 for Wave 2 runtime-resilience deploy
- chore: bump version to v0.9.42-rc1 for Wave 1 coordinator-ui/runtime-resilience deploy
- chore: bump VERSION to 0.9.41-rc1
- refactor(frontend): switch AKS npm auth to credprovider
- chore: bump VERSION to 0.9.40-rc1
- chore(release): bump VERSION to 0.9.37-rc1

### Docs
- docs: add hard rule - never approve preview/review gate without live-testing preview URL first
- docs: update e2e harness plan with v0.9.50-rc1 release milestone
- docs: add staging environment recovery/recreation authority to E2E plan
- docs: add operating rules to E2E harness plan
- docs: add continuous E2E harness + triage plan
- docs: explain pod-local write-back and caches (#253, #255)

### Tests
- test(e2e): make Playwright baseURL overridable via AKS_BASE_URL
- Test #264 reject wire payload serialization
- test(#255): restore npm sandbox E2E after Seraph review

### Other
- Bump version to 0.9.50-rc1
- Bump version to 0.9.49-rc1
- Bump version to 0.9.47-rc1
- Bump version to 0.9.46-rc1
- Surface failed-tool warning in collapsed clusters and failure reason/retryability on failed runs
- Polish Board intake toolbar and Dashboard stat tiles
- Replace Changes/Files modal with full-width split-view slide-in
- Add tile-grid views for Projects/Team and fix Start-task overlap
- Topology: cinematic zoom, auto-orientation, content-driven node sizing, tighter staircase spacing
- Implement pod-local implementation writeback (#253)
- Generalize pod-local execution workspaces
- Document pod-local assembly execution
- CoordinatorRun: redesign topology graph — compact pills, staircase layout, toolbar
- CoordinatorRun: flatten run tree into an aligned single-level list
- CoordinatorRun: run-wide chips, single thread, shared AI-credits, tree + header polish
- Ship v0.9.36-rc1: model catalog (#238), nested-app preview (#244), observability traces + token-breakdown (#245, #248)
- CoordinatorRun: enlarge run-summary chips and clarify the Plan chip count
- CoordinatorRun: collapse "Used N tools" groups by default in the Timeline
- CoordinatorRun: unified Messages surface (task-first tree, interleaved CLI-style activity, pinned composer)
- CoordinatorRun round 2: real native chat + agentic, rich tree, live minimap, declutter
- Complete cross-page coherence: shared UI kit + CoordinatorRun/Console reworks
- Migrate RUN/STEER/COORDINATOR panels off copilot-fluent-system
- Migrate dashboard/runs/badge components off copilot-fluent-system
- Migrate FILE/ARTIFACT/VIEWER components off copilot-fluent-system
- migrate FlowPage, ObservabilityRedirectPage, SignInPage onto shared UI kit
- migrate squad pages to shared UI kit
- Migrate web app to native FluentUI with Copilot (Day) theme


## [v0.9.35-rc1] - 2026-07-11

### Fixed
- Fix #239, #241, #243: coordinator assembly-phase resilience (v0.9.35-rc1)


## [v0.9.34-rc1] - 2026-07-11

### Fixed
- Fix #238: honor run-level model pin for ALL subtasks


## [v0.9.33-rc1] - 2026-07-11

### Fixed
- fix(coordinator): reviewer worktree fidelity (#236) + git-CLI worktree provisioning (#237)


## [v0.9.32-rc1] - 2026-07-11

### Fixed
- fix(aks): self-grant KV Secrets Officer + retry on RBAC propagation (#234)
- fix(coordinator): roster/breadth-aware outcome-spec drafter (#235)

### Changed
- chore(release): v0.9.32-rc1 (#235 outcome-spec breadth + #234 KV-RBAC)


## [v0.9.31-rc1] - 2026-07-11

### Fixed
- fix(coordinator): degrade single-eligible lockout to same-author fresh re-dispatch (#233)

### Changed
- chore(release): v0.9.31-rc1 (#233 single-eligible lockout degrade)


## [v0.9.30-rc1] - 2026-07-11

### Fixed
- fix(coordinator): reframe decomposition from minimality to outcome-completeness (#225)
- fix(rai): structured VERDICT sentinel contract for collective-assembly RAI gate (#231)
- fix(coordinator,sandbox): autopilot outcome-spec auto-confirm (#228) + transient k8s pod-claim retry (#230)

### Changed
- chore(release): v0.9.30-rc1 (#231 RAI sentinel + #225 outcome-complete decomposition + #226 steering test)

### Docs
- docs: sync decomposition (outcome-completeness) + RAI verdict contract (#225, #231)

### Tests
- test(coordinator): deterministic E2E coverage for mid-run steering queue->drain (#226)


## [v0.9.28-rc1] - 2026-07-11

### Other
- Ship v0.9.28-rc1: assembly-steering wave (#223 + cap-drop + #226)


## [v0.9.27-rc1] - 2026-07-11

### Fixed
- Fix #222: scope-independent worktree staging (stop dropping subdirectory deliverables)


## [v0.9.26-rc1] - 2026-07-11

### Fixed
- fix(pod-per-run): propagate AutoApproveTools to AgentHost via /configure (#221)


## [v0.9.25-rc1] - 2026-07-10

### Fixed
- fix(coordinator): pod-aware assembly-gate resumability probe (#220)


## [v0.9.24-rc1] - 2026-07-10

### Fixed
- Fix #218: coordinator lease heartbeat, ownership fencing, and per-project integration-build lock

### Other
- Bump version to 0.9.24-rc1 (#218 lease-heartbeat fix)
- Harden #218 lease heartbeat: make transient per-tick errors non-fatal


## [v0.9.23-rc1] - 2026-07-10

### Fixed
- fix(#217): remove app-side capacity/quota scheduler; let Kubernetes own pod scheduling

### Changed
- chore(release): v0.9.23-rc1 (#217 remove app-side capacity gate)

### Docs
- docs(#217): sync sandbox/coordinator/quota docs to K8s-owned scheduling


## [v0.9.22-rc1] - 2026-07-10

### Fixed
- fix(coordinator): deliver tool-approval gate live via heartbeat; guard child stall on pending approval (#212)


## [v0.9.21-rc1] - 2026-07-10

### Fixed
- fix(#196): forward tool-approval decisions to AgentHost pod in pod-per-run mode

### Docs
- docs(reliability): document FinalScribe + reaper creation-grace config keys (#207,#210)


## [v0.9.20-rc1] - 2026-07-10

### Fixed
- fix(coordinator,sandbox): bound final-Scribe recovery (#207) + reaper creation-grace (#210)
- Fix Azure Fluent MCP fidelity

### Other
- Ship Azure Fluent system
- Implement Azure Fluent system redesign
- Add self-contained Agent Fluent UI Kit


## [v0.9.19-rc1] - 2026-07-10

### Other
- v0.9.19-rc1: dependency-base propagation fix + UI fixes


## [v0.9.18-rc1] - 2026-07-10

### Other
- v0.9.18-rc1: decider-owned assembly steering routing (Fix-B) + worker RequireMtls drift fix


## [v0.9.17-rc1] - 2026-07-09

### Added
- feat(coordinator): resilient assembly-review loop (v0.9.17-rc1)


## [v0.9.16-rc1] - 2026-07-09

### Fixed
- fix(preview): discover app port via /proc/net/tcp{,6}; legible observe failures


## [v0.9.15-rc1] - 2026-07-09

### Fixed
- fix(preview): remove architecturally-invalid API-side sandbox reachability probe


## [v0.9.14-rc1] - 2026-07-09

### Fixed
- fix(preview): guarantee pod-IP-reachable preview URL via TCP forwarder + dynamic ports (v0.9.14-rc1)

### Docs
- docs(learnings): mark STEER1 resolved (live-proven v0.9.13-rc1); log in-place-resume follow-up


## [v0.9.13-rc1] - 2026-07-09

### Fixed
- fix(steering): reliable in-place revision recovery (v0.9.13-rc1)


## [v0.9.12-rc1] - 2026-07-09

### Added
- feat(steering+preview): unified autonomous steering + decoupled live preview (v0.9.12-rc1)


## [v0.9.11-rc1] - 2026-07-08

### Added
- feat(preview): enforce first-class live-preview provisioning in software-delivery pipeline

### Other
- Track A: durable terminal assembly events + build-test pod retention (v0.9.10-rc1)
- Run page UX fixes: deterministic tree order, outcome-spec rendering, RAI verdict cleanup, visible revision cycle


## [v0.9.8-rc1] - 2026-07-08

### Other
- Bind assembly Build & Test to a routable coordinator sandbox pod (pod-per-run)


## [v0.9.7-rc1] - 2026-07-08

### Fixed
- fix(preview-path): git in API image, RAI-before-BuildTest gate ordering, run-tree review/preview UX


## [v0.9.6-rc1] - 2026-07-08

### Fixed
- fix(runtime): inactivity watchdog for hung streaming agent turns
- fix(coordinator): root-cause fixes for stuck/failed orchestrations
- fix(e2e): point screenshot config baseURL at live staging host

### Docs
- docs(screenshots): add data-generation prerequisites so pages arent empty
- docs(screenshots): reconcile plan+spec to real app pages
- docs: add screenshot plan coverage for v0.9.5 pages
- docs: cover v0.9.5 staging wave
- docs: regenerate MCP tool index (88 -> 90 tools; skill_import wording)

### Other
- Block teamless orchestration + fix run-page review/topology/RAI UX + preview-from-build-test


## [v0.9.5] - 2026-07-07

### Fixed
- Fix coordinator run header wrapping
- Fix coordinator run state and review UX

### Changed
- chore(release): bump version 0.9.5

### Other
- Harden coordinator run and console experience
- Refine coordinator run action toolbar
- Checkpoint coordinator run polish


## [v0.9.4] - 2026-07-07

### Changed
- chore(release): bump version 0.9.4

### Other
- Polish board orchestration layout
- Polish dashboard overview
- Update overview page content
- Add product overview
- Move playwright-cli skill from .claude to .copilot


## [v0.9.3] - 2026-07-06

### Changed
- chore(release): bump version 0.9.3

### Other
- Unify and delight Create Project; fix Copilot runtime provisioning
- Ungate blueprint tabs in Projects create dialogs
- Polish console and projects UI


## [v0.9.2] - 2026-07-06

### Fixed
- fix(run-page): open the review panel when clicking "Review now" (was a no-op)
- fix(tool-approval): route approvals to the owning child subtask run id (recurrence of #196)
- fix(skills): show agent role in assignment UI; fix folder drag-drop import (ERR_ACCESS_DENIED)
- fix(dev): localhost sign-in wiring — port 5173, CORS AllowCredentials, GITHUB_AUTHORIZE_URL call-sites

### Added
- feat(run-page): responsive DAG reflow, wider session log, unhide Message coordinator, collapse low-signal events by default
- feat(orchestrations): stop and delete orchestrations from the list page
- feat(team): show assigned skills on agent detail panel

### Changed
- chore(release): bump version to 0.9.2
- chore(dev): Impeccable live-mode gating (DEV-only focus guard + inert z-index/pointer-events shims)
- refactor(metrics): extract usage-run loaders with postgres/sqlite dual path in DashboardReadService

### Docs
- docs: document v0.9.2 orchestration stop/delete + tool-approval routing + run-page UX + skills


## [v0.9.1] - 2026-07-06

### Fixed
- fix(web): unify /api base-path so GitHub sign-in works on staging and localhost
- fix(dev): align local frontend to :5173 + probe /health
- fix(web): settle completed tool calls + calm CLI-style tool rows
- fix(skills): block SSRF in skill import + review findings
- fix(web): orientation-aware SpineEdge + centered TB dag layout for coordinator graph
- fix(coordinator): consume live send safely
- fix(web): reuse shared ArtifactBrowser in session panel Changes/Files tabs

### Added
- feat(console): redesign /console as a true terminal UI (TUI)

### Changed
- chore: bump version to 0.9.1 (sign-in /api base-path fix)
- chore: bump version to 0.9.0

### Docs
- docs: v0.9.0 wave - console TUI, skills UX, artifact browser, graph, tool rows, live send

### Other
- Improve skill acquisition UX


## [v0.8.0] - 2026-07-06

### Fixed
- fix(timeline): resolve child_approval case shadowing from #50/#196 merge
- Fix skill catalog review findings: child-run injection, zip-slip hardening, stale-dir cleanup
- fix(coordinator): propagate child subtask outputs via shared worktree branches (#197)
- fix(web): replace remaining decorative glyphs
- fix(coordinator): surface steering events cross-replica so operator messages aren't lost
- fix(tool-approval): route child-subtask approvals to the owning child run id (#196)
- fix(run-page): compact header, flat run tree, denser session pane, clearer tool-call display, vertical graph
- fix(web): replace emoji/dingbats with FluentUI icons (constitution VIII)
- Fix build test gate terminal routing
- Fix build test workflow test imports
- Fix workflow save reload filtering
- Fix stale tool approval resolution states
- Fix coordinator graph viewport
- Fix overview attention links
- fix(coordinator): omit platform gates from workflow decomposition
- Fix child tool approval routing

### Added
- feat(skills): per-project skill catalog, acquisition, assignment + progressive disclosure (#51, #56)
- feat(mcp-integrations): add browser chat control console (#50)
- feat: harden blueprint and workflow generation

### Changed
- chore(release): bump VERSION to 0.8.0
- refactor(mcp-integrations): conversational TUI over reused coordinator machinery (#50)

### Docs
- docs: document v0.8.0 features
- docs: update v0.7.12 UI refinements

### Other
- Render agent/LLM/tool hierarchy in transaction trace (#166)
- Add preview-first delivery guidance
- Make blueprint generation gate-aware
- Add ReviewToTerminalAdapter stub to FakeWiring test double
- Add outcome plan phase to run console
- Rename outcome spec UI to outcome plan
- Redesign run page operator console
- Implement build test workflow gate
- Remove review policies and deprecate single-run starts
- Update catalog workflows for authored gates
- Remove coordinator agents summary
- Record coordinator cleanup decision
- Open assembly execution in session panel
- Remove dead single-run and review policy UI
- Make assembly review gates workflow-authored
- Surface coordinator session activity
- Polish new project dialogs
- Polish blueprint picker tabs and cards
- Scribe: log v0.7.12 iteration wave 2, merge decisions, archive old entries


## [v0.7.12] - 2026-07-05

### Fixed
- fix(web): keep outcome spec gate visible
- Fix stale assembly blocked latch
- fix(observability): emit App Insights model-turn telemetry
- Fix assemble-ready run artifact tabs
- Fix workflow selection empty response diagnostics
- fix(coordinator): capture final-message-only selection responses so no-delta output is not lost (#183)
- fix(coordinator): make workflow-selection turn tool-less and harden parse (#183)
- fix(web): disable tool-approval card when server resolves/expires it (#174)
- Fix App Insights workspace wiring
- Fix orchestration run remount on navigation
- fix(runs): notify clients when tool approval expires/resolves (#174)
- fix(workflows): reload freshly saved workflow so it becomes selectable (#175)
- fix(coordinator): flatten session tree, color-code status glyphs, show selected workflow
- fix(agent-host): remove duplicate build+runtime stages left by edit (#172)
- fix(workflows): use report_outcome for agent findings instead of writing report files (#170)
- fix(agent-host): align sandbox image with hosted-copilot-aks-sandbox reference (#171)
- fix(sandbox): default outbound network to enabled for new projects
- fix(network): open sandbox egress to RFC1918 ranges, keep IMDS blocked (#171)
- fix(agent-host): restore dev tools (Node 20, Python3, sudo) in AgentHost image (#171)
- fix(binder): bind review-policy revise loop to workflow start node so catalog build-test step survives (#168)
- fix(workspace): enforce integration branch git contract between subtasks (#169)
- fix(workflows): built-in catalog workflows always take precedence over stale project copies (#168)
- fix(web): remove Expand pipeline button from agent step cards (#162)
- fix(sandbox): persist sandbox info to DB so preview button survives stream eviction (#113)
- fix(dashboard): correct metric card titles and subtitles (#145)
- fix(workspace): make file tree panel scrollable (#149)
- fix(workflows): materialize default workflow so Workspace shows the dir
- fix(coordinator): honor explicit and active workflow on manual runs
- fix(web): add workflow dropdown to global Start task dialog
- fix(web): show all valid workflows in Start task dropdown
- fix(observability): use OTel App* table names and column mappings
- fix(web): add coordinator.assembly_review_preserved to EventType union
- fix(preview): pre-fill preview port from agent declaration or default to 8080 (#127)
- fix(preview): gate preview button on sandbox pod Bound phase (#126)
- fix(web): show 'review still available' instead of kicking the operator out on failure
- fix(orchestration): keep the review gate open when a coordinator run fails
- fix(sandbox): real TCP liveness check for preview and workflow-gated capability injection (#146)
- fix(coordinator): harden workflow selection parser and log raw response on failure (#151)
- Fix AppInsights metrics client initialization
- fix(orchestration): don't inject browser-preview mandate into the Coordinator run
- fix(observability): filter traces by message text in final union, not just in CTE
- fix(orchestration): guard git integration merge so only one pod assembles a run
- fix(orchestration): treat in_review with a pending review gate as intentional, not orphaned
- fix(orchestration): stop reconciler infinite loop for in_review runs with active assembly
- fix(observability): propagate run_id as telemetry dimension and include traces table in run trace query
- fix(observability): add APPLICATIONINSIGHTS_WORKSPACE_ID to api and worker deployments
- fix(sandbox): strengthen coordinator preview nudge to be explicit and assertive
- fix: exempt /api/version from GitHubOrgAuthorizationMiddleware
- Fix preview start guidance and liveness checks
- Fix AppInsights run trace correlation
- fix(diagnostics): key_vault health check uses IConfiguration, not ISecretStore
- Fix metrics card typography hierarchy
- fix: exempt /api/version from auth middleware
- fix(scripts): fix az acr import flag --registry -> --name
- fix(scripts): make install scripts fully idempotent
- fix: provision and mount mcp-api-key so Auth:ApiKey is set in production
- fix(auth): increase MCP OAuth access token TTL from 15m to 8h
- fix(metrics): lazy-initialize LogsQueryClient to prevent constructor crash

### Added
- feat(web): redesign coordinator graph UI (spine edges, card accents, minimap, zoom, session tree)
- feat(dag): remove column labels, smaller minimap, full-height panel respects left nav
- feat(dag): full-width bottom slide-in panel with session tree
- feat(dag): slide-in agent session panel with Messages/Changes/Files tabs (#173)
- feat(dag): minimap, click-to-open, pod tooltip, status top-left, no view-run button
- feat(dag): restore React Flow DAG with virtual column alignment
- feat(agent-host): full dev toolchain, sandbox manifest injection, security maintenance (#172)
- feat(web): show selected workflow + selection reason in Coordinator card (#160)
- feat(web): step detail slide-in panel on click (#161)
- feat(web): redesign orchestration page pipeline layout (#160)
- feat(coordinator): emit and persist workflow selection reasoning (#167)
- feat(web): Artifacts link in Coordinator card opens workspace file browser (#165)
- feat(web): OutcomeSpec as slide-in panel via button under Coordinator card (#164)
- feat(web): show Active badge for default workflow in task start dropdown
- feat(web): redesign coordinator steering as chat side panel (#163)
- feat(workflows): decouple trigger type from workflow definitions (#158)
- feat(workflows): teach generator and binder about the build-test gate (#157)
- feat(workflows): add mandatory build-test gate before human review (#157)

### Changed
- chore(observability): compact overview metric tiles
- chore(release): bump VERSION to 0.7.11 (workflow-selection + decompose identity fix)
- chore(release): bump VERSION to 0.7.10 (6-fix staging bundle: #174 #175 #176 #179 #180/181 #183)
- chore: bump VERSION to 0.7.9 for staging redeploy (graph UI fixes)
- chore: bump VERSION to 0.7.6
- chore: bump VERSION to 0.7.5
- chore: bump VERSION to 0.7.4
- chore: bump VERSION to 0.7.3
- chore(release): bump version to 0.7.2
- chore(workflows): rename "Default Run Workflow" to "Generic Workflow"
- chore(deploy): prefer VERSION file over git SHA for IMAGE_TAG default
- chore: bump version to 0.7.1
- refactor(workflows): remove standalone code-review workflow and harden selection parser
- chore: bump version to 0.7.0

### Docs
- docs: regenerate generated MCP references
- docs: update v0.7.11 experiences and telemetry
- docs: add repository blueprint suggestions
- docs(blueprints): document blueprint-match vs workflow-gen criteria + fix under-selection (#176)
- docs(coordinator): flat session tree, color-coded status glyphs, selected-workflow header badge
- docs: regenerate docs after workflow trigger removal (#158)
- docs: sync specs and docs to trigger-decoupling design (#158)
- docs(workflows): drop stale code-review workflow references from API.md and templates

### Tests
- test(web): cover outcome spec gate states

### Other
- Bump VERSION to 0.7.12
- Update project dialog tests
- Share new project dialog shell
- Unify blueprint panel tabs
- Align reconciler recovery test with harness failure mode
- Cover project creation blueprint flows
- Redesign new project dialogs
- Add GitHub repo blueprint suggestions
- Redesign overview dashboard
- Remove project relink feature
- Render session messages as sanitized markdown
- Clarify relink workspace boundary in UI
- Secure project relink path validation
- Redesign agent session messages
- Accept in-worktree absolute artifact paths
- Publish child run ids after launch
- Fail closed on AgentHost installation token scope
- Reject installation scope for Copilot model turns
- Thread submitting user into review model turns
- Thread user identity into selection and decompose
- revert(workflows): drop catalog-precedence change from #168
- Make build-test preview server agentic instead of static port lookup
- Keep coordinator pending during review gate
- Improve agent identity in run details
- Make workspace file tree scrollable
- Harden az acr build on Windows


## [v0.7.0] - 2026-07-01

### Fixed
- fix(workflow): pass submitting user ID to Scribe agent turn (#141)
- fix: add missing Postgres migration for AssemblyReviews table
- fix(observability): address rubber-duck review findings
- fix(observability): address rubber-duck findings — meter wiring, secret mapping, metric completeness
- fix(#95): disable Confirm/Commit buttons immediately on click to prevent double-submit
- fix(workspace): canonicalize per-project workspace root in path resolution (#90 #94)
- fix(workspace): use ref-aware API in board import picker + send ref on import (#90)
- fix(coordinator): fix double-resume destroy race for restart-resume (#88)
- fix(coordinator): persist review approval before gate clear + durable scribe spawn (#92 #93)
- fix(mcp): surface memory tool API errors (#91)
- fix(coordinator): add retry+lock-cleanup to assembly branch integration (#89)
- fix(dashboard): wire Range dropdown to both leaderboard and usage panels (#45)
- fix(metrics): accept from/to range params in dashboard endpoint (#45)
- fix(coordinator): harden restart-resume recovery and improve interrupted UX (#88)
- fix(coordinator): make assembly_blocked recoverable — steering Send/Redirect/Amend resume coordinator dispatch (#86)
- fix(coordinator): auto-resolve integration branch merge conflicts and emit event (#85)
- fix(rai): remove per-child-run RAI sub-launch — RAI runs once at coordinator assembly level (#84)
- fix(orchestration-ui): confirm outcome spec immediately updates UI (#82)
- fix(orchestration-ui): show pod chip only when execution pod is assigned (#77)
- fix(orchestration-ui): stop polling after 404 on coordinator runs for outcome-spec and work-plan (#76)
- fix(orchestration-runs): harden dispatch lock retry and stall cascade (#78)
- fix(orchestration-ui): suppress expected 404s for outcome-spec and work-plan (#76)
- Fix cleared remediation blockers
- fix(coordinator): isolate child run worktrees
- fix(workflows): preserve generated schedule triggers
- fix(workflows): preserve target repository in generation
- fix: show coordinator subtask run pills
- fix: align dashboard leaderboard columns
- fix(runs): persist human gates across replicas
- fix(runs): persist control state across replicas
- fix(review-merge): defer assembly review decisions across replicas
- fix(review-merge): defer review decisions across replicas
- fix(orchestration-runs): stop coordinator children across replicas
- fix(identity-access): persist device flow state across replicas (#34)
- fix(observability-operations): list preview sessions across replicas (#36)
- fix(workflows-automation): refresh definition registries across replicas (#38)
- fix(review-merge): serialize repository merges across replicas (#39)
- fix(identity-access): serialize GitHub token refresh across replicas (#40)
- fix(api): persist execution pod name to shared store for cross-replica graph display
- fix(api): share run-stream events across replicas via Postgres + read-through refresh
- fix(web): increase coordinator DAG node spacing so fan-out cards don't overlap
- fix(agenthost): deliver per-run worktree path to warm pods via /configure
- fix(k8s): allow kata sandbox egress to Azure-CDN Copilot endpoints
- fix: add agenthost-egress-allowlist NetworkPolicy for Copilot CLI connectivity
- fix: API resolves GitHub token and passes to /configure — remove pod KV dependency
- fix: sandbox egress — allow KV, Entra ID, and Copilot API FQDNs
- fix: API egress to agent-host port 8088 + healthz always 200
- fix: SandboxWarmPool updateStrategy OnReplenish — prevent rotation race
- fix: remove spec.env from AgentHost SandboxClaim — restore warm pool assignment
- fix: AgentHost dual-stack bind, lease TTL > probe timeout, api sessionAffinity
- fix: routing.md signals from Role.Responsibilities, not hardcoded buckets
- fix: SandboxClaim v1beta1 warmPoolRef — revert a731f70 body to v0.5.0 schema
- fix: routing.md per-agent signals, auto-sync on team creation, bigger capture form
- fix: autopilot gate on spec auto-confirm, SSE reconnect, cluster diagnostics UI
- fix: use correct v1beta1 SandboxClaim spec fields (sandboxTemplateRef + warmpool)
- fix: add DeferredDecisions migration to correct Postgres migrations project
- fix: deferred decision inbox for cross-replica coordinator confirm
- fix: pass submitting user to coordinator AI agent SetupAsync calls
- fix: don't inject AgentHost__KeyVaultUri via SandboxClaim env
- fix: initialize AgentHost Key Vault URI parsing
- fix: default AgentHost Key Vault URI for AKS deploy
- fix: guard against unsubstituted AGENTHOST_KEYVAULT_URI placeholder
- fix: copy Copilot native runtime into AgentHost image
- fix: restore AgentHost runtime-specific assets
- fix: publish AgentHost with linux-x64 runtime assets
- Fix 'Break into tasks' 400: pass run_id when file_path is null
- fix: add agent-host federated credential to setup script, remove mcp-api-key
- fix: remove mcp-api-key, fix resourcequotas RBAC
- fix: update ClusterPage test to match new DTO shape
- fix: multi-replica coordinator resume, cluster page types, button UX, log timestamps
- fix: strip coordinator sub-run suffixes in RunStoreSubmittingUserResolver
- fix(rbac): add list verb to sandboxclaims for reaper sweep
- fix(agent-host): copy Directory.Build.props in Dockerfile to fix NETSDK1152
- fix(web): add subtask.pending_capacity to EventType union
- fix: update DiagnosticsEndpointTests for RecordTickOutcome automationName param
- fix: release orphaned AgentHost pods on coordinator failure + sync user KV token to CSI SPC on sign-in
- fix: write user-scoped token at OAuth sign-in for pod shared store
- fix(recovery): GetLatestCheckpoint now delegates to the active checkpoint store
- fix(ui): friendly error + disabled buttons on run_not_active (interrupted run)
- fix(ui): show workflow picker when at least 1 manual workflow exists
- fix(build): pre-download Copilot CLI binary + CopilotSkipCliDownload=true in /src
- fix(build): use --msbuild-arg to pass ErrorOnDuplicatePublishOutputFiles to EF bundle
- fix(build): place Directory.Build.props at filesystem root for EF bundle
- fix(build): place Directory.Build.props at /tmp for EF bundle temp project
- fix(build): set MSBUILDADDITIONALCOMMANDLINEARGS for EF bundle temp project
- fix(build): copy Directory.Build.props into Docker build context
- fix(build): suppress Copilot CLI duplicate via Directory.Build.props
- fix(build): pass ErrorOnDuplicatePublishOutputFiles to EF migrations bundle
- fix(build): suppress duplicate Copilot CLI binary in API publish output
- fix(preview): keep preview button visible until environment cleanup
- fix(agent-runtime): upgrade Copilot SDK beta.2->1.0.0, disable session store
- fix(recovery): stop loser replica writing RunEvents + add Postgres advisory-lock leader guard
- fix: bypass GitHub org check for internal API key caller
- fix: authenticate internal agent loopback calls via shared API key
- fix: correct postgres FQDN and apply storageclass before PVC in deploy
- fix: postgres VNet integration — full DNS zone resource ID + conditional zonal-resiliency
- fix: idempotent keyvault create and federated credential in 15-setup-identity.sh
- fix: apply extensions.yaml for SandboxTemplate/WarmPool CRDs (v0.5.0)
- fix: agent-sandbox v0.5.0 + manifest.yaml (release.yaml renamed)
- fix: --nodepool-taints (not --node-taints) on az aks create
- fix: replica-safe web session exchange codes + copilot OAuth scope
- fix(api): inject AgentHost__UserId so agent-host uses the user's Copilot token
- fix(checkpoint): shared Postgres checkpoint store for replica-safe cross-pod resume
- fix(checkpoint): quiet the shared-volume fallback logging at startup
- fix(api): gate A2A turns on agent-host readiness (healthz) + connect-refused retry
- fix(api): make ResilientCheckpointStore survive multi-writer lock contention
- fix(k8s): raise namespace ResourceQuota for API surge headroom
- fix(sandbox): valid agent-host configmap JSON + warm pool replicas:0
- fix(sandbox): add lifecycle.shutdownPolicy=Delete and deploy-wire the agent-host image/template/warmpool
- fix(metrics): make MetricsService & DiagnosticsService provider-agnostic (BUG B)
- fix(sandbox): pivot SandboxClaim to native v1beta1 warmPoolRef contract
- fix(sandbox): correct SandboxClaim spec shape and Ready-condition readiness for v0.4.6 CRD
- fix(api): add missing WorkPlans assembly columns to Postgres migration
- fix(web): remove duplicate keepalive_url/keepaliveUrl in PortForwardSessionDto
- fix(preview): drop unsupported Istio Telemetry (AKS App Routing has no Istio CRDs) + correct NetworkPolicy gateway namespace + doc cleanup
- fix(preview): make sandbox browser-preview replica-safe (QA rejection fixes)
- fix(sandbox-preview): adopt Seraph security review for capability tokens
- fix(replica-safety): make CoordinatorSteeringQueue replica-safe via durable SteeringDirectives drain
- fix(replica-safety): Postgres-back PendingRequestStore, HeartbeatStatusStore, and web OAuth CSRF state
- fix(oauth): make MCP OAuth broker replica-safe via Postgres-backed store
- fix(blueprints): auto-roster LLM-declared bespoke roles during generation
- fix(spec-018): wire pod-per-run pod launch + real A2A client + AgentHost image
- fix(spec-018): complete provider-agnostic data layer so Postgres cutover works
- fix(oauth): match loopback redirect_uri ignoring port per RFC 8252
- fix(decisions): prevent slug-collision data loss in decision inbox + deep-dive docs
- fix(oauth): avoid untranslatable DateTimeOffset comparison in IsJtiDenied
- fix(oauth): serve AS metadata at /mcp-suffixed and OIDC well-known paths
- fix(auth): revert authenticated public_members probe - it triggers SAML 403
- fix(auth): make org public_members probe authenticated + treat GitHub rate-limit as Inconclusive
- fix(auth): exempt /api/auth/session/exchange from GitHub token middleware
- Fix MCP tool-call 401 by using stateless HTTP transport
- fix(web): drop unused ApiError import in ProjectSwitcher
- fix(runtime): point agent loopback tools at the real API binding
- fix(web): hide Repository folder field when workspace is auto-assigned
- fix(k8s): allow MCP->API ingress for OAuth JWKS validation
- fix(web): align ProjectSwitcher with gallery + two-column create dialog
- fix(infra): pin OAuth Issuer/Audience on migrate init container
- fix(infra): raise API container memory limit 2Gi -> 4Gi
- fix(api): recreate missing git worktree on orchestration recovery
- fix(web): distinguish 401 from empty in project lists
- fix(runtime): stop agent API tools hard-failing on recoverable HTTP errors
- Fix F5: replace session_token-in-URL with one-time code exchange
- fix(api): stop materializing default.yaml that duplicates built-in workflow
- fix(api): mount-readiness probe + 503 for workspace-unavailable
- fix(auth): pin OAuth issuer/audience in prod + distinguish inconclusive org re-check (Seraph T4-T7 fixes 1-2)
- fix(infra): pin Auth:Mcp:Issuer/Audience/JwksUri in mcp-deployment.yaml
- fix: bypass statx CIFS bug in PersistentVolumeWorkspaceProvider
- fix(auth): env-guard test bypass, redirect allowlist, oauth rate limit (F1-F4)
- fix: add git safe.directory wildcard for Azure File workspace mounts
- fix: auto-create project workspace directory on persistent volume
- fix: strip /api prefix from client paths, fix /docs with FileServer
- fix: API_URL default empty (same-origin), /docs redirect to /docs/
- fix: OAuth redirect double-slash causing SecurityError on history.replaceState
- fix: OAuth login button uses /auth/github/authorize not /api/auth/...
- fix: route /auth/* to API pod, fix docs SPA fallback override
- fix: network policy blocking gateway ingress, TLS cert ref, and label selectors
- fix: Dockerfile and deployment fixes for AKS
- fix: add --enable-acns, set westus2 default, add .dockerignore
- fix: rewrite 10-create-cluster.sh to match hosted-copilot-sandbox reference
- fix: remove software-specific assumption from casting prompts
- fix: comprehensive audit fixes across all subsystems (296 issues)
- fix: persist coordinator selected workflow to work plan; inject into decomposition prompt
- fix: workflow trigger evaluation - filter by trigger type at selection; event dispatch for task-added-to-ready
- fix: persist blueprint workflow set per project; filter WorkflowRegistry by allowed IDs
- fix: workflow save/default/generator - binder dry-run validation; peer_review now accepted
- fix: docs/reference/api.md — close unclosed HTML tag (VitePress build failure)
- fix: K8s probes -> /health; sandbox-claim API group extensions.agents.x-k8s.io/v1alpha1
- fix: workflow binder Agent->Agent topology + catalog runnable workflows + peer_review
- fix: backlog import DTO casing, dead approval events, revising spinner, RunCard approval indicator
- fix: inject active decisions into coordinator child worker system prompts
- fix: MCP /healthz probe endpoint, AddHttpContextAccessor, pass accessor to client
- fix: MCP /healthz probe endpoint, per-caller auth propagation, remove duplicate blueprint tool
- fix: Cilium FQDN egress allowlist for sandbox + agentweaver-sandbox base image Dockerfile
- fix: AKS docs accuracy (ASP.NET Core not nginx), Istio label, sandbox API group alignment
- fix: AKS deploy script - skip sandbox templates, identity envsubst, MCP build+secret
- fix: reset orch.phase to dispatching when assembly_changes_requested
- fix: propagate tool approval scope to sibling child runs via parent run allowlist
- fix: require distinct output filenames for parallel subtasks in decomposition prompt
- fix: align agent.intent rows with 'Used N tools' cluster header
- fix: parallel-dispatch shared-isolation subtasks without file-path serialization
- fix: show re-drafting spinner in OutcomeSpecPanel when spec already has content
- fix: improve workflow selector process-matching and set-as-default UX
- fix: revert wrong blueprint change; improve domain specificity and workflow selection
- fix: WorkspaceFilePicker snake_case fields + import from workspace page
- fix: trigger public_members fallback on any non-Member primary result
- fix: call public_members endpoint without auth header
- fix: correct 403 cause in comments/messages to SAML SSO enforcement
- fix: update OrgAccessNotGranted middleware message to reflect public_members fallback
- fix: public_members fallback for orgs with third-party app restrictions
- fix: add read:org scope to GitHub OAuth for private org membership checks
- fix: StartsWithSegments exempt prefix must not have trailing slash
- fix: wire IGitHubOrgAuthorizationService interface and github-authz HttpClient
- fix(frontend): replace Caddy with ASP.NET Core static file server
- fix(frontend): replace nginx with Caddy for SPA serving
- fix(aks): add --enable-acns for Cilium FQDN egress policy support
- fix(aks): remove Istio ambient/mesh resources; add Cilium for NetworkPolicy
- fix(catalog): fix blueprint rosters, dedupe groupings, fold GTM into PM (015-US2)
- fix(blueprint/casting): rollback, provenance, amend validation, proposal persistence, charter protection, workspace check
- fix(infra): migration upgrade path, sqlite exception handling, workflow binder validation, dynamic kanban columns
- fix(runs): persist events, request_changes response, worktree preservation, project ownership check
- fix(sandbox): isolation flag, root validation, shell validator wiring, output limits, governance YAML
- fix(coordinator): feedback loop, child run termination, stop semantics, child policy docs
- fix(mcp): SSE parsing, project/team tool field mismatches, inbox paths, URL encoding, docs
- fix(memory): tag OR semantics, filter params, missing endpoints, idempotency, tag normalization
- fix(rai): robust verdict-line parser to stop false-positive RED flags
- fix(009): always render homepage agent rail (show empty state when idle)
- fix(009): pickup-run 403 ownership, coordinator header dedup, per-agent rail
- fix(deps): patch SQLite native binary to resolve NU1903 (GHSA-2m69-gcr7-jv3q)
- fix(coordinator): grounded GOAL in spec event + coordinator-variant graph pre-confirmation
- fix(web): coordinator topology Human Review gate prompts the user
- fix(devscript): reliably kill the WSL backend API before relaunch
- fix(web): coordinator UX — child run rendering, status surfacing, assembly review, loopbacks
- fix(coordinator): surface orchestration status/reason, terminalize assembly, add topology loopbacks
- fix(web): render agent.intent like the muted "Used N tools" row
- fix(api): emit live workflow.step for executor gap nodes (dynamic graph)
- fix(web): live inline child sub-graph + node status/elapsed/message
- fix(dev): build API in WSL in start-dev.ps1 so the Linux apphost exists
- fix(web): clean 7 pre-existing test failures; surface review/merge lifecycle
- fix(008): exclude built-in agents (Scribe/Ralph/Rai) from coordinator dispatch
- Fix coordinator confirm-gate 409 race after revise
- fix(coordinator): redact child-failure reason before persistence (RAI YELLOW)
- fix(coordinator): Phase 2 smoke-test remediation — unified topology, sandbox-safe child prompt, observable failures
- fix(sandbox): guarantee run.degraded before done sentinel; agent self-correction
- fix(sandbox): emit run.degraded on sandbox denial; amber badge independent of agent self-assessment
- fix: resolve Scribe skip by falling back to workflow context and relaxing skip guard
- fix: ResumeSessionAsync must use inner.CreateSessionAsync not raw string overload
- fix: show amber Incomplete badge when report_outcome achieved=false
- fix: universe selection now dynamically sourced from backend policy
- fix: serve merged files from git tree after worktree is deleted
- fix: review card status, SSE reconnect after review, breadcrumb name
- fix: emit correct workflow step status for review outcomes
- fix: improve scribe skip diagnostics and guard logging
- fix: add list_directory to sandbox KnownFileTools allowlist
- fix: resume prior session on revision instead of creating fresh context
- fix: run cancellation and slow start
- fix: report_intent→agent.intent, reviewer SSE, memory page, run status labels, skipped inference
- fix: resolve scribe skipping by reading run context from DB instead of MAF state
- fix: circular identicon on workflow agent card
- fix: use shared AgentAvatar component on workflow agent card
- fix: review card, arc highlight, full-width buttons, scribe guard, project polling
- fix: guard useRunStream against empty runId; return empty history instead of 404
- fix: review/merge/scribe workflow step events + awaiting card style
- fix(workflow-viz): prop-based arc coordinates; add MemoriesPage
- fix(workflow-viz): loopback arcs exit/enter top/bottom center of cards
- fix(workflow-viz): orthogonal loop-back arcs, fixed height nodes, target lookup via id
- fix: EF Core SQLite DateTimeOffset ORDER BY not supported — sort client-side
- fix: PostRunScribeService LINQ DateTimeOffset bug and Scribe tool auth failure
- fix: persist run events to DB so Watch page can replay historical runs
- fix: 400 on /files when viewing Rai/Scribe sub-run watch page
- fix: 409 on /files for Completed runs and pending states on WorkflowRunPage
- fix(web): replace non-existent TaskListRegular with TaskListSquareLtrRegular
- fix(db): RunEvents table missing on existing databases
- fix: allow new Scaffolder API tools through sandbox; fix EF Core Contains; emit agent.task event
- fix(scribe): inject memory tools note programmatically; works with imported repo charters
- fix(scribe): add list_inbox, merge_inbox_entry, export_memory native tools; rewrite task prompt to use tool names
- Fix agent.system_prompt event to include charter (not just base prompt)
- fix: show agent name in workflow timeline; warn when charter missing
- fix: persist GitHub token across restarts on Linux
- fix: sort DateTimeOffset columns client-side in MemoryContextCompiler
- fix: remove New Run/Recent Runs from TeamPage, add agent selector to StartRunDialog, relative repo path
- fix: charter always applied regardless of memory compilation failure
- fix: guard null full_name on GitHub repo objects in CreateFromGitHubDialog
- fix(006): address architecture review findings
- fix: remove builtin MAF agent files from .github/agents — charters in .squad/agents/ are sufficient
- fix: enforce team_size in LLM selection instruction
- fix: tighten analyze tab layout, remove tabContent minHeight
- fix: replace emoji with Fluent UI icons in casting wizard
- fix(catalog): update team templates per product direction
- fix(ui): remove model ID field; universe as dropdown in casting wizard
- fix(ui): show project name in Team page breadcrumb
- fix(ui): remove origin badge from project card
- fix: force HTTP/1.1 on all GitHub API clients
- fix: scribe is a built-in agent, not castable
- fix: accept both camelCase and snake_case request_id in tool.approval_required reducer
- fix: review button two-row layout + rename to Commit and Merge
- Fix SSE recovery hang, wire report_intent as agent.intent, refine artifact browser UI
- fix(test): update ArtifactBrowser diff test for table-based DiffViewer
- fix: tree single-icon per file, diff line numbers + filename header, cancel silently
- fix(web): remove filter tabs re-added by trinity-tree agent; keep folder tree and icons
- fix(web): auto-scroll center, remove filter tabs, wider diff panel, fix icon names
- fix: segment-encode file paths in diff requests and reset state on runId change
- fix: inject workingDirectory for PermissionRequestShell in general handler path
- fix(ui): separate tools list from system prompt in debug card
- fix: Copilot shell allow in direct mode + Foundry report_intent prompt + expandable system_prompt
- fix: restore Copilot tool visibility + allow run_command in direct mode
- fix: inject system prompt into Copilot runner; Foundry tool aliases
- fix: tool name aliases + direct mode shell + stronger system prompt
- fix: policy reads from original repositoryPath not worktree; UI + types
- fix: wire direct mode end-to-end (test repo settings + UI + types)
- fix(web): strip BOM and normalize LF in all web source files
- fix: bwrap /workspace mount, governance denial logging, report_intent icon
- fix(start-dev): write bash script with LF line endings (no \r in cd path)
- fix(start-dev): fix WT semicolon splitting and checkpoint lock
- fix(security): address Seraph MEDIUM findings from Phase 6 review
- fix: harden bwrap sandbox, fix test races, normalize Foundry tool path aliases
- fix(linux): use MxcSdk.GetPlatformSupport() for Linux backend detection
- fix(linux): detect bwrap + bundled lxc-exec on native Linux host
- fix(sdk): roll forward global.json to latestMajor from 10.0.100
- fix(web): re-mount ToolClusterRow on turn completion to trigger collapse
- fix(wsl): resolve bundled lxc-exec via WSL2 mount path before executing
- fix(mxc): remove bundled bfscfg.exe — causes OS hang on Win11 25H2
- fix(mxc): skip base-container tier, fall through to WSL2 on Win11 25H2
- fix(mxc): revert to schema 0.4.0-alpha (AppContainer, no ViVeTool keys needed)
- fix(web): group tool clusters as they arrive within each turn
- fix(web): run_command shows command (not working dir) + collapse tool groups
- fix(copilot): proper governance for PermissionRequestCustomTool (post-review)
- fix: Copilot custom tools, mxc schema, denial reason transparency
- fix(copilot): mark built-in override tools with overridesBuiltInTool flag
- fix(web): hide trivial 'ok' results — no expand for report_intent and similar
- fix(web+sandbox): clean up timeline display and fix exit code indicator
- fix(sandbox): keep DeniedPaths empty — rely on allow-list for containment
- fix(web): flat tool call rows, no turn dividers, report_intent shows intent text
- fix(web): compact run timeline — reduce vertical spacing between turns/steps
- fix(runners): address post-review security and architecture findings
- Fix all security/architecture review findings (F1-F10)
- fix(security): validate and canonicalize repository_path on run submission
- fix(api): return 400 (not 500) for invalid run-submission inputs
- fix(workflow): build a fresh MAF Workflow per run to avoid single-use ownership error
- fix(worktree): delete worktree dir and prune before removing the branch
- fix(merge): apply approved merge to the working tree and loop blocked merges back to review
- fix(sandbox): add list_directory tool and accept "." as the sandbox root
- fix(runtime): emit run.completed once, from the watch loop
- fix(runtime): suppress SDK-internal tool events from the run stream
- fix(web): align review panel border with timeline cards; add MAF Workflows package
- fix(foundry): close sandbox escape — replace StartsWith with dual-layer governance
- fix(streaming): stream Copilot agent output live and gate replay by owner
- fix: use AzureOpenAIClient for Foundry endpoint + restore ME.AI 10.5.1
- fix: harden run endpoints against unhandled exceptions
- fix: suppress OperationCanceledException on SSE client disconnect
- fix: pass RepositoryPath as WorkingDirectory to Copilot session
- fix: pass SessionConfig with PermissionHandler.ApproveAll to AsAIAgent
- fix: replace SSE stream with polling in frontend
- fix: add result column migration for existing DBs
- fix: validate Foundry config lazily, not at startup
- fix: update .gitignore to include appsettings.Development.json
- fix: address all post-implementation code-review findings
- fix: WorktreeService repo-root resolution and branch placeholder
- fix: 415 Content-Type header and Swagger 404

### Added
- feat(observability): wire TransactionTracePanel to AppInsights distributed traces
- feat(observability): v0.7 observability UI — traces, model panels, agent breakdown (#44, #46, #117, #118, #119)
- feat(observability): add run throughput metrics for dashboard widgets (#106)
- feat(web): surface cost everywhere + fix DAG card overlap
- feat(k8s): open sandbox egress to all public domains/ports for research agents
- feat: pod-per-run mode + distributed coordinator lease
- feat: AgentHost warm pool with deferred /configure and runtime KV token fetch
- feat(019): frontend token and AIC usage UI
- feat(019): backend token usage store, projection service, API endpoints, metrics, MCP
- feat(019): capture AIC and token usage from AssistantUsageEvent
- feat(agent-host): add CSI-mounted token store (Option B)
- feat(k8s): Option B CSI user-token delivery for agent-host pods
- feat(spec-006): capacity-pending retry + full diagnostics health suite
- feat(web): add Automation column, Cluster page, and ClusterPage tests
- feat: surface PendingCapacity, run_not_active detail, and detailed diagnostics in the UI
- feat(sandbox): reap orphaned agent pods, quota pre-check, failure reasons
- feat: track automation name in heartbeat ring buffer
- feat(coordinator): allow manual workflow override when starting orchestration
- feat(gallery): declutter GitHub repo selector — sort, no description, URL field
- feat(board): Active after Ready, Problems in own area
- feat(mcp): expose start_preview as an MCP tool
- feat(api): agent-initiated start_preview tool with HITL approval gate
- feat(agents): auto-generate and materialize the Copilot agent definition
- feat(sandbox-preview): self-identifying -preview host label
- feat(sandbox-preview): bound preview target port to gateway ingress range
- feat(runs): advertise browser-preview capability to spawned agents
- feat(sandbox): Gateway-direct browser preview reverse-proxy leg
- feat(preview): keepalive ping, no-referrer security, keepalive_url DTO field
- feat(auth): store GitHub access + refresh tokens in Key Vault behind ISecretStore abstraction
- feat(agenthost): read GitHub token from shared RWX store for pod-per-run
- feat(ui): show executing pod name on agent boxes (K8s only)
- feat(spec-018): close P1.5 A2A round-trip gaps for pod-per-run PoC
- feat(spec-018): P2 Postgres data layer + P3 web/worker split & run leasing
- feat(spec-018): P1 agent execution in sandbox pods via A2A
- feat(api): expose workspace_auto_assigned on /api/server/info
- feat(runtime): let all agents read decisions and memory mid-run
- feat(web): make the header above a tool cluster collapse it
- feat(web): two-stage org/repo picker in Create-from-GitHub dialog
- feat(api): add GET /api/github/accounts and account-scoped repos
- feat(web): allow zoom-in up to 200% on workflow surfaces
- feat(web): group Workflows page into Active / Available / Invalid
- feat(auth): OAuth dynamic client registration (RFC 7591 / T5)
- feat(auth): rotating refresh tokens, MCP resource-server JWT + per-user identity (T4,T6,T7)
- feat(qa): MCP OAuth 2.1 — S1-S5 test scenarios + GitHubTokenAuthMiddleware test bypass
- feat(api): MCP OAuth 2.1 Authorization Server T1-T3 (metadata, JWKS, PKCE authorize/token)
- feat(infra): wire MCP OAuth 2.1 AS/RS routes, signing key, and env vars
- feat: add VitePress docs build to frontend image, serve at /docs
- feat: replace API key auth with GitHub OAuth token validation
- feat: sandbox preview port-forward proxy (backend service + endpoint)
- feat: prefer catalog roles in workflow/blueprint generation; allow bespoke with inline charter
- feat: make tool approval more prominent — warning styling, sticky banner, graph badge
- feat: implement peer_review node executor as agent binding
- feat: implement spec 017 AKS deployment amendments
- feat(workflows): new workflow from scratch — blank canvas, save, coordinator-selectable (015-US9)
- feat(blueprints): library-first workflow matching + IWorkflowGenerator fallback (015-FR062/063)
- feat(workflows): visual execution-graph workflow editor (015-US8)
- feat(mcp): workflow_generate, workflow_save, blueprint_generate MCP tools (015-FR064/065)
- feat(workflows): LLM workflow generation from natural-language description (015-US10)
- feat(workflows): workflow graph visualization on WorkflowsPage (015-US6)
- feat(blueprints): add AI Agent Engineering + Platform SRE blueprints (015-US4)
- feat(sandbox): Kubernetes-native sandbox execution via SandboxClaim warm pool (017-US2)
- feat(coordinator): replace ObserveChildAsync Task.Delay polling with IRunEventStream push (016-US2)
- feat(workflows): YAML workflow editor — edit and save workflows in-product (015-US7)
- feat(events): retire 10k cap and eviction machinery from RunStreamStore (016-US3)
- feat(storage): Azure Disk PVC for SQLite + Azure Files PVC for workspace (017-US5)
- feat(security): Istio ambient mTLS — PeerAuthentication STRICT + AuthorizationPolicies (017-US3)
- feat(events): introduce IRunEventStream with SQLite write-through + Channel pub/sub (016-US4)
- feat(workflows): generalize RunWorkflowGraphBinder with open executor factory (015-US1)
- feat(ui): add spec-to-backlog UI — OutcomeSpecPanel + KanbanBoard import (014-UI)
- feat(backlog): add spec-to-backlog decompose endpoint + workspace files API (014-backend)
- feat(aks): add AKS deployment manifests, Dockerfiles, and scripts (017-US1)
- feat(mcp): add backlog_decompose_spec MCP tool (014-MCP)
- feat: shared orchestration worktree for multi-agent coordinator runs
- feat: backlog Kanban + workflow engine, metrics/diagnostics dashboards, IA shell rework, sandbox & casting fixes
- feat(coordinator): steering-based recovery for parked/failed runs + board/graph UX
- feat(009): backlog/ready Kanban board, coordinator pickup, and run retrigger
- feat(coordinator): surface scribe/assembly work, team memory, filesystem browse, and terminal-state UI polish
- feat(coordinator): resilient checkpoints, assembly recovery, and live run-graph polish
- feat(web): Autopilot + auto-approve-tools toggles + audit timeline entries
- feat(coordinator): Autopilot (questions-only auto-answer) + auto-approve-tools run options
- feat(web): inline answer + permission affordances for bubbled questions
- feat(coordinator): ask_question tool — blocking HITL clarification + child-question bubbling
- feat(008): GitHub OAuth token refresh
- feat(008): implement Phase 3 collective assembly
- feat(008): node_type taxonomy + unified coordinator graph view
- feat(008): dynamic workflow graph descriptor (built at construction, not reflected)
- feat(008): child run identity, events endpoint, and timeline seed
- feat(coordinator): Phase 2 surface — steering runtime, Web topology view, MCP parity, HTTP endpoints
- feat: intent as timeline system message; fix useArtifactBrowser commitMessage
- feat: stream race fix + commit message in review panel
- feat: add Browse files button to Merge step card
- feat: restyle report_intent as system message row
- feat: workflow card polish — model name, revise status, modal fixes, faster file refresh
- feat: open execution stream in modal instead of navigating away
- feat: show live agent.intent text on workflow card instead of static placeholder
- feat: add per-card runtime timers to workflow diagram
- feat: separate workflow_run from execution
- feat(workflow-viz): agent role from team, variable card heights, scribe memories link, bolder arc labels
- feat(workflow-viz): role labels on cards, larger card height, back-to-workflow from watch
- feat: replace hand-rolled pipeline with React Flow + dagre diagram
- feat: Rai RED routes to Review; add Review-to-Agent return arc
- feat: Rai REVISE + Review RequestChanges retrigger loop
- feat(web): restyle Rai feedback arc to neutral connector style, connect into Agent card top
- feat(web): replace inline diagonal with red L-arc above pipeline cards for Rai feedback loop
- feat(web): replace conditional feedback banner with always-visible return arc on Rai connector
- feat(web): move rejection indicator onto connector arrow between Agent and Rai/Review
- feat(web): add feedback loop arc and fix ArrowRightRegular crash
- feat(web): redesign WorkflowRunPage to pipeline graph style
- feat: expose Scaffolder API ops as first-class agent tools
- feat: persist run events, review MAF step, delete runs endpoint
- feat: start run goes to workflow view; remove Watch button; add delete run
- feat: stream Rai and Scribe agent execution to their own sub-streams
- feat: render MAF workflow stages as visual pipeline bar in console
- feat: workflow step events for MAF run visualization
- feat: RaiAIAgent + ScribeAIAgent subclass CopilotAIAgent; charters read dynamically
- feat: split built-in agents into separate system section on TeamPage
- feat(006): block charter edits for built-in system agents
- feat(006): built-in agent guards, pixel-art avatars, MCP memory tools, updated docs
- feat(006): implement Scribe as IAgentRunner MAF workflow step
- feat: implement spec 006 - Memory and Decision Inbox
- feat(007): retire Scaffolder.Cli, register MCP server, add mcp.md docs
- feat(mcp): implement all 22 MCP tools (phases 3-7)
- feat(mcp): ScaffolderApiClient + SseClient
- feat(mcp): scaffold Scaffolder.Mcp project
- feat(squad): write squad-agentweaver.agent.md on team confirm (FR-015-020)
- feat: replace CLI with MCP server — constitution v1.5.0 + spec 007
- feat: AgentName on Run, charter as system prompt, project-scoped run endpoints
- feat(web): New Run dialog with agent picker and runs list on TeamPage
- feat: ScaffolderAgentRuntime helper with session serialization + spec 006 SessionContext update
- feat: SDK alignment -- config.json, identity files, gitignore, decisions/history format, Coordinator section
- feat: provision RAI policy + audit trail, add description to team.md
- feat: provision Scribe, Ralph, Rai as MAF agents on team confirm
- feat: seed history.md, routing.md, fix gitattributes, scaffold squad directories
- feat: fix AddMemberDialog to use full catalog roles
- feat: add GET /api/catalog/roles endpoint
- feat: add team rationale to LLM output and CastProposalDto
- feat: use proposal.rationale for Why this team display
- feat: show per-member justifications in rationale, remove redundant required roles input
- feat: add team size to Analyze tab, wire team_size field to API
- feat: add team_size parameter support to free_text and analysis casting modes
- feat: replace Configure tab with shared roles checkboxes section
- feat: add manual casting mode with explicit role selection
- feat: add Configure tab for manual role selection in casting wizard
- feat: restructure cast step with rationale, collapsible universe, team size and roles
- feat: move universe selection to review step with re-cast on change
- feat: rework cast step to tabbed layout, universe dropdown, rename CTAs
- feat: redesign team page with card grid and agent detail panel
- feat: add charter timestamps to TeamMemberDto and history endpoint
- feat(ui): redesign casting wizard as single-page form
- feat(catalog): add Azure Feature Delivery team template
- feat: agent team casting (feature 005)
- feat(005): plan Agent Team Casting; amend constitution to v1.4.0 (Copilot-only)
- feat: Allow tool scope, approval persistence fix, docs parity backfill
- feat: tool cluster expand UX, font hierarchy, report_outcome self-assessment
- feat: HITL approval scopes, Fluent 2 UI, 409 fix, DESIGN.md, sandbox system prompt
- feat: B3 request-changes feedback loop + review UX + stream hardening
- feat: per-file line counts, content endpoint, derivedRunStatus fix
- feat(web): flat changes list, improved files tree, syntax-highlighted viewer
- feat(web): two-tab artifact panel, file viewer modal, remove right diff panel
- feat(web): restore filter tabs in FileTreePanel tree view
- feat(web): three-panel horizontal layout for artifact browser
- feat(FR-034-041): implement artifact browser feature
- feat(artifact-browser): add artifact browser to Web UI and CLI (FR-041, SC-016)
- feat(ui): tools as separate card; agent.tools event
- feat: full debug info in agent.system_prompt event; Copilot prompt injection
- feat(ui): show literal tool call args in expanded ToolCallCard
- feat: emit agent.system_prompt event for debugging; stop overriding Copilot tools
- feat: add direct execution mode (direct: true in .scaffolder/settings.yml)
- feat: add start-dev.ps1 — launch API in WSL2, Web UI on Windows
- feat(phase6): align sandbox policy with Copilot CLI implementation
- feat: upgrade to Sabbour.Mxc.Sdk 0.1.2 (WSL2 bwrap/unshare support)
- feat(wsl): delegate WSL2 sandbox to Sabbour.Mxc.Sdk v0.1.2
- feat(wsl): discovery-based sandbox executor — bwrap/unshare, no lxc-exec
- feat(runner): instruct model to interpret run_command output via report_intent
- feat(002): T012 bundle mxc binaries + T017-api shell approval + scoped settings.yml
- feat(002): GitOps sandbox policy — .scaffolder/sandbox.yml
- feat(002): T020+T021 API sandbox endpoints + T022+T024 CLI + T035-T038 docs
- feat(web): T023+T025 sandbox badge, shell output in timeline, settings page
- feat(002): T018 dynamic project-scoped sandbox policy in SQLite
- feat(002): T017 HITL shell approval gate + T019 sandbox.warning event
- feat(002): Scaffolder.AgentTools package + refactor tools into ISandboxTool (T055-T057)
- feat(spike): Phase 0 — validate Sabbour.Mxc.Sdk v0.1.1 on Windows ARM64
- feat(events): add merge.started to bridge approve -> merge.completed/failed
- feat(spec/001): MAF workflow-native HITL review gate + no-changes skip
- feat(spec/001): review/merge, Foundry streaming, and run-timeline UI
- feat(runtime): surface individual Copilot tool events at parity with Foundry
- feat(sandbox): enforce run sandbox boundary across both model providers
- feat: add FoundryAgentRunner for MicrosoftFoundry model source
- feat: structured event pipeline for Story 2 (aligned to Copilot SDK events)
- feat: stream agent response over SSE
- feat: strip to MAF basics -- prove a Copilot turn
- feat: replace provider SDKs with correct implementations
- feat(spec/001): implement single-agent run — full vertical slice
- feat(tests): add Scaffolder.Tests with 43 passing tests
- feat(web): Phase 9 - React 19 + Fluent 2 Web UI [T059-T066/trinity]
- feat(cli): Phase 8 - CLI client [T052-T058/trinity]
- feat(governance): Phase 7 - responsible AI + NFR enforcement [T045-T050/morpheus+tank]
- feat(api): Phase 6 - review + merge [T041,T043,T044/tank+morpheus]
- feat(agent): Phase 5 - model source adapters + governance [T037-T040/morpheus]
- feat(api): Phase 4 - SSE streaming [T032-T036/tank]
- feat(api): Phase 3 complete - US1 agent loop, execution, endpoints [T026-T031/tank+morpheus]
- feat(agent+persistence): Phase 3 Wave 1-2 - sandbox, tools, event log, state machine [T020-T027/tank+morpheus]
- feat(persistence): repositories and DI wiring [T015-T019/tank]
- feat(persistence): EF Core initial migration - all 6 tables [T014/tank]
- feat(persistence): EF Core data model - all 6 entities [T007-T013/tank]
- feat(config): application settings schema and ScaffolderOptions [T006/tank]
- feat(setup): initialize all project scaffolds - Phase 1 Wave 2 [T002/tank T003/trinity T004/smith T005/trinity]
- feat(spec/001): event loop, Responsible AI, and governance spec update

### Changed
- refactor(observability): remove event-stream fallback from TransactionTracePanel
- chore(observability): remove DB-backed metrics layer, migrate dashboard to AppInsights
- chore(observability): add OTel/AppInsights instrumentation and AKS Managed Prometheus (#106)
- chore(ui): surface app version inside the Alpha badge (#109)
- chore(release): implement semver release process (#104)
- chore: graph zoom-in button, card navigation, and scroll indicator (#100)
- chore: replace AKS flowchart diagrams with block-beta block architecture diagrams (#101)
- chore(repo): add issue-form templates for all 6 type:* kinds
- build(aks): image-efficient redeploy + reproducible install scripts
- chore: remove dead legacy agentweaver-sandbox image/template/warmpool
- chore(deploy): apply serviceaccount-agenthost.yaml in 30-deploy.sh
- refactor(spec-006): drive reaper from heartbeat; cluster diagnostics endpoint
- chore(deps): upgrade GitHub.Copilot.SDK 1.0.0 -> 1.0.2
- chore: remove WORKFLOW_VERIFICATION_REPORT.txt
- chore: remove SandboxExec spike folder
- chore(api): quiet EF Core and framework Info log noise in committed config
- chore: stop tracking .squad runtime/config dir
- chore: ensure *.sh files always use LF line endings
- chore(k8s): flip API to pod-per-run agent execution (live)
- build(spec-018): apply Postgres + replicas:2 + RWX HOME cutover config
- build(spec-018): Postgres cutover tooling + worker manifest hardening
- build(spec-018): Dockerfile COPY for new Data/Migrations projects + deploy runbook
- chore: pre-audit snapshot — spec 006/007/008/009/011/012/013 implementation work
- refactor(coordinator): remove DraftDeterministic crutch from production
- refactor(rename): flip remaining plural scaffolders identifiers to agentweaver
- refactor(rename): rename web client + docs Scaffolder -> Agentweaver (phase B)
- refactor(rename): rename .NET solution Scaffolder.* -> Agentweaver.* (phase A)
- refactor(008): extract Program.cs endpoints into MapXEndpoints classes
- chore: remove legacy /watch route, simplify WatchPage to canonical route only
- chore: change web dev server port from 5173 to 8080
- refactor(runtime): consolidate system prompt; move memory tools guidance to Scribe charter
- refactor: CopilotAIAgent subclasses AIAgent for MAF session serialization
- refactor(006): implement Scribe as MAF workflow step
- refactor: remove stale charter templates, add incident_lead charter
- refactor: consolidate catalog roles round 2 (28 -> 22)
- refactor: consolidate overlapping catalog roles (qa, triage, docs)
- chore: remove errant speckit.plan artifacts; restore copilot-instructions
- refactor: minimal system prompt for both runners
- refactor: rename edit/create tools, clean up governance and native exclusions
- refactor: remove double-governance from tool bodies; rely on process isolation
- chore: remove local NuGet feed — Sabbour.Mxc.Sdk 0.1.2 now on nuget.org
- chore: ignore Vite build cache
- refactor(spec/001): single merge implementation via IMergeCoordinator.ExecuteMergeAsync
- refactor: switch FoundryClientFactory from OpenAI to Azure.AI.Inference
- chore: remove appsettings.Development.json configuration file
- chore: retarget to net10.0 (SDK 10.0.300)
- chore: remove Spec-Kit plan/tasks/implement references from Squad files
- chore(constitution): bump v1.1.0 -> v1.1.1, standardize runtime on .NET 10
- chore: migrate to .NET 9 (net9.0, SDK 9.0.314)
- chore(setup): pin to .NET 8 SDK while .NET 9 installer completes [squad-decision]
- chore(setup): scaffold solution structure and directory tree [T001/link]

### Docs
- docs: fix nav sidebar, remove AX TODO stub, fix README diagrams
- docs(reference): add Agentweaver-on-AX integration analysis
- docs: embed AKS block diagram in docs+README, add AX reference page, remove AX comparison from README
- docs: add AKS block diagram (excalidraw) and link from architecture-aks.md
- docs: narrow README reference section to AX only
- docs: add Reference section comparing Agentweaver to Agent eXecutor and Agent Substrate (#87)
- docs: update coordinator internals, reference, and experience for #76 #78 #82
- docs(sandbox): keep RealPath as supported API
- docs: repair Ralph PR docs dispositions
- docs(specs): add edit-workflows-with-generation-prompt spec (#59)
- docs(specs): add scheduled/event workflow triggers and import-and-sync skills specs
- docs(specs): add specs for backlog sync, PR action, browser console, agent skills, AKS personas
- docs: note pod-name persistence; exclude docs/ from image build context
- docs: sync to shipped features; remove stale sandbox references
- docs(squad): rebuild routing.md charter-derived; split out built-in agents
- docs: document per-run workingDirectory delivery via /configure
- docs: update coordinator autopilot, SSE reconnect, cluster diagnostics, and AKS diagram
- docs: pod-per-run + distributed coordinator lease
- docs: AgentHost warm pool architecture, deep-dive, UX, and reference docs
- docs: warm pool architecture, auth deep-dive, sandbox reference updates
- docs: security fix documentation pass
- docs(019): AI credit and token usage monitoring
- docs: update auth, sandbox, coordinator, API docs; add Cluster page and agent-token delivery guides
- docs: link published docs site in README
- docs: add workflow-selection deep-dive page and cross-links
- docs: set base to /agentweaver/ and add GitHub Pages deploy workflow
- docs: add GitHub repo social link to nav
- docs: document workflow picker + auto-selection in Start task dialog
- docs: add Agentweaver icon to README
- docs: remove all legacy SQLite backup job references; delete backup-cronjob.yaml
- docs: update deep-dive docs for PostgreSQL + 2-replica deployment
- docs: update AKS architecture doc for Postgres + 2 replicas
- docs: strip internal/removed config from configuration + deployment-aks
- docs: remove internal/unintended config references from getting-started
- docs: add GitHub OAuth App setup to getting-started
- docs: fix getting-started — OAuth token via sign-in, no static API key required
- docs: update sandbox preview — enabled by default in AKS
- docs(sandbox): document agent-initiated start_preview tool + HITL approval
- docs(kata): document dedicated kata user pool topology + scheduling
- docs(sandbox): document AgentHost cold-start readiness gate under replicas:0
- docs(checkpoint): document multi-replica checkpoint store resilience
- docs(sandbox): document v1beta1 warmPoolRef SandboxClaim contract + agent-host warm pool
- docs: document shipped sandbox browser-preview reverse proxy
- docs(agent-definition): document generation & per-project materialization
- docs: add MIT LICENSE
- docs(install): fix repo slug, add --image-tag, add Build & deploy section
- docs: fix dark-mode text for Mermaid sequence diagrams
- docs: add docs-sync mechanism (generator + CI drift check + skill)
- docs(install): true one-liner install -- bootstrap clone + one-command local/AKS
- docs: robust Mermaid lightbox binding + relabel example walkthroughs
- docs: full-width layout, top-bar nav, and Mermaid lightbox
- docs: fix Mermaid legibility and dark-mode rendering
- docs: restructure IA, re-ground against real code, add install + screenshots plan
- docs: fresh FluentUI-styled Mermaid architecture diagrams across all areas
- docs: add Microsoft Agent Framework coverage, MXC + preview sandbox, nav fixes
- docs: add deep-dive/reference/UX docs for pod execution, A2A, scaling, agent comms
- docs(experience): add UI/MCP user-experience guide
- docs(deep-dive): second coherence polish (gpt-5.5 cross-model pass)
- docs(deep-dive): coherence polish - remove hedging and legacy framing
- docs(spec-018): distributed agent execution + scaling design
- docs(deep-dive): add 7 new concept deep dives + grouped TOC
- docs(deep-dive): rewrite as concept/logic-first deep dives
- docs: deep accuracy pass — AKS deployment, architecture, guide docs
- docs: deep accuracy pass — reference docs (API endpoints, events, memory, sandbox)
- docs: deep accuracy pass — workflow, MCP, run-event-stream docs
- docs(spec): amend 017-aks-deployment spec with Cilium NetworkPolicy, GitHub org authz, external MCP auth, ISandboxExecutorRouter, SQLite reliability notes, and GitHub App redirect URL
- docs(spec): resolve feature 011 web app shell clarifications
- docs(spec): add feature 011 spec - Agentweaver web app shell / IA
- docs(spec): resolve feature 010 clarifications (load-once, per-task override, review policy)
- docs(spec): add feature 010 spec - YAML workflows + per-project Review Policies
- docs: finish Agentweaver rebrand, drop retired-CLI refs, fix docs build
- docs: catch up with spec 006 - workflow run UI, memory, MCP tools, events
- docs: remove CLI references, replace with MCP server throughout
- docs: update api.md, cli.md, web.md for feature 005 + deprecation notice on cli.md
- docs: update api.md, cli.md, web.md for feature 005 (team casting, agent runs)
- docs: update sandbox docs for Phase 6 (schema 0.5.0-alpha, network_enabled, selective mounts)
- docs(wsl): document Wslc upgrade path for when WSL 2.8.x ships
- docs(002): re-scope tools — remove memory/todo, restore report_intent
- docs(002): add sandboxed-execution spec and implementation plan
- docs: align spec to actual Copilot SDK event model
- docs: add VitePress docs site + Docs ceremony
- docs: ratify Scaffolder constitution v1.0.0 (7 principles; no-emoji rule scoped to product)

### Tests
- test(projects): provide IConfiguration to workspace provider
- test: fix pre-existing test failures across backend and frontend (#80)
- test(019): token usage store, projection service, and endpoint tests
- test: commit e2e Playwright harness source
- test(oauth): update McpOAuthServerTests for EF-backed broker + scope-factory signatures
- test(events): verify crash-safe replay — write-through durability tests (016-US1)
- test(008): restore injectable workflow-agent seam; fix content-safety terminal
- test(008): align 3 stale tests with current contracts
- test(002): unit tests for SandboxExec + SandboxedFileTools (T026-T029, T051-T052)
- test(runtime): add Copilot glob/grep escape canaries and both-provider tool-event parity assertions
- test(qa): Phase 10 - contract tests + integration QA + compliance [T051,T067-T079/smith+rai]

### Other
- bug(run-page): show preview sandbox button for completed runs (#99)
- bug(run-page): add preview sandbox to orchestration run page (#98)
- Move personas under specs/ and link from spec index
- Remove unused code and duplicate logic (code-bloat sweep)
- Replace legacy speckit specs with concise area-grouped product specs (#2-#37)
- Add user personas + persona-driven Playwright self-improvement harness (#1)
- Stop bundling/serving docs in frontend; redirect /docs to GitHub Pages
- security: remove installation token fallback — require user identity on all Copilot paths
- k8s: increase namespace quota to 32 CPU / 30 pods
- security: per-user GitHub token isolation and AgentHost SPC hardening
- spec: resolve FR-015 — per-model breakdown visible in dashboards
- spec: AI credit and token usage monitoring (019)
- Commit scaffold files to git on project creation
- revert: pause Task 3 DiagnosticsPage/StatusDot changes pending Cluster page design
- debug: add --verbose to ef bundle to diagnose failure
- scripts: remove backup-cronjob.yaml from 30-deploy.sh
- revert: remove erroneously re-added Auth__User env var
- config: set Auth__User=sabbour (static-key fallback owner)
- Remove static MCP API key; MCP auth via OAuth only
- infra: switch to 3-pool layout with CriticalAddonsOnly system taint
- infra(kata): wire sandbox pods to dedicated kata user pool (katapool)
- infra: apply spike verdict — DDC nested wildcard NOT supported; simplify to single-label fallback
- infra: add preview gateway bootstrap (gateway, RBAC, NetworkPolicy, deploy wiring)
- docs+ui: mark Agentweaver as alpha, MCP as experimental
- Remove platform-sre blueprint; fix AKS project creation; auto-fill repo folder
- config: set Auth:GitHub:AllowedOrg=microsoft
- Phase 2: dispatch child runs, observe, topology + subtask events
- Phase 2: coordinator orchestrator (decompose + persist work plan)
- Phase 2 foundation: coordinator EF entities, trimmed child pipeline, steering spike
- Rename revise action to 'Clarify and request changes' and clarify re-draft state
- Surface clarifying questions in revise dialog with Q/A template
- Implement Feature 008 Phase 1: Coordinator outcome-spec + confirm gate
- Add Squad Coordinator Agent implementation plan (008)
- Add Squad Coordinator Agent specification (008)
- execution route: /projects/:id/runs/:id/execution/:id + breadcrumbs
- workflow: fix arc clipping and clearance heuristics
- workflow: arc rounding, rename run→execution, team memory page
- PUT /sessions/current: upsert instead of 404 when no open session exists
- Filter workflow-orchestration events from Watch page timeline
- Auto-expire stale no-checkpoint AwaitingReview runs older than 24h on startup
- Allow abandoning any non-terminal run (in_progress, awaiting_review, etc.)
- Add DismissCircleRegular icon to Abandon button
- Separate Abandon/Delete UX for awaiting_review vs terminal runs
- Replace confirm() with Fluent UI dialog for run deletion
- Allow deleting AwaitingReview runs (force-decline + worktree cleanup)
- Phase 12: PostRunScribeService — memory flywheel close after successful runs
- ux: show server data directory in repository path hint
- ux: clarify repository path is server-side, not local machine
- ux: clarify working directory field in create project dialogs
- spec+plan: 006 add post-run Scribe loop-close (FR-031/032/033)
- spec+plan: 006 progressive disclosure via Context Compilation Pattern
- spec+plan: add agentweaver squad coordinator (US6, FR-015-020, SC-008-009)
- plan: 007-mcp-server implementation plan
- revert: remove spurious HTTP/1.1 fix and WinHttpHandler
- Commit pending session changes
- Expand destructive command patterns with comprehensive bash/shell list
- Dangerous commands surface for HITL approval instead of blocking
- Move sandbox policy into project settings, remove global Settings page
- Remove provider selection, hardcode GitHub Copilot
- [003-projects] Add repo picker to Create from GitHub dialog
- [003-projects] Reduce sign-in page padding/gaps
- [003-projects] Increase sign-in logo size to 160px
- [003-projects] Use agentweaver.png logo
- [003-projects] Rename brand to Agentweaver
- [003-projects] Fix OAuth state lost between requests
- [003-projects] Fix OAuth redirect URL to use absolute API_URL
- [003-projects] Switch to OAuth redirect flow, add avatar support
- [003-projects] Add full-page GitHub sign-in gate
- [003-projects] Redesign: GitHub device flow sign-in card UI
- [003-projects] Fix: add Accept: application/json to GitHub device flow requests
- [003-projects] Fix: normalize button sizes to medium (Sign in, Settings, Watch)
- [003-projects] Fix: defer GitHub ClientId validation; return 503 when not configured
- [003-projects] Fix: pass runId to TurnGroup for tool approvals; back-to-project nav on WatchPage
- [003-projects] Phase 7: tests + security review
- [003-projects] Phase 6: update reference docs for projects + github auth
- [003-projects] Phase 5: Web gallery, project pages, GitHub sign-in
- [003-projects] Phase 4: CLI project + github commands
- [003-projects] Phase 3: /api/projects, /api/auth/github, run-in-project endpoint
- [003-projects] Phase 1: workspace providers, git initializer, ProjectService
- [003-projects] Phase 0: domain types, interfaces, schema, SqliteProjectStore
- [Spec Kit] Simplify plan-gate review to rubber-duck only (constitution v1.3.0)
- [Spec Kit] Add 003-projects implementation plan; resolve FR-025
- [Spec Kit] Clarify 003-projects: resolve delete/dir/owner ambiguities; add FR-026
- [Spec Kit] Refine FR-005: unified GitHub sign-in replaces Copilot API key (003-projects)
- [Spec Kit] Add and clarify Projects feature spec (003-projects)
- Backend: SSE awaiting_review hang fix, commit endpoint, workspace listing
- spec(001): add User Story 5 (artifact browser) with FR-034-FR-041, SC-013-SC-017, and 2026-06-10 clarification
- debug: relax AGT to allow-all; fix Copilot shell dir; fix report_intent response
- config: disable shell in spike sandbox settings
- Wire ISandboxExecutor + 9 custom AIFunction tools into both runners (T013,T015,T016,T047,T048,T019,T018)
- Add Speckit plan, Squad scaffolding, and tasks for 001-single-agent-run
