# Frontend — Conceptual Deep Dive

## Purpose and Mental Model

Agentweaver's frontend is a browser-based control room for agent work. It does not run agents, decide orchestration topology, or persist long-term state itself. Its job is to:

1. authenticate the user,
2. let the user choose a project and issue commands,
3. ask the backend for authoritative snapshots,
4. subscribe to live run events,
5. fold those events into UI-friendly state, and
6. render the current state clearly enough that a human can steer, approve, inspect, or recover work.

The most important rebuilding idea is **snapshot + stream**:

- **Snapshots** answer, "What does the backend know right now?" They come from REST calls and are used when a page first loads, when a completed run is reopened, or when the UI needs metadata such as project lists, teams, graph descriptors, work plans, files, and settings.
- **Streams** answer, "What changed after I started watching?" They come from Server-Sent Events (SSE) on a run stream.
- **Reducers** turn raw events into display models: timelines, run status, graph state, coordinator topology, approval cards, and child request lists.

This gives the UI a robust mental model: the backend is the source of truth; the frontend is a deterministic projection of backend facts.

Where this lives:

- `apps/web/`
- `apps/Agentweaver.Web/`

## Frontend Boundary

The frontend has three runtime layers:

1. **React/Vite SPA** — the application the user interacts with. It owns routing, presentation, browser state, REST calls, SSE consumption, and UI projections.
2. **Agentweaver API** — the authoritative backend. It owns projects, auth, runs, orchestration, work plans, event logs, files, reviews, and mutations.
3. **Static web host** — a small ASP.NET Core app that serves the built SPA and redirects `/docs` to the external documentation site. It is not a backend-for-frontend and does not implement the SPA's API routes.

A rebuild should preserve that boundary. Avoid putting business decisions in the browser just because the browser has enough data to guess. For example, the coordinator graph is server-authored: the UI renders topology snapshots and deltas instead of recomputing dependencies on the client.

Trade-off: this makes the UI simpler and safer, but it means the backend must emit complete enough facts for the UI to render useful state.

Where this lives:

- `apps/web/src/`
- `apps/Agentweaver.Web/Program.cs`

## Technology Shape

The SPA is a TypeScript React app built with Vite. It uses React Router for browser routes, Fluent UI for the visual system, React Flow/Dagre for graph-like views, and Vitest/Testing Library for frontend tests.

The entrypoint mounts React into the `#root` element, wraps the app in React `StrictMode`, and uses a small error boundary so a render exception becomes a recoverable error screen instead of a blank page.

At the app root, the UI is wrapped in:

- a Fluent UI provider, so components share theme tokens,
- a browser router, so deep links are normal URLs,
- an auth gate, so protected app routes do not render until session validation completes,
- a persistent shell, so navigation and top-level context remain stable across pages.

`App.tsx` declares `<Routes>` explicitly; it does not consume a runtime route registry. `AppShell` supplies `ProjectListProvider` and `NotificationsProvider`. Showcase/provider examples are not the product's composition root.

Rebuild principle: keep the app root boring. Cross-cutting concerns belong there; feature behavior belongs in pages, hooks, reducers, and components.

Where this lives:

- `apps/web/src/main.tsx`
- `apps/web/src/App.tsx`
- `apps/web/package.json`
- `apps/web/vite.config.ts`

## Routing and Information Architecture

Routes are split into **global** destinations and **project-scoped** destinations.

Global routes do not require a project id:

- overview and project gallery/creation,
- Assistant, sessions, skills, observability, and cluster surfaces,
- platform settings, with its own authorization gate.

Project-scoped routes start with `/projects/:projectId` and represent the work surface for one project:

- dashboard,
- board,
- flow,
- orchestrations,
- workspace,
- settings,
- team / casting,
- memories,
- workflows,
- diagnostics / heartbeat,
- orchestration detail pages with embedded run inspection.

All signed-in routes sit inside the persistent shell. The shell is intentionally above individual pages because navigation, project switching, top bar status, and the floating orchestration action should not disappear when the user opens a deep orchestration page.

The shell derives the active project from the URL. When the user moves to a global page, it remembers the last active project in local storage so the project switcher and project-scoped navigation can still point somewhere useful. This is a UX convenience only; the route remains the source of truth for the currently displayed page.

Rebuild principle: routes should describe user intent, not implementation detail. An orchestration detail URL should be directly openable after refresh, and the page should be able to reconstruct its state from route parameters plus backend snapshots.

Where this lives:

- `apps/web/src/App.tsx`
- `apps/web/src/components/shell/`

## API Client Design

The frontend uses one conceptual API client: a typed wrapper around `fetch`. Each method describes a backend operation in application terms, while the private request layer handles shared mechanics:

- combine the configured base URL with a method path,
- attach session auth if present,
- include cookies for cookie-backed sessions,
- JSON-encode request bodies,
- parse successful JSON responses,
- throw a structured API error for non-OK responses.

### API origin, not an `/api` base path

**`API_URL` is an origin, or an empty string for same-origin requests.** Client method paths include `/api` where the actual endpoint does; authentication and protocol paths may be rooted elsewhere.

For example, `API_URL=""` plus `/api/projects` calls the same-origin API. `API_URL="http://localhost:5000"` plus `/api/projects` calls the development API. Configuring `/api` as the origin would produce the incorrect `/api/api/projects`.

This convention is what lets the same SPA run in multiple environments:

- local development can point at `http://localhost:5000`,
- containerized production can use `""`, with the gateway routing same-origin API requests,
- the bundle does not need to be rebuilt just because the API origin changes.

### Why centralize API calls?

