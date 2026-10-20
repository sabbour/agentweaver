import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { EffectiveProviderSelection, ProviderCandidate } from './contracts';
import { PlatformComponentsTable } from './PlatformComponentsTable';

function candidate(providerId: string, overrides: Partial<ProviderCandidate> = {}): ProviderCandidate {
  return {
    seam: 'sandboxRuntime',
    providerId,
    adapterVersion: '1.0.0',
    optionsSchemaVersion: 1,
    optionsRevision: 'rev-1',
    hosting: 'managed',
    advertisedCapabilities: [],
    requiredCapabilities: [],
    ...overrides,
  };
}

const components: EffectiveProviderSelection[] = [
  {
    seam: 'sandboxRuntime',
    cardinality: 'ordered',
    candidates: [
      candidate('first-provider', { layer: 'platform', advertisedCapabilities: ['network', 'gpu'] }),
      candidate('second-provider', { adapterVersion: '2.1.0', hosting: 'self-hosted' }),
    ],
  },
  { seam: 'model_gateway', cardinality: 'single', meterSource: 'tokens', candidates: [] },
  { seam: 'eventStore', cardinality: 'custom-mode', candidates: [candidate('store')] },
];

describe('PlatformComponentsTable', () => {
  it('says so when the run lists no components', () => {
    render(<PlatformComponentsTable components={[]} />);
    expect(screen.getByText('This run lists no platform components.')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('uses the default caption and column headers', () => {
    render(<PlatformComponentsTable components={components} />);
    const table = screen.getByRole('table', { name: 'Allowed implementations for this run' });
    expect(within(table).getAllByRole('columnheader').map((cell) => cell.textContent))
      .toEqual(['Platform component', 'Choice', 'Implementations, in order']);
  });

  it('accepts a custom caption', () => {
    render(<PlatformComponentsTable components={components} caption="Platform choices" />);
    expect(screen.getByRole('table', { name: 'Platform choices' })).toBeTruthy();
  });

  it('renders one row per component in the given order with readable labels', () => {
    render(<PlatformComponentsTable components={components} />);
    const headers = screen.getAllByRole('rowheader').map((cell) => cell.textContent);
    expect(headers).toEqual(['Sandbox runtime', 'Model gateway · tokens', 'Event store']);
    const rows = screen.getAllByRole('row').slice(1);
    expect(within(rows[0]).getByText('Ordered list')).toBeTruthy();
    expect(within(rows[1]).getByText('One')).toBeTruthy();
    expect(within(rows[1]).getByText('None')).toBeTruthy();
    expect(within(rows[2]).getByText('custom-mode')).toBeTruthy();
  });

  it('lists implementations in their accepted order with details', () => {
    render(<PlatformComponentsTable components={components} />);
    const firstRow = screen.getAllByRole('row')[1];
    const items = within(firstRow).getAllByRole('listitem');
    expect(items.map((entry) => within(entry).getByText(/provider$/).textContent)).toEqual(['first-provider', 'second-provider']);
    expect(items[0].textContent).toContain('version 1.0.0 · managed');
    expect(within(items[0]).getByText('platform')).toBeTruthy();
    expect(within(items[0]).getByText('Can: network, gpu')).toBeTruthy();
    expect(items[1].textContent).toContain('version 2.1.0 · self-hosted');
    expect(items[1].textContent).not.toContain('Can:');
  });
});
