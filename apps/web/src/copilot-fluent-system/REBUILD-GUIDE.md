# Rebuilding Agentweaver on the Azure Fluent System

**Purpose:** answer two questions.

1. Is the current library — its `DESIGN.md`, `tokens.css`, components, patterns, examples, and
   catalog — sufficient to point an LLM at and ask it to rebuild the Agentweaver app in this
   design language?
2. If so, what is the concrete process to do it?

This guide is the operator's manual for that rebuild. It is intentionally at the **package root**
(not under `catalog/`, which is a locked three-file contract) so it can grow without touching the
Figma-traceability docs.

---

## 1. Short answer

**Yes — sufficient for a faithful rebuild, with one honest caveat.**

The library already ships everything an LLM needs to rebuild the *real* Agentweaver surfaces:

- A single, enforced **usage contract** (`DESIGN.md`) with import rules, provider setup,
  token-only styling, component vocabulary, pattern rules, do/don't, and the **fidelity gate**.
- A **token layer** (`tokens.css`) that is the only styling source — no ad-hoc colors.
- **40+ composed components** and **12 patterns**, each carrying Figma node-ID lineage comments so
  fidelity is provable, not eyeballed.
- **Runnable examples** (`examples/*.example.tsx`) and a **live showcase** (`showcase/`) that
  renders every rendered component, all 8 Figma pattern families, the composed scenarios, and the
  full icon catalog.
- A **coverage ledger** (`COVERAGE.md`) that states, honestly, what is high-fidelity today and
  what is still a placeholder.

**Caveat:** the library is **presentational**. It does not carry Agentweaver's data layer — the
SSE run stream, ReactFlow DAG, hooks, routing, and API clients stay in the app. The rebuild is a
**skin-and-restructure**, not a logic port. And per `COVERAGE.md`, ~45 inventory nodes are still
`needs-mcp-extraction` placeholders; none of them are on the critical Agentweaver pages, so they
do not block the rebuild — but do not invent them.

---

## 1a. Live Portal capture refresh

The current library includes a sanitized live Azure Portal capture pass. It changed only reusable visual structure: `tokens.css` now centralizes Portal-like 13px Segoe density, `rgb(0 120 212)` Azure blue, neutral foreground/borders, 40px masthead, 32px row/control rhythm, flatter surfaces, compact command bars, tighter Essentials spacing, and a `NotificationPane` flyout surface. Treat those as package tokens and public component behavior; do not add real tenant, subscription, resource, or user data when rebuilding downstream screens.

## 2. What the library hands the LLM (the inputs)

| Artifact | Path | What it provides for the rebuild |
| --- | --- | --- |
| Usage contract | `DESIGN.md` | The enforceable rules: import surface, provider, tokens, pattern rules, do/don't, **fidelity gate** (`Coverage is not fidelity.` / `Recipe mapping and fidelity gate`). Load this first. |
| Tokens | `tokens.css` | The only styling source. Spacing, color, radius, typography, motion, reduced-motion + forced-colors guards. No new hex allowed. |
| Public surface | `index.ts` | The barrel — the **only** import entry. Provider, icons, components, approved Fluent primitives, and all patterns. |
| Components | `components.tsx` | 40+ composed Azure components (BladeHeader, ServiceMenu, EssentialsGrid, DataGrid, Copilot surfaces, ChainOfThought, AgenticProgress, …), each with node-ID lineage. |
| Patterns | `patterns.tsx` | 12 page-level compositions (see §4). Start here — patterns first, components second, foundations third. |
| Component inventory | `catalog/COMPONENTS.md` | 148-node inventory: export mapping + extraction status. Use to check whether a surface already exists. |
| Pattern inventory | `catalog/PATTERNS.md` | The 8 Figma pattern families + design guidance + traceability. |
| Icon inventory | `catalog/ICONS.md` | 1441-glyph / 27-collection vendored catalog + import status. |
| Coverage ledger | `COVERAGE.md` | Truthful high-fidelity vs. placeholder split, Copilot-surface fidelity table, gap list. Read before promising fidelity. |
| Examples | `examples/*.example.tsx` | Minimal, runnable usage of each pattern/component. Copy-paste starting points. |
| Live showcase | `showcase/` | The visual ground truth. Component browser, pattern browser, composed scenarios, icon catalog. Run it and compare. |