Centralization gives the app one place to solve auth, errors, request formatting, and response typing. Pages can stay focused on interaction flow: "create a run," "load graph," "approve review," or "list projects." It also makes conventions enforceable; a new endpoint should be added as a method that accepts application inputs and returns typed application data.

Trade-off: the API client can become large. Keep it organized around backend resource groups and avoid embedding page-specific UI decisions in it.

Where this lives:

- `apps/web/src/api/apiClient.ts`
- `apps/web/src/api/client.ts`
- `apps/web/src/api/types.ts`
- `apps/web/src/config.ts`

## Runtime Configuration and Static Hosting

The SPA is built once and configured at container startup. `index.html` loads `/env-config.js` before the React bundle. Runtime `window.__AGENTWEAVER_CONFIG__` supplies the API origin; an empty string selects same-origin routing.

This design separates build-time artifacts from deployment-time configuration:

- Vite builds static JavaScript, CSS, and assets.
- The container decides where the API is at startup.
- The ASP.NET Core host serves SPA files and redirects `/docs` and its descendants externally; it does not bundle the VitePress site.
- Non-HTML assets can be cached aggressively because their built filenames are content-addressed by Vite.
- HTML and fallback responses should not be treated as immutable because they bootstrap the current app version and runtime config.

Rebuild principle: static hosting should be dumb and predictable. Let the API own API behavior; let the SPA own client behavior; let the host serve files and route unknown non-doc paths back to `index.html` for client-side routing.

Where this lives:

- `apps/web/index.html`
- `apps/web/Dockerfile`
- `apps/web/docker-entrypoint.sh`
- `apps/Agentweaver.Web/Program.cs`

## Authentication and Session Flow

The UI starts in an auth gate. It does not render the signed-in shell until it has resolved any auth redirect and verified the current session with the backend.

Conceptually, sign-in works like this:

1. The unauthenticated page sends the browser to the backend Entra authorization endpoint.
2. The backend completes Entra authentication and redirects back to the SPA with a short-lived, one-time exchange code.
3. Before rendering protected routes, the auth gate exchanges that code for session information.
4. The frontend stores the session token and login in `sessionStorage`.
   A newly opened same-origin tab requests the token from an already authenticated tab
   through a transient `BroadcastChannel` exchange. The token is not copied to
   `localStorage`, cookies, URLs, or other durable cross-tab storage.
5. The API client sends the token as a bearer header when present and also includes cookies.
6. The auth gate asks the backend for auth status. The shared HttpOnly browser cookie can
   authenticate this bootstrap check, but it intentionally cannot authorize general platform
   APIs such as `/api/projects`; those calls still require the per-tab bearer token.
7. If the backend says the user is signed in, the shell renders. Otherwise, local session state is cleared and the sign-in page renders.

If multiple API calls reject the same stale bearer token at once, the client performs one
shared peer-recovery request and lets all failed calls retry with the recovered token. This
prevents a burst of concurrent 401 handlers from clearing a token that another call just
restored.

The stored login is not just display data. The auth gate compares it with the backend-reported login. If the browser has a token for one user but the backend session reports another, the UI clears local session state rather than silently mixing identities.

The top bar separately fetches auth status for avatar/login display and exposes sign-out. Sign-out calls the backend and returns the browser to the app root.

Trade-offs:

- `sessionStorage` limits token lifetime to the browser tab/session. Same-origin tabs can
  transfer the current token directly while an authenticated peer remains open; a new
  browser session with no authenticated peer must sign in again.
- Sending both bearer auth and cookies supports session bootstrap plus bearer-protected API
  calls without expanding cookie authentication to mutation endpoints, which would require a
  broader CSRF design.
- URL auth parameters are stripped after exchange so tokens/codes do not linger in browser history or copied links.

Where this lives:

- `apps/web/src/App.tsx`
- `apps/web/src/config.ts`
- `apps/web/src/pages/SignInPage.tsx`
- `apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs`

## State Management Philosophy

Agentweaver does not use a single global Redux-style store. State is scoped to the part of the UI that owns it:

- auth/session state lives in the auth gate and browser session storage,
- the project list lives in a small React context shared by shell components,
- the last active project lives in local storage as a navigation convenience,
- page-level forms and toggles live in local component state,
- run timelines and coordinator topology are derived from event streams through reducers,
- persisted backend state is reloaded through REST snapshots instead of being treated as browser-owned.

This keeps state lifetimes aligned with user workflows. A page can be remounted when the active project changes, forcing clean refetches. A deep orchestration page can be opened directly and rebuilt from snapshots plus the stream. A shell-level project switcher can share the project list without making every feature depend on a global app store.

Rebuild principle: store the minimum browser state needed for responsiveness and navigation. Anything authoritative should be fetched from, or streamed by, the backend.

Where this lives:

- `apps/web/src/hooks/useProjectList.tsx`
- `apps/web/src/components/shell/projectContext.ts`
- `apps/web/src/timeline/`
- `apps/web/src/state/topologyReducer.ts`

## Live Run Timeline: Event-Sourced UI Projection

The live run UI is the heart of the frontend. It treats a run as an ordered stream of facts.

A run can emit events such as:

- agent turn started / ended,
- message deltas and final messages,
- tool calls and tool results,
- shell/tool approval requests,
- workflow graph updates,
- sandbox warnings,
- review and merge lifecycle events,
- coordinator lifecycle events,
- subtask status changes,
- child questions or approvals,
- terminal completion/failure events.

The stream hook uses `fetch`, not browser `EventSource`. That is intentional: authenticated streams need custom headers such as `Authorization`, and replay after reconnect benefits from `Last-Event-ID`.

The hook keeps a bounded event buffer so a runaway stream does not grow the DOM forever. It recognizes terminal events so completed streams stop reconnecting. It uses reconnect backoff so transient network issues do not immediately fail the page.

