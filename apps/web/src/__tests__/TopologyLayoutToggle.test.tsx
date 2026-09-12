import { AzureFluentProvider } from '../copilot-fluent-system';
import { TopologyLayoutToggle } from '../components/TopologyLayoutToggle';
import { useTopologyLayoutEngine } from '../hooks/useTopologyLayoutEngine';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

function Harness() {
  const [engine, setEngine] = useTopologyLayoutEngine();
  return <TopologyLayoutToggle engine={engine} onChange={setEngine} />;
}

afterEach(() => {
  cleanup();
  window.localStorage.clear();
});

describe('TopologyLayoutToggle', () => {
  it('defaults to the balanced-grid rollout engine and explains comparison behavior', () => {
    render(<AzureFluentProvider density="compact"><Harness /></AzureFluentProvider>);

    expect((screen.getByRole('radio', { name: 'Balanced grid (current)' }) as HTMLInputElement).checked).toBe(true);
    expect(screen.getByText(/changes only card placement/i)).toBeTruthy();
  });

  it('makes the legacy staircase an explicit persisted comparison choice', () => {
    render(<AzureFluentProvider density="compact"><Harness /></AzureFluentProvider>);

    fireEvent.click(screen.getByRole('radio', { name: 'Legacy staircase (comparison)' }));

    expect((screen.getByRole('radio', { name: 'Legacy staircase (comparison)' }) as HTMLInputElement).checked).toBe(true);
    expect(window.localStorage.getItem('agentweaver.topology-layout-engine')).toBe('legacy-staircase');
  });
});
