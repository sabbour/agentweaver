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
  createIconCloudRegistryFromManifest,
  CreateResourcePattern,
  FormFieldRow,
  InlineCopilot,
  NotificationPane,
  PortalLayout,
  PortalRail,
  PortalTopNav,
  ResourceTagEditor,
  ServiceMenu,
} from '../copilot-fluent-system';
import {
  AzureFluentShowcaseApp,
  publicShowcaseComponentInventoryEntries,
  showcaseComponentInventoryEntries,
  showcaseComponentInventoryNodeIds,
  showcaseComponentInventorySourceNodeIds,
} from '../copilot-fluent-system/showcase/AzureFluentShowcaseApp';
import { componentCatalogData } from '../copilot-fluent-system/showcase/catalogData';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AzureIconDefinition } from '../copilot-fluent-system';
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

function exportedRuntimeNames(source: string) {
  return Array.from(source.matchAll(/^export\s+(?:function|const|class)\s+(\w+)/gm), (match) => match[1]);
}

function foundationExportNames(source: string) {
  const exportStart = source.indexOf('export {');
  const exportEnd = source.indexOf("} from '@fluentui/react-components'");
  return source
    .slice(exportStart, exportEnd)
    .split(/\r?\n/)
    .map((line) => line.trim().replace(/,$/, ''))
    .filter((line) => line && !line.startsWith('//') && !line.startsWith('export'));
}

afterEach(() => cleanup());