### Reconnect after coordinator confirmation

Coordinator runs pause the stream at the confirmation gate: when the run enters `awaiting_confirmation`, the backend closes the stream with a `done` event. At that point `OutcomeSpecPanel` fetches the latest spec directly from the REST API (`fetchSpec()`) so the panel always shows the persisted, authoritative spec rather than reconstructed stream state. The internal `terminalRef` is reset and `reconnectKey` is incremented, which causes `useRunStream` to re-open a fresh stream against the same run ID. When the user clicks **Confirm**, the frontend calls `onReconnect()`, which triggers the same `reconnectKey` increment and stream re-open. New coordinator events — work-plan creation, subtask dispatch, child run starts — start flowing immediately after confirmation without a manual page refresh.

The timeline builder is pure: given an event list, it returns the display model. It groups activity by reported intent, pairs tool calls with results, and keeps messages with the step that produced them. The same event sequence produces the same timeline whether it came live from SSE or from a persisted event log.

This is the key mental model: **SSE events are not rendered directly. They are normalized into durable UI concepts.**

Where this lives:

- `apps/web/src/api/sse.ts`
- `apps/web/src/timeline/runTimelineSteps.ts`
- `apps/web/src/components/RunTimeline.tsx`
- `apps/web/src/components/AgentSessionPanel.tsx`
- `apps/web/src/pages/CoordinatorRunPage.tsx`

## Snapshot + Stream Synchronization

A live stream alone is not enough. Users frequently open pages after work has already started or completed. A completed run may no longer have an active stream. A coordinator topology snapshot may have been emitted before the browser connected.

Agentweaver solves this by merging independent inputs; opening the stream does not wait for the REST seed:

1. **REST seed** — load the latest known snapshot or persisted event list.
2. **SSE stream** — subscribe concurrently and buffer live changes.
3. **Deduplication** — avoid showing the same event twice, usually by sequence id.
4. **Timeline projection** — derive display state from the merged event list. Run/generation guards reject stale seed responses; positive sequences are deduplicated, with restricted handling for sequence-zero singleton events.

The backend side of reconnect is a durable cursor, not a cross-replica live channel.
The Postgres event provider reads ordered rows after the last delivered sequence;
the client-side REST seed, buffering and reducer fold remain the separate steps above.

For embedded single-agent/child runs, the surface resolves run metadata, optionally fetches persisted events for terminal or parked states, fetches a graph descriptor when needed, and then merges live stream events over the seed.

For coordinator runs, the page loads graph/work-plan/children snapshots so the all-up graph and agent rail render immediately, then applies coordinator SSE events as live deltas.

Trade-off: merge logic adds complexity, but it gives a much better operator experience. Refreshing a finished run should not show an empty timeline just because the live stream has already closed.

Where this lives:

- `apps/web/src/pages/CoordinatorRunPage.tsx`
- `apps/web/src/api/sse.ts`

## Single-Agent Run Flow

Inspection of an existing single-agent or coordinator-child run follows this path. Public `POST /api/runs` is retired (410); new work enters through coordinator submission rather than a direct single-agent creation API:

1. A project orchestration creates work and, when needed, child runs.
2. The backend creates the run and returns identifiers.
3. The run appears in project/coordinator surfaces.
4. Embedded inspection resolves the run metadata and stream key.
5. The surface loads any persisted seed events and graph descriptor.
6. The surface opens the SSE stream.
7. The timeline and graph update as events arrive.
8. Review, request-changes, commit, and merge actions call the API and then refresh or reconnect the stream projection.

Run inspection is deliberately built from reusable pieces: timeline, graph/workflow panels, review controls, sandbox/files panels, and stream hooks. A rebuild should keep the stream projection independent from the visual layout so the same run projection can appear in different contexts.

Important edge case: coordinator child runs may not appear in the parent project run list because they are children, not top-level project runs. Embedded inspection can still resolve them directly by run id and treat that run id as the stream/graph key.

Where this lives:

- `apps/web/src/components/NewRunDialog.tsx`
- `apps/web/src/components/ReviewPanel.tsx`

## Coordinator Orchestration Flow

Coordinator mode is the multi-agent execution path. The frontend presents it as one orchestration, but internally it is a coordinator run plus child runs.

Conceptually:

1. The user gives a goal.
2. The coordinator drafts or confirms an outcome specification.
3. The backend decomposes the goal into a work plan and topology.
4. Subtasks are dispatched to child runs.
5. Child runs emit their own events, questions, tool approvals, and terminal states.
6. The coordinator stream re-projects the all-up lifecycle so the user can monitor and steer from one page.
7. When children are ready, assembly/review/merge phases progress through coordinator events.

The topology reducer is intentionally thin. It applies server-authored snapshots and deltas, merges subtask status updates, and attaches steering state to existing nodes. It does not invent dependencies or compute topology from scratch. This protects the UI from accidentally disagreeing with backend scheduling rules.

Coordinator pages also need special handling for child questions and approvals. The user sees them in the all-up coordinator page, but the response must be sent to the child run that asked. Therefore each displayed request carries the child run id and, when available, the subtask id.

Automation toggles such as autopilot and auto-approve tools are shown at the coordinator level, but backend behavior may cascade them to children. The UI uses optimistic state for responsiveness and reverts on API failure.

Rebuild principle: show the user one orchestration, but keep run ownership precise. Coordinator commands go to the coordinator; child answers and tool grants go to the requesting child.

Where this lives:

- `apps/web/src/components/StartOrchestrationDialog.tsx`
- `apps/web/src/pages/CoordinatorRunPage.tsx`
- `apps/web/src/state/topologyReducer.ts`
- `apps/web/src/components/AgentRail.tsx`

## How the UI Stays in Sync

