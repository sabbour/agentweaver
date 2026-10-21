import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { AgentsView } from './AgentsView';
import type { ProjectConfiguration } from './contracts';

/** Two agents (only Ada is cast) and three skills: shared, Bo-only, and disabled. */
function config(overrides: Partial<ProjectConfiguration> = {}): ProjectConfiguration {
  return {
    providerOverrides: [],
    orderedProviderOverrides: [],
    agentCharters: [
      { agentId: 'a1', name: 'Ada', role: 'Lead', charter: 'Lead the work.' },
      { agentId: 'a2', name: 'Bo', role: 'Tester', charter: 'Test the work.' },
    ],
    casting: [{ agentId: 'a1', role: 'Lead', order: 1 }],
    blueprintWorkflowReferences: [],
    skills: [
      { skillId: 'shared', enabled: true, order: 0 },
      { skillId: 'for-bo', enabled: true, order: 1, agentIds: ['a2'] },
      { skillId: 'off', enabled: false, order: 2 },
    ],
    runLimits: {},
    ...overrides,
  };
}

/** Renders the view with a save handler that succeeds, and returns that handler. */
function renderView(props: Partial<Parameters<typeof AgentsView>[0]> = {}) {
  const onSave = vi.fn().mockResolvedValue(true);
  render(
    <MemoryRouter>
      <AgentsView projectId="p1" configuration={config()} readOnly={false} onSave={onSave} {...props} />
    </MemoryRouter>,
  );
  return { onSave };
}

describe('AgentsView', () => {
  it('shows an empty state without agents', () => {
    renderView({ configuration: config({ agentCharters: [], casting: [] }) });
    expect(screen.getByText('No agents yet')).toBeTruthy();
  });

  it('filters agents by team membership', () => {
    renderView();
    expect(screen.getByText('Ada')).toBeTruthy();
    expect(screen.getByText('Bo')).toBeTruthy();
    fireEvent.click(screen.getByRole('tab', { name: 'In the team (1)' }));
    expect(screen.getByText('Ada')).toBeTruthy();
    expect(screen.queryByText('Bo')).toBeNull();
    fireEvent.click(screen.getByRole('tab', { name: 'Not in the team (1)' }));
    expect(screen.queryByText('Ada')).toBeNull();
    expect(screen.getByText('Bo')).toBeTruthy();
  });

  it('opens agent details with team position and applicable skills', async () => {
    renderView();
    fireEvent.click(screen.getByText('Bo'));
    const drawer = await screen.findByRole('dialog');
    expect(within(drawer).getByText('Not in the team')).toBeTruthy();
    expect(within(drawer).getByText('a2')).toBeTruthy();
    expect(within(drawer).getByText('shared')).toBeTruthy();
    expect(within(drawer).getByText('for-bo')).toBeTruthy();
    // Disabled skills are not listed, even when they apply to every agent.
    expect(within(drawer).queryByText('off')).toBeNull();
    expect(within(drawer).getByRole('link', { name: 'View memories' }).getAttribute('href')).toBe('/projects/p1/memories?agentId=a2');
  });

  it('saves only the selected agent charter', async () => {
    const { onSave } = renderView();
    fireEvent.click(screen.getByText('Ada'));
    fireEvent.click(await screen.findByRole('tab', { name: 'Charter' }));
    const save = screen.getByRole('button', { name: 'Save charter' }) as HTMLButtonElement;
    // Save stays disabled until something changes.
    expect(save.disabled).toBe(true);
    fireEvent.change(screen.getByRole('textbox', { name: /Charter content/ }), { target: { value: 'New charter.' } });
    await act(async () => { fireEvent.click(save); });
    // The saved configuration changes Ada's charter and leaves Bo untouched.
    const saved = onSave.mock.calls[0][0] as ProjectConfiguration;
    expect(saved.agentCharters).toEqual([
      { agentId: 'a1', name: 'Ada', role: 'Lead', charter: 'New charter.' },
      { agentId: 'a2', name: 'Bo', role: 'Tester', charter: 'Test the work.' },
    ]);
    expect(await screen.findByText('Charter saved.')).toBeTruthy();
  });

  it('hides Save charter when read-only', async () => {
    renderView({ readOnly: true });
    fireEvent.click(screen.getByText('Ada'));
    fireEvent.click(await screen.findByRole('tab', { name: 'Charter' }));
    expect(screen.queryByRole('button', { name: 'Save charter' })).toBeNull();
  });
});
