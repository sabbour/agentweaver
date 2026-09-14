# Experience research: identity, capabilities, and project/team journeys

## Scope and status

Bounded researcher 1 of the parent's three independent researchers. Research and edits
were confined to the `drawio-diagram-authoring` worktree and the twelve assigned experience
documents plus this report. No agents were launched. No product files, skills, inventories,
audit reports, shared assets, diagrams, or rendered images were changed. No commit.

Read `CONTRIBUTING.md`, both requested diagram skills, `plan-experience.json`, and
`experience.json` before editing. Existing worktree changes were retained. The worktree
was already on `squad/1305-drawio-diagram-authoring`, tracking `origin/dev` two commits
behind, with extensive understood parallel documentation work. No branch/index mutation.

Implementation, configuration, and test bodies are the evidence below. Legacy diagrams
and screenshots were not treated as truth. Screenshot dispositions use the audit's
inspection findings; I did not capture or inspect any new product screenshot.

The following are **content models for the parent**, not finished pitches. The parent
still owns reconciliation, native-library selection, XML, rendering, inspections,
iteration, promotion, hash/drift checks, and the integrated docs build.

## Reconciled facts that affect multiple figures

1. **Identity is Entra; capabilities are separate.** Browser sign-in uses Entra
   authorization code + server-held PKCE. MCP uses an Agentweaver-issued broker token
   whose subject is the Entra object ID, not a GitHub login. GitHub Repo App repository
   access and Copilot/provider readiness do not grant platform roles or project membership.
   Evidence: `apps\Agentweaver.Api\Auth\EntraOAuthRedirectService.cs:16-31`;
   `apps\Agentweaver.Api\Endpoints\OAuthAuthorizationServerEndpoints.cs:150-166`;
   `apps\Agentweaver.Api\Security\ProjectAuthorization.cs:59-84`;
   `apps\web\src\pages\ProjectSettingsPage.tsx:1078-1088`.
2. **Public MCP is broker-only.** Exact single resource audience, configured issuer,
   keyed RS256, lifetime, subject, and `mcp:invoke` are required. Missing/invalid tokens
   are 401; insufficient scope is 403. Raw Entra, GitHub, and API keys are rejected.
   HTTP forwards the exact validated token; STDIO forwards its configured broker token,
   never an API-key fallback. Evidence:
   `apps\Agentweaver.Mcp\McpBrokerAuthenticationHandler.cs:40-106`;
   `apps\Agentweaver.Mcp\AgentweaverApiClient.cs:332-382`;
   `tests\Agentweaver.Tests\Mcp\McpBrokerRealProcessTests.cs:183-251`.
3. **Request and event directions differ.** Browser initiates SSE HTTP request; API sends
   event payloads back. MCP consumes the API event stream, then emits client progress
   notifications. Do not label a browser-to-API arrow “SSE events.”
   Evidence: `apps\web\src\api\sse.ts:237-246`;
   `apps\Agentweaver.Api\Endpoints\RunEndpoints.cs:386`, `:481`, `:514`;
   `apps\Agentweaver.Mcp\AgentweaverApiClient.cs:598`;
   `apps\Agentweaver.Mcp\Tools\RunTools.cs:229-265`.
4. **Choose intake once.** Ready pickup atomically claims/reserves a run, then starts
   unattended. It is an alternative to manually starting that same goal.
   `coordinator_start` defaults to `defineOutcome` but accepts `direct`;
   `run_task` defaults to `direct`. Only Define Outcome requires the manual outcome gate.
   Evidence: `apps\Agentweaver.Api\Coordinator\CoordinatorPickupService.cs:186-249`;
   `apps\Agentweaver.Mcp\Tools\CoordinatorTools.cs:14-34`;
   `apps\Agentweaver.Mcp\Tools\RunTools.cs:137-158`;
   `apps\web\src\components\StartOrchestrationDialog.tsx:93-107`.
5. **Memory records do not automatically become team instructions.** Database/compiler,
   not exports, supplies context. Active approved architectural/scope decisions are
   eligible boundaries. Cross-agent memories require approved + high-importance +
   learning/pattern + `cross-team`, subject to budgets. Legacy records are excluded even
   for their named agent. Content remains untrusted JSON data. Coordinator children
   have a narrower decisions-only compilation path.
   Evidence: `apps\Agentweaver.Api\Memory\MemoryContextCompiler.cs:57-105`, `:159-226`;
   `tests\Agentweaver.Tests\Memory\MemoryContextCompilerSecurityTests.cs:22-166`.

