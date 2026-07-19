---
name: "Agentweaver — The Calm Workbench"
description: "Warm-monochrome product UI inspired by Microsoft Copilot (Day) and M, built entirely on native FluentUI v9 (@fluentui/react-components) via a custom theme. No blue; color is status only. Open-source: no private dependencies — agentic chat is hand-rolled on native FluentUI, Copilot-styled."
colors:
  # Surfaces (warm neutral canvas → lighter card)
  canvas: "#f8f4f1"            # app/sidebar background (colorNeutralBackground2)
  surface: "#fdfbf8"           # floating content panel / card (colorNeutralBackground1)
  surface-selected: "#efeae7"  # hover + selected fill (colorNeutralBackground3)
  surface-pressed: "#e7e1dc"   # pressed fill (colorNeutralBackground4)
  # Ink (warm near-black foreground ramp)
  ink: "#272320"               # primary text + primary action (colorNeutralForeground1)
  ink-hover: "#3f3935"         # secondary text / brand hover
  ink-pressed: "#1c1815"       # darkest press
  muted: "#635c57"             # secondary/muted text (colorNeutralForeground3)
  quiet: "#746d68"             # metadata / tertiary label (colorNeutralForeground4)
  on-ink: "#faf6f2"            # text on near-black surfaces (colorNeutralForegroundOnBrand)
  # Lines
  border: "#e2ddd9"            # default stroke (colorNeutralStroke1)
  border-subtle: "#ece7e3"     # faint divider (colorNeutralStroke2)
  # Focus + status (the only non-neutral hues; used sparingly)
  focus-ring: "#8c837c"        # soft warm focus ring (colorStrokeFocus2)
  success: "#16a149"           # healthy / positive status only
  danger: "#a62147"            # destructive / error status only (colorPaletteRedForeground1)
  warning: "#8a4b01"           # alpha / caution status only
typography:
  display:
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, -apple-system, sans-serif'
    fontSize: "28px"
    fontWeight: 600
    lineHeight: "32px"
    letterSpacing: "-0.01em"
  headline:
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, sans-serif'
    fontSize: "20px"
    fontWeight: 600
    lineHeight: "26px"
    letterSpacing: "normal"
  title:
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, sans-serif'
    fontSize: "16px"
    fontWeight: 600
    lineHeight: "22px"
    letterSpacing: "normal"
  body:
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, sans-serif'
    fontSize: "15px"
    fontWeight: 400
    lineHeight: "1.5"
    letterSpacing: "normal"
  nav:
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, sans-serif'
    fontSize: "16px"
    fontWeight: 500
    lineHeight: "24px"
    letterSpacing: "normal"
  label:
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, sans-serif'
    fontSize: "13px"
    fontWeight: 500
    lineHeight: "18px"
    letterSpacing: "normal"
rounded:
  sm: "6px"
  md: "8px"
  lg: "12px"
  xl: "16px"
  panel: "10px"
  dialog: "20px"
  pill: "9999px"
spacing:
  xs: "4px"
  s: "8px"
  m: "12px"
  l: "16px"
  xl: "24px"
  xxl: "32px"
components:
  nav-item:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    typography: "{typography.nav}"
    rounded: "{rounded.lg}"
    padding: "6px 8px"
    height: "40px"
  nav-item-hover:
    backgroundColor: "{colors.surface-selected}"
    textColor: "{colors.ink}"
  nav-item-selected:
    backgroundColor: "{colors.surface-selected}"
    textColor: "{colors.ink}"
    rounded: "{rounded.lg}"
  button-primary:
    backgroundColor: "{colors.ink}"
    textColor: "{colors.on-ink}"
    rounded: "{rounded.md}"
    padding: "6px 12px"
    height: "32px"
  button-primary-hover:
    backgroundColor: "{colors.ink-hover}"
  button-primary-pressed:
    backgroundColor: "{colors.ink-pressed}"
  button-subtle:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "6px 12px"
  button-subtle-hover:
    backgroundColor: "{colors.surface-selected}"
    textColor: "{colors.ink}"
  input:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "6px 10px"
    height: "32px"
  card:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.lg}"
    padding: "16px"
  dialog-surface:
    backgroundColor: "{colors.canvas}"
    textColor: "{colors.ink}"
    rounded: "{rounded.dialog}"
    padding: "24px"
  start-task-pill:
    backgroundColor: "#fffffbb3"
    textColor: "{colors.ink}"
    rounded: "{rounded.lg}"
    padding: "8px 12px"
    height: "40px"
