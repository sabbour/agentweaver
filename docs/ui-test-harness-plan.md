# Agentweaver UI Test Harness Plan (Playwright)

_Last updated: 2026-07-14_

## Goal

Add a **parallel, browser-driven validation track** to Agentweaver's continuous
autopilot testing — one that drives the real Web UI with Playwright to exercise,
fix, and regression-guard the **UI-facing** behavior the API-only persona harness
(`scripts/persona-harness/`) cannot see. The API harness proves the backend
lifecycle is correct (project → run → outcome spec → events → approvals) by reading
JSON. This harness proves the operator actually experiences that correctness in the
browser: the right affordance appears at the right time, the right node lights up,
the notification says what kind of action is needed, the slide-in panel renders the
session, and no console/network errors are thrown along the way.

It is the **frontend track of issue #1** — the original spike explicitly named
Playwright — kept deliberately complementary to, not competing with, the API track
that already became the primary E2E method (`decisions.md:953`).

This harness obeys the **same discipline** as the API harness:

1. It is a **DRIVER, not a JUDGE.** It captures deterministic, objective UI-state
   evidence and hard-fails only on unambiguous facts. Subjective "is this UX good?"
   judgment is deferred to a separate LLM/human judge.
2. Scenarios are **dynamically generated per persona brief**, not hardcoded
   click-by-click scripts or static release-validation/screenshot specs.
3. Auth is **manual headful login once, then stored session reuse** — never
   automated OAuth.
4. Evidence is **cross-referenced against kubectl logs + Application Insights**,
   not just what the DOM shows.

---

## Relationship to the shared persona / judge layer used by all three harnesses (read this first)

Agentweaver is now building **three** persona harnesses in parallel, each driving
the same product through a different surface:

| Harness | Surface | Drives via | Directory |
|---|---|---|---|
| **API harness** (exists) | Backend REST lifecycle | bearer-token HTTP calls | `scripts/persona-harness/` |
| **UI harness** (this spec) | Web UI | Playwright browser | `scripts/ui-persona-harness/` |
| **MCP harness** (Morpheus, in parallel) | MCP protocol / tool-call surface | MCP client | `scripts/mcp-persona-harness/` (Morpheus's spec) |

Per Ahmed's explicit requirement, **all three must share common personas and must be
judgeable through a common judging model** — a persona (Jordan, Maya, Priya, …) is
defined **once**, surface-agnostically, and each harness drives that same persona
through its own surface, so the same scenario can be compared across surfaces. This
section, and the dedicated **"Cross-Harness Shared Layer"** section below, specify
that shared layer; Morpheus's MCP spec references the same layer.

This UI harness is therefore **one of three consumers of a shared persona + judge
layer**, not a standalone extension of the API harness.

**Decision: a new sibling directory `scripts/ui-persona-harness/` that consumes a
shared persona-brief package and a shared judge core (below), rather than folding
Playwright into the API harness or forking its briefs/judge.**

Rationale:

- **Different runtime, same contracts.** The API harness is dependency-light
  (Node + `yaml`, no browser). Adding Playwright (a large native browser dependency
  and a headful, human-gated login step) into `scripts/persona-harness/` would make
  the fast, deterministic, CI-friendly API track heavier and would couple two
  independently useful tools. A sibling directory keeps each installable and
  runnable on its own.
- **Reuse, don't duplicate, the parts that are already right.** The concepts the
  API harness got right — the persona **brief** format, the **driver/judge
  separation**, the **judge prompt assembler** (`lib/judge.mjs`), the **cross-run
  meta-aggregation** (`lib/meta-aggregate.mjs`), and the **JUDGE.md** methodology —
  are surface-agnostic and must become the **shared layer** all three harnesses
  import, not code each harness copies. The migration path is in "Cross-Harness
  Shared Layer" below.
- **Avoids the active-edit collision.** `scripts/persona-harness/` is under active
  modification (Tank's approval-driving work, `harness/wip-persona-v1`). A separate
  directory lets the UI track proceed in parallel without touching files another
  agent is mid-edit on. Until the shared layer is extracted, shared modules are
  consumed **read-only** as a local import from `scripts/persona-harness/`; the
  target end-state is the extracted shared packages below.

What is **shared** (all three harnesses) vs **new** (UI-specific):

| Concern | Target home | UI harness action |
|---|---|---|
| Persona definitions (Jordan/Maya/Priya/…) — goals, voice, constraints, ≥2-pushback rule, authored "Success looks like" | **`scripts/persona-briefs/`** (new shared package) — surface-agnostic | **Consume** the shared persona; add a UI **surface adapter** per persona, not a copied brief |
| Judge core: prompt library + canonical verdict schema + JUDGE.md methodology | **`scripts/harness-judge/`** (new shared package, extracted from `lib/judge.mjs`) | **Consume** the shared judge core; register a **UI evidence adapter** |
| Meta-aggregation (`lib/meta-aggregate.mjs`) | **`scripts/harness-judge/`** (shared) | **Reuse as-is** — one verdict schema, so a batch can mix API + UI + MCP runs |
| Driver / evidence capture | — | **New, UI-specific** — Playwright (`lib/browser.mjs`, `agent-driver-ui/tools.mjs`) |
| Auth / storageState | — | **New, UI-specific** — headful manual login + stored session reuse |

---

## Cross-Harness Shared Layer

Ahmed's requirement: the three harnesses (API, UI, MCP) **share common personas**
and are **judgeable through a common model** — and he explicitly asked whether this
should be **three judges or one**. Below are the two decisions, stated explicitly.

### 1. Shared persona / brief format — one definition, per-surface adapters

**Recommendation: define each persona ONCE in a new shared package
`scripts/persona-briefs/`, surface-agnostic, and have each harness drive that same
persona through a thin per-surface adapter. Do NOT duplicate or re-adapt briefs
per harness.**

Layout of the shared package:

```
scripts/persona-briefs/
  package.json                 Zero heavy deps; imported by all three harnesses
  personas/
    priya.md                   Persona CORE — identity, goal, voice, constraints,
    jordan.md                  the mandatory ≥2-pushback rule, and the authored
    maya.md                    "Success looks like" / "Failure signals" criteria.
    ...                        NOTHING surface-specific (no "click", no "curl", no tool name).
  surfaces/
    priya.api.md               Per-surface ADAPTER — how THIS persona's intent maps to
    priya.ui.md                the surface's actions ONLY (e.g. UI: "the messy batch is
    priya.mcp.md               pasted into the coordinator composer"; API: "submit-goal";
    ...                        MCP: "the tool the persona would reach for"). Additive, thin.
  index.mjs                    Resolves persona core + optional surface adapter for a harness
```

- The **persona core** carries everything that must be identical across surfaces —
  who they are, what they want, their voice, their low-tolerance triggers, and the
  ≥2-pushback requirement. This is what makes "Jordan via API" and "Jordan via UI"
  the **same Jordan**.
- The **surface adapter** is thin and additive: it only says how that persona's
  intent expresses itself on that surface (a UI action vs an API call vs an MCP tool
  invocation). A persona with no adapter for a surface simply isn't run there.
- **Migration:** the existing `scripts/persona-harness/briefs/*.md` and the repo's
  `specs/personas/*.md` are the seed content. They are lifted into
  `scripts/persona-briefs/personas/` (core) once, with the API-specific phrasing
  peeled into `surfaces/*.api.md`. This is a coordinated extraction handed to the
  API-track owner / coordinator (not an out-of-band edit to Tank's in-flight files),
  the same way the judge extraction below is handled. Until it lands, the UI harness
  reads the existing briefs read-only and layers its UI adapter locally.

This directly satisfies "personas should be defined once in a surface-agnostic way
and each harness drives that SAME persona through its own surface."

### 2. Judge architecture — ONE shared judge core with per-surface evidence adapters (option a)

**Recommendation: option (a) — a single shared judge core (prompt library + one
canonical verdict schema + the JUDGE.md methodology) with three thin per-surface
evidence adapters (API call/response, UI DOM/screenshot/console/network, MCP
protocol/tool-call). NOT three separate judges.**

```
scripts/harness-judge/
  package.json
  JUDGE.md                     Canonical methodology: P0 objective / P1 subjective / CANNOT_DETERMINE,
                               pushback rules, two-layer (per-run + meta-aggregation). Surface-neutral core
                               + short per-surface appendices (JUDGE.api.md / JUDGE.ui.md / JUDGE.mcp.md).
  core.mjs                     Assembles the judge prompt from: persona core + authored criteria +
                               run metadata + a normalized EVIDENCE bundle. Emits the canonical
                               verdict schema `agentweaver.persona-judge-verdict/v1`.
  meta-aggregate.mjs           Cross-run + CROSS-SURFACE rollup (moved here from the API harness).
  adapters/
    api.mjs                    API transcript  -> normalized evidence (calls, bodies, outcome spec)
    ui.mjs                     UI transcript   -> normalized evidence (DOM snapshot, screenshot ref, console, network, log cross-ref)
    mcp.mjs                    MCP transcript  -> normalized evidence (tool calls, protocol frames)   [Morpheus]
  test/
    core.test.mjs              Verdict schema + prompt assembly
    adapters.*.test.mjs        Each adapter's evidence normalization
```

Each adapter's only job is to turn its surface's raw transcript into the **same
normalized evidence shape** (`{ turns:[{intent, action, objectiveFacts, evidence[]}],
personaCriteria, metadata }`). `core.mjs` then does the identical judging regardless
of surface, and always emits the **one** canonical verdict schema.

**Why (a) over (b) three separate judges:**

- **Consistent P0/P1 meaning across surfaces.** "P0 = objective mechanics succeeded"
  and "P1 = subjective quality vs the persona's authored criteria" must mean the same
  thing whether the evidence is a JSON body, a screenshot, or an MCP frame. A single
  core guarantees this by construction; three judges would drift — one might grade a
  P1 as PARTIAL that another would call FAIL for equivalent evidence, making verdicts
  incomparable.
- **Cross-surface meta-aggregation is the whole point and REQUIRES one schema.**
  Ahmed's example question — "did Jordan behave consistently whether driven via API
  vs UI vs MCP for the same scenario" — is only answerable if all three emit the
  identical `agentweaver.persona-judge-verdict/v1` block into one `verdicts/` pool
  that `meta-aggregate.mjs` rolls up. It can then report **cross-surface invariants**
  (a P0 mechanic that held on every surface), **cross-surface divergences** (the
  persona got a good plan via API but a confusing one via UI — a surface-specific
  defect), and **recurring findings** across surfaces. Three separate schemas would
  make this rollup impossible without a translation shim — which is just the shared
  core, arrived at the hard way.
- **Lower maintenance.** The judging methodology (pushback grading, CANNOT_DETERMINE
  discipline, the #315 regression-detection rule) is written and tested once. A
  methodology change (e.g. tightening what counts as grounded pushback) lands in one
  place, not three. Adding a fourth surface later = one new adapter, zero judge-core
  changes.
- **Surface nuance is preserved without forking the judge.** Genuinely
  surface-specific guidance (e.g. "a screenshot can show layout clarity but cannot
  prove a network call succeeded — read the network log for that") lives in a short
  per-surface **appendix** (`JUDGE.ui.md`) that the core includes alongside the
  neutral methodology. This gets option (b)'s tuning benefit without its consistency
  and maintenance costs.

So: **not "maybe 3 judges" — one judge core, three evidence adapters, one verdict
schema, one meta-aggregator.** The `lib/judge.mjs` already in the API harness is the
seed for `core.mjs`; extracting it (with the UI evidence adapter added) is Phase 2 of
the rollout below.

### 3. How this UI harness consumes the shared layer

The directory layout below is written to **consume** these shared packages, never
duplicate them:

- Personas come from `scripts/persona-briefs/` (core) + a local `surfaces/*.ui.md`
  adapter — the UI harness ships **no** copied persona definitions.
- Judging goes through `scripts/harness-judge/core.mjs` with the UI evidence adapter
  (`adapters/ui.mjs`); the UI harness ships **no** copied prompt/verdict logic —
  only the code that produces the raw UI transcript the adapter normalizes.
- Verdicts land in the **shared** `verdicts/` pool and are rolled up by the shared
  `meta-aggregate.mjs`, so UI runs meta-aggregate together with API and MCP runs.

Until the shared packages are extracted (a coordinated step, not an out-of-band edit
to actively-edited API-harness files), the UI harness imports the equivalent modules
read-only from `scripts/persona-harness/` and carries a thin local UI evidence shim;
the target end-state is the shared packages above.

---

## Goals

- Drive the **real deployed Web UI** (staging AKS, the same target the API harness
  hits) with Playwright, as a realistic operator would, from a persona brief.
- Capture **objective UI-state evidence** per step: DOM presence/absence of a
  keyed element, visible text, element attributes/roles, screenshots, browser
  console errors/warnings, and the network calls the page actually made.
- Hard-fail (driver P0) only on **deterministic UI facts** — a keyed element that
  must exist is missing, a page threw an uncaught console error, an expected API
  call returned a non-2xx that surfaced to the user, a required affordance never
  became reachable.
- Defer all **subjective UI/UX quality** ("is this layout clear? is this the right
  place for the button?") to the shared LLM/human judge, feeding it screenshots +
  DOM snapshots as evidence.
- Cross-reference every finding against **kubectl logs + Application Insights** so a
  browser symptom is tied to (or exonerated from) a backend cause before it is
  filed.
- Generate scenarios **per brief**, dynamically, so the harness probes realistic
  operator intents and can catch a **class** of regressions (e.g. phantom graph
  edges, #306) rather than replaying one fixed path.
- Be usable both for **fixing the listed open issues** (drive the broken flow,
  confirm the fix in the browser) and for **standing regression coverage** once
  those flows are correct.

## Non-goals

- **Not** a static, hand-written click script suite. No `release-validation.spec.ts`,
  no `oauth-e2e.spec.ts`, no fixed golden-screenshot spec. (Standing user
  instruction — do not build static release-validation/OAuth/screenshot specs.)
- **Not** an automated-OAuth harness. Browser login is a human step (below).
- **Not** a pixel-diff / visual-regression tool. Screenshots are **evidence for a
  judge**, not a hard assertion source. "The button moved 3px" must never be a
  driver hard-fail.
- **Not** a replacement for the API harness. It does not re-prove backend lifecycle
  correctness; it proves the browser reflects it. Where a check can be done purely
  via API, it stays in the API harness.
- **Not** a unit/component test runner. `apps/web` already has Vitest for that
  (713+ tests). This drives the assembled, deployed app end-to-end.

---

## Driver / judge separation (why the harness does not self-certify UI quality)

Mirrors the API harness's correction (`decisions.md:1319`, README "Driver / judge
separation"). The split, applied to the browser:

- **Driver (this code) hard-fails ONLY on deterministic UI facts (P0).**
  Objective, unambiguous browser truths:
  - A **keyed** element that the scenario asserts must exist is present/absent
    (`data-testid` / ARIA role / accessible name — never a brittle CSS-nth-child or
    a text string that is legitimately allowed to vary).
  - The page raised an **uncaught console error** or an unhandled promise rejection.
  - A network request the UI made returned a **non-2xx that reached the user** (an
    error toast, an error boundary, a failed panel load).
  - A required **affordance never became reachable** within a bounded wait (e.g. the
    Confirm-plan control never enabled; the preview link never appeared though the
    run reported preview-ready).
  These are objective; a regression is unambiguous, so they legitimately gate.
- **Judge (separate LLM/human pass) decides subjective UI quality (P1).** "Is the
  notification's type indicator actually clear? Is the slide-in panel's information
  hierarchy right? Does this look confusing?" is deferred. The driver hands the
  judge a **screenshot + a structured DOM snapshot + the visible text + the console
  and network logs**, plus the persona's authored "Success looks like" criteria.
  The judge never runs in the driver.
- **CANNOT_DETERMINE.** Genuinely unobservable through the browser (e.g. the backend
  was mid-deploy, the element depends on data the run never produced). **Never
  guessed** — excluded from scoring and reported distinctly.

The reporter's console banner reflects the **driver** verdict only —
`UI DRIVE+CAPTURE OK` / `UI DRIVER P0 FAIL` — and prints
`P1 — UI/UX quality: DEFERRED to LLM judge`, exactly paralleling the API harness's
`DRIVE+CAPTURE OK` / `DRIVER P0 FAIL` banner.

> **The trap this avoids:** an author-written heuristic like "the notification is
> good if it contains the word Review" would both miss real regressions and produce
> false fails on valid variations. The driver only records _"a type badge element
> with `data-testid=notification-type-badge` is present and its accessible text is
> `Human Review`"_; whether that badge is a **clear** type indicator for issue #319
> is the judge's call from the screenshot.

---

## Architecture

```
persona brief (briefs/*.md, shared format)
        │
        ▼
LLM-in-the-loop UI driver  ── decides each turn live from real DOM/network state
        │   (agent-driver-ui/tools.mjs — discrete Playwright tools)
        ▼
Playwright browser (Chromium, headful-authenticated via stored storageState)
        │   drives apps/web running on staging AKS
        ▼
Evidence capture per turn (lib/browser.mjs + lib/evidence.mjs)
   • DOM snapshot (keyed elements, roles, visible text)
   • screenshot (PNG, judge evidence only)
   • console log (errors/warnings)  ← objective P0 signal
   • network log (requests + statuses the page made)
   • cross-reference: kubectl logs + App Insights for the run_id/time window
        │
        ▼
Transcript (transcripts-ui/*.json)  — verbatim, lossless, screenshot paths embedded
        │
        ├── Driver verdict (objective P0 UI facts only) → reporter banner
        └── Judge prompt (shared lib/judge.mjs, extended) → LLM/human P1 verdict
                        │
                        ▼
              Cross-run meta-aggregation (shared lib/meta-aggregate.mjs)
```

### How Playwright drives the browser

- **Chromium via `@playwright/test`'s `chromium` browser type** (the library, not
  necessarily the `playwright test` runner — see below), launched with a
  pre-authenticated `storageState` so it starts already logged in.
- **Selectors are keyed and semantic, never positional.** The driver prefers, in
  order: `getByTestId` (`data-testid`, already used 86+ places in `apps/web/src`),
  then ARIA role + accessible name (`getByRole`), then a stable visible-text anchor.
  Positional/CSS-structural selectors are banned in the driver because they turn a
  cosmetic reflow into a false P0 fail. Where a scenario needs a stable hook that
  doesn't exist yet, the harness **files a `data-testid` request** against the
  component rather than reaching for a brittle selector (a real backend/frontend
  seam — see rollout).
- **Bounded, event-driven waits** (`expect(locator).toBeVisible({timeout})`,
  `waitForResponse`), never fixed `sleep`s, so timing flake is not misread as a
  driver P0 fail. A wait that times out is captured as a distinct
  `affordance-never-reachable` P0 fact, with the screenshot + network log attached
  so the judge/human can see whether it was a real defect or a slow backend.

### How personas / briefs work

Same **brief, not script** model as the API harness (`briefs/priya.md` is the
reference). A brief gives the driving LLM the persona's identity, goal, voice,
constraints, and a **mandatory ≥2 pushback** rule — it does **not** say which button
to click. A fresh-context LLM is handed only the brief and drives the browser
turn-by-turn, deciding each action from **what the page actually shows**, exactly as
the API driver decides from what the API actually returned.

- A **turn = one UI action** (navigate, click a keyed control, type into the
  coordinator composer, open a panel, read a notification), each recorded verbatim
  with the driver's `--thought` (intent) and a post-action DOM/console/network
  capture (composition).
- **Pushback in the UI** means the persona reacts to real rendered state and takes
  a real corrective UI action — e.g. sends a "actually change X" message to the
  coordinator (issue #272), clicks Clarify plan, or re-opens a panel that showed
  stale data — grounded in what was on screen, never pre-decided.
- **Persona core is shared; the surface adapter is UI-specific.** The persona core
  lives once in `scripts/persona-briefs/personas/` (identity, goal, voice,
  constraints, ≥2-pushback, authored criteria) and is the **same** definition the API
  and MCP harnesses drive. The UI harness adds only a thin `surfaces-ui/<persona>.ui.md`
  adapter mapping that persona's intent to UI actions (composer, notification center,
  node click, panel). A persona with no UI adapter simply isn't driven here. This is
  what makes "Jordan via API/UI/MCP" the same Jordan (see "Cross-Harness Shared
  Layer").
- **Dynamically generated, not fixed.** The shared brief-generation prompt-assembler
  pattern (from the API harness's `generate-brief.mjs`) is reused to have an LLM
  propose **new** persona cores + UI adapters (targeting a surface/issue class, with
  an exclusion list), so the harness probes the space of operator intents rather than
  replaying a fixed handful.

### How the judge integrates

The judge is the **shared judge core** (`scripts/harness-judge/core.mjs`, extracted
from the API harness's `lib/judge.mjs` — see "Cross-Harness Shared Layer"), invoked
through the **UI evidence adapter** (`adapters/ui.mjs`). The core assembles the
prompt and emits the one canonical verdict schema; the UI adapter's only job is to
turn the raw UI transcript into the normalized evidence shape by embedding, per turn:

- the **screenshot** (as an image reference / attachment the judging LLM can view),
- the **DOM snapshot** (keyed elements + roles + visible text — structured, not raw
  HTML dumps),
- the **console log** and **network log** for that turn,
- and the **kubectl/App Insights cross-reference** block for the run's time window.

The judge is asked the same two-layer question:

- **P0 (objective, already decided by the driver):** did each UI action succeed, did
  required elements appear, were there zero uncaught console errors, did no
  user-facing API call fail. The judge confirms the driver's P0 from evidence.
- **P1 (subjective, the judge's job):** compared to the persona's authored "Success
  looks like" criteria, was the UI actually clear/usable/correct — e.g. for #319,
  looking at the screenshot, can a user tell Human Review from Tool Approval at a
  glance? The judge quotes visible text and references the screenshot, and emits the
  same machine-readable `agentweaver.persona-judge-verdict/v1` block so
  `meta-aggregate.mjs` rolls UI and API verdicts together.

> **Why not a separate visual mechanism?** We deliberately reuse the LLM-judge
> pattern rather than inventing a pixel/visual-diff judge, because (a) modern judging
> LLMs can read screenshots, so UX-clarity judgment fits the same "read evidence,
> render verdict" contract; (b) a pixel-diff tool would smuggle a brittle,
> author-defined "correct look" back into the pipeline — exactly the embedded
> heuristic the driver/judge split forbids. The only UI-specific addition is teaching
> the prompt assembler to carry image + DOM evidence; the methodology is unchanged.

### How evidence / transcripts are captured

- Each turn produces a **lossless record**: action, `--thought`, the keyed target,
  the pre/post DOM snapshot, screenshot path, console entries, and network entries
  (method, URL, status — bodies where safe). Screenshots are written to
  `transcripts-ui/<run>/turn-<n>.png` and referenced by path in the JSON.
- **Cross-reference is a first-class capture step, not an afterthought.** After a
  turn that touches a run, the harness pulls the correlated
  `kubectl logs`/Application Insights slice for that `run_id` and time window and
  attaches it to the turn. So when the browser shows "preview unavailable" (#294),
  the transcript already contains whether the backend logged a port-discovery race
  or a genuine crash — the judge/human doesn't have to go dig it up, and a browser
  symptom is never filed without its backend context.
- Transcripts, screenshots, and verdicts are **git-ignored run artifacts**
  (`transcripts-ui/`, `verdicts/`), same as the API harness.

---

## Auth / session strategy (manual headful login, stored session reuse)

Per standing user instruction: **browser OAuth cannot be automated** — Ahmed must
log in manually in a headful browser once, and the session is then reused, not
re-authed every run.

Flow:

1. **One-time (or on session expiry) manual login.** A helper
   `node tools.mjs login --base-url <staging-url>` launches a **headful** Chromium,
   navigates to the app, and **pauses** (Playwright `page.pause()` / an explicit
   "press Enter when logged in" prompt). Ahmed completes the GitHub OAuth flow by
   hand in that visible browser.
2. **Persist `storageState`.** On confirmation, the harness writes Playwright's
   `storageState` (cookies + localStorage) to a **git-ignored, gitignored, local**
   path (`scripts/ui-persona-harness/.auth/staging.storageState.json`). This file
   is a credential — it is never committed, never logged, never attached to a
   finding, and lives only on the operator's machine.
3. **Reuse on every subsequent run.** The LLM-in-the-loop driver and any scenario
   run launch **headless** with `storageState` loaded, so they start already
   authenticated with no login step.
4. **Expiry handling is explicit, not silent.** If a run starts and the session is
   expired (redirected to login / 401 surfaced), the driver **stops with a clear
   `AUTH_EXPIRED` result** telling the operator to re-run `login` — it never tries to
   re-auth programmatically and never treats an expired session as a product bug.
5. **The `login` step is the only headful, human-gated part.** Everything after it
   is unattended, matching how the API harness uses a `gh auth token` bearer without
   re-minting it per run.

This mirrors the API harness's bearer-token model (resolve once, reuse) at the
browser layer, and is designed **for** the manual-login constraint rather than
assuming it away.

---

## Directory / file layout

A sibling of `scripts/persona-harness/`, reusing its shared modules:

```
A sibling of `scripts/persona-harness/` that **consumes the shared persona and judge
packages** (it ships no copied personas and no copied judge logic):

```
scripts/persona-briefs/          SHARED — persona cores + per-surface adapters (all three harnesses)
scripts/harness-judge/           SHARED — judge core + canonical verdict schema + meta-aggregate + evidence adapters
scripts/ui-persona-harness/      THIS harness (UI-specific driver + evidence only)
  README.md                    Mirrors the API harness README: why UI-driven, driver/judge split, auth flow
  package.json                 Declares @playwright/test; depends on ../persona-briefs + ../harness-judge; `npm test`
  playwright.config.ts         Chromium project, storageState wiring, headless default, headful `login` override
  lib/
    browser.mjs                Launch/attach Chromium with storageState; keyed-selector helpers; bounded waits
    evidence.mjs               Per-turn DOM snapshot + screenshot + console/network capture; lossless turn record
    crossref.mjs               kubectl logs + App Insights slice for a run_id/time-window (evidence, not verdict)
    reporter-ui.mjs            UI driver verdict + console banner (UI DRIVE+CAPTURE OK / UI DRIVER P0 FAIL)
    auth.mjs                   storageState load/validate; AUTH_EXPIRED detection; never re-auths
    (imports) ../../harness-judge/core.mjs           SHARED judge core (canonical verdict schema)
    (imports) ../../harness-judge/adapters/ui.mjs    SHARED — UI transcript -> normalized evidence
    (imports) ../../harness-judge/meta-aggregate.mjs SHARED — verdict rollup (API+UI+MCP mixed)
    (imports) ../../persona-briefs/index.mjs         SHARED — resolve persona core + UI surface adapter
  surfaces-ui/                   UI SURFACE ADAPTERS for shared personas (thin; NOT copied persona cores)
    priya.ui.md                  How Priya's intent maps to UI actions only (composer, notification center, ...)
    jordan.ui.md                 ...
  agent-driver-ui/
    tools.mjs                  Persona-agnostic discrete Playwright tools for an LLM to drive live:
                               login | goto | list-notifications | open-notification | click | type-coordinator |
                               open-session-panel | read-graph | read-tree | screenshot | check-approvals |
                               resolve-approval | capture | finish   (records a UI transcript; NO auto-confirm-to-deploy)
  transcripts-ui/              Emitted UI transcripts + screenshots (git-ignored)
  verdicts/                    -> symlink/points at the SHARED verdict pool so meta-aggregate mixes surfaces (git-ignored)
  .auth/                       storageState credential store (git-ignored; never committed/logged)
  test/
    evidence.test.mjs          Unit: DOM-snapshot shaping, console/network capture, redaction
    auth.test.mjs              Unit: storageState load/validate + AUTH_EXPIRED detection
    reporter-ui.test.mjs       Unit: deterministic P0 computation from a captured transcript
    ui-adapter.test.mjs        Unit: UI transcript -> normalized evidence (feeds the shared judge core)
```

The UI-specific `JUDGE.ui.md` appendix (what a screenshot/DOM snapshot can and cannot
prove) lives in the **shared** `scripts/harness-judge/` alongside `JUDGE.api.md` and
`JUDGE.mcp.md`, not here — so the methodology stays in one place.

Note the deliberate parallels to `scripts/persona-harness/`: `agent-driver-ui/`
mirrors `agent-driver/`, `reporter-ui.mjs` mirrors `reporter.mjs`. But personas and
judge logic are **imported from the shared packages**, never copied — a reader who
knows the API harness can navigate this one immediately, and the same Priya/Jordan
persona is provably the one both harnesses drive.

---

## Issue → coverage scenario mapping

Each issue becomes a **brief-driven scenario** (persona + goal + surfaces + authored
success criteria), not a fixed script. The "Driver P0 captures" column is what the
harness hard-checks objectively; the "Judge P1 decides" column is deferred to the
LLM/human judge from screenshot + DOM evidence.

| Issue | Surface / brief intent | Driver P0 captures (objective) | Judge P1 decides (subjective) |
|---|---|---|---|
| **#319** notification type indicator | Persona scans the notification center after a run parks | `notification-type-badge` element present per row; its accessible text distinguishes `human_review` vs `tool_approval`; badge maps to the `type` field the API returned | Is the indicator actually clear at a glance (icon+label), can the user tell action types apart without reading the title |
| **#288** epic: reliable tool approvals + global notifications | Persona with a pending tool approval + a pending human review, from anywhere in the app | Both pending items appear in the global notification surface; each CTA path navigates to a live, actionable control; no approval lost to stale state (item still present until resolved); resolve → item clears | Is "what is blocked" understandable; is scope choice meaningful/discoverable |
| **#289** epic: coherent live run tree/graph/session | Persona drives a multi-child run and inspects nodes | Selecting a node updates the session pane (no blank-on-switch); tree/graph node count matches run topology; session data refreshes (Changes/Files) | Is the roll-up/narrative coherent and understandable |
| **#290** epic: outcome-plan confirmation + messaging | Persona reviews a drafted plan, sends messages | User messages are **visible** in the transcript UI; node status reflects state; no duplicated/conflicting surfaces (single Outcome Plan region, not modal+inline) | Is the plan presented trustworthily; is messaging coherent |
| **#294** epic: dependable preview UX | Persona waits for and opens a preview | Preview affordance is **absent until** run reports preview-ready, then present and links to a reachable URL; transient failures degrade (retry state) rather than dead-ending — **cross-ref'd against backend port-discovery logs** | Is the wait/availability experience reassuring vs confusing |
| **#187** first-class Build & Test gate UI | Persona whose run goes through the Build & Test gate | A `build_test`-typed gate node renders as "Build & Test" (distinct from generic peer_review); its verdict routing (pass→next / request-changes→loop / declined→terminal) is visible; preview lights up after pass | Is the gate legible as a distinct, platform-owned step |
| **#188** Outcome Plan phase (not modal) | Persona confirms/clarifies a plan | Outcome Plan is a **docked phase** in the run page (graph+tree stay visible), **not** a blocking modal; renamed labels present ("Confirm plan"/"Clarify plan"/"Outcome"/"Open questions") | Does planning feel like a visible phase of orchestration vs a detached artifact |
| **#272** confirm/clarify via chat | Persona types "yes, that's right" / "actually change X" into the coordinator composer at a confirm gate | The chat message is **accepted** (run advances / re-drafts) — not ignored in favor of button-only; message echoes visibly | Is the natural-language path as trustworthy as the button |
| **#173** slide-in session panel on node click | Persona clicks a DAG node | Clicking a node opens a **docked right slide-in** (not a modal that hides the DAG); panel header (status/agent/role/model), Messages/Changes/Files tabs, and agent-sessions tree are present and populated | Is the panel's information hierarchy right/usable |
| **#283** session insights/observability panel | Persona opens the insights surface | A dedicated observability panel is reachable from the run view and shows token usage/model/timing/tool-call counts (**cross-ref'd against App Insights** for the same run) | Are the surfaced insights the right ones / clearly presented |
| **#316** memory + session history list views | Persona browses agent memory and session history | The new list views exist and consume the paginated envelope (`Pager` control present; page/size controls work; `total_count`/`total_pages` honored; no client-side truncation past page 1) | Is the listing navigable/understandable |
| **#306-class** phantom edges / render correctness | Persona drives a topology known to have triggered edge-occlusion | Rendered graph edges match the run's actual parent/child topology (no edge between unrelated nodes); **no uncaught render/console error**; captured as a repeatable class-check, not one hardcoded geometry | Is the graph visually coherent |
| **#1** the spike itself | — | This whole harness **is** the deliverable the spike asked for | See recommendation below |

Every row is expressed as a brief (persona goal + `surfaces:` + authored "Success
looks like"), so the LLM driver reaches the state its own way and the checks are on
**state**, not on a memorized click path — which is exactly what lets the harness
catch a **class** of regression (e.g. any phantom edge, #306) instead of one frozen
case.

---

## Rollout plan (built in parallel, without blocking the API track)

The harness is designed so multiple agents can build it concurrently, and so that
**no one touches `scripts/persona-harness/` files** while Tank is mid-edit there
(read-only import reference only until that track stabilizes).

**Phase 0 — scaffolding + auth (Trinity, first).**
Stand up `scripts/ui-persona-harness/` skeleton, `playwright.config.ts`, the
headful `login` → `storageState` flow (`lib/auth.mjs`), and prove one round trip:
manual login once, then a headless run that loads the deployed app authenticated and
captures a DOM snapshot + screenshot + console/network log for a single navigation.
No issue coverage yet — just the driver plumbing and the auth model working against
staging. This unblocks everyone else.

**Phase 1 — evidence + driver tools (Trinity + Smith, parallel).**
- Trinity: `lib/browser.mjs`, `lib/evidence.mjs`, `lib/crossref.mjs`,
  `reporter-ui.mjs`, and `agent-driver-ui/tools.mjs` (the discrete Playwright tool
  surface an LLM drives), with unit tests for the deterministic P0 computation and
  the redaction of the storageState credential from all output.
- Smith (test-scenario design): author the **persona cores + UI surface adapters**
  for the issue table above — persona identity, goal, and authored "Success looks
  like" criteria in the surface-agnostic `scripts/persona-briefs/` core, plus the
  thin `surfaces-ui/*.ui.md` adapter — reusing the existing `specs/personas/*.md`
  criteria where a persona already exists, coordinating with the API/MCP tracks so a
  new persona core is authored **once** and shared. Smith owns scenario **design**;
  Trinity owns the **driver** they run on. These are independent and proceed at the
  same time.

**Phase 2 — shared-layer extraction + UI judge adapter (Trinity + API-track owner +
Morpheus, coordinated).**
This is the phase that turns "the UI harness reads the API harness read-only" into
the real shared packages, and it is **cross-harness**, so it is coordinated with the
API-track owner and Morpheus (MCP), not done out-of-band on Tank's in-flight files:
- Extract `scripts/persona-briefs/` (persona cores + surface adapters) and
  `scripts/harness-judge/` (judge `core.mjs` from `lib/judge.mjs`, the canonical
  verdict schema, `meta-aggregate.mjs`, and the `adapters/` folder) as a **proposed
  diff handed to the API-track owner / coordinator to land**.
- Trinity contributes the **UI evidence adapter** (`adapters/ui.mjs`) and the
  `JUDGE.ui.md` appendix (image + DOM + console/network + cross-ref evidence);
  Morpheus contributes `adapters/mcp.mjs` and `JUDGE.mcp.md` in parallel — both plug
  into the same core without changing it.
- Until the extraction lands, each harness carries a thin local shim that formats its
  surface's evidence and delegates prompt assembly to the shared module read-only.

**Phase 3 — backend/frontend seams (Tank / Morpheus / frontend, only if needed).**
Two kinds of seam may surface; both are small, additive, and **do not block** the
above:
- **`data-testid` hooks** where a scenario needs a stable selector that a component
  doesn't yet expose. Filed against `apps/web` as tiny additive changes (the app
  already uses 86+ testids, so this is an established pattern), not brittle-selector
  workarounds.
- **A `storageState`/session-health seam** if we want a lightweight
  "is my session still valid" probe endpoint to make `AUTH_EXPIRED` detection crisp
  rather than inferring it from a login redirect. Optional; the redirect-based
  detection works without it.
These are handed to backend/frontend agents as discrete tickets; the harness
degrades gracefully without them.

**Phase 4 — first coverage runs + regression adoption.**
Run the Phase-1 briefs against staging, capture transcripts, normalize them through
the shared UI evidence adapter, render verdicts via the shared judge core, and
meta-aggregate across the batch (shared `meta-aggregate.mjs`, mixing API + UI + MCP
verdicts — including the cross-surface "did Jordan behave consistently across
surfaces" rollup). File any P0 driver fails and any judge-confirmed P1 issues with
the standing discipline (re-confirm still-reproduces, cross-ref logs, fix → deploy →
live-validate before closing).

**Sequencing discipline (matches the standing autopilot rules):**
- Trinity Phase 0 → then Trinity (driver) and Smith (persona cores + UI adapters)
  fan out in parallel.
- The shared-layer extraction (Phase 2) is coordinated across all three tracks, not
  forced, to avoid colliding with the API track's active edits.
- Everything is a driver-only capture until a judge (LLM/human) renders quality —
  no UI issue is closed on "the screenshot looks fine to me" alone; it goes through
  the judge and the deploy+live-validate closure rule.

---

## Recommendation on #1

**Keep #1 open, but narrow and re-scope it to _this_ Playwright/UI track — do not
close it as fully done, and do not treat the API harness as having superseded it.**

Reasoning:

- #1's title and body **explicitly name Playwright** and ask for persona-driven
  scenarios that reveal "UX gaps, missing affordances, confusing states." The API
  harness deliberately reads JSON and cannot see any of that — by the standing
  decision (`decisions.md:953`) it became the **primary** track and Playwright
  stayed the **secondary, frontend-specific** track that #1 was really about.
  Closing #1 on the API harness alone would leave the browser-facing half of the
  original research question unanswered.
- #1's completion signals — *personas authored*, *scenarios have observable success
  criteria*, *loop documented* — are **half-met**: personas and the brief/judge/loop
  are proven on the API side, but the **browser** driving loop this document
  specifies is not built yet.
- Therefore: **re-scope #1 to the UI track** (this plan), keep it open, and
  cross-reference it to the API harness (`scripts/persona-harness/`) as the already-
  delivered API half. Once Phase 0-4 above land and at least one UI persona brief
  drives → captures → is judged → is meta-aggregated end-to-end against staging,
  **#1 can close as fully satisfied** (both tracks delivered, loop documented across
  both). Filing a fresh narrower issue is unnecessary — #1's own text is already the
  right scope for the UI track; it just needs a comment re-pointing it at this plan
  and noting the API half is done under the separate persona-harness.

Concretely, the recommended comment on #1: _"API-driven half delivered as
`scripts/persona-harness/` (primary track, per decision 2026-07-14). This issue now
tracks the remaining Playwright/UI half, specified in `docs/ui-test-harness-plan.md`.
Close when the UI harness drives → captures → is judged → meta-aggregates one persona
brief end-to-end against staging."_

---

## Operating rules (inherited, apply to this track too)

- **Never call a UI fix "verified" from unit/component tests alone.** `apps/web`
  Vitest passing proves components render in isolation; it does **not** prove the
  deployed app behaves. Always drive the real deployed UI and capture the objective
  evidence before claiming "verified," and cross-reference backend logs.
- **Driver captures, judge decides.** No UI issue is closed on the strength of "it
  looks right" — subjective quality goes through the LLM/human judge from screenshot
  + DOM evidence.
- **Re-confirm still-reproduces before fixing.** Same as everywhere.
- **Issue closure requires deploy + live validation**, not review-passing alone
  (`decisions.md` process correction 2026-07-14).
- **Storage-state is a credential.** Never commit, log, or attach it to a finding.
- **Do not embed heuristic UI-quality pass/fail in the driver.** Only deterministic
  UI facts gate; everything else is deferred.