## Figure 1: experience-00-overview-fig1

**Takeaway:** People and assistants operate the same authoritative product through
different interfaces; client-initiated requests and server-originated events are distinct.

**Consumer:** `docs\experience\00-overview.md`.
**Stable PNG:** `docs\diagrams\experience-00-overview-fig1.png`.
**Editable source:** `docs\diagrams\src\experience-00-overview-fig1.drawio`.
**Disposition:** redesign, preserving stable identity.
**Suggested composition:** A5 landscape; two small entry lanes converge on an API
boundary, with authoritative product state beyond it. This is not a deployment inventory.

### Nodes and evidence

| ID | Concise node | Classification suggestion | Evidence |
|---|---|---|---|
| O1 | Human operator | `native:c4` person | `apps\web\src\App.tsx:82-99` |
| O2 | Web UI | `native:c4` container | `apps\web\src\App.tsx:103-125` |
| O3 | MCP client / assistant | `native:c4` external system | `apps\Agentweaver.Mcp\Program.cs:78-98` |
| O4 | Agentweaver MCP server | `native:c4` container | `apps\Agentweaver.Mcp\Program.cs:85-98` |
| O5 | Agentweaver API | `native:c4` container | `apps\Agentweaver.Mcp\AgentweaverApiClient.cs:343-382` |
| O6 | Projects, runs, teams, knowledge | `custom:agentweaver` grouped product-state card | `apps\Agentweaver.Mcp\Tools\CoordinatorTools.cs:50-123`; `apps\Agentweaver.Api\Memory\MemoryContextCompiler.cs:57-105` |

### Complete proposed connector set

| From → to | Label / meaning | Evidence |
|---|---|---|
| O1 → O2 | inspect / decide | `apps\web\src\App.tsx:103-125`; `apps\web\src\components\StartOrchestrationDialog.tsx:86-109` |
| O1 → O3 | ask assistant to operate | `apps\Agentweaver.Api\Assistant\AssistantRunService.cs:802-813`; external client is the analogous MCP caller |
| O2 → O5 | authenticated requests / open stream | `apps\web\src\api\sse.ts:237-246`; `apps\web\src\api\client.ts:1593` |
| O5 → O2 | snapshots and run events | `apps\Agentweaver.Api\Endpoints\RunEndpoints.cs:481-514`; `apps\web\src\api\sse.ts:246` |
| O3 → O4 | MCP tool call | `apps\Agentweaver.Mcp\Program.cs:85-98` |
| O4 → O5 | forward validated caller token | `apps\Agentweaver.Mcp\AgentweaverApiClient.cs:359-382` |
| O5 → O4 | API result / event stream | `apps\Agentweaver.Mcp\AgentweaverApiClient.cs:376-382`, `:598` |
| O4 → O3 | tool result / progress | `apps\Agentweaver.Mcp\Tools\RunTools.cs:229-265`; `tests\Agentweaver.Tests\Mcp\McpBrokerRealProcessTests.cs:240-251` |
| O5 → O6 | authoritative reads / mutations | `apps\Agentweaver.Api\Endpoints\MemoryEndpoints.cs:158-162`; `apps\Agentweaver.Api\Coordinator\CoordinatorPickupService.cs:186-194` |

The last edge denotes access to product state, not exclusively database storage:
workspaces/team files also exist. Do not put a database cylinder around every product
object. If space is tight, use an API-attached state group rather than a second backend.
Trust boundary: clients outside backend authorization; no “MCP is a superuser” bypass.

## Figure 2: experience-mcp-client-fig1

**Takeaway:** An assistant prepares and operates work through MCP, chooses one intake
path, and preserves explicit human decision boundaries.

**Consumers:** `docs\experience\mcp-client.md` and `docs\experience\00-overview.md`.
**Stable PNG:** `docs\diagrams\experience-mcp-client-fig1.png`.
**Editable source:** `docs\diagrams\src\experience-mcp-client-fig1.drawio`.
**Disposition:** redesign; absorbs `experience-00-overview-fig3`.
**Suggested composition:** A5 landscape journey, not a second auth/transport sequence.

