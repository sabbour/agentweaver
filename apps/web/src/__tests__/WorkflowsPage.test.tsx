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

  // Automation triggers (schedule + event) are disabled behind AUTOMATION_TRIGGERS_ENABLED
  // (see https://github.com/github/copilot-sdk/issues/551) — the schedule/event trigger
  // editors are unreachable via the page until GitHub Apps gain Copilot entitlements. The
  // dialog-editing behavior these tests used to exercise through the page remains implemented
  // in ScheduleTriggerDialog / the event trigger editor, ready to re-enable; here we only assert
  // the badges still render and the buttons are disabled with a "coming soon" tooltip.
  it('renders a schedule trigger badge and shows a disabled schedule button with a coming-soon tooltip', async () => {
    const scheduled = {
      ...sampleList.workflows[1],
      valid: true,
      error: null,
      trigger: { type: 'schedule' as const, interval: 'weekly' as const, day_of_week: 'monday', time_of_day: '09:00' },
    };
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({ default_workflow_id: 'default', workflows: [sampleList.workflows[0], scheduled] });

    renderPage('proj-1');

    expect(await screen.findByText('weekly · 09:00 UTC')).toBeDefined();
    const scheduleButton = screen.getByRole('button', { name: /edit schedule/i });
    expect(scheduleButton).toHaveProperty('disabled', true);
  });

  it('renders an event-trigger badge and shows a disabled event button with a coming-soon tooltip', async () => {
    const eventDriven = {
      ...sampleList.workflows[1],
      valid: true,
      error: null,
      trigger: { type: 'event' as const, event_name: 'github.issue_comment' },
    };
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({ default_workflow_id: 'default', workflows: [sampleList.workflows[0], eventDriven] });

    renderPage('proj-1');

    expect(await screen.findByText('event · issue_comment')).toBeDefined();
    const eventButton = screen.getByRole('button', { name: /edit event/i });
    expect(eventButton).toHaveProperty('disabled', true);
  });

  it('displays both schedule and event trigger badges with both buttons disabled', async () => {
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

    renderPage('proj-1');

    expect(await screen.findByText('weekly · 09:00 UTC')).toBeDefined();
    expect(screen.getByText('event · issues.labeled')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Edit schedule' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Edit event' })).toHaveProperty('disabled', true);
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

  it('shows a disabled schedule button for built-in workflows', async () => {
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({
      default_workflow_id: 'default',
      workflows: [sampleList.workflows[0]],
    });

    renderPage('proj-1');

    expect(await screen.findByRole('button', { name: /duplicate to project/i })).toBeDefined();
    expect(screen.getByRole('button', { name: /add schedule/i })).toHaveProperty('disabled', true);
  });
});
