import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { ObservabilityAgentsPage } from '../pages/observability/ObservabilityAgentsPage';
import { ObservabilityTracesPage } from '../pages/observability/ObservabilityTracesPage';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';

const transactionTracePanelSpy = vi.hoisted(() => vi.fn());
const agentTokenBreakdownSpy = vi.hoisted(() => vi.fn());

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getProject: vi.fn(),
    listProjectRuns: vi.fn(),
    getTeam: vi.fn(),
    getProjectMetrics: vi.fn(),
  },
}));

vi.mock('../components/runs/TransactionTracePanel', () => ({
  TransactionTracePanel: (props: unknown) => {
    transactionTracePanelSpy(props);
    return <div data-testid="transaction-trace-panel" />;
  },
}));

vi.mock('../components/runs/AgentTokenBreakdown', () => ({
  AgentTokenBreakdown: (props: unknown) => {
    agentTokenBreakdownSpy(props);
    return <div data-testid="agent-token-breakdown" />;
  },
}));

function Wrapper({
  initialEntry,
  path,
  children,
}: {
  initialEntry: string;
  path: string;
  children: ReactNode;
}) {
  return (
    <AzureFluentProvider density="compact">
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route path={path} element={children} />
        </Routes>
      </MemoryRouter>
    </AzureFluentProvider>
  );
}

beforeEach(() => {
  vi.useFakeTimers();
  vi.clearAllMocks();
  vi.mocked(apiClient.getProject).mockResolvedValue({
    project_id: 'p1',
    name: 'Silver Pancake',
    origin: 'blank',
    source_repository: null,
    working_directory: '',
    default_branch: 'main',
    owner: 'tester',
    default_provider: 'github-copilot',
    default_model_github_copilot: null,
    default_model_microsoft_foundry: null,
    available: true,
    state: 'active',
    created_at: '2026-07-29T00:00:00.000Z',
    updated_at: '2026-07-29T00:00:00.000Z',
  });
  vi.mocked(apiClient.getTeam).mockResolvedValue({
    project_name: 'Silver Pancake',
    universe: 'harry-potter',
    members: [
      {
        name: 'Harry',
        role_title: 'Backend Dev',
        charter_path: '',
        status: 'active',
        default_model: 'gpt-5',
        is_named: true,
        is_built_in: false,
      },
      {
        name: 'Hermione',
        role_title: 'Writer',
        charter_path: '',
        status: 'active',
        default_model: 'gpt-5',
        is_named: true,
        is_built_in: false,
      },
    ],
    layout: 'canonical',
    migration_available: false,
  });
});

afterEach(() => {
  vi.runOnlyPendingTimers();
  vi.useRealTimers();
});

describe('observability pages', () => {
  it('passes project team role titles into TransactionTracePanel on the traces page', async () => {
    vi.mocked(apiClient.listProjectRuns).mockResolvedValue({
      items: [
        {
          workflow_run_id: 'coord-run-1',
          execution_id: 'coord-run-1',
          task: 'Trace coordinator flow',
          agent_name: 'Coordinator',
          status: 'in_progress',
          coordinator_status: 'dispatching',
          started_at: '2026-07-29T00:00:00.000Z',
        },
      ],
      page: 1,
      page_size: 100,
      total_count: 1,
      total_pages: 1,
    });

    render(
      <Wrapper initialEntry="/projects/p1/observability/traces" path="/projects/:projectId/observability/traces">
        <ObservabilityTracesPage />
      </Wrapper>,
    );

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Preview trace' }));
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(350);
    });

    expect(transactionTracePanelSpy).toHaveBeenCalledWith(expect.objectContaining({
      runId: 'coord-run-1',
      roleByAgent: {
        Harry: 'Backend Dev',
        Hermione: 'Writer',
      },
    }));
  });

  it('passes project team role titles into AgentTokenBreakdown on the agents page', async () => {
    vi.mocked(apiClient.getProjectMetrics).mockResolvedValue({
      throughput: [],
      leaderboard: [],
      agentBreakdown: [
        { agentName: 'Harry', invocationCount: 2, totalTokens: 120, totalNanoAiu: 3000 },
      ],
    });

    render(
      <Wrapper initialEntry="/projects/p1/observability/agents" path="/projects/:projectId/observability/agents">
        <ObservabilityAgentsPage />
      </Wrapper>,
    );

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(agentTokenBreakdownSpy).toHaveBeenLastCalledWith(expect.objectContaining({
      roleByAgent: {
        Harry: 'Backend Dev',
        Hermione: 'Writer',
      },
    }));
  });
});