### Nodes and evidence

| ID | Node | Classification | Evidence |
|---|---|---|---|
| M1 | Human intent / judgment | `native:c4` person | `apps\Agentweaver.Mcp\Tools\CoordinatorTools.cs:64-95`; `apps\Agentweaver.Mcp\Tools\RunTools.cs:276-284` |
| M2 | Assistant | `native:c4` external system | `tests\Agentweaver.Tests\Mcp\McpBrokerRealProcessTests.cs:240-251` |
| M3 | MCP → API operations | `native:c4` grouped components | `apps\Agentweaver.Mcp\AgentweaverApiClient.cs:359-382` |
| M4 | Project and named team | `custom:agentweaver` | `apps\web\src\pages\ProjectGalleryPage.tsx:259-268`; `apps\Agentweaver.Api\Casting\CastingService.cs:899-927` |
| M5 | Ready pickup OR immediate start | `native:flowchart` decision | `apps\Agentweaver.Api\Coordinator\CoordinatorPickupService.cs:186-249`; `apps\Agentweaver.Mcp\Tools\CoordinatorTools.cs:20` |
| M6 | Coordinator / child work | `custom:agentweaver` | `apps\Agentweaver.Mcp\Tools\CoordinatorTools.cs:104-123` |
| M7 | Outcome confirmation, when requested | `native:flowchart` decision | `apps\Agentweaver.Mcp\Tools\CoordinatorTools.cs:20`, `:64-95` |
| M8 | Status and artifacts | `custom:agentweaver` run card; optional `native:flowchart` document | `apps\Agentweaver.Mcp\Tools\RunTools.cs:216-265`, `:364-391` |
| M9 | Approve / decline | `native:flowchart` decision | `apps\Agentweaver.Mcp\Tools\RunTools.cs:276-284` |

### Connectors / branches

| From → to | Meaning | Evidence |
|---|---|---|
| M1 → M2 → M3 | intent becomes explicit tool calls | `tests\Agentweaver.Tests\Mcp\McpBrokerRealProcessTests.cs:240-251` |
| M3 → M4 | inspect/create project, propose/confirm cast | `apps\web\src\pages\ProjectGalleryPage.tsx:259-268`; `apps\Agentweaver.Api\Casting\CastingService.cs:899-927` |
| M4 → M5 | choose how this work enters execution | `apps\Agentweaver.Mcp\Tools\CoordinatorTools.cs:14-34`; `apps\Agentweaver.Api\Coordinator\CoordinatorPickupService.cs:186` |
| M5 → M6 | queued: promote Ready, atomic claim/reserve, unattended start | `apps\Agentweaver.Api\Coordinator\CoordinatorPickupService.cs:186-249` |
| M5 → M7 | immediate Define Outcome: draft then wait | `apps\Agentweaver.Mcp\Tools\CoordinatorTools.cs:20`, `:50-95` |
| M7 → M1 → M3 → M7 | surface spec, request revision if needed | `apps\Agentweaver.Mcp\Tools\CoordinatorTools.cs:50-95` |
| M7 → M6 | authorized confirmation resumes dispatch | `apps\Agentweaver.Mcp\Tools\CoordinatorTools.cs:64-77` |
| M5 → M6 | immediate Direct: no manual outcome gate | `apps\Agentweaver.Mcp\Tools\RunTools.cs:143-158` |
| M6 → M3 → M2 | API state/progress returns through MCP | `apps\Agentweaver.Mcp\Tools\RunTools.cs:229-265` |
| M2 → M3 → M8 | list/read artifacts before deciding | `apps\Agentweaver.Mcp\Tools\RunTools.cs:364-391` |
| M8 → M2 → M1 | explain result and request judgment | binary review tool contract `apps\Agentweaver.Mcp\Tools\RunTools.cs:276-284` |
| M1 → M2 → M3 → M9 | explicitly authorized approve/decline | same review contract |