The UI stays in sync by following these rules:

1. **Use route params as identity.** A page knows which project/run to load from the URL.
2. **Fetch snapshots on entry.** Load enough REST data to render immediately, even for completed runs.
3. **Subscribe to the run stream.** Open one SSE stream for the run currently being watched.
4. **Replay from the last event id.** On reconnect, ask the backend for events after the last seen sequence.
5. **Deduplicate defensively.** Streams, snapshots, reconnects, and singleton events can overlap.
6. **Fold, do not mutate ad hoc.** Raw events become stable UI state through reducers and derived selectors.
7. **Let terminal events stop liveness.** Completed/failed/merged/declined states should not reconnect forever.
8. **Treat backend snapshots as authoritative.** Especially for coordinator topology, work plans, child ownership, and run status.

This pattern is close to event sourcing, but only on the client projection side. The frontend does not own the event log; it consumes the backend's event log and renders a projection.

## Error Handling and Recovery

Frontend error handling is layered:

- render errors are caught by the root error boundary,
- API non-OK responses become structured client errors,
- auth failures clear local session and return to sign-in surfaces,
- stream failures reconnect with backoff where safe,
- missing optional snapshots are tolerated when the stream can still provide state,
- missing durable logs fall back to live SSE when available,
- terminal/parked runs use persisted events because no live stream may exist.

A rebuild should distinguish between fatal and non-fatal failures. Failure to fetch an optional graph descriptor should not prevent the timeline from rendering. Failure to validate auth should prevent protected routes. Failure to reconnect a run stream after repeated attempts should surface an actionable status rather than silently freezing.

## Content and Safety Considerations

Timeline text is rendered through the shared safe Markdown surface. Display helpers shorten noisy file paths in row titles while the full result remains available in details.

The important design principle is to make untrusted run output observable without making it executable. Agent and tool output should be treated as data.

Where this lives:

- `apps/web/src/timeline/runTimelineSteps.ts`
- `apps/web/src/components/RunTimeline.tsx`
- `apps/web/src/components/SafeMarkdown.tsx`

## Rebuild Checklist

If rebuilding the Agentweaver frontend from scratch, implement in this order:

1. Static Vite React shell with routing and a root error boundary.
2. Runtime config loader that can set API base URL at deployment time.
3. Typed API client with centralized auth, credentials, JSON parsing, and API errors.
4. Entra sign-in handoff, session exchange, session validation, and sign-out.
5. Persistent app shell with global/project navigation and project context.
6. Project list/provider and project-scoped pages.
7. Run stream hook using fetch-based SSE with auth headers, `Last-Event-ID`, dedupe, terminal detection, and reconnect backoff.
8. Pure timeline reducer that folds raw events into display items.
9. Embedded single-agent/child run inspection using REST seeds plus live SSE.
10. Coordinator page using graph/work-plan/children seeds plus coordinator SSE.
11. Thin topology reducer that applies server-authored snapshots and deltas.
12. Review, approval, question-answering, and steering actions that call the correct owning run.
13. Static hosting with SPA fallback and external docs redirects.

## Gotchas and Conventions

- Configure `API_URL` as an origin or `""`; method paths retain their actual `/api` prefix.
- The static web host is not the API. It serves SPA files/fallbacks and redirects docs externally.
- Runtime API URL should override build-time environment so one bundle can deploy to multiple environments.
- Use fetch-based SSE, not plain `EventSource`, if authenticated headers and replay control are required.
- Finished or parked runs need REST seeds because their live stream may already be closed.
- Coordinator topology is server-authored; render it instead of recomputing it.
- Child questions and tool approvals shown on the coordinator page must be answered against the child run that asked.
- Keep browser state small. Backend state is authoritative; UI state is a projection.

