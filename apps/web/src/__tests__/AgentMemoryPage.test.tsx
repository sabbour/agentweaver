import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { AgentMemoryPage } from '../pages/AgentMemoryPage';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AgentMemoryDto } from '../api/types';
import type { ReactNode } from 'react';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getAgentMemory: vi.fn(),
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

function renderPage(projectId = 'proj-001', agentName = 'Alice') {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/team/${agentName}/memory`]}>
        <Routes>
          <Route path="/projects/:projectId/team/:agentName/memory" element={<AgentMemoryPage />} />
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

function pagedResult<T>(items: T[], pageNumber: number, pageSize: number, totalCount: number) {
  return {
    items,
    page: pageNumber,
    page_size: pageSize,
    total_count: totalCount,
    total_pages: Math.max(1, Math.ceil(totalCount / Math.max(1, pageSize))),
  } as never;
}

function makeMemory(id: string, over?: Partial<AgentMemoryDto>): AgentMemoryDto {
  return {
    id,
    agent_name: 'Alice',
    type: 'learning',
    importance: 'medium',
    content: `Memory ${id}`,
    tags: null as never,
    created_at: '2026-03-10T12:00:00Z',
    updated_at: '2026-03-10T12:00:00Z',
    ...over,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  cleanup();
});

describe('AgentMemoryPage', () => {
  it('renders paginated agent memory entries', async () => {
    const allEntries = Array.from({ length: 28 }, (_, index) =>
      makeMemory(`m${index + 1}`, { content: `Memory entry ${index + 1}` }),
    );
    vi.mocked(apiClient.getAgentMemory).mockImplementation(async (_projectId, _agentName, options) => {
      const pageNumber = options?.page ?? 1;
      const pageSize = options?.pageSize ?? 25;
      const start = (pageNumber - 1) * pageSize;
      return pagedResult(allEntries.slice(start, start + pageSize), pageNumber, pageSize, allEntries.length);
    });

    renderPage();

    await waitFor(() => expect(screen.getByText('Memory entry 1')).toBeTruthy());
    expect(screen.queryByText('Memory entry 26')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => expect(screen.getByText('Memory entry 26')).toBeTruthy());
    expect(screen.queryByText('Memory entry 1')).toBeNull();
  });

  it('shows an empty state when the agent has no memory entries', async () => {
    vi.mocked(apiClient.getAgentMemory).mockResolvedValue(pagedResult([], 1, 25, 0));

    renderPage();

    await waitFor(() => expect(screen.getByText('No memory entries yet')).toBeTruthy());
  });
});
