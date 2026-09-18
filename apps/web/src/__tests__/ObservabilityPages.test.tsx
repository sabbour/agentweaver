import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
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
    getRunTerminalDiagnostic: vi.fn(),
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
    blueprint_generation_model: null,
    workflow_generation_model: null,
    outcome_spec_generation_model: null,
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
        name: 'Hermione',
        role_title: 'Writer',
        charter_path: '',
        status: 'active',
        default_model: 'gpt-5',
        is_named: true,
        is_built_in: false,
      },
    ],
    retired_members: [
      {
        name: 'Harry',
        role_title: 'Backend Dev',
        charter_path: '',
        status: 'retired',
        default_model: 'gpt-5',
        is_named: true,
        is_built_in: false,
      },
      {
        name: 'Hermione',
        role_title: 'Former Writer',
        charter_path: '',
        status: 'retired',
        default_model: 'gpt-5',
        is_named: true,
        is_built_in: false,
      },
    ],
    layout: 'canonical',
    migration_available: false,
  });
  vi.mocked(apiClient.getRunTerminalDiagnostic).mockRejectedValue(new ApiError(404, 'not found'));
});

afterEach(() => {
  vi.runOnlyPendingTimers();
  vi.useRealTimers();
});

async function renderTracesPage(
  items: Awaited<ReturnType<typeof apiClient.listProjectRuns>>['items'],
  initialEntry = '/projects/p1/observability/traces',
) {
  vi.mocked(apiClient.listProjectRuns).mockResolvedValue({
    items,
    page: 1,
    page_size: 100,
    total_count: items.length,
    total_pages: 1,
  });

  render(
    <Wrapper initialEntry={initialEntry} path="/projects/:projectId/observability/traces">
      <ObservabilityTracesPage />
    </Wrapper>,
  );

  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
  });
}