These chained connector descriptions are routing constraints, not permission to draw
API-to-assistant shortcuts. Keep return traffic through MCP. `run_review` is binary;
do not label its decline branch “request changes.” Optional memory follow-up may be
a text annotation linking the team journey rather than adding a second knowledge graph.
Ready lost-claim/unavailable branches can be summarized as “not started by this pickup.”
Do not imply promoting Ready then calling `coordinator_start` is one mandatory sequence.

## Figure 3: experience-onboarding-auth-fig1

**Takeaway:** Entra establishes a browser identity/session; setup readiness and optional
GitHub capabilities are subsequent, distinct checks.

**Consumer:** `docs\experience\onboarding-auth.md`.
**Stable PNG:** `docs\diagrams\experience-onboarding-auth-fig1.png`.
**Editable source:** `docs\diagrams\src\experience-onboarding-auth-fig1.drawio`.
**Disposition:** retain semantic journey; use current session-exchange detail.
**Suggested composition:** A5 landscape UML sequence with browser/API/Entra lanes and
a compact readiness continuation, rather than mixing GitHub into sign-in.

### Nodes

| ID | Node | Classification | Evidence |
|---|---|---|---|
| B1 | Human and browser | `native:uml` actor/participant | `tests\Agentweaver.Tests\Auth\EntraSignInEndpointsTests.cs:231-270` |
| B2 | Agentweaver web UI | `native:uml` participant | `apps\web\src\App.tsx:82-99` |
| B3 | Agentweaver auth API | `native:uml` participant | `apps\Agentweaver.Api\Endpoints\AuthEndpoints.cs:290`, `:327`, `:434` |
| B4 | Microsoft Entra ID | `native:azure` Entra symbol within participant | `apps\Agentweaver.Api\Auth\EntraOAuthRedirectService.cs:16-31` |
| B5 | Server-side state/session | `native:database` store | `apps\Agentweaver.Api\Auth\EntraOAuthRedirectService.cs:230-239`, `:293-322`; `apps\Agentweaver.Api\Endpoints\AuthEndpoints.cs:434-452` |
| B6 | Setup readiness | `custom:agentweaver` readiness card | `apps\web\src\components\SetupReadiness.tsx:22-23`, `:189-195`, `:249` |
| B7 | App shell / project journey | `native:c4` UI container | `apps\web\src\App.tsx:82-125` |

### Connectors

| From → to | Meaning | Evidence |
|---|---|---|
| B1/B2 → B3 | start `/auth/entra/authorize` | `apps\Agentweaver.Api\Endpoints\AuthEndpoints.cs:290` |
| B3 → B5 | save expiring state, PKCE verifier, nonce | `apps\Agentweaver.Api\Auth\EntraOAuthRedirectService.cs:230-239` |
| B3 → B1 → B4 | redirect browser with code challenge | same service `:250-260` |
| B4 → B1 → B3 | callback code/state, bound browser cookie | `apps\Agentweaver.Api\Endpoints\AuthEndpoints.cs:327-365`; `tests\Agentweaver.Tests\Auth\EntraSignInEndpointsTests.cs:242-250` |
| B3 → B5 | atomically consume state | `apps\Agentweaver.Api\Auth\EntraOAuthRedirectService.cs:293-322` |
| B3 ↔ B4 | redeem code + stored verifier; validate result | same service `:333-355` |
| B3 → B2 | one-time frontend exchange code, not raw token in URL | `apps\Agentweaver.Api\Endpoints\AuthEndpoints.cs:416-417`; test `EntraSignInEndpointsTests.cs:248-261` |
| B2 → B3 → B5 | exchange code, issue browser session | `apps\Agentweaver.Api\Endpoints\AuthEndpoints.cs:434-452` |
| B2 → B6 → B7 | check platform access and required model setup, continue when ready | `apps\web\src\App.tsx:278-293`, `:344-379` |

The last continuation is user-level composition, not a claim that the SetupReadiness
presentational component performs authentication. Label model readiness “required for AI
work” and repository access “optional; needed for GitHub work.” No GitHub identity arrow.

## Figure 4: experience-onboarding-auth-fig2

**Takeaway:** MCP OAuth uses Entra-backed browser consent to issue an exact-resource
Agentweaver broker credential; MCP and API each enforce their boundary.

