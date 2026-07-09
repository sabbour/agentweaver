# Azure Fluent System showcase

This showcase is the library-local product shell for `apps/web/src/azure-fluent-system`. It has three primary experiences:

1. **Components** — a component preview backed by `catalog/COMPONENTS.md`
2. **Patterns** — a pattern example browser backed by `catalog/PATTERNS.md`
3. **Icons** — a visual icon browser backed by `catalog/ICONS.md`

Historical doctrine markers retained for validation: "exactly two primary experiences" and "inline icon catalog surface".

It is intentionally standalone and portable. Do not couple it to app routes, app state, or assets outside this library. Downstream projects should be able to use this showcase, the examples, the three checked-in catalog files, and the library sources without Figma MCP.

Start with the package-local `../DESIGN.md` when adopting this library in another project. The showcase is the live companion to that portable design addendum.

## Components view

The Components view is the fastest way to inspect the full checked-in Figma component inventory while keeping rendered previews front and center when they exist.

- Browse every inventoried component row with status filters.
- Select a rendered entry to see its focused live preview.
- Select a mapped or unextracted entry to see a concise status and next-step placeholder.
- Review local example paths, implementation files, and source-node links only for the selected entry.

## Patterns view

The pattern example browser uses the checked-in pattern catalog, not screenshots, as its source of truth.

Pattern families shown in the browser:

- Create / stepped form blade
- Browse Resource
- Notifications
- Delete A Resource
- Manage A Resource
- Service overview
- Feedback / CES / CVA
- Table of contents / pattern index

## Icons view

The icon browser is a lightweight visual index for the local Azure and fallback icon registry.

- Search by icon name or alias.
- Confirm the local source for each previewed icon.
- Use `catalog/ICONS.md` as the durable checked-in inventory.

## Local-first workflow

Ordinary downstream consumption should work from local files only:

1. Open the showcase and inspect the Components, Patterns, or Icons view for the task at hand.
2. Read `../DESIGN.md` for the enforceable package-local rules and anti-rules.
3. Follow the checked-in example paths, implementation files, and `catalog/ICONS.md` when icon assets or aliases are involved.
4. Import the needed primitives from the library root.
5. Compose the target task flow with the checked-in React components, CSS tokens, docs, and assets.
6. Treat Figma dev-mode URLs as citations only.
7. Use Figma MCP only if it is available and you are intentionally refreshing the catalog.

## Canonical stepped-form traceability

The concrete traceability target is the worked example `3203:24770`.

Canonical prompt shape:

`Implement this design from Figma. @https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates--Fluent-2-?node-id=3203-24770&m=dev`

## Run locally

```powershell
cd apps/web/src/azure-fluent-system
npm run showcase:dev
```

Open `http://127.0.0.1:4174/`.

## Validation gate

Run the doctrine gate whenever the showcase app or `DESIGN.md` changes:

```powershell
cd apps/web/src/azure-fluent-system
npm run showcase:validate-doctrine
```

## First viewport render integrity

This showcase is a visual FAIL until manual browser or screenshot inspection confirms the first viewport is a restrained, single coherent product surface. Reject any version that drifts back to dense dashboard-first demos, giant dark panels, clipped split panes, or screenshot-dependent explanations.