describe('observability pages', () => {
  it('passes project team role titles into TransactionTracePanel on the traces page', async () => {
    await renderTracesPage([
        {
          workflow_run_id: 'coord-run-1',
          execution_id: 'coord-run-1',
          task: 'Trace coordinator flow',
          agent_name: 'Coordinator',
          status: 'in_progress',
          coordinator_status: 'dispatching',
          started_at: '2026-07-29T00:00:00.000Z',
        },
      ]);

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Preview trace' }));
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(350);
    });

    expect(transactionTracePanelSpy).toHaveBeenCalledWith(expect.objectContaining({
      runId: 'coord-run-1',
      roleByAgent: {
        Hermione: 'Writer',
      },
    }));
  });

  it('renders recent coordinator runs newest-first and computes Latest as the max started_at', async () => {
    await renderTracesPage([
        {
          workflow_run_id: 'coord-run-newest',
          execution_id: 'coord-run-newest',
          task: 'Most recent run',
          agent_name: 'Coordinator',
          status: 'in_progress',
          coordinator_status: 'dispatching',
          started_at: '2026-08-24T00:00:00.000Z',
        },
        {
          workflow_run_id: 'coord-run-oldest',
          execution_id: 'coord-run-oldest',
          task: 'Older run',
          agent_name: 'Coordinator',
          status: 'complete',
          coordinator_status: 'complete',
          started_at: '2026-08-21T00:00:00.000Z',
        },
      ]);

    // "/runs" already returns newest-first — the page must not reverse that order.
    const taskLabels = screen.getAllByText(/(Most recent run|Older run)/).map((el) => el.textContent);
    expect(taskLabels).toEqual(['Most recent run', 'Older run']);

    // "Latest" must reflect MAX(started_at) across all candidates, not the first/last item.
    expect(screen.getByText(new Date('2026-08-24T00:00:00.000Z').toLocaleDateString())).toBeDefined();
  });

  it('clamps a long trace prompt to two lines and expands without hiding card controls', async () => {
    const longPrompt = [
      'Audit the deployed orchestration from the coordinator through every agent handoff.',
      'Keep the status, run identity, start date, and trace actions visible while the prompt is collapsed.',
      'Report the complete evidence after inspecting the trace tree and terminal diagnostics.',
    ].join('\n\n');
    await renderTracesPage([{
        workflow_run_id: 'coord-run-long',
        execution_id: 'coord-run-long',
        task: longPrompt,
        agent_name: 'Coordinator',
        status: 'in_progress',
        coordinator_status: 'dispatching',
        started_at: '2026-09-17T20:00:00.000Z',
      }]);

    const promptBody = screen.getByTestId('trace-prompt-body-coord-run-long');
    Object.defineProperties(promptBody, {
      scrollHeight: { configurable: true, value: 60 },
      clientHeight: { configurable: true, value: 40 },
    });
    fireEvent(window, new Event('resize'));

    expect(promptBody.textContent).toBe(longPrompt);
    expect(promptBody.getAttribute('data-expanded')).toBe('false');
    expect(promptBody.getAttribute('data-collapsed-lines')).toBe('2');
    expect(screen.getByText('Coordinator trace')).toBeDefined();
    expect(screen.getByText('dispatching')).toBeDefined();
    expect(screen.getByText(`Started ${new Date('2026-09-17T20:00:00.000Z').toLocaleString()}`)).toBeDefined();
    expect(screen.getByText('Run coord-run-long')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Preview trace' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Open run' })).toBeDefined();

    fireEvent.click(screen.getByRole('button', { name: 'Show more trace prompt' }));

    expect(promptBody.textContent).toBe(longPrompt);
    expect(promptBody.getAttribute('data-expanded')).toBe('true');
    expect(promptBody.hasAttribute('data-collapsed-lines')).toBe(false);
    expect(screen.getByRole('button', { name: 'Show less trace prompt' })).toBeDefined();

    fireEvent.click(screen.getByRole('button', { name: 'Show less trace prompt' }));

    expect(promptBody.getAttribute('data-expanded')).toBe('false');
    expect(promptBody.getAttribute('data-collapsed-lines')).toBe('2');
  });

  it('does not show prompt disclosure for a short trace prompt', async () => {
    const shortPrompt = 'Inspect the latest coordinator trace.';
    await renderTracesPage([{
        workflow_run_id: 'coord-run-short',
        execution_id: 'coord-run-short',
        task: shortPrompt,
        agent_name: 'Coordinator',
        status: 'complete',
        coordinator_status: 'complete',
        started_at: '2026-09-17T21:00:00.000Z',
      }]);

    const promptBody = screen.getByTestId('trace-prompt-body-coord-run-short');
    expect(promptBody.textContent).toBe(shortPrompt);
    expect(promptBody.hasAttribute('data-collapsed-lines')).toBe(false);
    expect(screen.queryByRole('button', { name: 'Show more trace prompt' })).toBeNull();
  });

  it('shows prompt disclosure for a sub-160-character trace prompt that wraps beyond two lines at narrow width', async () => {
    const wrappedPrompt = 'Inspect every coordinator handoff, preserve the action metadata, and identify the exact stage where the distributed trace stops reporting progress.';
    expect(wrappedPrompt.length).toBeLessThan(160);
    await renderTracesPage([{
        workflow_run_id: 'coord-run-wrapped',
        execution_id: 'coord-run-wrapped',
        task: wrappedPrompt,
        agent_name: 'Coordinator',
        status: 'in_progress',
        coordinator_status: 'dispatching',
        started_at: '2026-09-17T22:00:00.000Z',
      }]);

    const promptBody = screen.getByTestId('trace-prompt-body-coord-run-wrapped');
    Object.defineProperties(promptBody, {
      scrollHeight: { configurable: true, value: 60 },
      clientHeight: { configurable: true, value: 40 },
    });
    fireEvent(window, new Event('resize'));

    expect(promptBody.textContent).toBe(wrappedPrompt);
    expect(promptBody.getAttribute('data-collapsed-lines')).toBe('2');
    expect(screen.getByRole('button', { name: 'Show more trace prompt' })).toBeDefined();
  });

  it('opens a focused trace directly from a ?run= deep link, e.g. from the run detail page', async () => {
    await renderTracesPage([
        {
          workflow_run_id: 'coord-run-1',
          execution_id: 'coord-run-1',
          task: 'Trace coordinator flow',
          agent_name: 'Coordinator',
          status: 'in_progress',
          coordinator_status: 'dispatching',
          started_at: '2026-07-29T00:00:00.000Z',
        },
      ], '/projects/p1/observability/traces?run=coord-run-1');

    await act(async () => {
      await vi.advanceTimersByTimeAsync(350);
    });

    expect(transactionTracePanelSpy).toHaveBeenCalledWith(expect.objectContaining({ runId: 'coord-run-1' }));
  });

  it('shows only the safe terminal diagnostic and exposes correlation trace links for failed runs', async () => {
    const secret = 'secret-not-in-ui-53de';
    const toolOutput = 'The deployment tool completed normally.';
    const jwt = 'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature';
    const githubToken = 'ghp_abcdefghijklmnopqrstuvwxyz0123456789';
    const azureKey = `${'A'.repeat(86)}==`;
    vi.mocked(apiClient.listProjectRuns).mockResolvedValue({
      items: [{
        workflow_run_id: 'coord-run-failed',
        execution_id: 'coord-run-failed',
        task: 'Failed coordinator flow',
        agent_name: 'Coordinator',
        status: 'failed',
        coordinator_status: 'failed',
        started_at: '2026-09-09T00:00:00.000Z',
      }],
      page: 1, page_size: 100, total_count: 1, total_pages: 1,
    });
    vi.mocked(apiClient.getRunTerminalDiagnostic).mockResolvedValue({
      code: 'agent_host_turn_incomplete',
      message: `${toolOutput} https://agentweaver.blob.core.windows.net/runs/log?sv=2025-01-05&ss=b&sp=rl&se=2030-01-01&sig=abc%2Bdef%3D`,
      component: 'agent_host',
      timestamp: '2026-09-09T00:01:00.000Z',
      retryable: true,
      correlation_ids: {
        correlation_id: '0f8fad5bd9cb469fa16570867728950e',
        request_id: githubToken,
        trace_id: azureKey,
      },
      cause_chain: ['IOException', 'https://operator:password@example.test/trace', 'at C:\\agent\\Worker.cs', jwt],
    });

    render(
      <Wrapper initialEntry="/projects/p1/observability/traces" path="/projects/:projectId/observability/traces">
        <ObservabilityTracesPage />
      </Wrapper>,
    );
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    const diagnostic = screen.getByTestId('trace-terminal-diagnostic-coord-run-failed');
    expect(diagnostic.textContent).toContain('Terminal failure · agent_host');
    expect(diagnostic.textContent).toContain("Run failed with code 'agent_host_turn_incomplete'. Retry is available.");
    expect(diagnostic.textContent).not.toContain('abc%2Bdef%3D');
    expect(diagnostic.textContent).not.toContain('Authorization');
    expect(diagnostic.textContent).not.toContain(secret);
    expect(screen.getByRole('link', { name: 'correlation_id: 0f8fad5bd9cb469fa16570867728950e' }).getAttribute('href'))
      .toBe('/projects/p1/observability/traces?run=coord-run-failed&correlation=0f8fad5bd9cb469fa16570867728950e');
    expect(diagnostic.textContent).not.toContain(jwt);
    expect(diagnostic.textContent).not.toContain(githubToken);
    expect(diagnostic.textContent).not.toContain(azureKey);
    expect(diagnostic.textContent).not.toContain('password');
    expect(diagnostic.textContent).not.toContain('Worker.cs');
    expect(diagnostic.textContent).not.toContain(toolOutput);

    fireEvent.click(screen.getByTestId('trace-failure-filter'));
    expect(screen.getByText('Show all traces')).toBeDefined();
  });

  it('passes active and retired role titles into AgentTokenBreakdown on the agents page', async () => {
    vi.mocked(apiClient.getProjectMetrics).mockResolvedValue({
      throughput: [],
      leaderboard: [],
      agentBreakdown: [
        { agentName: 'Harry', invocationCount: 2, totalTokens: 120, totalNanoAiu: 3000 },
        { agentName: 'Hermione', invocationCount: 1, totalTokens: 60, totalNanoAiu: 1000 },
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