<details id="diagram-context-canonical-durable-event-stream">
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Postgres is the event relay</td></tr>
<tr><td>subtitle</td><td>Any API replica can serve a cursor over durable RunEvents—no sticky session required.</td></tr>
<tr><td>group-title0</td><td>Write path · replica A</td></tr>
<tr><td>group-title1</td><td>Read path · replica B</td></tr>
<tr><td>Run producer</td><td>Run producer</td></tr>
<tr><td>Run producer</td><td>Append a structured event</td></tr>
<tr><td>Run producer</td><td>runId + type + payload</td></tr>
<tr><td>EF event stream</td><td>EF event stream</td></tr>
<tr><td>EF event stream</td><td>Serialize writes per run</td></tr>
<tr><td>EF event stream</td><td>pg_advisory_xact_lock</td></tr>
<tr><td>RunEvents</td><td>RunEvents</td></tr>
<tr><td>RunEvents</td><td>Shared PostgreSQL table</td></tr>
<tr><td>RunEvents</td><td>(RunId, Sequence)</td></tr>
<tr><td>Web / MCP watcher</td><td>Web / MCP watcher</td></tr>
<tr><td>Web / MCP watcher</td><td>Consume ordered events</td></tr>
<tr><td>Web / MCP watcher</td><td>last delivered cursor</td></tr>
<tr><td>SSE endpoint</td><td>SSE endpoint</td></tr>
<tr><td>SSE endpoint</td><td>Emit id + event + data</td></tr>
<tr><td>SSE endpoint</td><td>ordered response frames</td></tr>
<tr><td>EF subscriber</td><td>EF subscriber</td></tr>
<tr><td>EF subscriber</td><td>Read Sequence &gt; cursor</td></tr>
<tr><td>EF subscriber</td><td>idle poll: 250 ms</td></tr>
<tr><td>e1</td><td>append</td></tr>
<tr><td>e2</td><td>commit</td></tr>
<tr><td>e3</td><td>ordered batch</td></tr>
<tr><td>e4</td><td>yield</td></tr>
<tr><td>e5</td><td>SSE frames</td></tr>
<tr><td>assurance-title</td><td>POSTGRES LANE ONLY</td></tr>
<tr><td>assurance-line1</td><td>SQLite register-channel / replay / tail is a separate implementation—not this architecture.</td></tr>
<tr><td>assurance-line2</td><td>Late-delta suppression is process-local; do not read it as a database-wide terminal fence.</td></tr>
<tr><td>Run producer</td><td>Input</td></tr>
<tr><td>Run producer</td><td>RunStreamEntry</td></tr>
<tr><td>Run producer</td><td>Identity</td></tr>
<tr><td>Run producer</td><td>runId + event type</td></tr>
<tr><td>Run producer</td><td>Body</td></tr>
<tr><td>Run producer</td><td>Structured payload</td></tr>
<tr><td>Run producer</td><td>Ack</td></tr>
<tr><td>Run producer</td><td>After durable commit</td></tr>
<tr><td>EF event stream</td><td>Lock</td></tr>
<tr><td>EF event stream</td><td>Per-run advisory lock</td></tr>
<tr><td>EF event stream</td><td>Next</td></tr>
<tr><td>EF event stream</td><td>MAX(Sequence) + 1</td></tr>
<tr><td>EF event stream</td><td>Write</td></tr>
<tr><td>EF event stream</td><td>Save transaction</td></tr>
<tr><td>EF event stream</td><td>Commit</td></tr>
<tr><td>EF event stream</td><td>Before acknowledgement</td></tr>
<tr><td>RunEvents</td><td>Table</td></tr>
<tr><td>RunEvents</td><td>Key</td></tr>
<tr><td>RunEvents</td><td>RunId + Sequence</td></tr>
<tr><td>RunEvents</td><td>Order</td></tr>
<tr><td>RunEvents</td><td>Ascending sequence</td></tr>
<tr><td>RunEvents</td><td>Reuse</td></tr>
<tr><td>RunEvents</td><td>Same type / payload</td></tr>
<tr><td>Web / MCP watcher</td><td>Client</td></tr>
<tr><td>Web / MCP watcher</td><td>Web or MCP</td></tr>
<tr><td>Web / MCP watcher</td><td>Resume</td></tr>
<tr><td>Web / MCP watcher</td><td>Last delivered cursor</td></tr>
<tr><td>Web / MCP watcher</td><td>Replica</td></tr>
<tr><td>Web / MCP watcher</td><td>No sticky requirement</td></tr>
<tr><td>Web / MCP watcher</td><td>History</td></tr>
<tr><td>Web / MCP watcher</td><td>Durable ordered events</td></tr>
<tr><td>SSE endpoint</td><td>Frame</td></tr>
<tr><td>SSE endpoint</td><td>id + event + data</td></tr>
<tr><td>SSE endpoint</td><td>Cursor</td></tr>
<tr><td>SSE endpoint</td><td>Last-Event-ID</td></tr>
<tr><td>SSE endpoint</td><td>Delivery</td></tr>
<tr><td>SSE endpoint</td><td>Yield ordered events</td></tr>
<tr><td>SSE endpoint</td><td>Close</td></tr>
<tr><td>SSE endpoint</td><td>After batch is drained</td></tr>
<tr><td>EF subscriber</td><td>Query</td></tr>
<tr><td>EF subscriber</td><td>Sequence &gt; cursor</td></tr>
<tr><td>EF subscriber</td><td>Idle</td></tr>
<tr><td>EF subscriber</td><td>Poll after 250 ms</td></tr>
<tr><td>EF subscriber</td><td>State</td></tr>
<tr><td>EF subscriber</td><td>Shared durable table</td></tr>
<tr><td>EF subscriber</td><td>Blocked</td></tr>
<tr><td>EF subscriber</td><td>Retryable: keep open</td></tr>
<tr><td>producer</td><td>Coordinator or run execution; Acknowledgement follows commit</td></tr>
<tr><td>append</td><td>Allocate MAX(Sequence) + 1; Save and commit transaction</td></tr>
<tr><td>store</td><td>Cross-replica ordered history; Explicit duplicates must match payload</td></tr>
<tr><td>client</td><td>Reconnect from the cursor; No local channel dependency</td></tr>
<tr><td>sse</td><td>Cursor advances after delivery; Drain batch before terminal close</td></tr>
<tr><td>reader</td><td>Query the shared durable table; Retryable assembly_blocked stays open</td></tr>
<tr><td>notes</td><td>POSTGRES LANE ONLY; SQLite register-channel / replay / tail is a separate implementation—not this architecture.; Late-delta suppression is process-local; do not read it as a database-wide terminal fence.</td></tr>
<tr><td>groups</td><td>Write path · replica A; Read path · replica B</td></tr>
</tbody></table>
</details>

