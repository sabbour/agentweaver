import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import type { TaskCardDto, WorkflowListResponse } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    listWorkflows: vi.fn(),
    setTaskWorkflowOverride: vi.fn(),
    editBacklogTask: vi.fn(),
    deleteBacklogTask: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { TaskCard } from '../components/board/TaskCard';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

const card: TaskCardDto = {
  kind: 'task',
  task_id: 'task-1',
  title: 'Wire the thing',
  description: 'Do it well',
  state: 'ready',
  order_key: 'a0',
  captured_by: 'user@example.com',
  created_at: '2026-06-22T10:00:00Z',
};

const list: WorkflowListResponse = {
  default_workflow_id: 'default',
  workflows: [
    {
      id: 'nightly',
      name: 'Nightly Sweep',
      description: null,
      trigger: { type: 'manual', event: null },
      source: '.agentweaver/workflows/nightly.yaml',
      valid: true,
      error: null,
      is_built_in: false,
      is_default: false,
    },
  ],
};

function renderCard(onMutated = vi.fn()) {
  return render(
    <Wrapper>
      <TaskCard
        card={card}
        columnId="intake"
        projectId="proj-1"
        onMutated={onMutated}
        onDragStartTask={vi.fn()}
        onDragEndTask={vi.fn()}
        isDragging={false}
      />
    </Wrapper>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  cleanup();
});

describe('TaskCard workflow override', () => {
  it('sets a per-task workflow override', async () => {
    vi.mocked(apiClient.listWorkflows).mockResolvedValue(list);
    vi.mocked(apiClient.setTaskWorkflowOverride).mockResolvedValue({ task_id: 'task-1', workflow_override_id: 'nightly' });
    const onMutated = vi.fn();

    renderCard(onMutated);

    fireEvent.click(screen.getByRole('button', { name: 'Set workflow' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: /Nightly Sweep/i }));

    await waitFor(() => expect(apiClient.setTaskWorkflowOverride).toHaveBeenCalledWith('proj-1', 'task-1', 'nightly'));
    await waitFor(() => expect(screen.getByText('Workflow override set.')).toBeDefined());
    expect(onMutated).toHaveBeenCalled();
  });

  it('surfaces a 409 when the task was just claimed', async () => {
    const { ApiError } = await import('../api/client');
    vi.mocked(apiClient.listWorkflows).mockResolvedValue(list);
    vi.mocked(apiClient.setTaskWorkflowOverride).mockRejectedValue(new ApiError(409, '{"error":"task_claimed"}'));
    const onMutated = vi.fn();

    renderCard(onMutated);

    fireEvent.click(screen.getByRole('button', { name: 'Set workflow' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: /Nightly Sweep/i }));

    await waitFor(() => expect(screen.getByText(/can no longer be changed/)).toBeDefined());
    expect(onMutated).toHaveBeenCalled();
  });
});
