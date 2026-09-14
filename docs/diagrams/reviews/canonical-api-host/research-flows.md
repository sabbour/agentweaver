## Flow map

Disposition names follow the two reports. **Arrows below represent implemented behavior, not legacy artwork.** Shared canonical diagrams are reference targets only.

### `00-system-overview-fig2` — retain: full single-agent lifecycle

- Request → orchestrator: validate accepted provider/capability boundary.
- Orchestrator → Git: create run-specific worktree/branch.
- Orchestrator → run store: persist `InProgress`, worktree identity and charter.
- Project context + task → `AgentTurnInput`: compose instructions, then launch workflow.
- Successful agent turn → Rai.
- Rai → agent: request revision below cap.
- Rai → safety-failed terminal: flagged **empty diff**.
- Rai → Scribe: unflagged **empty diff**.
- Rai → human review: nonempty diff after revision requirement clears.
- Review → merge / agent revision / declined terminal: approve / request changes / decline.
- Merge → review: retriable `blocked`; merge → Scribe: **any nonblocked result**, including terminal merge failure.
- Scribe → workflow output → watch loop: record outcome and terminal state.

**Scope correction:** label this a representative **full-run** pipeline, not every workflow or coordinator child. Children terminate assemble-ready/failed and omit their own Rai/review/merge/Scribe graph.