<details id="diagram-context-frontend-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Frontend: intent and projection</td></tr>
<tr><td>takeaway</td><td>The browser presents backend facts; the API remains the authority.</td></tr>
<tr><td>group-title-0</td><td>BROWSER · OPERATOR INTENT</td></tr>
<tr><td>group-title-1</td><td>BACKEND FACTS · UI PROJECTION</td></tr>
<tr><td>AuthGate</td><td>AuthGate</td></tr>
<tr><td>AuthGate</td><td>Validated SPA session</td></tr>
<tr><td>AuthGate</td><td>REST and SSE use the session bearer</td></tr>
<tr><td>AuthGate</td><td>App.tsx</td></tr>
<tr><td>Rendered controls</td><td>Rendered controls</td></tr>
<tr><td>Rendered controls</td><td>Status, timeline and graph</td></tr>
<tr><td>Rendered controls</td><td>Operator decisions become new API calls</td></tr>
<tr><td>Rendered controls</td><td>CoordinatorRunPage.tsx</td></tr>
<tr><td>AppShell</td><td>AppShell</td></tr>
<tr><td>AppShell</td><td>TopBar · LeftNav · project switcher</td></tr>
<tr><td>AppShell</td><td>ProjectList + Notifications providers</td></tr>
<tr><td>AppShell</td><td>AppShell.tsx:134-182</td></tr>
<tr><td>Client projection</td><td>Client projection</td></tr>
<tr><td>Client projection</td><td>Reducers combine backend facts</td></tr>
<tr><td>Client projection</td><td>Topology is server-authored, not invented</td></tr>
<tr><td>Route pages</td><td>Route pages</td></tr>
<tr><td>Route pages</td><td>Board · run · workspace</td></tr>
<tr><td>Route pages</td><td>Route parameters select the current scope</td></tr>
<tr><td>Route pages</td><td>App.tsx:80-127</td></tr>
<tr><td>Seed + live events</td><td>Seed + live events</td></tr>
<tr><td>Seed + live events</td><td>Independent REST and SSE inputs</td></tr>
<tr><td>Seed + live events</td><td>Positive sequence IDs deduplicate events</td></tr>
<tr><td>Seed + live events</td><td>useSeededRunStream.ts</td></tr>
<tr><td>API client</td><td>API client</td></tr>
<tr><td>API client</td><td>Typed requests and error handling</td></tr>
<tr><td>API client</td><td>API_URL origin + endpoint /api paths</td></tr>
<tr><td>API client</td><td>config.ts:13-36</td></tr>
<tr><td>Agentweaver API</td><td>Agentweaver API</td></tr>
<tr><td>Agentweaver API</td><td>Projects · runs · graph · events</td></tr>
<tr><td>Agentweaver API</td><td>Server owns persisted state and topology</td></tr>
<tr><td>AuthGate</td><td>enter</td></tr>
<tr><td>AppShell</td><td>contains</td></tr>
<tr><td>Route pages</td><td>request</td></tr>
<tr><td>API client</td><td>HTTP</td></tr>
<tr><td>Agentweaver API</td><td>history + SSE</td></tr>
<tr><td>Seed + live events</td><td>events</td></tr>
<tr><td>Client projection</td><td>render</td></tr>
<tr><td>scope</td><td>Read direction: intent down the left; backend facts rise on the right.</td></tr>
<tr><td>groups</td><td>BROWSER · OPERATOR INTENT; BACKEND FACTS · UI PROJECTION</td></tr>
</tbody></table>
</details>

<details id="diagram-context-frontend-fig2" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Routes: global and project scope</td></tr>
<tr><td>takeaway</td><td>App.tsx declares routes; AppShell supplies shared context, not a route registry.</td></tr>
<tr><td>group-title-0</td><td>GLOBAL · NO PROJECT PARAMETER</td></tr>
<tr><td>group-title-1</td><td>SHARED SHELL + PROJECT ROUTES</td></tr>
<tr><td>App.tsx Routes</td><td>App.tsx Routes</td></tr>
<tr><td>App.tsx Routes</td><td>/ · /overview · /projects</td></tr>
<tr><td>App.tsx Routes</td><td>Global sessions, settings and assistant</td></tr>
<tr><td>App.tsx Routes</td><td>App.tsx:80-100</td></tr>
<tr><td>AppShell</td><td>AppShell</td></tr>
<tr><td>AppShell</td><td>ProjectListProvider</td></tr>
<tr><td>AppShell</td><td>NotificationsProvider wraps shell content</td></tr>
<tr><td>AppShell</td><td>AppShell.tsx:134-182</td></tr>
<tr><td>Operator destinations</td><td>Operator destinations</td></tr>
<tr><td>Operator destinations</td><td>/console → /assistant</td></tr>
<tr><td>Operator destinations</td><td>/sessions is global; ?project scopes it</td></tr>
<tr><td>Operator destinations</td><td>App.tsx:93-100,130-135</td></tr>
<tr><td>Project route family</td><td>Project route family</td></tr>
<tr><td>Project route family</td><td>/projects/:projectId</td></tr>
<tr><td>Project route family</td><td>Dashboard · board · flow · orchestrations</td></tr>
<tr><td>Project route family</td><td>App.tsx:104-126</td></tr>
<tr><td>Platform settings</td><td>Platform settings</td></tr>
<tr><td>Platform settings</td><td>/platform-settings</td></tr>
<tr><td>Platform settings</td><td>Non-admin users redirect to /overview</td></tr>
<tr><td>Platform settings</td><td>App.tsx:87-92</td></tr>
<tr><td>Project resources</td><td>Project resources</td></tr>
<tr><td>Project resources</td><td>Workspace · settings · team</td></tr>
<tr><td>Project resources</td><td>Cast · agent memory · memories · skills</td></tr>
<tr><td>Project resources</td><td>App.tsx:110-117</td></tr>
<tr><td>Global observability</td><td>Global observability</td></tr>
<tr><td>Global observability</td><td>/observability · /traces · /agents</td></tr>
<tr><td>Global observability</td><td>Redirect pages resolve destination scope</td></tr>
<tr><td>Global observability</td><td>App.tsx:99-101</td></tr>
<tr><td>Project operations</td><td>Project operations</td></tr>
<tr><td>Project operations</td><td>Observability · workflows</td></tr>
<tr><td>Project operations</td><td>Diagnostics · heartbeat · cluster</td></tr>
<tr><td>Project operations</td><td>App.tsx:118-125</td></tr>
<tr><td>AppShell</td><td>wraps</td></tr>
<tr><td>App.tsx Routes</td><td>declares</td></tr>
<tr><td>scope</td><td>Cards group declared paths, not navigation dependencies. Global admin/observability are independent routes.</td></tr>
<tr><td>groups</td><td>GLOBAL · NO PROJECT PARAMETER; SHARED SHELL + PROJECT ROUTES</td></tr>
</tbody></table>
</details>

