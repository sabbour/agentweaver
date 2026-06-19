import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import { CaptureTaskForm } from '../components/board/CaptureTaskForm';
import type { BacklogTaskDto } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: { captureBacklogTask: vi.fn() },
}));

import { apiClient } from '../api/apiClient';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

const captured: BacklogTaskDto = {
  task_id: 't1', project_id: 'proj-1', title: 'Real task', description: null, state: 'backlog', order_key: 'a', captured_by: 'alice', created_at: '2026-01-01T00:00:00Z',
};

beforeEach(() => { vi.clearAllMocks(); });
afterEach(() => { cleanup(); });

describe('CaptureTaskForm (FR-001/002)', () => {
  it('blocks an empty/whitespace title client-side (no API call)', async () => {
    const onCaptured = vi.fn();
    render(<Wrapper><CaptureTaskForm projectId="proj-1" onCaptured={onCaptured} /></Wrapper>);

    const input = screen.getByLabelText('New task title') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '   ' } });
    // Add button stays disabled for whitespace-only titles.
    const addBtn = screen.getByRole('button', { name: 'Add' });
    expect(addBtn.hasAttribute('disabled')).toBe(true);

    expect(vi.mocked(apiClient.captureBacklogTask)).not.toHaveBeenCalled();
    expect(onCaptured).not.toHaveBeenCalled();
  });

  it('captures a valid task then refetches the board', async () => {
    vi.mocked(apiClient.captureBacklogTask).mockResolvedValue(captured);
    const onCaptured = vi.fn();
    render(<Wrapper><CaptureTaskForm projectId="proj-1" onCaptured={onCaptured} /></Wrapper>);

    fireEvent.change(screen.getByLabelText('New task title'), { target: { value: 'Real task' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add' }));

    await waitFor(() => expect(vi.mocked(apiClient.captureBacklogTask)).toHaveBeenCalledWith('proj-1', { title: 'Real task' }));
    await waitFor(() => expect(onCaptured).toHaveBeenCalled());
  });
});
