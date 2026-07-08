# Azure Fluent System examples

Checked-in usage samples for the public Azure Fluent System APIs:

- `provider-layout.example.tsx` — `AzureFluentProvider`, `BladeHeader`, `ManageResourcePattern`, `ServiceMenu`
- `blade-header.example.tsx` — `BladeHeader` action and prompt ribbon composition
- `resource-tag-editor.example.tsx` — controlled `ResourceTagEditor`
- `azure-data-grid-filtering.example.tsx` — `DataToolbar`, `FilterBar`, `AzureDataGrid`, `Pager`
- `copilot-composer-response.example.tsx` — `CopilotComposer` + `CopilotResponse`
- `create-resource-pattern.example.tsx` — derived `CreateResourcePattern`
- `icon-registry.example.tsx` — `AzureIconProvider`, `AzureIcon`, and icon registry helpers

These examples are intentionally library-local reference files. They are not wired into the app build, but they are written as compilable-looking TSX so other agents and projects can reuse the contracts directly from source control.