<details id="diagram-context-frontend-fig3" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Static hosting and API origins</td></tr>
<tr><td>takeaway</td><td>The Web host serves the SPA; API_URL is an origin or empty, never /api.</td></tr>
<tr><td>group-title-0</td><td>WEB HOST · STATIC DELIVERY</td></tr>
<tr><td>group-title-1</td><td>BROWSER · RUNTIME DESTINATIONS</td></tr>
<tr><td>Browser request</td><td>Browser request</td></tr>
<tr><td>Browser request</td><td>Assets or a client-side route</td></tr>
<tr><td>Browser request</td><td>The Web host is not the API host</td></tr>
<tr><td>Browser request</td><td>Web/Program.cs:39-65</td></tr>
<tr><td>Runtime configuration</td><td>Runtime configuration</td></tr>
<tr><td>Runtime configuration</td><td>window.__AGENTWEAVER_CONFIG__</td></tr>
<tr><td>Runtime configuration</td><td>API_URL selects the API origin</td></tr>
<tr><td>Runtime configuration</td><td>config.ts:13-36</td></tr>
<tr><td>Static file middleware</td><td>Static file middleware</td></tr>
<tr><td>Static file middleware</td><td>Default files + static assets</td></tr>
<tr><td>Static file middleware</td><td>Non-HTML assets get immutable caching</td></tr>
<tr><td>Static file middleware</td><td>Web/Program.cs:39-50</td></tr>
<tr><td>Origin resolution</td><td>Origin resolution</td></tr>
<tr><td>Origin resolution</td><td>Origin string, or &quot;&quot; = same-origin</td></tr>
<tr><td>Origin resolution</td><td>Client endpoints append their own /api</td></tr>
<tr><td>SPA route fallback</td><td>SPA route fallback</td></tr>
<tr><td>SPA route fallback</td><td>Unknown route → index.html</td></tr>
<tr><td>SPA route fallback</td><td>React handles the resulting route</td></tr>
<tr><td>SPA route fallback</td><td>Web/Program.cs:61-65</td></tr>
<tr><td>API destination</td><td>API destination</td></tr>
<tr><td>API destination</td><td>REST + authenticated fetch SSE</td></tr>
<tr><td>API destination</td><td>Same-origin still uses /api endpoints</td></tr>
<tr><td>API destination</td><td>api/sse.ts:239-337</td></tr>
<tr><td>Documentation route</td><td>Documentation route</td></tr>
<tr><td>Documentation route</td><td>/docs and /docs/{path}</td></tr>
<tr><td>Documentation route</td><td>Temporary redirect preserves suffix</td></tr>
<tr><td>Documentation route</td><td>Web/Program.cs:52-59</td></tr>
<tr><td>External documentation</td><td>External documentation</td></tr>
<tr><td>External documentation</td><td>Configured documentation base URL</td></tr>
<tr><td>External documentation</td><td>Not the local SPA fallback</td></tr>
<tr><td>Browser request</td><td>asset</td></tr>
<tr><td>Static file middleware</td><td>unmatched</td></tr>
<tr><td>Runtime configuration</td><td>supplies</td></tr>
<tr><td>Origin resolution</td><td>requests</td></tr>
<tr><td>Documentation route</td><td>302 redirect</td></tr>
<tr><td>scope</td><td>Parallel concerns: static delivery, runtime API selection and external docs redirection.</td></tr>
<tr><td>groups</td><td>WEB HOST · STATIC DELIVERY; BROWSER · RUNTIME DESTINATIONS</td></tr>
</tbody></table>
</details>