Evidence: [launch](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Runs/RunOrchestrator.cs#L164-L255), [conditional edges](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs#L335-L426), [child boundary](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs#L790-L825).

### `00-system-overview-fig5` — retain: memory feedback

- Agent → decision inbox: submit pending observation with source/run provenance.
- Post-run Scribe service → eligible inbox entries: select **same project, agent, run and run-time window**.
- Low-risk learning/pattern/update → approved active decisions: automatic promotion.
- Architectural/scope proposals → pending coordinator review: **not** automatic promotion.
- Run outcome → current open session: append summary.
- Committed relational memory → exporter → workspace mirrors: refresh exported context.
- Approved active architectural/scope decisions + eligible agent memories + current session → context compiler → subsequent prompt.

Do **not** draw exported files feeding back as automatic trusted policy, or every observation entering future prompts. Compiler trust filters and memory budgets apply; child prompts use the decisions-only variant.

Evidence: [inbox write](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs#L125-L143), [promotion/session/export](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Runs/PostRunScribeService.cs#L25-L150), [context selection](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs#L55-L160).

### `agent-definition-fig1` — redesign: five generated outputs

`MCP tool sources → parser/grouping → generator`, with the existing handwritten agent template supplying prose; regenerate **only its tool-map block**. Fan out to:

1. `C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring\docs\reference\mcp-tools.md`
2. `C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring\.github\agents\agentweaver.agent.md`
3. `C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring\apps\Agentweaver.Api\Projects\Templates\agentweaver.agent.md`
4. `C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring\docs\public\agents\agentweaver.agent.md`
5. `C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring\apps\Agentweaver.Web\wwwroot\agents\agentweaver.agent.md`

Then:

- Embedded copy → `AgentDefinitionTemplate` → newly created project’s agent file: best-effort, **never overwrite existing file**.
- Public/deployed copies → anonymous download consumers.
- `--check` → **all five outputs**: compare expected bytes; fail on drift/missing file.

Evidence: [targets/check loop](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/scripts/gen-docs.mjs#L243-L300), [target paths](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/scripts/gen-docs.mjs#L30-L56), [materialization](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Projects/AgentDefinitionTemplate.cs#L33-L75).

### `agent-framework-fig1` — retain: typed adapters

- `AgentTurnOutput → review adapter`: save merge data in workflow state; emit `WorkflowReviewRequest`.
- `WorkflowReviewRequest → RequestPort → external reviewer`: suspend/request decision.
- Reviewer → RequestPort: matching `WorkflowReviewDecision`.
- Approved decision + saved `AgentTurnOutput` → merge adapter → `MergeInput`.
- Blocked `MergeOutput` + saved output → blocked adapter → **same review port**.

Show state writes/reads as separate data arrows; a decision alone does not contain the worktree/tree/repository merge contract.

Evidence: [typed contracts/state adapters](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs#L409-L440), [blocked adapter](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs#L532-L545).

### `agent-framework-fig3` — redesign: response delivery ≠ restoration

Draw **three separate paths**:

1. **Live response:** reviewer → API role/state/provider checks → pending request correlation → existing `StreamingRun.SendResponseAsync` → suspended port continues.
2. **Another replica:** API without local workflow, but with pending request → durable deferred decision → owning watch-loop poll → `SendResponseAsync`.
3. **Process recovery:** recovery service → latest checkpoint → read persisted run/effective definition → rebuild correct full/child graph → `ResumeStreamingAsync` → restart watch loop/recover suspended gate.

Checkpoint side arrows: MAF → `CheckpointManager` → selected store; PostgreSQL → shared checkpoint rows; SQLite/dev → file-store variant. **Do not draw PostgreSQL failure automatically switching to files**, nor every approval restoring a checkpoint.

Evidence: [live/deferred decision selection](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/RunEndpoints.cs#L887-L964), [live response](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/RunEndpoints.cs#L1052-L1060), [deferred consumer](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Runs/RunWatchLoopService.cs#L164-L227), [restore](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs#L1561-L1586).

### `agent-framework-fig2` — retain: MAF/service handoff

- Confirmed spec-phase `CoordinatorOutcome` → persisted work-plan/subtask check.
- Confirmed + auto-dispatch + nonempty plan → `StartDispatch`.
- Handoff → release MAF registry/checkpoints; **leave coordinator run and event stream active**.
- Dispatch/assembly services ↔ relational work plan, subtasks and child runs: drive/recover later phases.
- Assembly pipeline → Rai/rubberduck/build-test/Scribe executors: direct `HandleAsync(..., NoOpWorkflowContext.Instance, ...)`, not another checkpointed MAF assembly graph.
- Restart → re-arm service driver from persisted phase; spec phase alone resumes its MAF checkpoint.

Evidence: [handoff/recovery boundary](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs#L1172-L1250), [direct Rai/review calls](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Coordinator/CollectiveAssemblyPipeline.cs#L132-L169), [build-test](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Coordinator/CollectiveAssemblyPipeline.cs#L327), [Scribe](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Coordinator/CollectiveAssemblyPipeline.cs#L499).

### `api-core-fig4` / `canonical-api-host` — redesign; absorb `api-core-fig7`

**Startup lane:** configuration/DI → database migration → leader-gated recovery → workspace health checks → serve. No client-request arrow through bootstrap.

**Request lane:**

`client → forwarded headers → exception handler → routing → CORS → rate limiter → endpoint-authorization integrity → authentication → unmatched-endpoint handling → authorization → endpoint → resource/project-role check → service/store/workspace → DTO/result → client`.

Integrity validates endpoint metadata, **not URL-prefix exemptions**. Worker role exposes probes rather than this application-route surface.

Evidence: [startup and exact middleware order](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Program.cs#L1200-L1300), [integrity behavior](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Auth/EndpointAuthorization.cs#L139-L174), [resource-role example](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/RunEndpoints.cs#L429-L445).

### `00-system-overview-fig8` → `canonical-durable-event-stream-sequence`

**Important implementation refinement beyond the audit:** depict **both endpoint paths**, not universal database polling.

- Producer → shared event store: durable append before acknowledgement.
- PostgreSQL append → per-run transaction lock → allocate sequence → persist.
- Subscriber replica **without local stream entry** → `SubscribeAsync(cursor)` → poll shared rows → SSE frames → advance cursor.
- Subscriber **with local entry** → atomic `GetSnapshotSince(cursor)` + `WaitForChangeAsync` → SSE frames.
- Local review gate → close transport with `done`; this is **not run completion**.
- Persisted terminal replay → drain batch → finish; retryable assembly-blocked is not an unconditional terminal.

Evidence: [durability/polling](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs#L15-L37), [shared subscription](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs#L143-L171), [endpoint fallback selection](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/RunEndpoints.cs#L457-L491), [local entry path](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/RunEndpoints.cs#L514-L555).

### `frontend-fig1`, `frontend-fig7`; snapshot/projection canonical references

- Route params → page → REST metadata/graph/work-plan/children fetches.
- `useSeededRunStream` → REST event history **and independently** `useRunStream`: do not imply stream waits for snapshot.
- REST seed + live events → merge: positive-sequence dedupe; restricted sequence-zero singleton handling.
- Merged events + server-authored topology seed → reducers/projection → timeline, graph, status and controls.
- Browser → stream endpoint: authenticated `fetch`, credentials included, `Last-Event-ID`.
- Frames → parser → dedupe → bounded buffer/cursor.
- Unexpected disconnect → bounded reconnect backoff; `done`/terminal → stop; explicit reconnect → reopen after gate action.
- Stale REST response → generation/run guard: reject cross-run replacement.

Evidence: [snapshot/live composition](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/web/src/hooks/useSeededRunStream.ts#L43-L151), [merge rules](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/web/src/timeline/mergeRunEvents.ts#L23-L79), [transport](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/web/src/api/sse.ts#L239-L337), [topology projection](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/web/src/pages/CoordinatorRunPage.tsx#L2842).

### `frontend-fig2`, `frontend-fig3`, `frontend-fig6` — redesign

- **Routes:** shell/router → current global and project-scoped surfaces; redirects are explicit arrows (`/console → /assistant`, project sessions → global sessions with project query). Include platform-settings authorization and current skills/observability/cluster surfaces.
- **Hosting:** browser → Web host → static SPA; unknown SPA route → index fallback; `/docs[/…] → external documentation redirect`. Runtime config supplies **API origin**, with `""` meaning same-origin; clients append `/api`.
- **Auth:** browser → API Entra authorize → Entra → API callback/state validation → one-time exchange code → frontend → session-exchange POST → validated access token + browser session → per-tab `sessionStorage` → REST/SSE bearer.
- Same-origin tab → `BroadcastChannel` request/response → transient token transfer; no localStorage/token-in-URL arrow.

Evidence: [routes](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/web/src/App.tsx#L80-L135), [hosting](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Web/Program.cs#L39-L65), [origin/storage](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/web/src/config.ts#L13-L138), [callback/exchange](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs#L398-L455).

### `mcp-server-fig2` — redesign: discovery, consent and routing

- MCP client → `/mcp` → challenge containing protected-resource metadata.
- Client → either protected-resource metadata URL → configured resource/authorization server/scope.
- Client/browser → authorization server → exact-resource + `mcp:invoke` validation → Entra sign-in when needed → consent.
- Consent approve → authorization code; deny → `access_denied`; existing sufficient consent may skip prompt.
- Code + PKCE → `/oauth/token` → broker access/refresh tokens.
- Refresh → exact-resource/grant/family-revocation checks → token response.
- Client bearer → MCP validation → tool → API client → API: forward **same validated broker bearer**; API independently authorizes.
- Stdio → configured broker token, only without inbound HTTP context.
- Gateway `/mcp` and protected-resource metadata → MCP service; API owns authorization/token/revocation/JWKS endpoints.

Evidence: [gateway routes](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/k8s/base/mcp-httproute.yaml#L30-L46), [consent/resource/family checks](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs#L40-L190), [PKCE/endpoints](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Program.cs#L982-L998), [MCP validation](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs#L64-L115), [forwarding](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Mcp/AgentweaverApiClient.cs#L353-L391).

### `project-generation-model-settings-fig1` — retain

- Settings controls → project update → three persisted nullable preferences.
- Blueprint preference → resolver → blueprint generator.
- Workflow preference → resolver → **fallback workflow generator when no library workflow is selected**.
- Outcome-spec preference → coordinator input → spec drafter.
- Resolver precedence: project → per-flow configuration → shared generation model → built-in default.
- Separate authority arrow: caller/project → execution-plan/provider admission → model invocation. Preference is **not authorization**.

Evidence: [save values](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/web/src/pages/ProjectSettingsPage.tsx#L617-L648), [stored update](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs#L815-L817), [admission/preferences](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/BlueprintEndpoints.cs#L119-L141), [fallback](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Blueprints/BlueprintService.cs#L765-L773), [precedence](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Generation/GenerationModelOptions.cs#L37-L75), [spec consumer](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Coordinator/CopilotCoordinatorSpecDrafter.cs#L145).

### `project-skills-fig1` — redesign; defaults-preview companion

- Connected checkout sync / repository import / marketplace / upload / manual entry → discover/parse/validate → content-hash upsert → project catalog.
- Missing connected source → `Missing`; malformed same-source existing entry → `Malformed`; invalid candidate → rejection.
- Catalog → explicit agent assignments → active-assignment lookup.
- Shared execution filesystem → remove stale folders → **materialize successfully first** → metadata/path in prompt → agent reads relevant skill.
- Pod-local/unavailable filesystem or write failure → **inline full instructions**, not dangling path.

**Defaults preview:** blueprint role bindings + confirmed active team + bundled skills + current catalog/assignments → side-effect-free preview/digest. Explicit apply → recompute preview → compare digest → transactional state guard → catalog inserts/reactivations/assignments, or stale rejection. Do not draw preview automatically acquiring/applying skills.

Evidence: [acquisition/status paths](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Skills/SkillCatalogService.cs#L335-L372), [upsert validation](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Skills/SkillCatalogService.cs#L1045-L1156), [delivery branches](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Skills/SkillPromptComposer.cs#L45-L160), [preview/apply](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Skills/SkillDefaultsService.cs#L55-L250).

### `projects-fig1`, `projects-fig2` — retain

**Provisioning**
- Create request + new project ID → selected workspace provider → resolved path → create directory/write-delete probe → workspace handle.
- Local: supplied absolute path, otherwise configured root/project ID.
- Persistent volume: mount root/project ID; **ignore requested path**.
- Healthy workspace → Git initialization/clone → scaffolds → project persistence; failure → error/rollback, not active project.

**Relations**
- Project → defaults, stable base checkout, team/workflow files: owns/configures.
- Run → project: belongs to; run → own worktree/branch: executes against.
- Child run → own worktree/branch/tree hash → collective assembly: contributes artifact.
- Workspace provider → project checkout: provisions; sandbox/AgentHost → execution workspace: executes. Do not equate provider, project, worktree and sandbox.

Evidence: [local provider](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Infrastructure/LocalFilesystemWorkspaceProvider.cs#L33-L62), [persistent provider](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Infrastructure/PersistentVolumeWorkspaceProvider.cs#L29-L75), [project creation](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Projects/ProjectService.cs#L47-L111), [run worktree](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Runs/RunOrchestrator.cs#L194-L245).

### `repo-blueprint-suggestions-fig1` — redesign

- Suggested tab + repository string → suggestion API → parse owner/repo.
- Service → ambient credential boundary → **null token in current registration**.
- Service → GitHub repository metadata, languages, root contents: anonymous metadata requests.
- Signals → ordered deterministic catalog matching → blueprint/rationale/confidence/signals → UI.
- Invalid input/unavailable metadata/recoverable transport failure → fallback response → Templates option.
- **Caller cancellation → propagate cancellation**, not fallback.
- Generate tab → separate model-generation path; no model arrow in suggestions.

Evidence: [UI request](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/web/src/components/BlueprintPicker.tsx#L487-L517), [registered null-token boundary](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Program.cs#L265-L271), [null result](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Auth/EntraOnlyGitHubCredentialBoundary.cs#L38-L39), [requests/fallback/cancellation/scoring](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs#L40-L184).

## Owned-doc corrections to carry with the pitches

- **Five targets**, not three: [agent-definition prose](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/agent-definition.md#L46-L65), also lines 88 and 103.
- Replace “every human gate is RequestPort” with **MAF human gates**; separate service-driven assembly approval. Rename the PostgreSQL sequence currently captioned file-store safety net: [agent-framework](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/agent-framework.md#L54-L108).
- Remove token/org-prefix gate explanations and universal in-process-channel delivery: [api-core middleware](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/api-core.md#L173-L198), [event delivery](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/api-core.md#L283-L302).
- Correct hosting/auth artwork and residual “GitHub sign-in” checklist: [frontend hosting/auth](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/frontend.md#L162-L202), [checklist](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/frontend.md#L431).
- Replace unconditional lazy skill delivery with materialize-before-pointer/inline branches: [project-skills](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/project-skills.md#L10-L16).
- Correct caller-token and cancellation claims: [repo suggestions](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/repo-blueprint-suggestions.md#L20-L37).
- Remove obsolete `/api/github/accounts` and `/api/github/repos` picker narrative from [projects](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/projects.md#L123) and [repo suggestions](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/repo-blueprint-suggestions.md#L29). Current flow is metadata-only repository selections → verified **single-use selection code** → project creation: [implemented contract](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/GitHubRepositorySelectionEndpoints.cs#L12-L89).
- Qualify “promotion is explicit”: low-risk, run-provenanced entries are automatically promoted by the post-run service; architectural/scope entries remain reviewed: [overview](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/00-system-overview.md#L215).

Test evidence inspected—not executed—corroborates [cross-instance event tailing](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/EfRunEventStreamTests.cs#L28), [non-clobbering agent materialization](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/Projects/AgentDefinitionTemplateTests.cs#L69), and [pod-local skill delivery](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/Skills/SkillPodPerRunDeliveryTests.cs#L139).
