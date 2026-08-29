import { AzureFluentProvider } from '../copilot-fluent-system';
import { VisualWorkflowEditor } from '../components/VisualWorkflowEditor';
import { apiClient } from '../api/apiClient';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
/**
 * Coverage for #186 — the workflow editor's RAI/Rubberduck/Human Review gate palette:
 * add/configure/remove affordances, branch-routing UI (incl. loop-backs), the
 * merge/scribe read-only backward-compat path, and inline validation for
 * unrouted gate verdicts. Complements the pure-function round-trip coverage in
 * workflowYaml.test.ts with the actual rendered editor surface.
 */

// ResizeObserver is absent in happy-dom; ReactFlow requires it.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

vi.mock('../api/apiClient', () => ({
  apiClient: {
    saveWorkflowYaml: vi.fn(),
    getSystemRuntime: vi.fn().mockResolvedValue({ kubernetes: false, podName: null }),
  },
}));

// #540 — spy on React Flow's imperative fitView while delegating to the real
// implementation, so tests can assert *when* the editor re-fits the viewport
// without losing real layout/rendering behavior.
const fitViewSpy = vi.hoisted(() => vi.fn());
const selectionChange = vi.hoisted(() => ({
  current: undefined as undefined | ((params: unknown) => void),
}));
vi.mock('@xyflow/react', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@xyflow/react')>();
  return {
    ...actual,
    ReactFlow: (props: React.ComponentProps<typeof actual.ReactFlow>) => {
      selectionChange.current = props.onSelectionChange as ((params: unknown) => void) | undefined;
      return <actual.ReactFlow {...props} />;
    },
    useReactFlow: () => {
      const real = actual.useReactFlow();
      return {
        ...real,
        fitView: (...args: Parameters<typeof real.fitView>) => {
          fitViewSpy(...args);
          return real.fitView(...args);
        },
      };
    },
  };
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  selectionChange.current = undefined;
});

