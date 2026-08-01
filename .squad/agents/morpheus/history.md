# Morpheus — History (Summarized)

## 2026-06-07 through 2026-06-29 — Core runtime, sandbox, and coordinator foundations

- Built core runtime/run types, sandbox validation and tooling, provider integrations, and major Coordinator orchestration foundations.
- Rejected moving workspace creation into the untrusted sandbox pod, then drove the pod-per-run AgentHost architecture with durable worker-side orchestration and A2A transport.
- Shipped key sandbox and security follow-through: per-user token scoping, per-run A2A bearer tokens, and multiple recovery/preview/runtime fixes while keeping build/test coverage healthy.

## 2026-07-05 through 2026-07-15 — Workflow selection, harness work, and steering reliability

- Completed the #176 blueprint-matching/workflow-generation decision and participated in multiple release and staging validation waves.
- Authored the MCP harness design and coordinated shared harness/judge contracts with Tank and Trinity.
- Reopened and fixed the #272 outcome-spec steer/reply path by restoring orphaned deferred-decision draining and then replacing brittle regex classification with an LLM-backed classifier that still fails closed to revise.

## 2026-07-20T14-05-53-07-00 — Branching strategy design, adversarial review, and final settlement

- Authored the initial protected-trunk design, then fully explored the strongest no-Merge-Queue alternative (promotion through `dev` plus an ephemeral release-candidate tier) instead of assuming trunk would win automatically.
- Verified that GitHub Merge Queue is unavailable on the current personal-account repository, which materially changed the trade-off analysis.
- The settled durable outcome is still protected `main` only: every change via PR, required `.NET tests` / `Node toolchain tests` / `Web tests` / `Docs build`, squash-only merge, automatic branch deletion, narrow audited admin bypass, and release tagging from the exact green merged SHA.
- Key cross-agent learning: tags already provide the immutable published-release boundary, so Agentweaver does not need extra long-lived branch tiers unless future maintenance-line or organization-level Merge Queue realities change.

2026-07-31T03:40:59+03:00 — Publish-apps exploration completed discussion-only. Morpheus confirmed publish is legal only as a declarative post-approval tail node converged by a reconciler, and rejected mapping coordinator runs onto OpenAI Responses/Conversations.
