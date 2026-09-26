import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { MemoriesPage } from '../pages/MemoriesPage';
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
import type { AgentMemoryDto, AgentMemoryRevisionDto, DecisionDto, DecisionInboxEntryDto, SessionHistoryDto } from '../api/types';
import type { ReactNode } from 'react';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    getDecisions: vi.fn(),
    getDecisionsInbox: vi.fn(),
    getProjectMemory: vi.fn(),
    getProjectSessions: vi.fn(),
    mergeDecisionInboxEntry: vi.fn(),
    promoteDecisionInboxEntry: vi.fn(),
    rejectDecisionInboxEntry: vi.fn(),
    createAgentMemory: vi.fn(),
    updateAgentMemory: vi.fn(),
    getAgentMemoryRevisions: vi.fn(),
    restoreAgentMemory: vi.fn(),
    getDecisionRevisions: vi.fn(),
    restoreDecision: vi.fn(),
  },
}));

// Pagination contract (`.squad/decisions/inbox/niobe-pagination-contract.md`): these client
// methods now resolve a `{ items, page, page_size, total_count, total_pages }` envelope.
function page<T>(items: T[]) {
  return { items, page: 1, page_size: 25, total_count: items.length, total_pages: 1 } as never;
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

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

function renderPage(projectId = 'proj-001') {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/memories`]}>
        <Routes>
          <Route path="/projects/:projectId/memories" element={<MemoriesPage />} />
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

function makeActive(id: string): DecisionDto {
  return {
    id,
    agent_name: 'Architect',
    type: 'architecture',
    status: 'merged',
    title: 'Active decision',
    content: 'Active content',
    rationale: 'Active rationale',
    revision: 1,
    current_revision_id: `decision-${id}-r1`,
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
  };
}

function makePending(id: string): DecisionInboxEntryDto {
  return {
    id,
    agent_name: 'Architect',
    slug: 'scope-cut',
    type: 'scope',
    title: 'Cut the export feature',
    content: 'Defer CSV export to a later milestone.',
    rationale: 'Keeps the milestone shippable.',
    status: 'pending',
    created_at: '2026-02-02T00:00:00Z',
    updated_at: '2026-02-02T00:00:00Z',
  };
}

// Like makePending, but with a unique title per id so pagination tests can assert exactly
// which entries are visible on a given page.
function makeNumberedPending(id: string, index: number): DecisionInboxEntryDto {
  return {
    id,
    agent_name: 'Architect',
    slug: `scope-cut-${index}`,
    type: 'scope',
    title: `Proposal ${index}`,
    content: `Pending proposal number ${index}.`,
    rationale: undefined,
    status: 'pending',
    created_at: '2026-02-02T00:00:00Z',
    updated_at: '2026-02-02T00:00:00Z',
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  cleanup();
});

function makeSession(id: string, over?: Partial<SessionHistoryDto>): SessionHistoryDto {
  return {
    id,
    session_id: `session-${id}`,
    focus_area: 'Investigate UI refresh',
    active_issues: JSON.stringify(['#316']),
    summary: 'Updated the frontend to surface paginated history.',
    serialized_state: null,
    started_at: '2026-03-10T12:00:00Z',
    ended_at: '2026-03-10T12:15:00Z',
    ...over,
  };
}

function makeMemory(id: string, over?: Partial<AgentMemoryDto>): AgentMemoryDto {
  return {
    id,
    agent_name: 'Smith',
    type: 'learning',
    importance: 'medium',
    content: 'Before update',
    status: 'active',
    revision: 1,
    current_revision_id: `memory-${id}-r1`,
    created_at: '2026-09-23T00:00:00Z',
    updated_at: '2026-09-23T00:00:00Z',
    ...over,
  };
}

function makeMemoryRevision(revision: number): AgentMemoryRevisionDto {
  return {
    revision_id: `memory-42-r${revision}`,
    memory_id: 42,
    revision,
    actor: 'Smith',
    reason: 'updated',
    agent_name: 'Smith',
    type: 'learning',
    importance: 'medium',
    content: `Revision content ${revision}`,
    status: 'active',
    trust_state: 'pending',
    created_at: '2026-09-23T00:00:00Z',
  };
}

describe('MemoriesPage — Decisions tab', () => {
  it('renders a Proposed section for pending inbox entries', async () => {
    vi.mocked(apiClient.getDecisions).mockResolvedValue(page([makeActive('d1')]));
    vi.mocked(apiClient.getDecisionsInbox).mockResolvedValue(page([makePending('p1')]));

    renderPage();

    await waitFor(() => expect(screen.getByText('Pending proposals')).toBeTruthy());
    expect(screen.getByText('Cut the export feature')).toBeTruthy();
    expect(screen.getByText('Proposed')).toBeTruthy();
    expect(screen.getByText(/merge, promote, or reject/)).toBeTruthy();
    // Active decision still renders as the primary list.
    expect(screen.getByText('Active decision')).toBeTruthy();
  });

  it('does not render a Proposed section when the inbox is empty', async () => {
    vi.mocked(apiClient.getDecisions).mockResolvedValue(page([makeActive('d1')]));
    vi.mocked(apiClient.getDecisionsInbox).mockResolvedValue(page([]));

    renderPage();

    await waitFor(() => expect(screen.getByText('Active decision')).toBeTruthy());
    expect(screen.queryByText('Proposed — awaiting Coordinator')).toBeNull();
  });

  it('ignores non-pending inbox entries', async () => {
    vi.mocked(apiClient.getDecisions).mockResolvedValue(page([makeActive('d1')]));
    vi.mocked(apiClient.getDecisionsInbox).mockResolvedValue(page([
      { ...makePending('p1'), status: 'merged' },
    ]));

    renderPage();

    await waitFor(() => expect(screen.getByText('Active decision')).toBeTruthy());
    expect(screen.queryByText('Proposed — awaiting Coordinator')).toBeNull();
  });

  it('shows the combined empty state when both active and pending are empty', async () => {
    vi.mocked(apiClient.getDecisions).mockResolvedValue(page([]));
    vi.mocked(apiClient.getDecisionsInbox).mockResolvedValue(page([]));

    renderPage();

    await waitFor(() => expect(screen.getByText('No decisions recorded yet')).toBeTruthy());
  });

  it('pages pending proposals from the server instead of capping at a fixed snapshot', async () => {
    vi.mocked(apiClient.getDecisions).mockResolvedValue(page([]));
    const allPending = Array.from({ length: 30 }, (_, i) => makeNumberedPending(`p${i + 1}`, i + 1));
    vi.mocked(apiClient.getDecisionsInbox).mockImplementation(async (_projectId, options) => {
      const pageNumber = options?.page ?? 1;
      const pageSize = options?.pageSize ?? 25;
      const start = (pageNumber - 1) * pageSize;
      return pagedResult(allPending.slice(start, start + pageSize), pageNumber, pageSize, allPending.length);
    });

    renderPage();

    await waitFor(() => expect(screen.getByText('Proposal 1')).toBeTruthy());
    expect(screen.queryByText('Proposal 26')).toBeNull();
    // Total pending shown in the metric row reflects the full server-side count, not just
    // the current page's item count.
    expect(screen.getByText('30')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => expect(screen.getByText('Proposal 26')).toBeTruthy());
    expect(screen.queryByText('Proposal 1')).toBeNull();
    expect(
      vi.mocked(apiClient.getDecisionsInbox).mock.calls.some(([, options]) => options?.page === 2 && options?.pageSize === 25),
    ).toBe(true);
  });

  it('resets to page 1 and refetches when the pending-proposals page size changes', async () => {
    vi.mocked(apiClient.getDecisions).mockResolvedValue(page([]));
    const allPending = Array.from({ length: 30 }, (_, i) => makeNumberedPending(`p${i + 1}`, i + 1));
    vi.mocked(apiClient.getDecisionsInbox).mockImplementation(async (_projectId, options) => {
      const pageNumber = options?.page ?? 1;
      const pageSize = options?.pageSize ?? 25;
      const start = (pageNumber - 1) * pageSize;
      return pagedResult(allPending.slice(start, start + pageSize), pageNumber, pageSize, allPending.length);
    });

    renderPage();

    await waitFor(() => expect(screen.getByText('Proposal 1')).toBeTruthy());

    fireEvent.click(screen.getByRole('combobox', { name: 'Rows per page' }));
    fireEvent.click(await screen.findByRole('option', { name: '10 / page' }));

    await waitFor(() =>
      expect(
        vi.mocked(apiClient.getDecisionsInbox).mock.calls.some(([, options]) => options?.page === 1 && options?.pageSize === 10),
      ).toBe(true),
    );
    // Only 10 items should render on the reset first page at the new page size.
    expect(screen.getByText('Proposal 10')).toBeTruthy();
    expect(screen.queryByText('Proposal 11')).toBeNull();
  });

  it('falls back to the last valid pending-proposals page after removing the only item on page 2', async () => {
    vi.mocked(apiClient.getDecisions).mockResolvedValue(page([]));
    let allPending = Array.from({ length: 26 }, (_, i) => makeNumberedPending(`p${i + 1}`, i + 1));
    vi.mocked(apiClient.getDecisionsInbox).mockImplementation(async (_projectId, options) => {
      const pageNumber = options?.page ?? 1;
      const pageSize = options?.pageSize ?? 25;
      const start = (pageNumber - 1) * pageSize;
      return pagedResult(allPending.slice(start, start + pageSize), pageNumber, pageSize, allPending.length);
    });
    vi.mocked(apiClient.rejectDecisionInboxEntry).mockImplementation(async (_projectId, entryId) => {
      allPending = allPending.filter(entry => entry.id !== entryId);
    });

    renderPage();

    await waitFor(() => expect(screen.getByText('Proposal 1')).toBeTruthy());

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => expect(screen.getByText('Proposal 26')).toBeTruthy());
    expect(screen.queryByText('Proposal 1')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Reject' }));

    await waitFor(() => expect(screen.getByText('Proposal 1')).toBeTruthy());
    expect(screen.queryByText('Proposal 26')).toBeNull();
    expect(screen.getByText('Pending proposals')).toBeTruthy();
    expect(
      vi.mocked(apiClient.getDecisionsInbox).mock.calls.some(([, options]) => options?.page === 1 && options?.pageSize === 25),
    ).toBe(true);
  });
});

describe('MemoriesPage — Agent memory tab', () => {
  beforeEach(() => {
    vi.mocked(apiClient.getDecisions).mockResolvedValue(page([]));
    vi.mocked(apiClient.getDecisionsInbox).mockResolvedValue(page([]));
    vi.mocked(apiClient.getProjectSessions).mockResolvedValue(page([]));
  });

  it('updates memory and reads the durable value back after refresh', async () => {
    let stored = makeMemory('42');
    vi.mocked(apiClient.getProjectMemory).mockImplementation(async () => page([stored]));
    vi.mocked(apiClient.updateAgentMemory).mockImplementation(async (_projectId, _agentName, _memoryId, body) => {
      stored = { ...stored, ...body, updated_at: '2026-09-23T00:01:00Z' };
      return stored;
    });

    const first = renderPage();
    fireEvent.click(screen.getByRole('tab', { name: 'Agent memory' }));
    await waitFor(() => expect(screen.getByText('Before update')).toBeTruthy());

    fireEvent.click(screen.getByRole('button', { name: 'Update' }));
    fireEvent.change(screen.getByDisplayValue('Before update'), { target: { value: 'After update' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(apiClient.updateAgentMemory).toHaveBeenCalledWith(
      'proj-001',
      'Smith',
      '42',
      {
        expected_revision: 1,
        type: 'learning',
        content: 'After update',
        reason: 'Updated from the Memories page',
      },
    ));
    await waitFor(() => expect(screen.getByText('After update')).toBeTruthy());

    first.unmount();
    renderPage();
    fireEvent.click(screen.getByRole('tab', { name: 'Agent memory' }));
    await waitFor(() => expect(screen.getByText('After update')).toBeTruthy());
  });

  it('loads later revision-history pages', async () => {
    vi.mocked(apiClient.getProjectMemory).mockResolvedValue(page([makeMemory('42')]));
    vi.mocked(apiClient.getAgentMemoryRevisions).mockImplementation(
      async (_projectId, _agentName, _memoryId, options) => {
        const pageNumber = options?.page ?? 1;
        return pagedResult(
          [makeMemoryRevision(pageNumber === 1 ? 101 : 76)],
          pageNumber,
          25,
          101,
        );
      },
    );

    renderPage();
    fireEvent.click(screen.getByRole('tab', { name: 'Agent memory' }));
    await waitFor(() => expect(screen.getByText('Before update')).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: 'History' }));
    await waitFor(() => expect(screen.getByText('Revision 101')).toBeTruthy());

    fireEvent.click(screen.getByRole('button', { name: 'Load older revisions' }));

    await waitFor(() => expect(screen.getByText('Revision 76')).toBeTruthy());
    expect(apiClient.getAgentMemoryRevisions).toHaveBeenCalledWith(
      'proj-001', 'Smith', '42', { page: 2, pageSize: 25 });
  });
});

describe('MemoriesPage — Session history tab', () => {
  beforeEach(() => {
    vi.mocked(apiClient.getDecisions).mockResolvedValue(page([]));
    vi.mocked(apiClient.getDecisionsInbox).mockResolvedValue(page([]));
    vi.mocked(apiClient.getProjectMemory).mockResolvedValue(page([]));
  });

  it('renders paginated session history from the backend', async () => {
    const allSessions = Array.from({ length: 28 }, (_, index) =>
      makeSession(`s${index + 1}`, {
        session_id: `session-${index + 1}`,
        focus_area: `Focus area ${index + 1}`,
      }),
    );
    vi.mocked(apiClient.getProjectSessions).mockImplementation(async (_projectId, options) => {
      const pageNumber = options?.page ?? 1;
      const pageSize = options?.pageSize ?? 25;
      const start = (pageNumber - 1) * pageSize;
      return pagedResult(allSessions.slice(start, start + pageSize), pageNumber, pageSize, allSessions.length);
    });

    renderPage();
    fireEvent.click(screen.getByRole('tab', { name: 'Session history' }));

    await waitFor(() => expect(screen.getByText('Focus area 1')).toBeTruthy());
    expect(screen.queryByText('Focus area 26')).toBeNull();
    expect(screen.getAllByText('Active issues: #316').length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => expect(screen.getByText('Focus area 26')).toBeTruthy());
    expect(screen.queryByText('Focus area 1')).toBeNull();
    expect(
      vi.mocked(apiClient.getProjectSessions).mock.calls.some(([, options]) => options?.page === 2 && options?.pageSize === 25),
    ).toBe(true);
  });

  it('shows an empty state when there is no session history', async () => {
    vi.mocked(apiClient.getProjectSessions).mockResolvedValue(page([]));

    renderPage();
    fireEvent.click(screen.getByRole('tab', { name: 'Session history' }));

    await waitFor(() => expect(screen.getByText('No session history yet')).toBeTruthy());
  });
});
