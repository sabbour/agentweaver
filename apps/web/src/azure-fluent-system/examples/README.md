# Azure Fluent System examples

Checked-in usage samples for the public Azure Fluent System APIs. These files are library-local references: they are written as compilable-looking TSX that another agent or project can copy into a real app and wire to live data.

For the integrated pattern workbench with broader catalog coverage, see `apps/web/src/azure-fluent-system/showcase/README.md`.

## Coverage

| Surface | Example file(s) | Notes |
| --- | --- | --- |
| Provider / layout wiring | `provider-layout.example.tsx` | Shows `AzureFluentProvider` + `ManageResourcePattern` shell composition. |
| Accordion | `accordion.example.tsx` | Bordered and borderless collapsible sections with expanded/collapsed examples. |
| Portal shell / top nav / command bar | `portal-shell.example.tsx` | Exercises `PortalTopNav`, `PortalRail`, `PortalLayout`, `CommandBar`, and `FeedbackFooter`. |
| BladeHeader | `blade-header.example.tsx` | Action bar, overflow, dismiss, and prompt ribbon. |
| CopyButton | `copy-button.example.tsx` | Icon-only and labeled copy affordances. |
| CodeSnippet | `code-snippet.example.tsx` | Scrollable code block with line numbers, fold markers, and copy affordance. |
| ServiceMenu | `service-menu.example.tsx` | Search, nested items, favorites, and collapsed mode. |
| ResourceTagEditor | `resource-tag-editor.example.tsx` | Controlled row editing, validation, add, delete, and reset. |
| AzureDataGrid + filtering | `azure-data-grid-filtering.example.tsx` | `DataToolbar`, `FilterBar`, `AzureDataGrid`, and `Pager`. |
| Browse resources | `browse-resource-pattern.example.tsx` | First-class `BrowseResourcePattern` surface with loading/error/empty hooks. |
| Form footer + forms + step wizard | `form-wizard.example.tsx` | Groups `AzureForm`, `FormFooter`, and `StepWizardPattern`. |
| CreateResourcePattern | `create-resource-pattern.example.tsx` | Multi-step create flow with validation and review content. |
| Copilot composer / response | `copilot-composer-response.example.tsx` | Controlled composer state, response parts, attachments, and stop flow. |
| Copilot workspace | `copilot-workspace.example.tsx` | `CopilotWorkspacePattern` with service menu, response, and composer composition. |
| InlineCopilot | `inline-copilot.example.tsx` | Inline generate UX with suggestions, error, and generated states. |
| AgenticProgress / chain-of-thought list | `agentic-progress.example.tsx` | Explainable progress rows, approvals, denial path, and artifacts. |
| Tab lists | `tab-popovers.example.tsx` | `AzureTabList` with status indicators and content switching. |
| Popovers / callouts | `tab-popovers.example.tsx` | `HelpPopover` and `CalloutPopover` shown alongside tabbed content. |
| Service overview | `service-overview-feedback.example.tsx` | `ServiceOverviewPattern` cards and entry actions. |
| Notification pane + feedback footer | `service-overview-feedback.example.tsx` | Side-pane style notifications with restrained feedback affordance. |
| Delete resource | `service-overview-feedback.example.tsx` | `DeleteResourceDialog` trigger + confirmation copy. |
| Error / notifications | `service-overview-feedback.example.tsx` | `ErrorPattern` and `NotificationPattern` examples. |
| Icon registry usage | `icon-registry.example.tsx` | `AzureIconProvider`, `AzureIcon`, and manifest/static registries. |

## Intentional folds

| Public API | Covered in | Why it is linked |
| --- | --- | --- |
| `Pager` | `azure-data-grid-filtering.example.tsx`, `browse-resource-pattern.example.tsx` | Pager is primarily consumed with browse/filtering data surfaces. |
| `ManageResourcePattern` | `provider-layout.example.tsx` | The pattern is most useful when shown together with provider + header + service navigation wiring. |
| `FilteringPattern` | `browse-resource-pattern.example.tsx` | `FilteringPattern` is a thin title wrapper around `BrowseResourcePattern`; the browse sample documents the full contract. |
| `FormBladePattern` | `form-wizard.example.tsx` | `StepWizardPattern` is built on the same footer-and-form shell, so the grouped sample demonstrates the shared composition. |
| `CommandBar`, `PortalTopNav`, `PortalRail`, `PortalLayout`, `FeedbackFooter` | `portal-shell.example.tsx` | The shell primitives are intentionally grouped so the catalog-backed chrome reads as one Azure page hierarchy instead of isolated widgets. |

No public surfaces from the current standalone library are intentionally omitted without coverage or an explicit fold.

## When to use the showcase

Use `apps/web/src/azure-fluent-system/showcase/` when you need the full library workbench rather than a single focused sample. The showcase keeps the Azure UI Kit / Fluent 2 pattern language front and center while covering multiple Azure pattern families.