**Consumer:** `docs\experience\onboarding-auth.md`.
**Stable PNG:** `docs\diagrams\experience-onboarding-auth-fig2.png`.
**Editable source:** `docs\diagrams\src\experience-onboarding-auth-fig2.drawio`.
**Disposition:** redesign. Do not reuse GitHub-era shared auth diagrams unchanged.
**Suggested composition:** A5 landscape UML sequence; collapse the Entra sign-in
subsequence into a named reference to figure 1 to keep this readable.

### Nodes

| ID | Node | Classification | Evidence |
|---|---|---|---|
| A1 | MCP client | `native:uml` participant | `apps\Agentweaver.Mcp\Program.cs:85-98` |
| A2 | Human browser | `native:uml` actor/participant | `apps\Agentweaver.Api\Endpoints\OAuthAuthorizationServerEndpoints.cs:109-180` |
| A3 | MCP resource `/mcp` | `native:c4` container | `apps\Agentweaver.Mcp\Program.cs:91-98` |
| A4 | Agentweaver OpenIddict authorization server | `native:c4` component | `apps\Agentweaver.Api\Program.cs:982-996` |
| A5 | Entra sign-in / browser session | `native:azure` Entra plus referenced sequence | `apps\Agentweaver.Api\Endpoints\AuthEndpoints.cs:402-414` |
| A6 | Authorized API operations | `native:c4` component | `apps\Agentweaver.Api\Security\ProjectAuthorization.cs:59-84` |
| A7 | Consent/grant state | `native:database` | `apps\Agentweaver.Api\Endpoints\OAuthAuthorizationServerEndpoints.cs:150-176`, `:49-71` |

### Connectors

| From → to | Meaning | Evidence |
|---|---|---|
| A1 → A3 → A1 | missing-token call returns 401 discovery challenge | `apps\Agentweaver.Mcp\McpBrokerAuthenticationHandler.cs:92-106`; real-process tests `:183-191` |
| A1 → A3 → A1 | discover exact resource, issuer, scope | `apps\Agentweaver.Mcp\Program.cs:92-93`; real-process tests `:157-174` |
| A1 → A4 → A1 | discover metadata/JWKS; register native client when needed | `apps\Agentweaver.Api\Program.cs:976-987`; `apps\Agentweaver.Api\Endpoints\OAuthAuthorizationServerEndpoints.cs:24-28` |
| A1 → A2 → A4 | open `/oauth/authorize` with resource and PKCE challenge | authorization endpoint `:87-100` |
| A4 → A2 → A5 | absent session: offer Entra sign-in | authorization endpoint `:109-138`; `tests\Agentweaver.Tests\Auth\OpenIddictAuthorizationServerTests.cs:405-421` |
| A5 → A4 | callback session resumes saved request | `apps\Agentweaver.Api\Endpoints\AuthEndpoints.cs:402-414`; OAuth endpoint `:240-257` |
| A4 ↔ A7 | inspect saved consent / create bound transaction | OAuth endpoint `:150-176` |
| A4 → A2 → A4 | show client/identity/access; Allow or Deny | OAuth endpoint `:148-188`; authorization tests `:340-401` |
| A4 → A1 | approved code or denied OAuth error, via browser callback | OAuth endpoint `:154-160`; authorization tests `:491` |
| A1 → A4 → A1 | redeem code + verifier + exact redirect/resource; broker token | OAuth endpoint `:31-76`; `apps\Agentweaver.Api\Program.cs:982-996` |
| A1 → A3 | broker-authenticated tool call | `apps\Agentweaver.Mcp\McpBrokerAuthenticationHandler.cs:40-87` |
| A3 → A6 → A3 | exact validated bearer; independent API auth and result | `apps\Agentweaver.Mcp\AgentweaverApiClient.cs:359-382`; `apps\Agentweaver.Api\Security\ProjectAuthorization.cs:59-84` |
| A3 → A1 | MCP result | `tests\Agentweaver.Tests\Mcp\McpBrokerRealProcessTests.cs:240-251` |

Optional refresh rail: A1 → A4 → A1, refresh grant with exact resource and family
revocation checks (`OAuthAuthorizationServerEndpoints.cs:39-59`). Draw it only if legible.
Existing consent can skip the new prompt; denied/expired/invalid paths must not join a
success arrow. GitHub Repo/Copilot capabilities are not nodes in this auth sequence.
Default external access lifetime is eight hours; the built-in Assistant's separate
per-turn issuer uses five minutes. Avoid one universal lifetime badge.

