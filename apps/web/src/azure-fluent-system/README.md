# Azure Fluent System module

Reusable Azure Fluent-style React components and patterns built on `@fluentui/react-components` v9. Import from `apps/web/src/azure-fluent-system` while this repository does not have a packages workspace. The module is isolated so it can move later to a standalone package such as `@org/azure-fluent-system`.

```tsx
import {
  AzureFluentProvider,
  AzureIconProvider,
  BladeHeader,
  ServiceMenu,
  DataToolbar,
  FilterBar,
  AzureDataGrid,
  ResourceTagEditor,
  CopilotComposer,
} from './azure-fluent-system';
```

## Checked-in recipes and examples

The library now owns its offline handoff artifacts, so other agents can use the checked-in package directly:

- `apps/web/src/azure-fluent-system/recipes/implementation-recipes.json`
- `apps/web/src/azure-fluent-system/recipes/implementation-recipe-gap-list.json`
- `apps/web/src/azure-fluent-system/recipes/standalone-system-plan.json`
- `apps/web/src/azure-fluent-system/examples/README.md`
- `apps/web/src/azure-fluent-system/examples/*.example.tsx`

Use the recipe JSON for API contracts and evidence-backed implementation guidance. Use `examples/` for compilable-looking TSX samples covering provider/layout, BladeHeader, ResourceTagEditor, AzureDataGrid + filtering, Copilot composer/response, CreateResourcePattern, and icon registry wiring.

## Production usage shape

Wrap consumers once so Fluent tokens and the portable `azf-` CSS contract are present:

```tsx
<AzureFluentProvider density="cozy">
  <BladeHeader
    title="Virtual machines"
    subtitle="Compute resources"
    actions={[{ id: 'refresh', label: 'Refresh', onClick: refresh }]}
    onDismiss={closeBlade}
  />
</AzureFluentProvider>
```

Use the data surface without card wrappers:

```tsx
<DataToolbar actions={[{ id: 'create', label: 'Create', appearance: 'primary', onClick: createVm }]} />
<FilterBar filters={filters} searchValue={query} onSearchChange={setQuery} />
<AzureDataGrid
  items={resources}
  getRowId={(resource) => resource.id}
  selectedRowId={selectedId}
  onRowClick={openResource}
  columns={[{ columnId: 'name', header: 'Name', sortable: true, sortValue: (r) => r.name, renderCell: (r) => r.name }]}
/>
```

Use tag editing as a controlled component:

```tsx
<ResourceTagEditor
  rows={tags}
  resources={resources}
  validation={tagErrors}
  onRowChange={(rowId, patch) => setTags(updateTag(rowId, patch))}
  onAddRow={addTag}
  onDeleteRow={deleteTag}
/>
```

Copilot surfaces support explicit loading/stop/error states and do not expose hidden reasoning:

```tsx
<CopilotComposer
  value={prompt}
  onChange={setPrompt}
  onSend={sendPrompt}
  isRunning={isRunning}
  onStop={stopPrompt}
  attachments={attachments}
/>
```

## Icon source hierarchy

1. General system icons use the installed `@fluentui/react-icons` package, which is the React package for the Microsoft Fluent system icons family (`microsoft/fluentui-system-icons`). Do not add another dependency for these unless a missing glyph requires it.
2. Azure/resource-specific product glyphs should come from IconCloud (`https://iconcloud.design/`), the approved authenticated source. This module does not store credentials, scrape private APIs, or redistribute raw Figma glyphs.
3. The Figma Community Microsoft Fluent System Iconography file is visual/reference guidance only unless assets are explicitly exported under acceptable terms.

Export approved SVG/PNG assets from IconCloud into workflow-generated artifacts first, normalize and deduplicate them, then check the approved assets into this module under `./assets/icons/azure/`. Register a small explicit set with `createIconCloudRegistry`:

```tsx
const registry = createIconCloudRegistry(['VirtualMachine', 'StorageAccount'] as const, {
  basePath: '/azure-icons',
});

<AzureIconProvider registry={registry}>
  <AzureIcon name="VirtualMachine" size={18} label="Virtual machine" />
</AzureIconProvider>
```

For normalized IconCloud manifests, use `createIconCloudRegistryFromManifest` so the registry follows the manifest file paths instead of assuming every asset is named directly after the icon. The checked-in Azure icon bundle lives in `./assets/icons/azure/azure-icons-manifest.json` with SVGs under `./assets/icons/azure/assets/`:

```tsx
const manifestUrl = new URL('./assets/icons/azure/azure-icons-manifest.json', import.meta.url);
const iconsBaseUrl = new URL('./assets/icons/azure/', import.meta.url).toString();
const azureIconsManifest = await fetch(manifestUrl).then((response) => response.json());

const registry = createIconCloudRegistryFromManifest(azureIconsManifest, {
  basePath: iconsBaseUrl,
  filter: (icon) => icon.collections?.includes('Compute') ?? icon.category === 'Compute',
  getKey: (icon) => `${icon.category ?? icon.collection}/${icon.name}`,
});
```

The checked-in manifest keeps each icon `file` entry relative to the manifest directory (`assets/...svg`), so consumers can fetch the manifest and point `basePath` at the checked-in folder without depending on session artifacts or absolute local paths.
