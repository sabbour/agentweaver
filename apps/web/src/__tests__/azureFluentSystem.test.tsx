import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import {
  AgenticProgress,
  AzureAccordion,
  AzureDataGrid,
  AzureFluentProvider,
  AzureStepList,
  BladeHeader,
  CodeSnippet,
  CopilotComposer,
  CopilotResponse,
  CopyButton,
  CreateResourcePattern,
  FormFieldRow,
  InlineCopilot,
  NotificationPane,
  PortalLayout,
  PortalRail,
  PortalTopNav,
  ResourceTagEditor,
  ServiceMenu,
  createIconCloudRegistryFromManifest,
} from '../azure-fluent-system';
import type { AzureIconDefinition } from '../azure-fluent-system';
import {
  AzureFluentShowcaseApp,
  showcaseComponentInventoryEntries,
  showcaseComponentInventoryNodeIds,
  showcaseComponentInventorySourceNodeIds,
} from '../azure-fluent-system/showcase/AzureFluentShowcaseApp';
import { componentCatalogData } from '../azure-fluent-system/showcase/catalogData';
import type { ReactNode } from 'react';

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider>{children}</AzureFluentProvider>;
}

function parseComponentCatalogRows(markdown: string) {
  const lines = markdown.split(/\r?\n/);
  const inventoryHeaderIndex = lines.findIndex((line) => line.includes('| Figma node reference / dev-mode URL |'));

  return lines
    .slice(inventoryHeaderIndex + 2)
    .filter((line) => line.startsWith('|') && !line.startsWith('| ---') && !line.startsWith('| Figma node reference'))
    .map((line) => {
      const [
        figmaNodeReference,
        extractionStatus,
        extractionDate,
        extractedFrom,
        implementedMapping,
        ,
        showcase,
      ] = line.split('|').slice(1, -1).map((part) => part.trim());

      return {
        figmaNodeReference,
        extractionStatus,
        extractionDate,
        extractedFrom,
        implementedMapping,
        showcase,
      };
    });
}

afterEach(() => cleanup());

