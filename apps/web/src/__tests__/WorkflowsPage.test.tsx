import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { type ReactNode } from 'react';
import type { WorkflowListResponse } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    listWorkflows: vi.fn(),
    syncWorkflows: vi.fn(),
    setDefaultWorkflow: vi.fn(),
    getProject: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { WorkflowsPage } from '../pages/WorkflowsPage';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
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
      trigger: { type: 'manual', event: null },
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
      trigger: { type: 'event', event: 'task-added-to-ready' },
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
  it('lists workflows with default/validation badges and trigger info', async () => {
    vi.mocked(apiClient.listWorkflows).mockResolvedValue(sampleList);

    renderPage('proj-1');

    await waitFor(() => expect(screen.getByText('Default Workflow')).toBeDefined());
    expect(screen.getByText('Nightly Sweep')).toBeDefined();
    expect(screen.getByText('Default')).toBeDefined();
    expect(screen.getByText('Built-in')).toBeDefined();
    expect(screen.getByText('Invalid')).toBeDefined();
    expect(screen.getByText('Unknown node type: foo')).toBeDefined();
    expect(screen.getByText('Trigger: event (task-added-to-ready)')).toBeDefined();
  });

  it('shows an empty state when no workflows are found', async () => {
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({ default_workflow_id: 'default', workflows: [] });

    renderPage('proj-1');

    await waitFor(() => expect(screen.getByText('No workflows found')).toBeDefined());
    expect(screen.getByText('Sync to load from .agentweaver/workflows/.')).toBeDefined();
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
});
