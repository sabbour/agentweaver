---
"agentweaver": minor
---

Unify model-provider precedence and terminology across the platform, and fix four AI-generation
features that were silently broken for GitHub Copilot users.

**One shared resolver, one precedence rule.** Introduced `EffectiveModelProviderResolver`, a single
backend service that resolves the effective AI model provider for a project (or the platform
default when there is no project) and returns a discriminated result: BYOK, project GitHub
Copilot, platform GitHub Copilot, or unavailable-with-reason. The rule is now applied consistently
everywhere: a project's explicit GitHub Copilot override always wins if configured, otherwise the
platform default applies (deployment-wide BYOK first, then the platform GitHub Copilot binding).
This replaces nine different, previously inconsistent precedence implementations across the
app-entry AI gate, project status/readiness endpoints, coordinator run startup, the Assistant
service, backlog decomposition, and marketplace catalog classification.

**Fixed: four non-run AI generators that only worked with BYOK.** Blueprint generation, workflow
generation, skill generation, and casting generation each fabricated a synthetic run id and asked
for a run-bound Copilot capability snapshot that was never created for that id — so any project
using GitHub Copilot (rather than BYOK) silently failed to generate. All four now issue a real,
short-lived, purpose-bound capability through the resolver when the effective provider is Copilot,
or use the BYOK path directly when it isn't.

**Fixed: capability issuance was owner-only.** Backlog decomposition and marketplace catalog
classification previously only accepted a project Copilot credential belonging to the exact human
who originally connected it, silently ignoring BYOK and the platform default, and blocking any
other authorized project Contributor. Both now use the shared resolver, so any authorized project
member can use the project's effective model provider.

**Fixed: BYOK runs no longer require a bogus Copilot snapshot.** Pod-per-run coordinator startup
unconditionally demanded a GitHub Copilot capability snapshot even when the project was running on
BYOK. It now only requires a Copilot snapshot when the resolver's result is actually Copilot-sourced.

**Terminology unified: "model provider" vs. "GitHub repository access".** Renamed the vocabulary
used for the source of AI inference (GitHub Copilot, custom endpoint, Azure OpenAI, Anthropic) to
"model provider" throughout the UI and API, distinct from GitHub repository access (Repo App
installation/grants), which keeps its GitHub-specific naming. Notably:
- `GitHubCopilotConnectionRequirement`/`GitHubCopilotConnectionAction` → `ModelProviderConnectionRequirement`/`ModelProviderConnectionAction`,
  with the error code `github_copilot_connection_required` → `model_provider_connection_required`,
  and a single ambiguous `connect_project_copilot_app` action split into distinct
  `configure_project_model_provider` / `configure_platform_model_provider` codes.
- `GitHubCopilotConnectionPicker` → `ProjectModelProviderSettings`; `GitHubCopilotConnectionRequiredAction` → `ModelProviderRequiredAction`.
- Fixed a routing bug where a platform-scoped "connect" prompt sent the user to Account Settings
  instead of Platform Settings.
- `apps/web/src/api/errors.ts` now has separate repository-access vs. model-provider error message
  maps, and no longer misrepresents an unrelated `404` (e.g. a deleted run) as a GitHub connection
  problem.
- The left-nav identity popover now shows separate "Repository access" and "Model provider" rows,
  instead of a single row that mislabeled a BYOK deployment as "AI source: GitHub Copilot".

**Dead code removed.** `GitHubCapabilityBroker.TryAuthorizeAsync`/`GitHubCapabilityGrant` (no
callers), and run-level `InteractiveRepository`/`InteractiveCopilot` capability snapshot capture
(never redeemed by any credential consumer, and the `InteractiveCopilot` variant was built from Repo
App authorization data rather than an actual Copilot binding).