Configuration corroboration: `k8s\base\mcp-deployment.yaml:48-54` points MCP to the
API service and configured public OAuth origin. This does not mean the client uses the
internal service hostname as the OAuth resource.

## Figure 5: experience-team-casting-memory-fig1

**Takeaway:** Confirmed named teams accumulate reviewable knowledge; only eligible
database records feed future context, while files remain inspectable mirrors.

**Consumer:** `docs\experience\team-casting-memory.md`.
**Stable PNG:** `docs\diagrams\experience-team-casting-memory-fig1.png`.
**Editable source:** `docs\diagrams\src\experience-team-casting-memory-fig1.drawio`.
**Disposition:** redesign.
**Suggested composition:** A5 landscape; casting lane above a knowledge-governance lane,
with one compiler feedback path and a clearly secondary export branch.

### Nodes

| ID | Node | Classification | Evidence |
|---|---|---|---|
| T1 | Human / authorized reviewer | `native:c4` person | `apps\Agentweaver.Api\Endpoints\MemoryEndpoints.cs:189-200` |
| T2 | Cast proposal: roles, names, charters | `custom:agentweaver` | `apps\Agentweaver.Api\Casting\CastingService.cs:899-927` |
| T3 | Confirm: new / augment / recast | `native:flowchart` decision | same service `:909-922`, `:946-999` |
| T4 | Named roster and work | `custom:agentweaver` | same service `:1015-1024`, `:1073-1110`, `:1189` |
| T5 | Agent memory | `custom:agentweaver` | `apps\Agentweaver.Api\Endpoints\MemoryEndpoints.cs:150-167` |
| T6 | Decision inbox | `custom:agentweaver` | `apps\Agentweaver.Api\Memory\DecisionPromotion.cs:60-80`; `apps\web\src\pages\MemoriesPage.tsx:359` |
| T7 | Approved ledger / rejected audit record | `custom:agentweaver` | `apps\Agentweaver.Api\Memory\MemoryContextCompiler.cs:57-64`; memory trust tests `:107-166` |
| T8 | Authoritative knowledge database | `native:database` | `apps\Agentweaver.Api\Endpoints\MemoryEndpoints.cs:158-162` |
| T9 | Context eligibility / compiler | `custom:agentweaver` | `apps\Agentweaver.Api\Memory\MemoryContextCompiler.cs:57-105`, `:159-226` |
| T10 | Inspectable file mirrors | `native:flowchart` document(s) | `apps\Agentweaver.Api\Endpoints\MemoryEndpoints.cs:160-162`, `:554` |

### Connectors

| From → to | Meaning | Evidence |
|---|---|---|
| T1 → T2 | request and inspect cast proposal | `apps\Agentweaver.Api\Casting\CastingService.cs:125`, `:245`, `:408`, `:439` |
| T2 → T3 → T4 | confirm selected intent; persist roster/charters/history | same service `:899-927`, `:946-999`, `:1073-1189` |
| T4 → T5 → T8 | record memory as pending with provenance | `apps\Agentweaver.Api\Endpoints\MemoryEndpoints.cs:150-167` |
| T4 → T6 | propose a decision, not direct policy | `apps\Agentweaver.Api\Memory\DecisionPromotion.cs:60-80`; `apps\web\src\pages\MemoriesPage.tsx:359` |
| T1 → T5 | authorized promotion makes approved memory eligible | `apps\Agentweaver.Api\Endpoints\MemoryEndpoints.cs:171-205` |
| T6 → T1 → T7 | owner/verified Coordinator accepts or rejects | `apps\Agentweaver.Api\Endpoints\DecisionsEndpoints.cs:207-218`, `:286-299` invoke the approver guard before mutation |
| T7 → T8 | persist ledger / audit state | `apps\Agentweaver.Api\Endpoints\DecisionsEndpoints.cs:211-219`, `:295-299` |
| T8 → T9 | select approved boundaries and eligible memories/session | same compiler `:57-105` |
| T9 → T4 | future context as untrusted data; child scope can be narrower | same compiler `:159-226`; security tests `:22-166` |
| T8 → T10 | export mirror for inspection | `apps\Agentweaver.Api\Endpoints\MemoryEndpoints.cs:465-489` |
| T10 → T6 | optional import of missing pending inbox entries, not approval | `apps\Agentweaver.Api\Endpoints\MemoryEndpoints.cs:528-548` |