describe('azure-fluent-system hardened components', () => {
  it('renders BladeHeader with real actions and no fabricated defaults', () => {
    const onSave = vi.fn();
    render(
      <Wrapper>
        <BladeHeader
          title="Virtual machines"
          subtitle="Compute"
          actions={[{ id: 'save', label: 'Save', appearance: 'primary', onClick: onSave }]}
        />
      </Wrapper>,
    );
    expect(screen.getByRole('heading', { name: 'Virtual machines', level: 1 })).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Pin' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(onSave).toHaveBeenCalledTimes(1);
  });

  it('filters and selects ServiceMenu items with selected rail semantics', () => {
    const onSelect = vi.fn();
    render(
      <Wrapper>
        <ServiceMenu
          selectedId="activity"
          onSelect={onSelect}
          groups={[{ id: 'manage', label: 'Manage', items: [{ id: 'overview', label: 'Overview' }, { id: 'activity', label: 'Activity log' }] }]}
        />
      </Wrapper>,
    );
    expect(screen.getByRole('button', { name: 'Activity log' }).getAttribute('aria-current')).toBe('page');
    fireEvent.change(screen.getByLabelText('Filter navigation'), { target: { value: 'over' } });
    expect(screen.queryByRole('button', { name: 'Activity log' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Overview' }));
    expect(onSelect).toHaveBeenCalledWith('overview');
  });

  it('sorts and activates AzureDataGrid rows', () => {
    const onRowClick = vi.fn();
    render(
      <Wrapper>
        <AzureDataGrid
          getRowId={(item) => item.id}
          onRowClick={onRowClick}
          items={[{ id: 'b', name: 'Beta' }, { id: 'a', name: 'Alpha' }]}
          columns={[{ columnId: 'name', header: 'Name', sortable: true, sortValue: (item) => item.name, renderCell: (item) => item.name }]}
        />
      </Wrapper>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Name' }));
    const cells = screen.getAllByRole('cell');
    expect(cells[0].textContent).toBe('Alpha');
    fireEvent.click(screen.getByText('Alpha'));
    expect(onRowClick).toHaveBeenCalledWith({ id: 'a', name: 'Alpha' });
  });

  it('edits ResourceTagEditor rows and adds rows', () => {
    const onRowChange = vi.fn();
    const onAddRow = vi.fn();
    render(
      <Wrapper>
        <ResourceTagEditor
          rows={[{ id: 'r1', name: 'env', value: 'prod', resourceId: 'vm1' }]}
          resources={[{ id: 'vm1', label: 'VM one' }]}
          onRowChange={onRowChange}
          onAddRow={onAddRow}
        />
      </Wrapper>,
    );
    fireEvent.change(screen.getByLabelText('Tag value for row 1'), { target: { value: 'test' } });
    expect(onRowChange).toHaveBeenCalledWith('r1', { value: 'test' });
    fireEvent.click(screen.getByRole('button', { name: 'Add tag' }));
    expect(onAddRow).toHaveBeenCalledTimes(1);
  });

  it('sends and stops CopilotComposer with accessible controls', () => {
    const onSend = vi.fn();
    const onStop = vi.fn();
    const { rerender } = render(
      <Wrapper>
        <CopilotComposer value="Summarize" onChange={() => undefined} onSend={onSend} />
      </Wrapper>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    expect(onSend).toHaveBeenCalledTimes(1);
    rerender(
      <Wrapper>
        <CopilotComposer value="Summarize" onChange={() => undefined} onSend={onSend} isRunning onStop={onStop} />
      </Wrapper>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Stop response' }));
    expect(onStop).toHaveBeenCalledTimes(1);
  });

  it('shows AgenticProgress approval actions without exposing reasoning', () => {
    const onApprove = vi.fn();
    render(
      <Wrapper>
        <AgenticProgress
          defaultOpenItems={['deploy']}
          onApprove={onApprove}
          steps={[{ id: 'deploy', title: 'Deploy change', body: 'Waiting for approval', needsInput: true, riskText: 'This updates production.' }]}
        />
      </Wrapper>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    expect(onApprove).toHaveBeenCalledWith('deploy');
    expect(screen.queryByText(/chain of thought/i)).toBeNull();
  });

  it('renders CopilotResponse user and assistant surfaces with extracted metadata cues', () => {
    render(
      <Wrapper>
        <CopilotResponse
          parts={[
            { id: 'user', type: 'text', author: 'user', content: 'Summarize the incident.' },
            { id: 'assistant', type: 'text', title: 'Copilot', badge: 'AI-generated content may be incorrect', content: 'Two clusters share the same failing policy assignment.', supportingText: '1 request left' },
          ]}
        />
      </Wrapper>,
    );

    expect(screen.getByText('Summarize the incident.')).toBeDefined();
    expect(screen.getByText('AI-generated content may be incorrect')).toBeDefined();
    expect(screen.getByText('1 request left')).toBeDefined();
  });

  it('renders InlineCopilot open and guided states with extracted title and prompt cues', () => {
    const onChange = vi.fn();
    render(
      <Wrapper>
        <div className="azf-stack azf-gap-s">
          <InlineCopilot
            open
            trigger={<button type="button">Open inline Copilot</button>}
            value=""
            onChange={onChange}
            onSubmit={() => undefined}
            placeholder="Ask Copilot to draft, fix, or explain"
          />
          <InlineCopilot
            open
            trigger={<button type="button">Summarize with Copilot</button>}
            title="Summarize with Copilot"
            value="Summarize deployment failures from the last 24 hours"
            onChange={onChange}
            onSubmit={() => undefined}
            suggestions={[{ id: 'steps', label: 'Add next steps' }]}
          />
        </div>
      </Wrapper>,
    );

    expect(screen.getByRole('textbox', { name: 'Ask Copilot to draft, fix, or explain' })).toBeDefined();
    expect(screen.getAllByText('Summarize with Copilot').length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole('button', { name: 'Add next steps' }));
    expect(onChange).toHaveBeenCalledWith('Add next steps');
  });

  it('renders AzureAccordion with bordered and borderless variants', () => {
    render(
      <Wrapper>
        <div className="azf-stack azf-gap-m">
          <AzureAccordion
            defaultOpenItems={['details']}
            items={[
              { id: 'details', title: 'Details', content: <div>Expanded content</div> },
              { id: 'dependencies', title: 'Dependencies', content: <div>Collapsed content</div> },
            ]}
            multiple
          />
          <AzureAccordion
            bordered={false}
            items={[{ id: 'overview', title: 'Overview', content: <div>Overview content</div> }]}
            defaultOpenItems={['overview']}
            multiple
          />
        </div>
      </Wrapper>,
    );

    expect(screen.getByText('Expanded content')).toBeDefined();
    expect(screen.getByText('Overview content')).toBeDefined();
  });

  it('copies values with CopyButton and shows copied state', async () => {
    const onCopy = vi.fn(async () => undefined);
    render(
      <Wrapper>
        <CopyButton value="az aks show --name prod" label="Click here to copy" onCopy={onCopy} />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Click here to copy' }));
    expect(onCopy).toHaveBeenCalledWith('az aks show --name prod');
    expect(await screen.findByText('Copied')).toBeDefined();
  });

  it('renders CodeSnippet with line numbers and fold markers', () => {
    render(
      <Wrapper>
        <CodeSnippet
          title="ARM template"
          lines={[
            { lineNumber: 1, text: '{', foldState: 'expanded' },
            { lineNumber: 2, tokens: [{ text: '\"name\"', tone: 'key' }, { text: ': ', tone: 'operator' }, { text: '\"prod\"' }] },
            { lineNumber: 3, text: '}', tokens: [{ text: '}', tone: 'operator' }] },
          ]}
        />
      </Wrapper>,
    );

    expect(screen.getByText('ARM template')).toBeDefined();
    expect(screen.getByText('1')).toBeDefined();
    expect(screen.getByText('−')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Copy' })).toBeDefined();
  });

  it('provides a derived CreateResourcePattern with validation and fixed footer actions', () => {
    const onCreate = vi.fn();
    const onStepSelect = vi.fn();
    render(
      <Wrapper>
        <CreateResourcePattern
          title="Create storage account"
          currentStepId="basics"
          onStepSelect={onStepSelect}
          validationSummary="Fix required fields"
          steps={[
            {
              id: 'basics',
              label: 'Basics',
              description: 'Name and scope',
              content: (
                <FormFieldRow label="Subscription" htmlFor="test-subscription">
                  <input id="test-subscription" value="Contoso" readOnly />
                </FormFieldRow>
              ),
            },
            {
              id: 'review',
              label: 'Review',
              description: 'Confirm before submission',
              status: 'warning',
              content: <div>Review content</div>,
            },
          ]}
          primaryAction={{ id: 'create', label: 'Create', appearance: 'primary', onClick: onCreate }}
          secondaryAction={{ id: 'previous', label: 'Previous' }}
        />
      </Wrapper>,
    );
    expect(screen.getByRole('heading', { name: 'Create storage account', level: 1 })).toBeDefined();
    expect(screen.getByText('Fix required fields')).toBeDefined();
    fireEvent.click(screen.getByRole('tab', { name: /Step 2: Review/i }));
    expect(onStepSelect).toHaveBeenCalledWith('review');
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));
    expect(onCreate).toHaveBeenCalledTimes(1);
  });

  it('renders portal shell primitives with search and rail navigation', () => {
    const onSearchChange = vi.fn();
    const onInsights = vi.fn();
    render(
      <Wrapper>
        <PortalLayout
          topNav={
            <PortalTopNav
              brand={{ product: 'Microsoft Azure', area: 'Portal' }}
              searchValue="storage"
              onSearchChange={onSearchChange}
              persona={{ name: 'Ahmed Sabbour', secondaryText: 'Contoso Engineering' }}
            />
          }
          rail={
            <PortalRail
              items={[
                { id: 'home', label: 'Home', icon: <span>H</span>, selected: true },
                { id: 'insights', label: 'Insights', icon: <span>I</span>, onClick: onInsights },
              ]}
            />
          }
          header={<BladeHeader title="Resource Type" />}
        >
          <div>Portal body</div>
        </PortalLayout>
      </Wrapper>,
    );

    expect(screen.getByRole('searchbox', { name: 'Search Azure resources' })).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Insights' }));
    expect(onInsights).toHaveBeenCalledTimes(1);
    expect(screen.getByText('Portal body')).toBeDefined();
  });

  it('renders step and notification primitives from grouped inventory', () => {
    const onStepSelect = vi.fn();
    render(
      <Wrapper>
        <div className="azf-stack azf-gap-m">
          <AzureStepList
            selectedValue="scope"
            onStepSelect={onStepSelect}
            steps={[
              { id: 'scope', label: 'Scope', description: 'Choose subscription' },
              { id: 'review', label: 'Review', description: 'Check warnings', status: 'warning' },
            ]}
          />
          <NotificationPane
            items={[
              {
                id: 'n1',
                title: 'Firewall validation blocked',
                body: 'Resolve the private endpoint policy before rollout.',
                tone: 'warning',
              },
            ]}
          />
        </div>
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('tab', { name: 'Step 2: Review' }));
    expect(onStepSelect).toHaveBeenCalledWith('review');
    expect(screen.getByText('Firewall validation blocked')).toBeDefined();
  });

  it('creates an IconCloud registry from normalized manifest paths', () => {
    const registry = createIconCloudRegistryFromManifest(
      {
        icons: [
          { name: 'Virtual Machines', collection: 'Compute', category: 'Compute', collections: ['Compute'], file: 'assets/compute--virtual-machines.svg' },
          { name: 'Storage Accounts', collection: 'Storage', category: 'Storage', collections: ['Storage'], file: 'assets/storage--storage-accounts.svg' },
        ],
      },
      {
        basePath: '/azure-icons/',
        filter: (icon) => icon.collections?.includes('Compute') ?? false,
        getKey: (icon) => `${icon.category}/${icon.name}`,
      },
    );

    const definition = registry['Compute/Virtual Machines'] as AzureIconDefinition;
    expect(definition.src).toBe('/azure-icons/assets/compute--virtual-machines.svg');
    expect(definition.alt).toBe('Virtual Machines');
    expect(registry['Storage/Storage Accounts']).toBeUndefined();
  });

  it('documents the pattern doctrine in DESIGN.md', () => {
    const design = readFileSync(resolve(process.cwd(), '..', '..', 'DESIGN.md'), 'utf8');
    expect(design).toContain('Coverage is not fidelity.');
    expect(design).toContain('Resource Type node `4417:3962` is representative of one pattern family, not the whole showcase scope.');
    expect(design).toContain('pattern workbench/gallery');
  });

  it('documents a local-first downstream workflow without requiring MCP', () => {
    const portableDesign = readFileSync(resolve(process.cwd(), 'src', 'azure-fluent-system', 'DESIGN.md'), 'utf8');
    const libraryReadme = readFileSync(resolve(process.cwd(), 'src', 'azure-fluent-system', 'README.md'), 'utf8');
    const readme = readFileSync(resolve(process.cwd(), 'src', 'azure-fluent-system', 'showcase', 'README.md'), 'utf8');
    const patternsCatalog = readFileSync(resolve(process.cwd(), 'src', 'azure-fluent-system', 'catalog', 'PATTERNS.md'), 'utf8');
    expect(portableDesign).toContain('# Azure Fluent System — usage contract');
    expect(portableDesign).toContain('Refresh a catalog row from Figma MCP only when');
    expect(portableDesign).not.toContain('Agentweaver');
    expect(libraryReadme).toContain('Local-first downstream workflow');
    expect(libraryReadme).toContain('Use `DESIGN.md` as the portable design-system addendum');
    expect(readme).toContain('Local-first workflow');
    expect(readme).toContain('Read `../DESIGN.md` for the enforceable package-local rules and anti-rules.');
    expect(readme).toContain('Ordinary downstream consumption should work from local files only:');
    expect(patternsCatalog).toContain('Use this workflow in downstream projects where Figma MCP may not exist:');
    expect(patternsCatalog).toContain('Use local artifacts for ordinary consumption.');
    expect(componentCatalogData.portability?.downstreamConsumptionDoesNotRequireFigmaMcp).toBe(true);
    expect(componentCatalogData.localConsumptionWorkflow?.length).toBeGreaterThan(2);
    expect(componentCatalogData.traceabilityNotes?.[0]).toContain('without Figma MCP');
  });

  it('ships a grouped component inventory catalog for the reusable library surface', () => {
    expect(componentCatalogData.groups.some((group) => group.id === 'portal-shell-navigation' && group.libraryExports.includes('PortalTopNav'))).toBe(true);
    expect(componentCatalogData.groups.some((group) => group.id === 'step-wizard-and-derived-create-resource' && group.sourceNodes.includes('3203:24770'))).toBe(true);
    expect(componentCatalogData.groups.some((group) => group.status === 'implemented-derived')).toBe(true);
    expect(componentCatalogData.groups.some((group) => group.id === 'forms-input-rows-footer' && (group.implementationFiles?.includes('components.tsx') ?? false))).toBe(true);
    expect(componentCatalogData.groups.some((group) => group.id === 'accordion' && group.mcpNodes?.some((node) => node.nodeId === '29739:1810' && node.nodeUrl?.includes('29739-1810')))).toBe(true);
    expect(componentCatalogData.groups.some((group) => group.id === 'accordion' && group.mcpNodes?.some((node) => node.nodeId === '30028:627' && node.status === 'extracted'))).toBe(true);
    expect(componentCatalogData.groups.some((group) => group.id === 'copy-button' && group.libraryExports.includes('CopyButton'))).toBe(true);
    expect(componentCatalogData.groups.some((group) => group.id === 'copilot-composer' && group.mcpNodes?.some((node) => node.nodeId === '32382:38689' && node.status === 'showcase-placeholder'))).toBe(true);
    expect(componentCatalogData.groups.some((group) => group.id === 'agentic-progress' && group.mcpNodes?.some((node) => node.nodeId === '27950:10571' && node.status === 'implemented-rendered'))).toBe(true);
    expect(componentCatalogData.inventoryCoverage?.inventoryComponentCount).toBe(148);
    expect(componentCatalogData.inventoryCoverage?.exactManifestNameNodeAudit?.coveredCount).toBe(105);
    expect(componentCatalogData.inventoryCoverage?.exactManifestNameNodeAudit?.missingCount).toBe(43);
    expect(componentCatalogData.inventoryCoverage?.components?.length).toBe(148);
    expect(componentCatalogData.inventoryCoverage?.coverageTable.reduce((sum, row) => sum + row.count, 0)).toBe(148);
    expect(componentCatalogData.inventoryCoverage?.coverageTable.some((row) => row.status === 'implemented-rendered' && row.count === 26)).toBe(true);
    expect(componentCatalogData.inventoryCoverage?.coverageTable.some((row) => row.status === 'needs-mcp-extraction' && row.count === 45)).toBe(true);
    expect(componentCatalogData.inventoryCoverage?.coverageTable.some((row) => row.status === 'showcase-placeholder' && row.count === 77)).toBe(true);
    expect(componentCatalogData.inventoryCoverage?.components?.some((row) => row.nodeId === '30028:627' && row.coverageStatus === 'implemented-rendered')).toBe(true);
    expect(componentCatalogData.inventoryCoverage?.components?.some((row) => row.nodeId === '32382:40353' && row.coverageStatus === 'implemented-rendered')).toBe(true);
  });

  it('exposes three primary showcase experiences with preview-first browsers', () => {
    render(<AzureFluentShowcaseApp />);

    expect(screen.getByRole('tab', { name: /^Components/i }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('tab', { name: /^Patterns/i })).toBeDefined();
    expect(screen.getByRole('tab', { name: /^Icons/i })).toBeDefined();
    expect(screen.getByText('Component inventory')).toBeDefined();
    expect(screen.getByText(/Browse every cataloged Figma component/i)).toBeDefined();
    expect(screen.getByLabelText('Filter component inventory')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Needs extraction' })).toBeDefined();
    expect(screen.getByLabelText('Live component preview')).toBeDefined();
    expect(screen.getByText('Selection details')).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Accordion' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Accordion' })).toBeDefined();
    expect(screen.queryByText('Traceability citations')).toBeNull();
    expect(screen.queryByText('Local workflow')).toBeNull();
    expect(screen.queryByText('Inventory coverage')).toBeNull();
    expect(screen.queryByText('Icon catalog surface')).toBeNull();

    const inventoryList = screen.getByRole('list', { name: 'Component inventory entries' });

    // Grouped entries surface friendly export titles. The old look-alike child-layer names
    // (".Chat Input [Azure]", ".Reasoning (CoT)", "Copilot Row Swap", "Upload File") are collapsed
    // into their owning components and must no longer appear as standalone sidebar rows.
    expect(within(inventoryList).getByRole('button', { name: 'File upload' })).toBeDefined();
    expect(within(inventoryList).getByRole('button', { name: 'Scrollbar' })).toBeDefined();
    expect(within(inventoryList).queryByRole('button', { name: '.Chat Input [Azure]' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Copilot Row Swap' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Upload File' })).toBeNull();

    // Export-backed groups render live previews addressed by their friendly title.
    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Code snippet' }));
    expect(screen.getByRole('heading', { name: 'Code snippet' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Copy' })).toBeDefined();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Pager' }));
    expect(screen.getByRole('navigation', { name: 'Pagination' })).toBeDefined();
    expect(screen.getByRole('combobox', { name: 'Rows per page' })).toBeDefined();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'CopilotComposer' }));
    expect(screen.getByRole('heading', { name: 'CopilotComposer' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Agents on' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Agents off' })).toBeDefined();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'File upload' }));
    expect(screen.getByRole('heading', { name: 'File upload' })).toBeDefined();
    expect(screen.queryByText('Live preview not available yet')).toBeNull();

    // A grouped entry that still needs MCP extraction shows the placeholder rather than a live preview.
    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Scrollbar' }));
    expect(screen.getByText('Live preview not available yet')).toBeDefined();

    fireEvent.click(screen.getByRole('tab', { name: /^Patterns/i }));

    expect(screen.getByText('Pattern browser')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Create / stepped form blade Live preview' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Delete A Resource Live preview' })).toBeDefined();
    expect(screen.getByText('Rich design context')).toBeDefined();
    expect(screen.getByText(/Local files are authoritative for ordinary usage/i)).toBeDefined();
    expect(screen.getByText(/Dev-mode URL:/i)).toBeDefined();
    expect(screen.getAllByText(/3203:24770/).length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole('tab', { name: /^Icons/i }));

    expect(screen.getByText('Icon browser')).toBeDefined();
    expect(screen.getByText(/Search icon names and aliases/i)).toBeDefined();
    expect(screen.getByLabelText('Filter icons')).toBeDefined();
    expect(screen.getByText('Compute/Virtual Machine')).toBeDefined();
    expect(screen.getByText('Storage/Storage Accounts')).toBeDefined();
    expect(screen.getByText('FluentFallback')).toBeDefined();

    fireEvent.change(screen.getByLabelText('Filter icons'), { target: { value: 'storage' } });
    expect(screen.getByText('Storage/Storage Accounts')).toBeDefined();
    expect(screen.queryByText('Compute/Virtual Machine')).toBeNull();
  });

  it('keeps the showcase inventory aligned with COMPONENTS.md coverage requirements', () => {
    const catalogMarkdown = readFileSync(resolve(process.cwd(), 'src', 'azure-fluent-system', 'catalog', 'COMPONENTS.md'), 'utf8');
    const parsedRows = parseComponentCatalogRows(catalogMarkdown);
    expect(parsedRows).toHaveLength(componentCatalogData.inventoryCoverage?.inventoryComponentCount ?? 0);

    // Every catalog node must appear in exactly one grouped entry — grouping collapses look-alike
    // child layers without ever dropping a component from coverage.
    expect(showcaseComponentInventorySourceNodeIds).toHaveLength(parsedRows.length);
    expect(new Set(showcaseComponentInventorySourceNodeIds).size).toBe(parsedRows.length);
    expect(showcaseComponentInventorySourceNodeIds).toContain('32382:38689');
    expect(showcaseComponentInventorySourceNodeIds).toContain('25412:31783');

    // Grouping must actually reduce the number of sidebar entries below the raw node count.
    expect(showcaseComponentInventoryNodeIds.length).toBeGreaterThan(0);
    expect(showcaseComponentInventoryNodeIds.length).toBeLessThan(parsedRows.length);
    expect(new Set(showcaseComponentInventoryNodeIds).size).toBe(showcaseComponentInventoryNodeIds.length);

    expect(catalogMarkdown).toContain('| `Pager` | `examples/azure-data-grid-filtering.example.tsx` · pager rendered | Yes |');
  });

  it('shows the grouped component inventory in the browser and keeps non-rendered rows discoverable', () => {
    const catalogMarkdown = readFileSync(resolve(process.cwd(), 'src', 'azure-fluent-system', 'catalog', 'COMPONENTS.md'), 'utf8');
    const parsedRows = parseComponentCatalogRows(catalogMarkdown);
    render(<AzureFluentShowcaseApp />);

    const inventoryList = screen.getByRole('list', { name: 'Component inventory entries' });
    const items = within(inventoryList).getAllByRole('listitem');
    expect(items).toHaveLength(showcaseComponentInventoryEntries.length);
    expect(items.length).toBeLessThan(parsedRows.length);

    // A rendered, export-backed component stays reachable under the default filter.
    expect(within(inventoryList).getByRole('button', { name: 'Accordion' })).toBeDefined();

    // Non-rendered rows remain discoverable through the coverage filters. Drive the assertion
    // from the grouped entries so it tracks the real coverage split instead of hard-coded counts.
    const needsExtractionCount = showcaseComponentInventoryEntries.filter(
      (entry) => entry.coverageStatus === 'needs-mcp-extraction',
    ).length;
    fireEvent.click(screen.getByRole('button', { name: 'Needs extraction' }));
    if (needsExtractionCount > 0) {
      expect(within(inventoryList).getAllByRole('listitem')).toHaveLength(needsExtractionCount);
    } else {
      expect(within(inventoryList).getByText('No components matched')).toBeDefined();
    }
    expect(within(inventoryList).queryByRole('button', { name: 'Accordion' })).toBeNull();

    const needsImplementationCount = showcaseComponentInventoryEntries.filter(
      (entry) => entry.coverageStatus === 'needs-implementation',
    ).length;
    fireEvent.click(screen.getByRole('button', { name: 'Needs implementation' }));
    if (needsImplementationCount > 0) {
      expect(within(inventoryList).getAllByRole('listitem')).toHaveLength(needsImplementationCount);
    } else {
      expect(within(inventoryList).getByText('No components matched')).toBeDefined();
    }
  });
});