function Wrapper({ children }: { children: React.ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

const YAML_WITH_UNROUTED_RAI = `id: sample
name: Sample
description: A sample workflow.
start: implement

nodes:
  - id: implement
    type: prompt
    label: Implement
    agent: backend-engineer

  - id: rai-check
    type: check
    label: RAI Check
    role: review
    kind: gate
    gate_kind: rai
    branches:
      - revise
      - safety-failed
      - no-changes
      - review

  - id: done
    type: terminal
    label: Done

edges:
  - from: implement
    to: rai-check
  - from: rai-check
    to: done
    when: review
`;

const YAML_WITH_LEGACY_TAIL = `id: legacy
name: Legacy
description: Uses the platform-owned merge/scribe tail.
start: implement

nodes:
  - id: implement
    type: prompt
    label: Implement
    agent: backend-engineer

  - id: merge
    type: merge
    label: Merge

  - id: scribe
    type: scribe
    label: Scribe

edges:
  - from: implement
    to: merge
  - from: merge
    to: scribe
`;

const YAML_WITH_SCHEDULE = `id: scheduled
name: Scheduled workflow
description: Runs every week.
start: done
trigger:
  type: schedule
  interval: weekly
  day_of_week: monday
  time_of_day: "09:00"
nodes:
  - id: done
    type: terminal
    label: Done
edges: []
`;

const YAML_WITH_EVENT = `id: event-driven
name: Event-driven workflow
description: Runs for issues.
start: done
trigger:
  type: event
  event_name: github.issues
nodes:
  - id: done
    type: terminal
    label: Done
edges: []
`;

function renderEditor(yaml: string) {
  return render(
    <Wrapper>
      <VisualWorkflowEditor projectId="proj-1" workflowId="sample" initialYaml={yaml} />
    </Wrapper>,
  );
}

describe('VisualWorkflowEditor — gate palette (#186)', () => {
  it('warns inline when a gate has unrouted verdicts', async () => {
    renderEditor(YAML_WITH_UNROUTED_RAI);

    // rai-check declares revise / safety-failed / no-changes / review but only
    // "review" has an outgoing edge — the other three should surface as unrouted.
    await waitFor(() => {
      const warning = screen.getByText(/unrouted verdict/i);
      expect(warning.textContent).toContain('rai-check');
      expect(warning.textContent).toContain('revise');
      expect(warning.textContent).toContain('safety-failed');
      expect(warning.textContent).toContain('no-changes');
    });
  });

  it('offers gates, actions, and primitives in a grouped add-node palette, but never Merge/Scribe', async () => {
    const user = userEvent.setup();
    renderEditor(YAML_WITH_UNROUTED_RAI);

    await user.click(await screen.findByRole('button', { name: /add node/i }));

    await waitFor(() => {
      expect(screen.getByRole('menuitem', { name: /rai check/i })).toBeDefined();
      expect(screen.getByRole('menuitem', { name: /rubberduck review/i })).toBeDefined();
      expect(screen.getByRole('menuitem', { name: /human review/i })).toBeDefined();
      // "Build & Test" must now appear EXACTLY once (#558). It previously showed
      // twice — once as the pre-configured SPECIAL_GATES preset and once as the raw
      // build_test node-type — with identical labels, which was confusing. The raw
      // primitive is dropped from the palette; the preset is the single entry point.
      expect(screen.getAllByRole('menuitem', { name: /build & test/i })).toHaveLength(1);
      expect(screen.getByRole('menuitem', { name: /open pull request/i })).toBeDefined();
      expect(screen.getByRole('menuitem', { name: /^publish/i })).toBeDefined();
    });

    // The palette is grouped under scannable headers (#558).
    expect(screen.getByText('Reviewers & gates')).toBeDefined();
    expect(screen.getByText('Agent steps')).toBeDefined();
    expect(screen.getByText('Actions')).toBeDefined();
    expect(screen.getByText('Flow control')).toBeDefined();

    // Representative primitives remain reachable in their groups.
    expect(screen.getByRole('menuitem', { name: /prompt \(agent turn\)/i })).toBeDefined();
    expect(screen.getByRole('menuitem', { name: /peer review/i })).toBeDefined();
    expect(screen.getByRole('menuitem', { name: /fan-out/i })).toBeDefined();

    expect(screen.queryByRole('menuitem', { name: /^merge$/i })).toBeNull();
    expect(screen.queryByRole('menuitem', { name: /^scribe$/i })).toBeNull();

    await user.click(screen.getByRole('menuitem', { name: /^publish/i }));
    expect(await screen.findByRole('textbox', { name: 'Prompt' })).toBeDefined();
  });

  it('renders existing merge/scribe tail nodes read-only for backward compatibility', async () => {
    renderEditor(YAML_WITH_LEGACY_TAIL);

    // Selecting the legacy merge node (via the canvas node) should show the
    // read-only notice rather than editable fields. We drive selection through
    // the underlying model by clicking the rendered node label.
    const mergeNode = await screen.findByText('Merge');
    fireEvent.click(mergeNode);

    await waitFor(() => {
      expect(screen.getByText(/platform-owned tail steps/i)).toBeDefined();
    });
  });
});

describe('VisualWorkflowEditor — viewport re-fit on add (#540)', () => {
  it('re-fits the viewport after adding a node so it is not clipped by the canvas overflow', async () => {
    const user = userEvent.setup();
    renderEditor(YAML_WITH_UNROUTED_RAI);

    await screen.findByText('Implement');
    // Let the initial mount-time fitView (driven by the `fitView` prop, not our
    // imperative hook) settle before asserting on our spy.
    fitViewSpy.mockClear();

    await user.click(await screen.findByRole('button', { name: /add node/i }));
    await user.click(await screen.findByRole('menuitem', { name: /rubberduck review/i }));

    await waitFor(() => {
      expect(fitViewSpy).toHaveBeenCalled();
    });
  });

  it('does not re-fit the viewport when an existing node is merely renamed', async () => {
    renderEditor(YAML_WITH_UNROUTED_RAI);

    const implementNode = await screen.findByText('Implement');
    fitViewSpy.mockClear();

    fireEvent.click(implementNode);
    const labelInput = await screen.findByDisplayValue('Implement');
    fireEvent.change(labelInput, { target: { value: 'Implement (renamed)' } });
    fireEvent.blur(labelInput);

    await waitFor(() => {
      expect((labelInput as HTMLInputElement).value).toBe('Implement (renamed)');
    });
    expect(fitViewSpy).not.toHaveBeenCalled();
  });
});

describe('VisualWorkflowEditor — selection persistence (#1007)', () => {
  async function selectRaiCheck() {
    fireEvent.click(await screen.findByText('RAI Check'));
    return screen.findByRole('textbox', { name: 'Gate branches' });
  }

  it('keeps a selected node inspector open after editing workflow metadata', async () => {
    renderEditor(YAML_WITH_UNROUTED_RAI);
    await selectRaiCheck();

    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), {
      target: { value: 'Renamed workflow' },
    });

    await waitFor(() => {
      expect(screen.getByRole('textbox', { name: 'Gate branches' })).toBeDefined();
    });
    expect(screen.queryByText('Select a node or edge')).toBeNull();
  });

  it('keeps a selected node inspector open after text and type edits', async () => {
    const user = userEvent.setup();
    renderEditor(YAML_WITH_UNROUTED_RAI);
    await selectRaiCheck();

    const label = screen.getByRole('textbox', { name: 'Label' });
    fireEvent.change(label, { target: { value: 'Review gate' } });
    fireEvent.blur(label);

    await waitFor(() => {
      expect(screen.getByRole('textbox', { name: 'Gate branches' })).toBeDefined();
    });

    await user.click(screen.getByRole('combobox', { name: 'Type' }));
    await user.click(await screen.findByRole('option', { name: 'Prompt (agent turn)' }));

    await waitFor(() => {
      expect(screen.getByRole('textbox', { name: 'Prompt' })).toBeDefined();
    });
    expect(screen.queryByText('Select a node or edge')).toBeNull();
  });

  it('keeps a selected edge inspector open after editing its text', async () => {
    renderEditor(YAML_WITH_UNROUTED_RAI);

    await waitFor(() => expect(selectionChange.current).toBeDefined());
    act(() => {
      selectionChange.current?.({ nodes: [], edges: [{ data: { index: 1 } }] });
    });
    const when = await screen.findByRole('textbox', { name: 'When' });
    fireEvent.change(when, { target: { value: 'approved' } });
    fireEvent.blur(when);

    await waitFor(() => {
      expect(screen.getByRole('textbox', { name: 'When' })).toBeDefined();
    });
    expect(screen.queryByText('Select a node or edge')).toBeNull();
  });

  it('clears the inspector when the selected node is deleted', async () => {
    const user = userEvent.setup();
    renderEditor(YAML_WITH_UNROUTED_RAI);
    await selectRaiCheck();

    await user.click(screen.getByRole('button', { name: 'Delete node' }));

    await waitFor(() => {
      expect(screen.getByText('Select a node or edge')).toBeDefined();
    });
  });
});

