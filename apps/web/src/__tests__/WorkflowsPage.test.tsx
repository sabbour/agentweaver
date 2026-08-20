import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { WorkflowsPage } from '../pages/WorkflowsPage';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { WorkflowListResponse } from '../api/types';
import type { ReactNode } from 'react';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    listWorkflows: vi.fn(),
    syncWorkflows: vi.fn(),
    setDefaultWorkflow: vi.fn(),
    getProject: vi.fn(),
    getWorkflowYaml: vi.fn(),
    saveWorkflowYaml: vi.fn(),
    runWorkflowNow: vi.fn(),
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

function renderPage(projectId: string) {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/workflows`]}>
        <Routes>
          <Route path="/projects/:projectId/workflows" element={<WorkflowsPage />} />
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

const sampleList: WorkflowListResponse = {
  default_workflow_id: 'default',
  workflows: [
    {
      id: 'default',
      name: 'Default Workflow',
      description: 'The built-in default.',
      source: 'built-in',
      valid: true,
      error: null,
      is_built_in: true,
      is_default: true,
    },
    {
      id: 'nightly',
      name: 'Nightly Sweep',
      description: null,
      source: '.agentweaver/workflows/nightly.yaml',
      valid: false,
      error: 'Unknown node type: foo',
      is_built_in: false,
      is_default: false,
    },
  ],
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getProject).mockResolvedValue({ name: 'Demo' } as never);
});

afterEach(() => {
  cleanup();
});

describe('WorkflowsPage', () => {
  it('lists workflows with default/validation badges', async () => {
    vi.mocked(apiClient.listWorkflows).mockResolvedValue(sampleList);

    renderPage('proj-1');

    await waitFor(() => expect(screen.getByText('Default Workflow')).toBeDefined());
    expect(screen.getByText('Nightly Sweep')).toBeDefined();
    // Section headers
    expect(screen.getByText('Active workflow')).toBeDefined();
    expect(screen.getByText('Invalid workflows')).toBeDefined();
    // Badges
    expect(screen.getByText('Active')).toBeDefined();
    expect(screen.getByText('Built-in')).toBeDefined();
    expect(screen.getByText('Invalid')).toBeDefined();
    // Content
    expect(screen.getByText('Unknown node type: foo')).toBeDefined();
  });

  it('shows an empty state when no workflows are found', async () => {
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({ default_workflow_id: 'default', workflows: [] });

    renderPage('proj-1');

    await waitFor(() => expect(screen.getByText('No workflows found')).toBeDefined());
    expect(screen.getByText('Sync to load workflow definitions from .agentweaver/workflows/.')).toBeDefined();
  });

  it('calls the sync endpoint and refreshes the list', async () => {
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({ default_workflow_id: 'default', workflows: [] });
    vi.mocked(apiClient.syncWorkflows).mockResolvedValue(sampleList);

    renderPage('proj-1');

    await waitFor(() => expect(screen.getByText('No workflows found')).toBeDefined());

    const syncButtons = screen.getAllByRole('button', { name: /sync/i });
    fireEvent.click(syncButtons[0]);

    await waitFor(() => expect(apiClient.syncWorkflows).toHaveBeenCalledWith('proj-1'));
    await waitFor(() => expect(screen.getByText('Default Workflow')).toBeDefined());
  });

  it('surfaces a load error', async () => {
    const { ApiError } = await import('../api/client');
    vi.mocked(apiClient.listWorkflows).mockRejectedValue(new ApiError(403, 'Forbidden'));

    renderPage('proj-1');

    await waitFor(() => expect(screen.getByText(/API error 403/)).toBeDefined());
  });

  it('sets a workflow as default via the picker', async () => {
    const validList: WorkflowListResponse = {
      default_workflow_id: 'default',
      workflows: [
        { ...sampleList.workflows[0] },
        { ...sampleList.workflows[1], valid: true, error: null },
      ],
    };
    vi.mocked(apiClient.listWorkflows).mockResolvedValue(validList);
    vi.mocked(apiClient.setDefaultWorkflow).mockResolvedValue({
      ...validList,
      default_workflow_id: 'nightly',
      workflows: [
        { ...validList.workflows[0], is_default: false },
        { ...validList.workflows[1], is_default: true },
      ],
    });

    renderPage('proj-1');

    await waitFor(() => expect(screen.getByText('Nightly Sweep')).toBeDefined());

    fireEvent.click(screen.getByRole('button', { name: /set as default/i }));
    fireEvent.click(await screen.findByRole('menuitem', { name: /Nightly Sweep/i }));

    await waitFor(() => expect(apiClient.setDefaultWorkflow).toHaveBeenCalledWith('proj-1', 'nightly'));
    await waitFor(() => expect(screen.getByText(/Default workflow set to Nightly Sweep/)).toBeDefined());
  });

  it('renders and edits a project workflow schedule trigger', async () => {
    const scheduled = {
      ...sampleList.workflows[1],
      valid: true,
      error: null,
      trigger: { type: 'schedule' as const, interval: 'weekly' as const, day_of_week: 'monday', time_of_day: '09:00' },
    };
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({ default_workflow_id: 'default', workflows: [sampleList.workflows[0], scheduled] });
    vi.mocked(apiClient.getWorkflowYaml).mockResolvedValue('id: nightly\nname: Nightly Sweep\nstart: done\nnodes: []\nedges: []\n');
    vi.mocked(apiClient.saveWorkflowYaml).mockResolvedValue({ name: 'Nightly Sweep' } as never);

    renderPage('proj-1');

    expect(await screen.findByText('weekly · 09:00 UTC')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: /edit schedule/i }));
    expect(await screen.findByText('Schedule workflow')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: /save schedule/i }));

    await waitFor(() => expect(apiClient.saveWorkflowYaml).toHaveBeenCalledWith(
      'proj-1',
      'nightly',
      expect.stringContaining('trigger:'),
    ));
  });

  it('renders an event-trigger badge and loads the configured event editor state', async () => {
    const eventDriven = {
      ...sampleList.workflows[1],
      valid: true,
      error: null,
      trigger: { type: 'event' as const, event_name: 'github.issue_comment' },
    };
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({ default_workflow_id: 'default', workflows: [sampleList.workflows[0], eventDriven] });
    vi.mocked(apiClient.getWorkflowYaml).mockResolvedValue(`id: nightly
name: Nightly Sweep
start: done
nodes: []
edges: []
trigger:
  type: event
  event_name: github.issue_comment
  if:
    - comment_matches:
        pattern: ^/agentweaver:triage$
`);

    renderPage('proj-1');

    expect(await screen.findByText('event · issue_comment')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: /edit event/i }));
    expect(await screen.findByText('Event trigger')).toBeDefined();
    expect(screen.getByText('Issue comment')).toBeDefined();
    expect((screen.getByRole('textbox', { name: 'Exact command match' }) as HTMLInputElement).value).toBe('/agentweaver:triage');
  });

  it('displays and edits schedule and event triggers without replacing either one', async () => {
    const combined = {
      ...sampleList.workflows[1],
      valid: true,
      error: null,
      triggers: [
        { type: 'schedule' as const, interval: 'weekly' as const, day_of_week: 'monday', time_of_day: '09:00' },
        { type: 'event' as const, event_name: 'github.issues.labeled' },
      ],
    };
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({
      default_workflow_id: 'default',
      workflows: [sampleList.workflows[0], combined],
    });
    vi.mocked(apiClient.getWorkflowYaml).mockResolvedValue(`id: nightly
name: Nightly Sweep
start: done
nodes: []
edges: []
triggers:
  - type: schedule
    interval: weekly
    day_of_week: monday
    time_of_day: "09:00"
  - type: event
    event_name: github.issues.labeled
    if:
      - has_label:
          label: roadmap-review
`);
    vi.mocked(apiClient.saveWorkflowYaml).mockResolvedValue({ name: 'Nightly Sweep' } as never);

    renderPage('proj-1');

    expect(await screen.findByText('weekly · 09:00 UTC')).toBeDefined();
    expect(screen.getByText('event · issues.labeled')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Edit schedule' })).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Edit event' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Save event' }));

    await waitFor(() => expect(apiClient.saveWorkflowYaml).toHaveBeenCalled());
    const savedYaml = vi.mocked(apiClient.saveWorkflowYaml).mock.calls[0]?.[2] ?? '';
    expect(savedYaml).toContain('triggers:');
    expect(savedYaml).toContain('type: schedule');
    expect(savedYaml).toContain('type: event');
  });

  it('makes the Issues action explicit and can switch an opened trigger to labeled', async () => {
    const eventDriven = {
      ...sampleList.workflows[1],
      valid: true,
      error: null,
      triggers: [{ type: 'event' as const, event_name: 'github.issues.opened' }],
    };
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({
      default_workflow_id: 'default',
      workflows: [sampleList.workflows[0], eventDriven],
    });
    vi.mocked(apiClient.getWorkflowYaml).mockResolvedValue(`id: nightly
name: Nightly Sweep
start: done
nodes: []
edges: []
trigger:
  type: event
  event_name: github.issues.opened
  if:
    - has_label:
        label: agentweaver:triage
`);
    vi.mocked(apiClient.saveWorkflowYaml).mockResolvedValue({ name: 'Nightly Sweep' } as never);

    renderPage('proj-1');

    fireEvent.click(await screen.findByRole('button', { name: 'Edit event' }));
    const actionSelect = await screen.findByRole('combobox', { name: 'Issue action' });
    expect((actionSelect as HTMLSelectElement).value).toBe('opened');
    fireEvent.change(actionSelect, { target: { value: 'labeled' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save event' }));

    await waitFor(() => expect(apiClient.saveWorkflowYaml).toHaveBeenCalled());
    expect(vi.mocked(apiClient.saveWorkflowYaml).mock.calls[0]?.[2])
      .toContain('event_name: github.issues.labeled');
  });

  it('builds OR event conditions and saves them back to YAML', async () => {
    const projectWorkflow = { ...sampleList.workflows[1], valid: true, error: null };
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({ default_workflow_id: 'default', workflows: [sampleList.workflows[0], projectWorkflow] });
    vi.mocked(apiClient.getWorkflowYaml).mockResolvedValue('id: nightly\nname: Nightly Sweep\nstart: done\nnodes: []\nedges: []\n');
    vi.mocked(apiClient.saveWorkflowYaml).mockResolvedValue({ name: 'Nightly Sweep' } as never);

    renderPage('proj-1');

    fireEvent.click(await screen.findByRole('button', { name: /add event/i }));
    expect(await screen.findByText('Event trigger')).toBeDefined();

    fireEvent.change(screen.getByRole('combobox', { name: 'GitHub event' }), { target: { value: 'issue_comment' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add condition' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Match any of' }));

    const commandInputs = screen.getAllByRole('textbox', { name: 'Exact command match' });
    fireEvent.change(commandInputs[0], { target: { value: '/agentweaver:triage' } });
    fireEvent.change(commandInputs[1], { target: { value: '/agentweaver:rerun' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save event' }));

    await waitFor(() => expect(apiClient.saveWorkflowYaml).toHaveBeenCalledWith(
      'proj-1',
      'nightly',
      expect.stringContaining('event_name: github.issue_comment'),
    ));
    expect(vi.mocked(apiClient.saveWorkflowYaml).mock.calls[0]?.[2]).toContain('or:');
    expect(vi.mocked(apiClient.saveWorkflowYaml).mock.calls[0]?.[2]).toContain('pattern: ^/agentweaver:triage$');
    expect(vi.mocked(apiClient.saveWorkflowYaml).mock.calls[0]?.[2]).toContain('pattern: ^/agentweaver:rerun$');
  });

  it('preserves values by splitting them into AND rows when Match any of is turned off', async () => {
    const projectWorkflow = { ...sampleList.workflows[1], valid: true, error: null };
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({ default_workflow_id: 'default', workflows: [sampleList.workflows[0], projectWorkflow] });
    vi.mocked(apiClient.getWorkflowYaml).mockResolvedValue('id: nightly\nname: Nightly Sweep\nstart: done\nnodes: []\nedges: []\n');

    renderPage('proj-1');

    fireEvent.click(await screen.findByRole('button', { name: /add event/i }));
    expect(await screen.findByText('Event trigger')).toBeDefined();
    fireEvent.change(screen.getAllByRole('combobox')[0], { target: { value: 'issue_comment' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add condition' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Match any of' }));

    let commandInputs = screen.getAllByRole('textbox', { name: 'Exact command match' });
    fireEvent.change(commandInputs[0], { target: { value: '/agentweaver:triage' } });
    fireEvent.change(commandInputs[1], { target: { value: '/agentweaver:rerun' } });

    fireEvent.click(screen.getByRole('checkbox', { name: 'Match any of' }));

    commandInputs = screen.getAllByRole('textbox', { name: 'Exact command match' });
    expect(commandInputs).toHaveLength(2);
    expect((commandInputs[0] as HTMLInputElement).value).toBe('/agentweaver:triage');
    expect((commandInputs[1] as HTMLInputElement).value).toBe('/agentweaver:rerun');
  });

  it('queues a workflow-bound run from Run now', async () => {
    const runnable = { ...sampleList.workflows[1], valid: true, error: null };
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({ default_workflow_id: 'default', workflows: [sampleList.workflows[0], runnable] });
    vi.mocked(apiClient.runWorkflowNow).mockResolvedValue({ task_id: 'task-1' });

    renderPage('proj-1');

    fireEvent.click((await screen.findAllByRole('button', { name: /run now/i }))[1]);
    await waitFor(() => expect(apiClient.runWorkflowNow).toHaveBeenCalledWith('proj-1', 'nightly'));
    expect(await screen.findByText(/Queued a run for "Nightly Sweep"/)).toBeDefined();
  });

  it('duplicates a built-in workflow into a project workflow and opens the visual editor', async () => {
    vi.mocked(apiClient.listWorkflows).mockResolvedValue(sampleList);
    vi.mocked(apiClient.getWorkflowYaml).mockResolvedValue('id: default\nname: Default Workflow\nstart: done\nnodes: []\nedges: []\n');
    vi.mocked(apiClient.saveWorkflowYaml).mockResolvedValue({ name: 'Copy of Default Workflow' } as never);

    renderPage('proj-1');

    fireEvent.click(await screen.findByRole('button', { name: /duplicate to project/i }));
    await waitFor(() => expect(apiClient.saveWorkflowYaml).toHaveBeenCalledWith(
      'proj-1',
      'default-copy',
      expect.stringContaining('id: default-copy'),
    ));
  });

  it('keeps schedule configuration read-only for built-in workflows', async () => {
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({
      default_workflow_id: 'default',
      workflows: [sampleList.workflows[0]],
    });

    renderPage('proj-1');

    expect(await screen.findByRole('button', { name: /duplicate to project/i })).toBeDefined();
    expect(screen.queryByRole('button', { name: /schedule/i })).toBeNull();
  });
});
