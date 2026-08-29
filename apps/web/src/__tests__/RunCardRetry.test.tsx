import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { RunCard } from '../components/board/RunCard';
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
import type { RunCardDto } from '../api/types';
import type { ReactNode } from 'react';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    retryRun: vi.fn(),
    deleteRun: vi.fn(),
    archiveRun: vi.fn(),
  },
}));

const mockNavigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => mockNavigate };
});

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <AzureFluentProvider density="compact">
      <MemoryRouter initialEntries={['/projects/proj-1/board']}>
        <Routes>
          <Route path="/projects/:projectId/board" element={<>{children}</>} />
          <Route path="/projects/:projectId/orchestrations/:runId" element={<div>Run detail</div>} />
        </Routes>
      </MemoryRouter>
    </AzureFluentProvider>
  );
}

function makeCard(overrides: Partial<RunCardDto> = {}): RunCardDto {
  return {
    kind: 'run',
    run_id: 'run-123',
    task: 'Do something',
    status: 'in_progress',
    stage_id: 'coordinator',
    started_at: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  mockNavigate.mockReset();
});

afterEach(() => {
  cleanup();
});

describe('RunCard — Retry button', () => {
  it('renders Retry button when status is "failed"', () => {
    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'failed' })} projectId="proj-1" />
      </Wrapper>,
    );
    expect(screen.getByTestId('run-card-retry')).toBeTruthy();
  });

  it('renders Retry button when status is "merge_failed"', () => {
    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'merge_failed' })} projectId="proj-1" />
      </Wrapper>,
    );
    expect(screen.getByTestId('run-card-retry')).toBeTruthy();
  });

  it('does NOT render Retry button for in_progress status', () => {
    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'in_progress' })} projectId="proj-1" />
      </Wrapper>,
    );
    expect(screen.queryByTestId('run-card-retry')).toBeNull();
  });

  it('does NOT render Retry button for completed status', () => {
    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'completed' })} projectId="proj-1" />
      </Wrapper>,
    );
    expect(screen.queryByTestId('run-card-retry')).toBeNull();
  });

  it('does NOT render Retry button for declined status', () => {
    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'declined' })} projectId="proj-1" />
      </Wrapper>,
    );
    expect(screen.queryByTestId('run-card-retry')).toBeNull();
  });

  it('clicking Retry calls apiClient.retryRun with the run_id and navigates to the new run', async () => {
    vi.mocked(apiClient.retryRun).mockResolvedValue({
      run_id: 'new-run-456',
      retried_from: 'run-123',
      status: 'in_progress',
    });

    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'failed' })} projectId="proj-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByTestId('run-card-retry'));

    await waitFor(() =>
      expect(vi.mocked(apiClient.retryRun)).toHaveBeenCalledWith('run-123'),
    );

    await waitFor(() =>
      expect(mockNavigate).toHaveBeenCalledWith('/projects/proj-1/orchestrations/new-run-456'),
    );
  });

  it('restores Retry and reports an API failure', async () => {
    vi.mocked(apiClient.retryRun).mockRejectedValue(new Error('GitHub Copilot connection is required'));

    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'failed' })} projectId="proj-1" />
      </Wrapper>,
    );

    const retry = screen.getByTestId('run-card-retry') as HTMLButtonElement;
    fireEvent.click(retry);

    await waitFor(() => expect(retry.disabled).toBe(false));
    expect(screen.getByText(/Retry failed: GitHub Copilot connection is required/)).toBeTruthy();
    expect(mockNavigate).not.toHaveBeenCalled();
  });
});

