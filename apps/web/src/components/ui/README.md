# Agentweaver UI pattern kit (`components/ui/`)

The **coherence contract** for the app. Every page composes from these patterns so
the whole product reads as one calm, warm-monochrome workbench — and so the three
systemic failures from the app critique are structurally impossible to reintroduce:

- **No Azure/blade/resource/operator vocabulary** — `PageHeader` is just a title.
- **No uppercase tracked eyebrows** — `PageSection` uses a sentence-case title + a
  faint 1px divider instead.
- **No blue, no hero-metric grids, no identical card grids** — status color only;
  `RichList` and `MetricRow` replace the banned templates.

Rules for everything here: native `@fluentui/react-components` +
`@fluentui/react-icons` only, theme CSS vars only (no hard-coded hex except
unavoidable measured shadows), and **no `copilot-fluent-system` imports**.

Import from the barrel:

```ts
import {
  PageContainer, PageHeader, PageSection,
  RichList, ListRow, MetricRow, StatTile,
  EmptyState, LoadingState, ErrorState, AppCard,
  Display, Headline, TitleText, Body, Label,
} from '../components/ui';
```

## Typography — the one type convention

Hierarchy is carried by **size + weight**, never color or uppercase tracking.
Roles (from DESIGN.md): `display` 28/600, `headline` 20/600, `title` 16/600,
`body` 15/1.5, `nav` 16/500, `label` 13/500.

- Named components: `<Display>`, `<Headline>`, `<TitleText>`, `<Body>`, `<Label>`.
- Generic: `<TypeText role="title" tone="muted">`.
- Compose a role onto another element via `useTypographyStyles()` class names.
- `tone` may be `default | muted | quiet` (maps to the warm neutral fg ramp).

You almost never set font-size/weight by hand — pick a role.

## Components

### `PageContainer`
Standard in-page vertical rhythm. The shell already supplies the panel + gutter,
so this adds no outer padding — it only stacks blocks and optionally caps the
readable width.
```tsx
<PageContainer width="full | readable | narrow">…</PageContainer>
```

### `PageHeader`
The one header for every page. Replaces all `BladeHeader`/`CommandBar` headers.
```tsx
<PageHeader
  title="Overview"
  description="Optional muted sentence."
  breadcrumbs={<>…</>}      // optional, rendered above the title
  actions={<Button…/>}      // optional, right-aligned, transparent toolbar
/>
```
Title is the Display role. **No eyebrow, no Azure words**, actions stay transparent.

### `PageSection`
The structural replacement for the banned eyebrow: a sentence-case Title (16/600)
+ optional description + a faint 1px divider.
```tsx
<PageSection title="Recent projects" description="…" actions={<Link…/>}>
  …section body…
</PageSection>
```
Pass `hideDivider` to drop the divider. **Never** render an all-caps tracked label.

### `RichList` + `ListRow`
The M rich-list pattern — the app's default collection affordance. Replaces
hero-metric grids and identical card grids.
```tsx
<RichList aria-label="Workflows" bordered dividers>
  <ListRow
    media={<FlowRegular />} bubble
    primary="triage-and-fix" primaryAside={<Badge…/>}
    secondary="Reusable pipeline definition"
    meta={<span>2h ago</span>}
    actions={<Button appearance="subtle" icon={<EditRegular/>} />}
    as={Link} to="/…"           // or onClick — makes the whole row interactive
  />
</RichList>
```
Rows get a rounded warm hover fill; actions are quiet at rest and revealed on
hover/focus (`actionsAlwaysVisible` to pin them). `bordered` wraps rows in a
soft-ring card; `dividers` draws faint inter-row hairlines.

### `MetricRow` + `StatTile`
Restrained metrics — **not** the hero-metric template. Label in muted sentence
case, value in ink.
```tsx
<MetricRow items={[{ label: 'In flight', value: 3, icon: <PlayRegular/> }]} />
<StatTile label="Active projects" value={12} hint="last 7 days" />
```
Prefer `MetricRow` (inline, faint separators) as the default; reserve `StatTile`
for a rare standalone metric.

### `EmptyState` / `LoadingState` / `ErrorState`
Consistent status surfaces across pages.
```tsx
<EmptyState title="No workflows yet" description="…" icon={<FlowRegular/>} action={<Button…/>} />
<LoadingState rows={3} label="Loading workflows" />       // Fluent Skeleton, reduced-motion aware
<ErrorState message={err} onRetry={reload} />             // retry action built in
```
Status color is limited to the error icon and always paired with text.

### `AppCard`
Thin wrapper over Fluent `Card` with the soft-ring style (no hard border, no
shadow, 12px radius, 16px padding). Never nest cards. Pass `interactive` for a
subtle hover on clickable cards.

### `AppDialog`
Reuse the existing `AppDialog` for modals (warm canvas surface, layered soft
shadow, blurred backdrop, full-width primary + text Cancel footer). Do not
duplicate it.

## When to use what

| Need | Use |
| --- | --- |
| Page title + actions | `PageHeader` |
| Group content under a heading | `PageSection` |
| A list of things (workflows, runs, projects) | `RichList` + `ListRow` |
| A few summary numbers | `MetricRow` (or `StatTile` for one) |
| Nothing to show | `EmptyState` |
| Data loading | `LoadingState` |
| Request failed | `ErrorState` (with `onRetry`) |
| A bordered content block | `AppCard` |
| A modal | `AppDialog` |
| Any text | a typography role (`Display`/`Body`/…) |

Reference implementations: `pages/WorkflowsPage.tsx` (rich list) and
`pages/OverviewPage.tsx` (header + sections + metrics + states).