---

# Agentweaver Design System

## Overview

**Creative north star: The Calm Workbench.** A warm, quiet surface where an engineer monitors and steers long-running agent work without being rushed or intimidated. Rich in capability, calm in presentation.

Agentweaver is a **product** UI (design serves the work), rendered with **native FluentUI v9** (`@fluentui/react-components`). The entire warm-monochrome look is delivered by one custom Fluent `Theme` — `apps/web/src/theme.ts` (`agentweaverLightTheme`) — passed to a single `<FluentProvider>`. There is **no separate component kit**; import controls directly from FluentUI and let the theme carry the identity.

House style, in one line: **warm monochrome, near-black actions, no blue.** A single warm-neutral canvas (`#f8f4f1`) frames a slightly lighter floating content panel (`#fdfbf8`). Ink is a warm near-black (`#272320`). The only saturated color anywhere is **status** — green healthy, red danger, amber alpha/caution — never brand, never decoration.

Layout is **Copilot-shaped**: a single persistent left rail (no top bar), a rounded floating content panel inset ~6px on all sides, the signed-in persona pinned to the rail bottom, and a floating "Start task" pill top-right of the content. Light only. Density is comfortable but efficient — denser than the Copilot desktop reference because Agentweaver does more per screen, but never an enterprise grid.

Mood: calm, precise, approachable. Explicitly **not** Azure portal blue, not a dense resource/blade grid, not a dark theatrical console, not SaaS-cream, not gradient-heavy AI.

## Colors

The palette is a warm-neutral monochrome ramp plus three status hues. Sourced from Microsoft Copilot (copilot.com Day theme) and M/Scout light tokens.

**Surfaces**
- **Canvas `#f8f4f1`** — the app background and the left rail (they share one tone, so the rail reads as part of the canvas, separated only by spacing).
- **Surface `#fdfbf8`** — the floating content panel and cards; one step lighter than the canvas so content lifts without a hard border.
- **Selected/Hover `#efeae7`** — warm fill for hovered/selected nav items, list rows, subtle buttons.
- **Pressed `#e7e1dc`** — one step deeper for active/pressed.

**Ink (warm near-black ramp)**
- **Ink `#272320`** — primary text AND the primary action color (brand == near-black). Body contrast on canvas is ~13:1.
- **Muted `#635c57`** — secondary and supporting text; still ≥4.5:1 on canvas. Do not go lighter for body copy.
- **Quiet `#746d68`** — metadata, timestamps, quiet labels only.
- **On-ink `#faf6f2`** — text/icons on near-black surfaces (primary buttons).

**Lines**
- **Border `#e2ddd9`** — default 1px stroke; cards prefer a soft ring over a hard border.
- **Border-subtle `#ece7e3`** — faint dividers between nav groups and list rows.

**Focus + Status (used sparingly)**
- **Focus ring `#8c837c`** — a soft warm gray ring (not a hard black outline); Copilot/M focus feel.
- **Success `#16a149`**, **Danger `#a62147`**, **Warning `#8a4b01`** — status only. Always pair with a label or icon; never signal state with color alone.

**No blue.** Communication Blue and every accent blue are banned. Links, selection, focus, and primary actions are all warm near-black.

## Typography

One family, multiple weights: **Segoe UI** with a system sans fallback (Copilot ships Ginto and M ships Segoe Sans; Segoe UI is the closest broadly-available match). Base body is **15px / 1.5** — airy, a hair denser than Copilot's 16px because Agentweaver pages carry more.

- **Display 28px / 600** (letter-spacing -0.01em) — page titles ("Overview", "Agents").
- **Headline 20px / 600** — dialog titles, major section headers.
- **Title 16px / 600** — card titles, sub-section headers.
- **Nav 16px / 500 / 24px (1.5rem)** — left-rail items (matched to Copilot).
- **Body 15px / 400 / 1.5** — default prose; cap measure at ~65–75ch.
- **Label 13px / 500** — field labels, badges, quiet metadata.