**Load order for the model:** `DESIGN.md` → `README.md` → `COVERAGE.md` → `catalog/PATTERNS.md`
→ `patterns.tsx` → the relevant `examples/*.example.tsx` → `catalog/COMPONENTS.md` /
`components.tsx` only when a pattern is not enough.

---

## 3. The rebuild process

### Phase 0 — Prime the model

Give the LLM the load-order files above and one hard instruction: **only import from the
`copilot-fluent-system` barrel; style only with its tokens; never introduce raw hex, px colors, or a
second UI kit.** Point it at the running showcase as the visual oracle.

### Phase 1 — Establish the shell

Wrap the app in `AzureFluentProvider` (density `compact` matches Azure Portal). Replace the global
layout chrome with `PortalLayout` / `PortalRail` / `PortalTopNav`. Everything below inherits tokens.

### Phase 2 — Map pages to patterns (archetype table)

Agentweaver has ~19 pages. Rebuild by archetype, not one-off. Recommended mapping:

| Agentweaver page(s) | Archetype | Library pattern(s) |
| --- | --- | --- |
| `CoordinatorRunPage` | Agentic run workspace | **`CoordinatorRunPattern`** + `AgenticApprovalPattern` + `BladeHeader` + `ChainOfThought` + `CopilotComposer`/`CopilotResponse` |
| `WorkspacePage`, `FlowPage` | Copilot workspace | `CopilotWorkspacePattern` (ReactFlow canvas stays app-owned inside it) |
| `OverviewPage`, `DashboardPage` | Service overview | `ServiceOverviewPattern` + `EssentialsGrid` |
| `ProjectGalleryPage`, `WorkflowsPage`, `OrchestrationsPage`, `MemoriesPage` | Browse / list | `BrowseResourcePattern` / `FilteringPattern` (toolbar + filters + `AzureDataGrid` + `Pager`) |
| `ProjectPage`, `ClusterPage`, `TeamPage`, `SkillsPage` | Manage resource (blade) | `ManageResourcePattern` (`BladeHeader` + `ServiceMenu` + content) |
| `CastingWizardPage` | Multi-step create | `StepWizardPattern` / `CreateResourcePattern` / `FormBladePattern` |
| `SettingsPage`, `ProjectSettingsPage` | Settings form | `FormBladePattern` (+ `FormFooter`) |
| `DiagnosticsPage`, `HeartbeatPage` | Status / telemetry | `AzureDataGrid` + status components |
| cross-page toasts / failures | Notification / error | `NotificationPattern` / `ErrorPattern` |
| `SignInPage` | Auth | approved Fluent primitives (no dedicated pattern) |

### Phase 3 — Rebuild page-by-page, pattern-first

For each page: pick the pattern, feed it the page's existing data via props, then drop to
individual components only for gaps. Never rebuild a blade header, list toolbar, or Copilot
surface by hand — the pattern already encodes the Figma fidelity.

### Phase 4 — Keep the logic, swap the surface

The library takes **props and callbacks**; it owns no state. Keep Agentweaver's hooks, SSE stream,
ReactFlow graph, routing, and API clients exactly as they are. Bind them to pattern props
(`value`/`onChange`/`onSend`, `steps`, `onApprove`/`onDeny`, `items`/`columns`, `actions`). The
rebuild changes the render tree, not the data flow.

### Phase 5 — Verify against the fidelity checks

Run the same checks this library holds itself to:

```
# from apps/web
npx tsc -b --pretty false
npx vitest run --config vitest.config.ts src/__tests__/azureFluentSystem.test.tsx
```

Then compare the rebuilt page against the showcase in a real browser. `Coverage is not fidelity` —
screenshot or manual browser inspection is required before you call a page done.

---

## 4. Worked example — rebuilding `CoordinatorRunPage`

This is the flagship surface and the reason `CoordinatorRunPattern` exists. The real page composes
a run header, a reasoning/plan stream, agent-session panels, an artifacts panel, cost/automation
chips, and human approval gates.

Recommended structure:

```tsx
import {
  AzureFluentProvider,
  CoordinatorRunPattern,
  AgenticApprovalPattern,
} from '../copilot-fluent-system';

function CoordinatorRunView({ run, steering, onSteer, onSend, onApprove, onDeny }) {
  return (
    <AzureFluentProvider density="compact">
      <CoordinatorRunPattern
        title={`Coordinator · ${run.name}`}
        subtitle={`${run.agentCount} agents · ${run.stepsComplete} of ${run.stepsTotal} steps`}
        runActions={run.actions}          // Pause run / View logs — real run controls
        copilotActions={run.copilotActions}
        reasoning={{
          title: 'Run reasoning',
          subtitle: `${run.artifacts.length} artifacts created`,
          steps: run.reasoningSteps,      // map your SSE stream -> AzfAgentStep[]
          artifacts: run.artifacts,       // map artifacts -> AzfArtifact[]
          onApprove,
          onDeny,
        }}
        response={{ parts: run.summaryParts }}
        composer={{ value: steering, onChange: onSteer, onSend }}
      />
    </AzureFluentProvider>
  );
}
```

Map the rest of the page's moving parts onto library pieces:

- **OutcomePlanPanel / reasoning stream** → the `reasoning` prop (renders `ChainOfThought`:
  status-icon step rows, Activity/Artifacts tabs, inline approval block).
- **AgentSessionPanel approvals / AutomationToggle gate** → `AgenticApprovalPattern` — the compact
  human-in-the-loop card built on `AgenticProgress`.
- **CoordinatorArtifactsPanel** → `AzfArtifact[]` on the reasoning prop (renders `ArtifactPill`).
- **Run summary** → `response` prop (`CopilotResponse`).
- **Operator steering box** → `composer` prop (`CopilotComposer`).
- **CostChip / status pills** → foundation Badges / `EssentialsGrid`.
- **ReactFlow DAG** → **stays app-owned**; place it inside the pattern's main column if you want the
  graph and reasoning side by side, or keep it on a sibling tab.

The composed **"Composed scenarios"** section in the showcase Patterns tab renders both of these
patterns live — use it as the pixel oracle while wiring the real data.

---

## 5. Guardrails the rebuilding LLM must follow

These are lifted straight from `DESIGN.md` §6 (Do / don't) and the design guidance:

- **Import only from the barrel.** No deep imports, no second component kit.
- **Tokens only.** No raw hex, rgb, or px color values. If a value is missing, add a token — don't
  inline it.
- **Patterns before components before foundations.** Don't hand-roll a blade header or Copilot
  surface that already exists.
- **Never eyeball a Figma surface.** If a new surface is needed, extract it via the Figma MCP and
  cite the node ID in a lineage comment — exactly how every component here was built.
- **Presentational only.** Keep app state/data in the app; pass it in as props.
- **Verify before "done."** tsc + focused tests + a real browser pass. Coverage is not fidelity.

---

## 6. Honest limitations

- **~45 inventory nodes are `needs-mcp-extraction` placeholders** (`COVERAGE.md`). They are not on
  the core Agentweaver pages, but if a rebuild needs one, extract it first — do not fabricate.
- **App-specific widgets are out of scope**: the ReactFlow DAG, SSE plumbing, routing, and auth are
  Agentweaver's own; the library skins around them, it doesn't replace them.
- **Density/theme**: the showcase runs `compact` dark by default; confirm the target theme with the
  provider before comparing pixels.

---

## 7. Paste-ready system prompt for the rebuilding LLM

> You are rebuilding the Agentweaver web app on the in-repo `copilot-fluent-system` design library.
> Read, in order: `copilot-fluent-system/DESIGN.md`, `README.md`, `COVERAGE.md`,
> `catalog/PATTERNS.md`, `patterns.tsx`, and the relevant `examples/*.example.tsx`. Rules: import
> only from the `copilot-fluent-system` barrel; style only with `tokens.css` tokens (no raw
> hex/rgb/px colors, no second UI kit); rebuild each page pattern-first using the archetype mapping
> in `REBUILD-GUIDE.md` §3; keep all existing app state, data, SSE, ReactFlow, routing, and API
> logic and bind it to pattern props; never eyeball a Figma surface — if a new one is needed,
> extract it via the Figma MCP and cite the node ID. Start with `CoordinatorRunPage` using
> `CoordinatorRunPattern` + `AgenticApprovalPattern` (see §4). After each page, run `tsc -b`, the
> `azureFluentSystem` tests, then compare against the live
> showcase before moving on.

See also: `COVERAGE.md` (what's high-fidelity vs. placeholder) and `DESIGN.md` (the enforceable
contract).