describe('VisualWorkflowEditor — stable drag targets (#557)', () => {
  it('exposes editor-owned node, handle, and canvas test ids without React Flow class selectors', async () => {
    renderEditor(YAML_WITH_UNROUTED_RAI);

    expect(await screen.findByTestId('workflow-canvas')).toBeDefined();
    expect(await screen.findByTestId('workflow-node-implement')).toBeDefined();
    expect(await screen.findByTestId('workflow-node-implement-handle-source')).toBeDefined();
    expect(await screen.findByTestId('workflow-node-rai-check-handle-target')).toBeDefined();
  });
});

describe('VisualWorkflowEditor — schedule trigger (#561)', () => {
  it('edits the YAML buffer and persists the schedule with the normal editor save', async () => {
    const user = userEvent.setup();
    vi.mocked(apiClient.saveWorkflowYaml).mockResolvedValue({ name: 'Scheduled workflow' } as never);
    renderEditor(YAML_WITH_SCHEDULE);

    expect(await screen.findByText('weekly · 09:00 UTC')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Edit schedule trigger' }));

    fireEvent.change(await screen.findByRole('combobox', { name: 'Day of week' }), { target: { value: 'tuesday' } });
    fireEvent.change(screen.getByLabelText('UTC time'), { target: { value: '13:30' } });
    await user.click(screen.getByRole('button', { name: 'Save schedule' }));

    expect(screen.getByText('weekly · 13:30 UTC')).toBeDefined();
    expect(screen.getByText('Unsaved changes')).toBeDefined();
    expect(apiClient.saveWorkflowYaml).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(apiClient.saveWorkflowYaml).toHaveBeenCalledWith(
      'proj-1',
      'scheduled',
      expect.stringContaining('day_of_week: tuesday'),
    ));
    expect(vi.mocked(apiClient.saveWorkflowYaml).mock.calls.at(-1)?.[2]).toContain('time_of_day: 13:30');
  });

  it('removes an existing schedule from the buffer before saving', async () => {
    const user = userEvent.setup();
    vi.mocked(apiClient.saveWorkflowYaml).mockResolvedValue({ name: 'Scheduled workflow' } as never);
    renderEditor(YAML_WITH_SCHEDULE);

    expect(await screen.findByText('weekly · 09:00 UTC')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Edit schedule trigger' }));
    await user.click(await screen.findByRole('button', { name: 'Remove schedule' }));

    expect(screen.getByText('Manual only')).toBeDefined();
    expect(screen.getByText('Unsaved changes')).toBeDefined();

    await user.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(apiClient.saveWorkflowYaml).toHaveBeenCalled());
    expect(vi.mocked(apiClient.saveWorkflowYaml).mock.calls.at(-1)?.[2]).not.toContain('trigger:');
  });

  it('adds a schedule without replacing the current event trigger', async () => {
    const user = userEvent.setup();
    vi.mocked(apiClient.saveWorkflowYaml).mockResolvedValue({ name: 'Event-driven workflow' } as never);
    renderEditor(YAML_WITH_EVENT);

    fireEvent.click(await screen.findByRole('button', { name: 'Add schedule trigger' }));
    await user.click(await screen.findByRole('button', { name: 'Save schedule' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(apiClient.saveWorkflowYaml).toHaveBeenCalled());
    const savedYaml = vi.mocked(apiClient.saveWorkflowYaml).mock.calls.at(-1)?.[2] ?? '';
    expect(savedYaml).toContain('triggers:');
    expect(savedYaml).toContain('type: event');
    expect(savedYaml).toContain('event_name: github.issues');
    expect(savedYaml).toContain('type: schedule');
  });
});