Weight and size carry hierarchy, not color and not uppercase tracking. Avoid all-caps eyebrows as section scaffolding (use faint dividers instead).

## Elevation

**Tonal-first, shadow-second.** Depth comes mostly from the warm tonal stack (canvas → surface → selected), not from heavy shadows.

- **Content panel** — no drop shadow; separated from the canvas by the ~6px inset gap, the lighter fill, and its `10px` rounded corners. At most a 1px `border-subtle` hairline if definition is needed.
- **Cards** — a soft ring (border at low opacity) rather than a hard border or shadow.
- **Dialogs** — the one place real shadow is used: a layered soft stack `0 16px 24px rgba(0,0,0,0.08), 0 8px 16px rgba(0,0,0,0.03), 0 0 1px rgba(0,0,0,0.08)` over a dimmed, lightly-blurred backdrop (`rgba(0,0,0,0.10)` + `blur(2px)`).
- **Floating "Start task" pill** — a translucent warm-white surface with a small soft shadow, lifting on hover.

Motion is functional only: 150ms ease-out transitions on hover/selection, a 1px `translateY` nudge on button press, dialog fade/zoom-in. Every animation honors `prefers-reduced-motion`.

## Components

Import all controls from `@fluentui/react-components`; the theme styles them. App-specific chrome lives in `apps/web/src/components/shell/` (shell) and `apps/web/src/components/ui/` (shared patterns like `AppDialog`).