describe('RunCard — card navigation', () => {
  it('clicking the card navigates to the orchestration detail', () => {
    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'in_progress' })} projectId="proj-1" />
      </Wrapper>,
    );

    const card = screen.getByTestId('run-card-run-123');
    fireEvent.click(card);
    expect(mockNavigate).toHaveBeenCalledWith('/projects/proj-1/orchestrations/run-123');
  });

  describe('RunCard — archive action', () => {
    it('archives the run without triggering card navigation and calls onMutated', async () => {
      const onMutated = vi.fn();
      vi.mocked(apiClient.archiveRun).mockResolvedValue(undefined);

      render(
        <Wrapper>
          <RunCard card={makeCard({ status: 'completed' })} projectId="proj-1" onMutated={onMutated} />
        </Wrapper>,
      );

      fireEvent.click(screen.getByLabelText('Archive run'));

      await waitFor(() =>
        expect(vi.mocked(apiClient.archiveRun)).toHaveBeenCalledWith('run-123'),
      );
      await waitFor(() => expect(onMutated).toHaveBeenCalled());
      expect(mockNavigate).not.toHaveBeenCalled();
    });
  });

  it('the card container is a div, not an anchor (no nested anchor violation)', () => {
    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'in_progress' })} projectId="proj-1" />
      </Wrapper>,
    );
    const card = screen.getByTestId('run-card-run-123');
    expect(card.tagName.toLowerCase()).toBe('div');
  });

  it('the "Retried from" inner link is a valid anchor inside the div container', () => {
    render(
      <Wrapper>
        <RunCard
          card={makeCard({ status: 'in_progress', retried_from: 'old-run-aabbccdd' })}
          projectId="proj-1"
        />
      </Wrapper>,
    );
    const card = screen.getByTestId('run-card-run-123');
    const innerAnchor = card.querySelector('a');
    expect(innerAnchor).toBeTruthy();
    // The card itself is not an anchor, so there is no nested <a> inside <a>.
    expect(card.tagName.toLowerCase()).toBe('div');
  });

  it('clicking the "Retried from" link does NOT trigger card navigation', () => {
    render(
      <Wrapper>
        <RunCard
          card={makeCard({ status: 'in_progress', retried_from: 'old-run-aabbccdd' })}
          projectId="proj-1"
        />
      </Wrapper>,
    );
    const card = screen.getByTestId('run-card-run-123');
    const innerAnchor = card.querySelector('a')!;
    fireEvent.click(innerAnchor);
    // Card navigation must not fire (stopPropagation on the inner link)
    expect(mockNavigate).not.toHaveBeenCalledWith('/projects/proj-1/orchestrations/run-123');
  });
});

describe('RunCard — long task text truncation', () => {
  it('clamps a long, multi-paragraph task prompt to a few lines instead of rendering it in full', () => {
    const longTask = Array.from({ length: 20 }, (_, i) => `Paragraph ${i + 1} of a very long task prompt.`).join('\n\n');

    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'failed', task: longTask })} projectId="proj-1" />
      </Wrapper>,
    );

    const taskEl = screen.getByTestId('run-card-task');
    // Griffel (makeStyles) injects the clamp declarations via an atomic CSS class rather than an
    // inline style, so assert against the generated stylesheet rule instead of computed style
    // (jsdom's getComputedStyle doesn't resolve rules from injected <style> tags).
    const taskClass = [...taskEl.classList].find((c) => c !== undefined) ? taskEl.className : '';
    let foundClamp = false;
    for (const sheet of Array.from(document.styleSheets)) {
      for (const rule of Array.from((sheet as CSSStyleSheet).cssRules ?? [])) {
        const cssText = (rule as CSSStyleRule).cssText ?? '';
        if (cssText.includes('-webkit-line-clamp: 3') || cssText.includes('-webkit-line-clamp:3')) {
          foundClamp = true;
        }
      }
    }
    expect(taskClass.length > 0).toBe(true);
    expect(foundClamp).toBe(true);
  });

  it('exposes the full task text via the title attribute for hover/focus access', () => {
    const longTask = 'A very long task prompt. '.repeat(50);

    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'failed', task: longTask })} projectId="proj-1" />
      </Wrapper>,
    );

    const taskEl = screen.getByTestId('run-card-task');
    expect(taskEl.getAttribute('title')).toBe(longTask);
  });

  it('renders the "(coordinator run)" fallback with a matching title when task is empty', () => {
    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'failed', task: '' })} projectId="proj-1" />
      </Wrapper>,
    );

    const taskEl = screen.getByTestId('run-card-task');
    expect(taskEl.textContent).toBe('(coordinator run)');
    expect(taskEl.getAttribute('title')).toBe('(coordinator run)');
  });

  it('renders short task text normally without visual regression', () => {
    render(
      <Wrapper>
        <RunCard card={makeCard({ status: 'failed', task: 'Fix bug' })} projectId="proj-1" />
      </Wrapper>,
    );

    const taskEl = screen.getByTestId('run-card-task');
    expect(taskEl.textContent).toBe('Fix bug');
    expect(taskEl.getAttribute('title')).toBe('Fix bug');
  });
});