Do not draw T10 → T4: exported files are not the compiler authority. Show legacy exclusion
at T9 and approved cross-team eligibility alongside T5, not as a blanket “all memories
go to all agents” loop. Project Session history may be a T8/T9 annotation; global personal
Assistant conversations are a distinct feature and must not be the same node.

## Owned documentation changes and audit dispositions

Exact changed documentation paths:

| Path | Applied corrections / dispositions |
|---|---|
| `docs\experience\00-overview.md` | Correct event direction; remove placeholder Overview/shell embeds and fictional captions; current Overview regions; add Account/Platform/Assistant/Cluster/full Observability route mapping; Dashboard vs Board; current orchestration/session inspection; generated tool discovery and Skills; start modes; merge fig3 consumer into MCP fig1; owned `.drawio` provenance. |
| `docs\experience\README.md` | Retain linked journey table; replace GitHub sessions with Entra/capability/broker model; qualify confirmation; replace full static catalog promise; add personal Assistant and Skills journeys. |
| `docs\experience\onboarding-auth.md` | Keep Entra sign-in and setup/capability narrative; add one-time browser session exchange; replace GitHub/org OAuth issuance and three-path bearer acceptance with actual OpenIddict broker flow; exact resource/scope and 401/403 distinction; role-based authorization; remove STDIO API-key fallback; `.drawio` provenance. |
| `docs\experience\mcp-client.md` | Branch queue pickup vs immediate start; qualify Define Outcome/Direct and run_task defaults; preserve binary review and draft-before-save; avoid API-to-assistant response shortcut; qualified retry; generated/live catalog discovery, representative Skills/run/selection tools; `.drawio` provenance. |
| `docs\experience\assistant-sessions.md` | Replace raw Entra-to-MCP claim with per-turn renewable five-minute broker credentials; separate personal conversations from project work-focus sessions; correct current Idle dormancy and same-run wake behavior. |
| `docs\experience\agent-definition.md` | Keep file path and non-overwrite model; make materialization best-effort; explain existing project copies do not auto-upgrade, and filesystem failure does not fail project creation. |
| `docs\experience\projects.md` | Remove five placeholder embeds/captions; enforce Repo App selections/code; correct Dashboard landing and retired run links; selected-range metrics; provider readiness vs model IDs; current settings sections, preview defaults, and absence of Review policy tab; qualify Driver file creation. |
| `docs\experience\repo-blueprint-suggestions.md` | Remove placeholder embed/caption; keep deterministic metadata-to-catalog matching separate from Generate; Repo App-authorized selection and create-time code; fallback does not waive authorization; remove obsolete personal-account route narrative. |
| `docs\experience\project-generation-model-settings.md` | Remove placeholder embed/caption; retain provider readiness separation, three overrides, future-call scope and reset-to-null; current General section/buttons and evidence lines. |
| `docs\experience\project-skills.md` | Remove outdated catalog and placeholder import embeds/captions; five acquisition paths; preview-before-import and acquisition vs assignment; current blueprint-default preview and repository scan behavior. |
| `docs\experience\team-casting-memory.md` | `.drawio` provenance and database/compiler authority; remove unsuitable roster/drawer/cast/memory embeds/captions; retain only audited real casting proposal example with bounded caption; assigned skills; three memory tabs and session distinction; exact cross-team eligibility and child decisions-only path. |
| `docs\experience\agent-communication.md` | Retain shared handoff canonical, fix alt; retain small decision Mermaid with adjacent authority/eligibility; remove automatic finalized/cross-team propagation claims; top-down dependency semantics, conditional confirmation, visible pod/transport distinctions. |

Additional allowed path changed: this report,
`docs\diagrams\reviews\experience-00-overview-fig1\research-identity.md`.