- **Left rail nav item** — icon (20px) + label (nav type), `12px` radius, `6px 8px` padding, ~40px tall. Resting: transparent. Hover: `#efeae7`. Selected: `#efeae7` fill + warm ink text (never dark-inverted). The pill is inset ~8px from both rail edges (it floats; it must not bleed to the rail edges). Groups are separated by faint `#ece7e3` dividers, not uppercase headings.
- **Primary button** (`appearance="primary"`) — near-black `#272320` bg, `#faf6f2` text, `8px` radius. Hover lightens to `#3f3935`; press to `#1c1815` + 1px translateY; focus shows the soft ring; disabled drops to ~50% opacity.
- **Subtle/ghost button** (`appearance="subtle"`) — transparent; hover fills warm `#efeae7`. Use for secondary and toolbar actions. Command/toolbar backgrounds stay transparent.
- **Split "Create" action** — a primary button with a menu (Blank / From GitHub) rather than two wide separate buttons.
- **Input / textarea** (Fluent `Field` + `Input`) — subtle filled `#fdfbf8` (or transparent) with a 1px `#e2ddd9` border, `8px` radius, placeholder in `#635c57`; on focus the border tightens and the soft ring appears.
- **Card** — surface `#fdfbf8`, `12px` radius, soft ring (not a hard border), 16px padding. Never nest cards. Avoid identical repeating card grids where a rich list fits better.
- **Dialog** (`AppDialog`, native Fluent `Dialog`) — warm canvas surface `#f8f4f1`, `20px` radius, layered soft shadow, dimmed blurred backdrop, vertically + horizontally centered (never override Fluent's `position: fixed`), `max-height: calc(100vh - 48px)` with internal scroll. Header: title (headline), optional muted description. Footer: full-width near-black primary with a text/subtle Cancel.
- **"Start task" pill** — floating top-right of the content panel; translucent warm-white `rgba(255,255,251,0.7)`, `12px` radius, 14px/400 text, ~40px tall, soft shadow. Matches Copilot's "Temporary" pill.
- **Page pattern** — default to a rich **list** (M-style rows with hover actions), not a dense command-strip-over-table. Reserve a top command strip only for genuine bulk-grid pages.
- **Status** — a colored dot/badge plus a text label (Healthy, Idle, Alpha). Never color-only.

### Agentic chat surfaces — native FluentUI, Copilot-styled (no private deps)

Agentweaver is open-source, so it takes **no private dependencies**. Agentic chat surfaces (the operator dock / Console, the CoordinatorRunPage composer + transcript, the RunTimeline activity accordion, any future assistant) are **hand-rolled on native FluentUI**, styled to the copilot.com Day look via `agentweaverLightTheme`. `@1js/fluentai` and `@1js/fai-react-chat-input` are not shipped dependencies — the last runtime usage (RunTimeline's activity accordion) was replaced with a native FluentUI `Accordion` implementation. Their component **type definitions remain useful only as historical design reference** for mirroring the real Copilot chat anatomy natively — never imported in shipped code.

Build chat from the shared native components under `apps/web/src/components/ui/copilot/`:
- **Composer** — Fluent `Textarea` (auto-grow) in a rounded (~24px) warm-white card with a send button and optional attach / mode-dropdown slots (echoes copilot's composer). Transparent, calm, no blue.
- **MessageList / MessageBubble** (user vs assistant) and **OutputCard** (assistant response container with optional streaming/progress + feedback) — warm-monochrome, never a blue chat accent.
These compose with the Agentic Progress components (below) so a run/console surface = messages + agentic steps + composer.

### Run activity, tool calls & approvals — Agentic Progress (native)

Run views (steps, tool calls, artifacts, human approvals) use the **native Agentic Progress** components under `apps/web/src/components/ui/agentic/` (reimplemented from the pattern in `copilot-fluent-system/examples/agentic-progress.example.tsx` + `agentic-approval-pattern.example.tsx` — read those as the design reference; do not import the kit). Keep the data model:

- A **step** carries: `title`, `body`, a `status` of `complete | running | warning | pending | blocked`, an optional `needsInput` flag, a `riskText` string for decisions, and `artifacts` (each a titled chip with a type + icon + `onOpen`).
- Steps render as a vertical, expandable timeline (running/needs-input steps open by default); status is shown by an icon + label, never color alone.
- A **needs-input** step surfaces an inline, plainly-worded approval with explicit **Approve / Deny** actions and the risk stated before the human decides (human-in-the-loop is a product principle — make the consequence legible).
- **Tool calls** and their results read as steps/sub-rows with artifact chips, not raw logs; keep them scannable.

Use this for the CoordinatorRunPage timeline, orchestration/run detail, and any approval gate.

## Do's and Don'ts

**Do**
- Do drive all color from `theme.ts` tokens (Fluent CSS vars) so the whole app stays coherent from one source.
- Do keep the canvas and rail the same warm tone; let spacing and the lighter panel create separation.
- Do use near-black for primary actions, links, selection, and focus.
- Do reserve saturated color for status only, and always pair it with a label or icon.
- Do prefer rich lists and tonal layering; keep toolbars transparent.
- Do build every agentic chat surface natively on `@fluentui/react-components`, styled through `agentweaverLightTheme` — no private-feed dependency.
- Do model run activity, tool calls, and approvals on the Agentic Progress step vocabulary (status / needsInput / riskText / artifacts) with inline, plainly-worded human approvals.
- Do keep body text at `#272320`/`#635c57` on the warm canvas; verify ≥4.5:1.
- Do honor `prefers-reduced-motion` on every transition.

**Don't**
- Don't introduce blue (Communication Blue or any accent blue) anywhere.
- Don't add `@1js/fluentai`, `@1js/fai-react-chat-input`, or any private-feed package as a shipped dependency — hand-roll the composer, message list, chain-of-thought, streamed output, and citation UI natively instead.
- Don't rebuild the retired `copilot-fluent-system` kit or import from it in new code; use native FluentUI + the theme. (Its `examples/agentic-*` files stay as a design *reference* for the run/approval vocabulary only.)
- Don't use Azure portal chrome: resource/blade subtitles, command-bar-over-dense-grid defaults, or enterprise grid density.
- Don't put uppercase tracked eyebrows above sections; use faint dividers.
- Don't let nav pills bleed to the rail edges, or let dialog surfaces stick to the top of the window.
- Don't use light-gray body text on the cream canvas ("for elegance") — it fails contrast and reads as AI slop.
- Don't invent marketing copy, dead controls, or Azure-service language; show only what the app actually does.
- Don't nest cards or repeat identical card grids where a list is the better affordance.
