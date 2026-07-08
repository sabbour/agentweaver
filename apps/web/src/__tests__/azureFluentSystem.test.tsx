import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { AzureFluentProvider, AgenticProgress, AzureDataGrid, BladeHeader, CopilotComposer, CreateResourcePattern, ResourceTagEditor, ServiceMenu, createIconCloudRegistryFromManifest } from '../azure-fluent-system';
import type { AzureIconDefinition } from '../azure-fluent-system';
import type { ReactNode } from 'react';

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider>{children}</AzureFluentProvider>;
}

afterEach(() => cleanup());

describe('azure-fluent-system hardened components', () => {
  it('renders BladeHeader with real actions and no fabricated defaults', () => {
    const onSave = vi.fn();
    render(<Wrapper><BladeHeader title="Virtual machines" subtitle="Compute" actions={[{ id: 'save', label: 'Save', appearance: 'primary', onClick: onSave }]} /></Wrapper>);
    expect(screen.getByRole('heading', { name: 'Virtual machines', level: 1 })).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Pin' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(onSave).toHaveBeenCalledTimes(1);
  });

  it('filters and selects ServiceMenu items with selected rail semantics', () => {
    const onSelect = vi.fn();
    render(<Wrapper><ServiceMenu selectedId="activity" onSelect={onSelect} groups={[{ id: 'manage', label: 'Manage', items: [{ id: 'overview', label: 'Overview' }, { id: 'activity', label: 'Activity log' }] }]} /></Wrapper>);
    expect(screen.getByRole('button', { name: 'Activity log' }).getAttribute('aria-current')).toBe('page');
    fireEvent.change(screen.getByLabelText('Filter navigation'), { target: { value: 'over' } });
    expect(screen.queryByRole('button', { name: 'Activity log' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Overview' }));
    expect(onSelect).toHaveBeenCalledWith('overview');
  });

  it('sorts and activates AzureDataGrid rows', () => {
    const onRowClick = vi.fn();
    render(<Wrapper><AzureDataGrid getRowId={(item) => item.id} onRowClick={onRowClick} items={[{ id: 'b', name: 'Beta' }, { id: 'a', name: 'Alpha' }]} columns={[{ columnId: 'name', header: 'Name', sortable: true, sortValue: (item) => item.name, renderCell: (item) => item.name }]} /></Wrapper>);
    fireEvent.click(screen.getByRole('button', { name: 'Name' }));
    const cells = screen.getAllByRole('cell');
    expect(cells[0].textContent).toBe('Alpha');
    fireEvent.click(screen.getByText('Alpha'));
    expect(onRowClick).toHaveBeenCalledWith({ id: 'a', name: 'Alpha' });
  });

  it('edits ResourceTagEditor rows and adds rows', () => {
    const onRowChange = vi.fn();
    const onAddRow = vi.fn();
    render(<Wrapper><ResourceTagEditor rows={[{ id: 'r1', name: 'env', value: 'prod', resourceId: 'vm1' }]} resources={[{ id: 'vm1', label: 'VM one' }]} onRowChange={onRowChange} onAddRow={onAddRow} /></Wrapper>);
    fireEvent.change(screen.getByLabelText('Tag value for row 1'), { target: { value: 'test' } });
    expect(onRowChange).toHaveBeenCalledWith('r1', { value: 'test' });
    fireEvent.click(screen.getByRole('button', { name: 'Add tag' }));
    expect(onAddRow).toHaveBeenCalledTimes(1);
  });

  it('sends and stops CopilotComposer with accessible controls', () => {
    const onSend = vi.fn();
    const onStop = vi.fn();
    const { rerender } = render(<Wrapper><CopilotComposer value="Summarize" onChange={() => undefined} onSend={onSend} /></Wrapper>);
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    expect(onSend).toHaveBeenCalledTimes(1);
    rerender(<Wrapper><CopilotComposer value="Summarize" onChange={() => undefined} onSend={onSend} isRunning onStop={onStop} /></Wrapper>);
    fireEvent.click(screen.getByRole('button', { name: 'Stop response' }));
    expect(onStop).toHaveBeenCalledTimes(1);
  });

  it('shows AgenticProgress approval actions without exposing reasoning', () => {
    const onApprove = vi.fn();
    render(<Wrapper><AgenticProgress defaultOpenItems={['deploy']} onApprove={onApprove} steps={[{ id: 'deploy', title: 'Deploy change', body: 'Waiting for approval', needsInput: true, riskText: 'This updates production.' }]} /></Wrapper>);
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    expect(onApprove).toHaveBeenCalledWith('deploy');
    expect(screen.queryByText(/chain of thought/i)).toBeNull();
  });
  it('provides a derived CreateResourcePattern with validation and fixed footer actions', () => {
    const onCreate = vi.fn();
    const onStepSelect = vi.fn();
    render(<Wrapper><CreateResourcePattern title="Create storage account" currentStepId="basics" onStepSelect={onStepSelect} validationSummary="Fix required fields" steps={[{ id: 'basics', label: 'Basics', content: <div>Basics content</div> }, { id: 'review', label: 'Review', content: <div>Review content</div> }]} primaryAction={{ id: 'create', label: 'Create', appearance: 'primary', onClick: onCreate }} secondaryAction={{ id: 'previous', label: 'Previous' }} /></Wrapper>);
    expect(screen.getByRole('heading', { name: 'Create storage account', level: 1 })).toBeDefined();
    expect(screen.getByText('Fix required fields')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Review' }));
    expect(onStepSelect).toHaveBeenCalledWith('review');
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));
    expect(onCreate).toHaveBeenCalledTimes(1);
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
});