No screenshot files were changed or deleted. The sole retained screenshot consumer in
the owned batch is `/screenshots/casting-wizard-review.png`, which the audit identified
as a real matching proposal-review example. No invented replacement images.

Only audit-declared shared stable diagrams remain referenced:
`canonical-coordinator-journey.png` and `canonical-agent-communication-handoff.png`.
Their assets and provenance migration are the shared owner's concern.

## Additional source-backed differences from the audit snapshot

- **Assistant idle behavior has moved beyond the audit's “30-minute closure.”**
  Current code parks `RunStatus.Idle`, leaves `ended_at` null, emits `run.idle`, and does
  not complete the stream. Same conversation wakes to InProgress. Corrected docs to
  current code, not the earlier audit wording.
  Evidence: `apps\Agentweaver.Api\Assistant\AssistantRunService.cs:1122-1164`;
  `tests\Agentweaver.Tests\Assistant\AssistantRunEndpointsTests.cs:847-904`.
- **Settings rail has no Review policy panel.** It is General, Access, Repository,
  Background, Sandbox policy, Danger Zone. Corrected both Projects and the overview
  mapping rather than preserving that older caption.
  Evidence: `apps\web\src\pages\ProjectSettingsPage.tsx:159-196`.
- **Skills has Preview blueprint defaults in addition to the five acquisition paths.**
  Documented it as preview/apply, not a sixth unqualified import.
  Evidence: `apps\web\src\pages\SkillsPage.tsx:949-967`.
- **Memory compiler is narrower than “approved + cross-team.”** High importance and
  learning/pattern type also apply; coordinator children may use decisions only.
  Evidence: `apps\Agentweaver.Api\Memory\MemoryContextCompiler.cs:83-92`, `:159`.

## Native symbols and credits

Use draw.io native C4 people/containers for interface and authority boundaries, UML
participants for sign-in/consent sequences, native flowchart decisions for confirmation
and review, and database/document symbols only for actual stores/files. Use the native
Azure Entra symbol for Microsoft identity; do not invent a GitHub sign-in icon for broker
auth. Custom Agentweaver cards are appropriate for Project, Team, Proposal, Coordinator,
Run, Memory, Decision Inbox, and context eligibility.

No external logo or image was downloaded. Suggested native-library assets still require
the parent's actual library/asset provenance records. Retain the prescribed warm Fluent
palette/Segoe UI hierarchy, but do not use dashed marigold rails for ordinary request
responses: reserve them for actual revision/return semantics. No PNG inspection or
four-pass/9x validation is claimed by this research.

## Validation and remaining handoff work

- PASS: `git diff --check` over the twelve owned Markdown files.
- PASS: read-only Python checks across all twelve files: balanced fenced blocks, local
  Markdown link targets, all nine image targets exist, only audit-declared owned/shared
  diagram consumers, only the retained casting-proposal screenshot, and no fig3 PNG
  consumer left.
- No runtime tests executed: no runtime files changed. Test bodies were evidence.
- The mandated integrated `npm run docs:build` was **not run by this bounded researcher**:
  it writes outside the owned-document/report allowance, and this worktree has no existing
  Markdown/VitePress dependencies at the checked roots. Parent should run it under its
  broader authorization after dependency setup and diagram promotion. This is not a
  claim that the docs build passed.
- Owned provenance now names intended canonical `.drawio` sources. Parent must promote
  the actual matching sources/PNGs before claiming those visuals are current.
- Shared journey confirmation details and shared handoff rendering remain their owners'
  reconciliation task. No foreign asset was edited.
- Tool attribute descriptions can lag actual API retry behavior (RunTools still says
  “fresh”); docs follow `RunRetryTests` for in-place versus new-run behavior.
- No unresolved identity/capability contradiction remains in the edited narrative.
  Evidence references are worktree-relative and should be rechecked if product code
  changes before promotion; some untouched historical inline citations predate this
  audit and are not the authoritative map for drawing.

### Research process note

A guessed `MemoryWriteContextResolver.cs` path did not exist. I recovered by searching
the scoped implementation and reading the actual authority guard in
`Endpoints\MemoryEndpoints.cs:189-200`. No file was created to fill that nonexistent
abstraction. This reinforces the report's rule: source symbols and test behavior, not
plausible names or legacy diagrams, determine the content model.