describe('copilot-fluent-system hardened components', () => {
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
          steps={[{ id: 'deploy', title: 'Deploy change', body: 'Waiting for approval', needsInput: true, riskText: 'This updates sample resources.' }]}
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
        <CopyButton value="az aks show --name aks-cluster-sample" label="Click here to copy" onCopy={onCopy} />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Click here to copy' }));
    expect(onCopy).toHaveBeenCalledWith('az aks show --name aks-cluster-sample');
    expect(await screen.findByRole('button', { name: 'Copied' })).toBeDefined();
    expect(screen.getByRole('status').textContent).toBe('Copied');
  });

  it('renders CodeSnippet with line numbers and fold markers', () => {
    render(
      <Wrapper>
        <CodeSnippet
          title="ARM template"
          lines={[
            { lineNumber: 1, text: '{', foldState: 'expanded' },
            { lineNumber: 2, tokens: [{ text: '"name"', tone: 'key' }, { text: ': ', tone: 'operator' }, { text: '"sample"' }] },
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
                  <input id="test-subscription" value="Sample subscription" readOnly />
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
              persona={{ name: 'Signed-in user', secondaryText: 'Organization directory' }}
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



  it('exposes live Portal capture density tokens and flyout notification anatomy', () => {
    const systemRoot = resolve(process.cwd(), 'src', 'copilot-fluent-system');
    const tokens = readFileSync(resolve(systemRoot, 'tokens.css'), 'utf8');
    expect(tokens).toContain('--azf-portal-font-size: 14px');
    expect(tokens).toContain('--azf-portal-topbar-height: 40px');
    expect(tokens).toContain('--azf-portal-control-height: 32px');
    expect(tokens).toContain('--azf-density-row-height: var(--azf-portal-row-height)');
    expect(tokens).toContain('--azf-portal-brand: rgb(36 36 36)');

    render(
      <Wrapper>
        <NotificationPane
          surface="flyout"
          title="Activity"
          items={[{ id: 'n1', title: 'Policy review needed', body: 'Review the affected resource before continuing.', tone: 'warning', unread: true }]}
        />
      </Wrapper>,
    );

    expect(screen.getByLabelText('Activity').getAttribute('data-surface')).toBe('flyout');
    expect(screen.getByText('Policy review needed')).toBeDefined();
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

  it('documents the pattern guidance in DESIGN.md', () => {
    const design = readFileSync(resolve(process.cwd(), '..', '..', 'DESIGN.md'), 'utf8');
    expect(design).toContain('Coverage is not fidelity.');
    expect(design).toContain('Resource Type node `4417:3962` is representative of one pattern family, not the whole showcase scope.');
    expect(design).toContain('pattern workbench/gallery');
  });

  it('documents a local-first downstream workflow without requiring MCP', () => {
    const portableDesign = readFileSync(resolve(process.cwd(), 'src', 'copilot-fluent-system', 'DESIGN.md'), 'utf8');
    const libraryReadme = readFileSync(resolve(process.cwd(), 'src', 'copilot-fluent-system', 'README.md'), 'utf8');
    const readme = readFileSync(resolve(process.cwd(), 'src', 'copilot-fluent-system', 'showcase', 'README.md'), 'utf8');
    const patternsCatalog = readFileSync(resolve(process.cwd(), 'src', 'copilot-fluent-system', 'catalog', 'PATTERNS.md'), 'utf8');
    const webPackage = JSON.parse(readFileSync(resolve(process.cwd(), 'package.json'), 'utf8')) as { scripts?: Record<string, string> };
    const packagePackage = JSON.parse(readFileSync(resolve(process.cwd(), 'src', 'copilot-fluent-system', 'package.json'), 'utf8')) as { scripts?: Record<string, string> };
    const rootGitignore = readFileSync(resolve(process.cwd(), '..', '..', '.gitignore'), 'utf8');
    const removedScriptName = ['showcase', 'validate-readiness'].join(':');
    const removedValidatorFile = ['validate-showcase', 'readiness.mjs'].join('-');
    const removedSidecarDir = ['.impeccable'].join('');
    const removedSidecarFile = ['design', 'json'].join('.');
    const removedSidecarPath = ['apps/web/src/copilot-fluent-system', removedSidecarDir, removedSidecarFile].join('/');
    expect(portableDesign).toContain('# Copilot Fluent System — usage contract');
    expect(portableDesign).toContain('The Barrel-Only Rule');
    expect(portableDesign).toContain('The No-Blue Rule');
    expect(portableDesign).toContain('Traceability only; downstream');
    expect(portableDesign).not.toContain('Agentweaver');
    expect(libraryReadme).toContain('Local-first downstream workflow');
    expect(libraryReadme).toContain('Sanitized Portal capture note');
    expect(libraryReadme).toContain('Use `DESIGN.md` as the portable design-system addendum');
    expect(readme).toContain('Local-first workflow');
    expect(readme).toContain('Read `../DESIGN.md` for the enforceable package-local rules and anti-rules.');
    expect(readme).toContain('Ordinary downstream consumption should work from local files only:');
    expect(readme).toContain('focused Azure Fluent tests');
    expect(readme).not.toContain(removedScriptName);
    expect(patternsCatalog).toContain('Use this workflow in downstream projects where Figma MCP may not exist:');
    expect(patternsCatalog).toContain('Use local artifacts for ordinary consumption.');
    expect(Object.keys(webPackage.scripts ?? {})).not.toContain(removedScriptName);
    expect(Object.keys(packagePackage.scripts ?? {})).not.toContain(removedScriptName);
    expect(existsSync(resolve(process.cwd(), 'src', 'copilot-fluent-system', 'showcase', removedValidatorFile))).toBe(false);
    expect(existsSync(resolve(process.cwd(), 'src', 'copilot-fluent-system', removedSidecarDir, removedSidecarFile))).toBe(false);
    expect(rootGitignore).not.toContain(removedSidecarPath);
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
    expect(componentCatalogData.inventoryCoverage?.exactManifestNameNodeAudit?.coveredCount).toBe(104);
    expect(componentCatalogData.inventoryCoverage?.exactManifestNameNodeAudit?.missingCount).toBe(44);
    expect(componentCatalogData.inventoryCoverage?.components?.length).toBe(148);
    expect(componentCatalogData.inventoryCoverage?.coverageTable.reduce((sum, row) => sum + row.count, 0)).toBe(148);
    expect(componentCatalogData.inventoryCoverage?.coverageTable.some((row) => row.status === 'implemented-rendered' && row.count === 25)).toBe(true);
    expect(componentCatalogData.inventoryCoverage?.coverageTable.some((row) => row.status === 'needs-mcp-extraction' && row.count === 45)).toBe(true);
    expect(componentCatalogData.inventoryCoverage?.coverageTable.some((row) => row.status === 'showcase-placeholder' && row.count === 78)).toBe(true);
    expect(componentCatalogData.inventoryCoverage?.components?.some((row) => row.nodeId === '30028:627' && row.coverageStatus === 'implemented-rendered')).toBe(true);
    expect(componentCatalogData.inventoryCoverage?.components?.some((row) => row.nodeId === '32382:40353' && row.coverageStatus === 'implemented-rendered')).toBe(true);
  });

  it('keeps public Azure Fluent components and approved Fluent foundations represented in the showcase browser', () => {
    const systemRoot = resolve(process.cwd(), 'src', 'copilot-fluent-system');
    const showcaseSource = readFileSync(resolve(systemRoot, 'showcase', 'AzureFluentShowcaseApp.tsx'), 'utf8');
    const representedNames = new Set([
      ...Array.from(showcaseSource.matchAll(/exportName:\s*'([^']+)'/g), (match) => match[1]),
      ...Array.from(showcaseSource.matchAll(/codeName:\s*'([^']+)'/g), (match) => match[1]),
    ]);

    const helperOnly = new Set(['useAzureIconRegistry', 'createIconCloudRegistry', 'createIconCloudRegistryFromManifest', 'useToastController']);
    const nonVisualFoundationUtilities = new Set(['makeStyles', 'mergeClasses', 'shorthands', 'tokens', 'useId']);
    const foundationSource = readFileSync(resolve(systemRoot, 'foundations.tsx'), 'utf8');
    const foundationNames = foundationExportNames(foundationSource);
    expect(foundationNames.filter((name) => nonVisualFoundationUtilities.has(name))).toEqual(Array.from(nonVisualFoundationUtilities));
    expect(foundationSource).toContain('Non-visual foundation utilities');

    const publicRuntimeComponents = [
      ...exportedRuntimeNames(readFileSync(resolve(systemRoot, 'components.tsx'), 'utf8')),
      ...exportedRuntimeNames(readFileSync(resolve(systemRoot, 'patterns.tsx'), 'utf8')),
      ...exportedRuntimeNames(readFileSync(resolve(systemRoot, 'provider.tsx'), 'utf8')),
      ...exportedRuntimeNames(readFileSync(resolve(systemRoot, 'icons.tsx'), 'utf8')),
      ...foundationNames,
    ].filter((name) => !helperOnly.has(name) && !nonVisualFoundationUtilities.has(name));

    expect(publicRuntimeComponents.filter((name) => !representedNames.has(name))).toEqual([]);
    expect(readFileSync(resolve(systemRoot, 'icons.tsx'), 'utf8')).toContain('non-visual helper APIs');
    expect(readFileSync(resolve(systemRoot, 'foundations.tsx'), 'utf8')).toContain('approved building blocks');
  });

  it('blocks screenshot and arbitrary media artifacts from public showcase previews', () => {
    const systemRoot = resolve(process.cwd(), 'src', 'copilot-fluent-system');
    const showcaseSource = readFileSync(resolve(systemRoot, 'showcase', 'AzureFluentShowcaseApp.tsx'), 'utf8');
    const examplesRoot = resolve(systemRoot, 'examples');
    const exampleSources = readdirSync(examplesRoot)
      .filter((fileName) => fileName.endsWith('.example.tsx'))
      .map((fileName) => readFileSync(resolve(examplesRoot, fileName), 'utf8'))
      .join('\n');

    const publicPreviewSource = `${showcaseSource}\n${exampleSources}`;
    expect(publicPreviewSource).not.toMatch(/_largeimage|largeimage|devtools|test-pattern|wallpaper|scenery|browser screenshot|network screenshot/i);
    expect(publicPreviewSource).not.toMatch(/Slide\s+\d+|contact[- ]sheet|presentation screenshot|slide deck|numbered slides/i);
    expect(publicPreviewSource).not.toMatch(/Ahmed|Sabbour|Contoso|stcontoso|aks-prod|prod-eastus|vm-prod|rg-prod|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}/i);
    expect(publicPreviewSource).not.toMatch(/\.(png|jpe?g|webp|gif)\b/i);
    expect(exampleSources).not.toMatch(/<img|backgroundImage|background-image/i);

    const imgMatches = Array.from(showcaseSource.matchAll(/<img\b/g));
    expect(imgMatches).toHaveLength(1);
    expect(showcaseSource.slice(Math.max(0, imgMatches[0].index - 400), imgMatches[0].index + 400)).toContain('azf-showcase-icon-grid__glyph');
  });

  it('exposes primary showcase experiences with preview-first browsers', () => {
    const showcaseSource = readFileSync(resolve(process.cwd(), 'src', 'copilot-fluent-system', 'showcase', 'AzureFluentShowcaseApp.tsx'), 'utf8');
    const { container } = render(<AzureFluentShowcaseApp />);

    expect(screen.getByRole('tab', { name: /^Components/i }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('tab', { name: /^Patterns/i })).toBeDefined();
    expect(screen.queryByRole('tab', { name: /^Usage examples/i })).toBeNull();
    expect(screen.getByRole('tab', { name: /^Icons/i })).toBeDefined();
    expect(screen.getByRole('region', { name: 'Portal-style showcase highlights' })).toBeDefined();
    expect(screen.getAllByText('AKS resource list').length).toBeGreaterThan(0);
    expect(screen.getByText(/Azure Kubernetes Service list with resource group, subscription, type, and status columns/i)).toBeDefined();
    expect(screen.getAllByText('Global search').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Settings flyout').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Activity flyout').length).toBeGreaterThan(0);
    expect(screen.getByText('Component browser')).toBeDefined();
    expect(screen.queryByText(/Browse Azure Fluent components/i)).toBeNull();
    expect(screen.queryByText(/Showing \d+ entries/i)).toBeNull();
    expect(screen.getByLabelText('Filter components')).toBeDefined();
    expect(screen.queryByRole('button', { name: 'All' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Live preview' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Related preview' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Design review' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Planned preview' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Review planned' })).toBeNull();
    expect(showcaseSource).not.toMatch(/Live preview|Related preview|Design review|Planned preview|Review planned/);
    expect(screen.getByLabelText('Component example')).toBeDefined();
    expect(screen.getByText('Reference details')).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Accordion' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Accordion' })).toBeDefined();
    expect(screen.queryByText('Traceability citations')).toBeNull();
    expect(screen.queryByText('Local workflow')).toBeNull();
    expect(screen.queryByText('Inventory coverage')).toBeNull();
    expect(screen.queryByText('Icon catalog surface')).toBeNull();

    const inventoryList = screen.getByRole('list', { name: 'Component entries' });
    const inventoryButtonNames = within(inventoryList).getAllByRole('button').map((button) => button.getAttribute('aria-label') ?? '');
    const copilotEntryNames = [
      'Copilot composer',
      'Copilot response',
      'Inline Copilot',
      'Copilot prompt ribbon',
      'Artifact pill',
      'Agentic progress',
      'Reasoning panel',
      'Copilot workspace',
    ];
    const groupedCopilotEntryNames = inventoryButtonNames.filter((name) => copilotEntryNames.includes(name));
    const copilotEntryIndexes = groupedCopilotEntryNames.map((name) => inventoryButtonNames.indexOf(name));
    expect(within(inventoryList).getByText('Copilot')).toBeDefined();
    expect(groupedCopilotEntryNames).toContain('Copilot composer');
    expect(groupedCopilotEntryNames).toContain('Copilot response');
    expect(groupedCopilotEntryNames).toContain('Copilot workspace');
    expect(groupedCopilotEntryNames.length).toBeGreaterThan(3);
    expect(Math.max(...copilotEntryIndexes) - Math.min(...copilotEntryIndexes) + 1).toBe(groupedCopilotEntryNames.length);
    expect(inventoryButtonNames.slice(copilotEntryIndexes[0], copilotEntryIndexes[0] + groupedCopilotEntryNames.length)).toEqual(groupedCopilotEntryNames);

    // Grouped entries surface friendly export titles. The old look-alike child-layer names
    // (".Chat Input [Azure]", ".Reasoning (CoT)", "Copilot Row Swap", "Upload File") are collapsed
    // into their owning components and must no longer appear as standalone sidebar rows.
    expect(within(inventoryList).getByRole('button', { name: 'File upload' })).toBeDefined();
    expect(within(inventoryList).getByRole('button', { name: 'Avatar' })).toBeDefined();
    expect(within(inventoryList).getByRole('button', { name: 'Breadcrumb' })).toBeDefined();
    expect(within(inventoryList).getByRole('button', { name: 'Card' })).toBeDefined();
    expect(within(inventoryList).getByRole('button', { name: 'Carousel' })).toBeDefined();
    expect(within(inventoryList).getByRole('button', { name: 'Dialog' })).toBeDefined();
    expect(within(inventoryList).getByRole('button', { name: 'Dropdown' })).toBeDefined();
    expect(within(inventoryList).getByRole('button', { name: 'Drawer' })).toBeDefined();
    expect(within(inventoryList).getByRole('button', { name: 'Menu' })).toBeDefined();
    expect(within(inventoryList).getByRole('button', { name: 'Message bar' })).toBeDefined();
    expect(within(inventoryList).getByRole('button', { name: 'Tag picker' })).toBeDefined();
    expect(within(inventoryList).getByRole('button', { name: 'Toast' })).toBeDefined();
    expect(within(inventoryList).getAllByRole('button', { name: 'Toolbar' }).length).toBeGreaterThan(0);
    expect(within(inventoryList).queryByRole('button', { name: '.Chat Input [Azure]' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Copilot Row Swap' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Upload File' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Scrollbar' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: '.Horizontal Swap' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: '.Popover Content (Dark)' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Breadcrumb button' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Breadcrumb divider' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Breadcrumb item' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Dialog surface' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Dialog body' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Dialog title' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Dialog actions' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Dialog content' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Dialog trigger' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Card header' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Card footer' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Card preview' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Carousel card' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Drawer header' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Drawer body' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Overlay drawer' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Inline drawer' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Menu trigger' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Menu list' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Menu item' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Menu popover' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Message bar body' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Message bar actions' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Message bar title' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Option' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Option group' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Tag picker control' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Toast title' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Toolbar button' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: 'Toolbar divider' })).toBeNull();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Carousel' }));
    expect(screen.getByRole('heading', { name: 'Carousel' })).toBeDefined();
    expect(container.querySelector('.azf-showcase-component-preview-panel img')).toBeNull();
    expect(screen.queryByText(/Slide\s+\d+|0(?:3[0-9]|4[0-9])|contact[- ]sheet|presentation/i)).toBeNull();

    // Export-backed groups render examples addressed by their friendly title.
    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Code snippet' }));
    expect(screen.getByRole('heading', { name: 'Code snippet' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Copy' })).toBeDefined();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Pager' }));
    expect(screen.getByRole('navigation', { name: 'Pagination' })).toBeDefined();
    expect(screen.getByRole('combobox', { name: 'Rows per page' })).toBeDefined();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Divider' }));
    expect(screen.getByRole('heading', { name: 'Divider' })).toBeDefined();
    expect(screen.getByLabelText('Horizontal divider example')).toBeDefined();
    expect(screen.getByText('Activity summary')).toBeDefined();
    expect(screen.getByText('Recommended action')).toBeDefined();
    expect(screen.getByText('Review checkpoints')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Open logs' })).toBeDefined();
    expect(screen.queryByText('Inbound rules')).toBeNull();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Essentials' }));
    expect(screen.getByRole('heading', { name: 'Essentials' })).toBeDefined();
    expect(screen.getByText('Resource group')).toBeDefined();
    expect(screen.getByText('sample-platform-rg')).toBeDefined();
    expect(screen.getAllByText(/Essentials renders the collapsible resource-summary/i).length).toBeGreaterThan(0);
    expect(screen.queryByText(/EssentialsGrid renders/i)).toBeNull();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Search filter pills' }));
    expect(screen.getByRole('heading', { name: 'Search filter pills' })).toBeDefined();
    expect(screen.getByLabelText('Search filter pills example')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Compute' })).toBeDefined();
    expect(screen.getAllByText(/Use compact category pills to refine search results/i).length).toBeGreaterThan(0);
    expect(screen.queryByText(/FilterPills renders/i)).toBeNull();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Service menu' }));
    expect(screen.getByRole('heading', { name: 'Service menu' })).toBeDefined();
    expect(screen.getByRole('navigation', { name: 'Service navigation' })).toBeDefined();
    expect(screen.getByLabelText('Selected service menu item')).toBeDefined();
    expect(screen.getAllByText('Private access').length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'Open private endpoint settings' })).toBeDefined();
    expect(container.querySelector('.azf-showcase-component-preview-panel img')).toBeNull();
    expect(screen.queryByText(/game|media|wallpaper|scenery|desktop|diagram|test-pattern/i)).toBeNull();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Notification pane' }));
    expect(screen.getByRole('heading', { name: 'Notification pane' })).toBeDefined();
    expect(screen.getByRole('complementary', { name: 'Activity updates' })).toBeDefined();
    expect(screen.getByLabelText('Selected notification detail')).toBeDefined();
    expect(screen.getAllByText('Firewall validation blocked').length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'Review policy' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'View all activity' })).toBeDefined();
    expect(container.querySelector('.azf-showcase-component-preview-panel img')).toBeNull();
    expect(screen.queryByText(/test-pattern|scenery|desktop|diagram/i)).toBeNull();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Portal shell / top nav / rail' }));
    expect(screen.getByRole('heading', { name: 'Portal shell / top nav / rail' })).toBeDefined();
    expect(screen.getByText('Microsoft Azure')).toBeDefined();
    expect(screen.getAllByText('aks-cluster-alpha').length).toBeGreaterThan(0);
    expect(screen.getByRole('table', { name: 'AKS resources' })).toBeDefined();
    expect(screen.getAllByText('Resource group').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Subscription').length).toBeGreaterThan(0);
    expect(screen.getAllByLabelText('Global search flyout').length).toBeGreaterThan(0);
    expect(screen.getAllByLabelText('Settings flyout').length).toBeGreaterThan(0);
    expect(screen.getByRole('complementary', { name: 'Activity' })).toBeDefined();
    expect(container.querySelector('.azf-showcase-component-preview-panel img')).toBeNull();
    expect(screen.queryByText(/test-pattern|scenery|desktop|diagram|wallpaper|screenshot/i)).toBeNull();
    expect(screen.queryByText(/Related shell details|Local shell preview|Portal navigation details/i)).toBeNull();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Copilot composer' }));
    expect(screen.getByRole('heading', { name: 'Copilot composer' })).toBeDefined();
    expect(screen.queryByRole('heading', { name: 'CopilotComposer' })).toBeNull();
    expect(screen.getByLabelText('Ready composer')).toBeDefined();
    expect(screen.getByLabelText('Running composer')).toBeDefined();
    expect(screen.queryByText('Live preview')).toBeNull();
    expect(screen.queryByText('Running / agents off')).toBeNull();
    expect(screen.getByRole('button', { name: 'Agents on' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Agents off' })).toBeDefined();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Copilot response' }));
    expect(screen.getByRole('heading', { name: 'Copilot response' })).toBeDefined();
    expect(screen.queryByRole('heading', { name: 'CopilotResponse' })).toBeNull();
    expect(screen.getByLabelText('Resolved Copilot response')).toBeDefined();
    expect(screen.getByLabelText('Loading response')).toBeDefined();
    expect(screen.getByText('AKSControlPlane')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Open incident' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Helpful' })).toBeDefined();
    expect(screen.queryByText('Resolved answer')).toBeNull();
    expect(screen.queryByText('Loading and request count')).toBeNull();
    expect(screen.queryByText(/deploymentTemplate\.json/i)).toBeNull();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'Copilot workspace' }));
    const copilotWorkspacePreview = screen.getByLabelText('Component example');
    expect(container.querySelector('.azf-copilot-workspace-demo')).toBeDefined();
    expect(screen.getAllByRole('heading', { name: 'Copilot workspace' })).toHaveLength(1);
    expect(within(copilotWorkspacePreview).queryByRole('heading', { name: 'Copilot workspace' })).toBeNull();
    expect(within(copilotWorkspacePreview).getByLabelText('Compact Copilot workspace preview')).toBeDefined();
    expect(screen.queryByText(/Copilot workspace composes/i)).toBeNull();
    expect(screen.getAllByText('A compact Copilot task workspace with navigation, response, actions, and composer.').length).toBeGreaterThan(0);
    expect(within(copilotWorkspacePreview).getByText('Tasks')).toBeDefined();
    expect(within(copilotWorkspacePreview).getByText('Chat')).toBeDefined();
    expect(within(copilotWorkspacePreview).queryByText('Workspace chat')).toBeNull();
    expect(screen.getByText('AKSControlPlane')).toBeDefined();
    expect(screen.getByText('rollout-failures.kql')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Open workbook' })).toBeDefined();
    expect(screen.queryByText(/deploymentTemplate\.json/i)).toBeNull();

    fireEvent.click(within(inventoryList).getByRole('button', { name: 'File upload' }));
    expect(screen.getByRole('heading', { name: 'File upload' })).toBeDefined();
    expect(screen.queryByText('Live preview not available yet')).toBeNull();

    expect(screen.queryByText('Live preview not available yet')).toBeNull();

    fireEvent.click(screen.getByRole('tab', { name: /^Patterns/i }));

    expect(screen.getByText('Pattern browser')).toBeDefined();
    expect(container.querySelector('.azf-showcase-app__main img')).toBeNull();
    expect(screen.queryByText(/Slide\s+\d+|0(?:3[0-9]|4[0-9])|contact[- ]sheet|presentation/i)).toBeNull();
    expect(screen.getAllByRole('button', { name: /AKS resource list/i }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole('button', { name: /Global search/i }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole('button', { name: /Settings flyout/i }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole('button', { name: /Activity flyout/i }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole('heading', { name: 'AKS resource list' }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole('table').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Kubernetes service').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Resource group').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Subscription').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Type').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Status').length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'Create resource flow' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Delete resource' })).toBeDefined();
    expect(screen.queryByText('Live preview')).toBeNull();
    expect(screen.getByText('When to use')).toBeDefined();
    expect(screen.getByText(/Use for Azure Kubernetes Service browse pages/i)).toBeDefined();
    fireEvent.click(screen.getAllByRole('button', { name: /Global search/i })[0]);
    expect(screen.getByRole('heading', { name: 'Global search' })).toBeDefined();
    expect(screen.getAllByLabelText('Global search flyout').length).toBeGreaterThan(0);
    expect(screen.getByText('Azure Kubernetes Service')).toBeDefined();
    fireEvent.click(screen.getAllByRole('button', { name: /Settings flyout/i })[0]);
    expect(screen.getByRole('heading', { name: 'Settings flyout' })).toBeDefined();
    expect(screen.getAllByLabelText('Settings flyout').length).toBeGreaterThan(0);
    expect(screen.getByText('Portal settings')).toBeDefined();
    fireEvent.click(screen.getAllByRole('button', { name: /Activity flyout/i })[0]);
    expect(screen.getByRole('heading', { name: 'Activity flyout' })).toBeDefined();
    expect(screen.getByRole('complementary', { name: 'Portal activity' })).toBeDefined();
    expect(screen.getByText('Composed scenarios')).toBeDefined();
    expect(screen.getAllByText('Copilot triage panel').length).toBeGreaterThan(0);
    expect(screen.queryByText(/Dev-mode URL:/i)).toBeNull();
    expect(screen.queryByRole('tab', { name: /^Usage examples/i })).toBeNull();
    expect(screen.queryByText(/Live examples are rendered here as product scenarios/i)).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Provider and resource layout' })).toBeNull();
    expect(screen.queryByText(/examples\//i)).toBeNull();

    fireEvent.click(screen.getByRole('tab', { name: /^Icons/i }));

    expect(screen.getByText('Icon browser')).toBeDefined();
    expect(screen.getByText(/Search icon names and aliases/i)).toBeDefined();
    expect(screen.getByLabelText('Filter icons')).toBeDefined();
    expect(screen.getByText('Compute/Virtual Machine')).toBeDefined();
    expect(screen.getByText('Storage/Storage Accounts')).toBeDefined();
    expect(screen.queryByText(new RegExp(`Fluent${'Fallback'}`))).toBeNull();

    fireEvent.change(screen.getByLabelText('Filter icons'), { target: { value: 'storage' } });
    expect(screen.getByText('Storage/Storage Accounts')).toBeDefined();
    expect(screen.queryByText('Compute/Virtual Machine')).toBeNull();
  });

  it('keeps the showcase inventory aligned with COMPONENTS.md coverage requirements', () => {
    const catalogMarkdown = readFileSync(resolve(process.cwd(), 'src', 'copilot-fluent-system', 'catalog', 'COMPONENTS.md'), 'utf8');
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

  it('shows only rendered public component inventory in the browser', () => {
    const catalogMarkdown = readFileSync(resolve(process.cwd(), 'src', 'copilot-fluent-system', 'catalog', 'COMPONENTS.md'), 'utf8');
    const parsedRows = parseComponentCatalogRows(catalogMarkdown);
    render(<AzureFluentShowcaseApp />);

    const inventoryList = screen.getByRole('list', { name: 'Component entries' });
    const items = within(inventoryList).getAllByRole('listitem');
    expect(items).toHaveLength(publicShowcaseComponentInventoryEntries.length);
    expect(items.length).toBeLessThan(parsedRows.length);
    expect(publicShowcaseComponentInventoryEntries.length).toBeLessThan(
      showcaseComponentInventoryEntries.filter((entry) => entry.coverageStatus === 'implemented-rendered' && Boolean(entry.previewEntry)).length,
    );
    expect(publicShowcaseComponentInventoryEntries.every((entry) => entry.previewEntry)).toBe(true);

    // Rendered, export-backed components stay reachable without exposing status-only entries.
    expect(within(inventoryList).getByRole('button', { name: 'Accordion' })).toBeDefined();
    expect(within(inventoryList).queryByRole('button', { name: 'Scrollbar' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: '.Horizontal Swap' })).toBeNull();
    expect(within(inventoryList).queryByRole('button', { name: '.Popover Content (Dark)' })).toBeNull();
    expect(publicShowcaseComponentInventoryEntries.map((entry) => entry.title)).toEqual(
      expect.not.arrayContaining([
        'Breadcrumb button',
        'Breadcrumb divider',
        'Breadcrumb item',
        'Card header',
        'Card footer',
        'Card preview',
        'Carousel card',
        'Dialog actions',
        'Dialog surface',
        'Dialog body',
        'Dialog content',
        'Dialog title',
        'Dialog trigger',
        'Drawer body',
        'Drawer header',
        'Inline drawer',
        'Menu trigger',
        'Menu list',
        'Menu item',
        'Menu popover',
        'Message bar actions',
        'Message bar body',
        'Message bar title',
        'Option',
        'Option group',
        'Overlay drawer',
        'Tag picker control',
        'Toast title',
        'Toolbar button',
        'Toolbar divider',
      ]),
    );
    expect(screen.queryByText('Design review')).toBeNull();
    expect(screen.queryByText('Planned preview')).toBeNull();
  });
});