<details id="diagram-context-frontend-fig6" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Entra sign-in and browser session</td></tr>
<tr><td>takeaway</td><td>The callback returns a one-time code; session exchange delivers the SPA bearer.</td></tr>
<tr><td>group-title-0</td><td>SIGN-IN · API + ENTRA</td></tr>
<tr><td>group-title-1</td><td>SESSION · PER-TAB BROWSER STATE</td></tr>
<tr><td>Browser sign-in</td><td>Browser sign-in</td></tr>
<tr><td>Browser sign-in</td><td>Begin API Entra authorization</td></tr>
<tr><td>Browser sign-in</td><td>State binds the browser callback</td></tr>
<tr><td>Browser sign-in</td><td>AuthEndpoints.cs</td></tr>
<tr><td>Session exchange</td><td>Session exchange</td></tr>
<tr><td>Session exchange</td><td>POST the one-time exchange code</td></tr>
<tr><td>Session exchange</td><td>Returns validated token + browser session</td></tr>
<tr><td>Session exchange</td><td>AuthEndpoints.cs:398-455</td></tr>
<tr><td>Microsoft Entra ID</td><td>Microsoft Entra ID</td></tr>
<tr><td>Microsoft Entra ID</td><td>Authenticate the user</td></tr>
<tr><td>Microsoft Entra ID</td><td>Identity authority, not GitHub OAuth</td></tr>
<tr><td>Per-tab sessionStorage</td><td>Per-tab sessionStorage</td></tr>
<tr><td>Per-tab sessionStorage</td><td>Store the SPA bearer</td></tr>
<tr><td>Per-tab sessionStorage</td><td>No durable localStorage token</td></tr>
<tr><td>Per-tab sessionStorage</td><td>config.ts:60-138</td></tr>
<tr><td>API callback</td><td>API callback</td></tr>
<tr><td>API callback</td><td>Validate callback and state</td></tr>
<tr><td>API callback</td><td>Issue a one-time frontend exchange code</td></tr>
<tr><td>Same-origin peer tab</td><td>Same-origin peer tab</td></tr>
<tr><td>Same-origin peer tab</td><td>BroadcastChannel request/response</td></tr>
<tr><td>Same-origin peer tab</td><td>Transient token transfer to a new tab</td></tr>
<tr><td>Same-origin peer tab</td><td>config.ts:207-240</td></tr>
<tr><td>Frontend callback</td><td>Frontend callback</td></tr>
<tr><td>Frontend callback</td><td>Receive exchange code</td></tr>
<tr><td>Frontend callback</td><td>Do not treat the code as an access token</td></tr>
<tr><td>Authenticated requests</td><td>Authenticated requests</td></tr>
<tr><td>Authenticated requests</td><td>REST and fetch-based SSE</td></tr>
<tr><td>Authenticated requests</td><td>Bearer token; API authorizes resources</td></tr>
<tr><td>Authenticated requests</td><td>api/sse.ts:239-337</td></tr>
<tr><td>Browser sign-in</td><td>sign in</td></tr>
<tr><td>Microsoft Entra ID</td><td>callback</td></tr>
<tr><td>API callback</td><td>code</td></tr>
<tr><td>Frontend callback</td><td>POST code</td></tr>
<tr><td>Session exchange</td><td>session</td></tr>
<tr><td>Per-tab sessionStorage</td><td>transfer</td></tr>
<tr><td>Per-tab sessionStorage</td><td>bearer</td></tr>
<tr><td>scope</td><td>Only a one-time exchange code crosses the callback URL; the session token stays out of URLs.</td></tr>
<tr><td>groups</td><td>SIGN-IN · API + ENTRA; SESSION · PER-TAB BROWSER STATE</td></tr>
</tbody></table>
</details>

<details id="diagram-context-frontend-fig7" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Live timeline: independent inputs</td></tr>
<tr><td>takeaway</td><td>REST seed and live SSE run concurrently, then merge into a guarded projection.</td></tr>
<tr><td>group-title-0</td><td>INDEPENDENT INPUTS</td></tr>
<tr><td>group-title-1</td><td>MERGE · PROJECT · RECOVER</td></tr>
<tr><td>Current run ID</td><td>Current run ID</td></tr>
<tr><td>Current run ID</td><td>Route chooses stream scope</td></tr>
<tr><td>Current run ID</td><td>Run/generation guards reject stale seeds</td></tr>
<tr><td>Current run ID</td><td>useSeededRunStream.ts:43-151</td></tr>
<tr><td>Merge run events</td><td>Merge run events</td></tr>
<tr><td>Merge run events</td><td>Positive-sequence deduplication</td></tr>
<tr><td>Merge run events</td><td>Restricted sequence-zero singleton rules</td></tr>
<tr><td>Merge run events</td><td>mergeRunEvents.ts:23-79</td></tr>
<tr><td>Persisted event history</td><td>Persisted event history</td></tr>
<tr><td>Persisted event history</td><td>REST seed requested independently</td></tr>
<tr><td>Persisted event history</td><td>A seed failure does not block live SSE</td></tr>
<tr><td>Persisted event history</td><td>useSeededRunStream.ts:85-134</td></tr>
<tr><td>Timeline projection</td><td>Timeline projection</td></tr>
<tr><td>Timeline projection</td><td>Reducers + server topology seed</td></tr>
<tr><td>Timeline projection</td><td>Render graph, timeline, status and controls</td></tr>
<tr><td>Timeline projection</td><td>CoordinatorRunPage.tsx</td></tr>
<tr><td>Live event transport</td><td>Live event transport</td></tr>
<tr><td>Live event transport</td><td>Authenticated fetch + credentials</td></tr>
<tr><td>Live event transport</td><td>Last-Event-ID resumes the cursor</td></tr>
<tr><td>Live event transport</td><td>api/sse.ts:239-337</td></tr>
<tr><td>Parser and event buffer</td><td>Parser and event buffer</td></tr>
<tr><td>Parser and event buffer</td><td>Dedupe and bounded retention</td></tr>
<tr><td>Parser and event buffer</td><td>Cursor advances with accepted events</td></tr>
<tr><td>Unexpected disconnect</td><td>Unexpected disconnect</td></tr>
<tr><td>Unexpected disconnect</td><td>Bounded reconnect backoff</td></tr>
<tr><td>Unexpected disconnect</td><td>Explicit reconnect reopens after gate action</td></tr>
<tr><td>done / terminal</td><td>done / terminal</td></tr>
<tr><td>done / terminal</td><td>Stop the current transport</td></tr>
<tr><td>done / terminal</td><td>A gate done is not universal run completion</td></tr>
<tr><td>done / terminal</td><td>api/sse.ts; RunEndpoints.cs</td></tr>
<tr><td>Current run ID</td><td>seed</td></tr>
<tr><td>Current run ID</td><td>live</td></tr>
<tr><td>Persisted event history</td><td>seed events</td></tr>
<tr><td>Live event transport</td><td>frames</td></tr>
<tr><td>Parser and event buffer</td><td>events</td></tr>
<tr><td>Merge run events</td><td>merged</td></tr>
<tr><td>Live event transport</td><td>disconnect</td></tr>
<tr><td>Parser and event buffer</td><td>done</td></tr>
<tr><td>scope</td><td>No REST→SSE prerequisite. Backend topology is an input; browser reducers do not author it.</td></tr>
<tr><td>groups</td><td>INDEPENDENT INPUTS; MERGE · PROJECT · RECOVER</td></tr>
</tbody></table>
</details>
