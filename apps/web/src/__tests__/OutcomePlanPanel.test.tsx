import userEvent from '@testing-library/user-event';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { OutcomePlanPanel } from '../components/OutcomePlanPanel';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { OutcomeSpec } from '../api/types';
import type { ReactNode } from 'react';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    getOutcomeSpec: vi.fn(),
    confirmOutcomeSpec: vi.fn(),
    reviseOutcomeSpec: vi.fn(),
    decomposeSpec: vi.fn(),
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

const staleAwaitingEvent = {
  sequence: 1,
  type: 'coordinator.outcome_spec',
  payload: {
    status: 'awaiting_confirmation',
    goal: 'Ship the feature',
    desiredOutcome: 'A working feature',
  },
} as const;

const awaitingSpec: OutcomeSpec = {
  status: 'awaiting_confirmation',
  goal: 'Ship the feature',
  desiredOutcome: 'A working feature',
};

const confirmedSpec: OutcomeSpec = { ...awaitingSpec, status: 'confirmed', confirmedBy: 'Ahmed' };

// A spec whose Open questions arrive crammed into one string ("1. … 2. …").
const specWithQuestions: OutcomeSpec = {
  status: 'awaiting_confirmation',
  goal: 'Add color endpoints',
  desiredOutcome: 'Two endpoints',
  clarifyingQuestions: ['1. What should the exact URL paths be? 2. Should GET /colors remain as-is?'],
};

const gateArmingError = () =>
  new ApiError(409, JSON.stringify({ error: 'no_pending_gate', message: 'The Outcome plan is not awaiting confirmation.' }));

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getOutcomeSpec).mockResolvedValue(awaitingSpec);
});

afterEach(() => {
  vi.useRealTimers();
  cleanup();
});

describe('OutcomePlanPanel confirm retry', () => {
  it('retries a 409 no_pending_gate gate-arming race then confirms without surfacing the error', async () => {
    // The gate may still be arming after a revise re-draft: first two confirms 409, third succeeds.
    vi.mocked(apiClient.confirmOutcomeSpec)
      .mockRejectedValueOnce(gateArmingError())
      .mockRejectedValueOnce(gateArmingError())
      .mockResolvedValueOnce(confirmedSpec);

    render(
      <Wrapper>
        <OutcomePlanPanel runId="run-1" events={[]} streamStatus="streaming" />
      </Wrapper>,
    );

    const confirmButton = await screen.findByRole('button', { name: /confirm/i });
    await userEvent.click(confirmButton);

    await waitFor(
      () => expect(document.body.textContent).toContain('Outcome plan confirmed'),
      { timeout: 4000 },
    );

    expect(vi.mocked(apiClient.confirmOutcomeSpec)).toHaveBeenCalledTimes(3);
    expect(document.body.textContent).not.toContain('no_pending_gate');
    expect(document.body.textContent).not.toContain('API error 409');
  });

  it('disables actions while confirm is pending and then shows confirmed state', async () => {
    let resolveConfirm: (value: OutcomeSpec) => void = () => {};
    vi.mocked(apiClient.confirmOutcomeSpec).mockImplementation(
      () => new Promise<OutcomeSpec>((resolve) => { resolveConfirm = resolve; }),
    );

    render(
      <Wrapper>
        <OutcomePlanPanel runId="run-1" events={[]} streamStatus="streaming" />
      </Wrapper>,
    );

    const confirmButton = await screen.findByRole('button', { name: /confirm plan/i });
    await userEvent.click(confirmButton);

    expect((screen.getByRole('button', { name: /confirming/i }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: /Clarify plan/i }) as HTMLButtonElement).disabled).toBe(true);
    expect(vi.mocked(apiClient.confirmOutcomeSpec)).toHaveBeenCalledTimes(1);

    resolveConfirm(confirmedSpec);

    await waitFor(() => expect(screen.getByText('Confirmed')).toBeTruthy());
    expect(screen.queryByRole('button', { name: /confirm plan/i })).toBeNull();
  });

  it('re-enables confirm after a non-conflict API error', async () => {
    vi.mocked(apiClient.confirmOutcomeSpec).mockRejectedValue(new ApiError(500, 'server exploded'));

    render(
      <Wrapper>
        <OutcomePlanPanel runId="run-1" events={[]} streamStatus="streaming" />
      </Wrapper>,
    );

    await userEvent.click(await screen.findByRole('button', { name: /confirm plan/i }));

    await waitFor(() => expect(screen.getByText(/API error 500: server exploded/i)).toBeTruthy());
    expect((screen.getByRole('button', { name: /confirm plan/i }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole('button', { name: /Clarify plan/i }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('keeps the REST confirmed status when stale SSE still says awaiting confirmation after confirm', async () => {
    vi.mocked(apiClient.confirmOutcomeSpec).mockResolvedValue(confirmedSpec);
    const onReconnect = vi.fn();

    render(
      <Wrapper>
        <OutcomePlanPanel
          runId="run-1"
          events={[staleAwaitingEvent]}
          streamStatus="streaming"
          onReconnect={onReconnect}
        />
      </Wrapper>,
    );

    await userEvent.click(await screen.findByRole('button', { name: /confirm plan/i }));

    await waitFor(() => expect(screen.getByText('Confirmed')).toBeTruthy());
    expect(screen.getByText(/Outcome plan confirmed by Ahmed/i)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /confirm plan/i })).toBeNull();
    expect(onReconnect).toHaveBeenCalledTimes(1);
  });

  it('shows the updated interrupted message for run_not_active failures', async () => {
    vi.mocked(apiClient.confirmOutcomeSpec).mockRejectedValue(
      new ApiError(409, JSON.stringify({ error: 'run_not_active' })),
    );

    render(
      <Wrapper>
        <OutcomePlanPanel runId="run-1" events={[]} streamStatus="streaming" />
      </Wrapper>,
    );

    await userEvent.click(await screen.findByRole('button', { name: /confirm plan/i }));

    await waitFor(() =>
      expect(screen.getByText('This run is no longer active, so the Outcome plan cannot be confirmed.')).toBeTruthy(),
    );
  });
});

describe('OutcomePlanPanel clarify dialog', () => {
  it('splits crammed Open questions into separate answer fields and composes Q/A feedback', async () => {
    vi.mocked(apiClient.getOutcomeSpec).mockResolvedValue(specWithQuestions);
    vi.mocked(apiClient.reviseOutcomeSpec).mockResolvedValue({ ...specWithQuestions, status: 'drafting' });

    render(
      <Wrapper>
        <OutcomePlanPanel runId="run-1" events={[]} streamStatus="streaming" />
      </Wrapper>,
    );

    const openBtn = await screen.findByRole('button', { name: /Clarify plan/i });
    await userEvent.click(openBtn);

    // Each open question gets its own answer box (2) plus the additional-feedback box (1).
    const boxes = await screen.findAllByRole('textbox', { hidden: true });
    expect(boxes.length).toBe(3);

    await userEvent.type(boxes[0], 'Use /colors/grayscale and /colors/color');
    await userEvent.type(boxes[1], 'Keep it as-is');

    await userEvent.click(screen.getByRole('button', { name: /^send$/i, hidden: true }));

    await waitFor(() => expect(vi.mocked(apiClient.reviseOutcomeSpec)).toHaveBeenCalledTimes(1));
    const composed = vi.mocked(apiClient.reviseOutcomeSpec).mock.calls[0][1] as string;
    expect(composed).toContain('Q: What should the exact URL paths be?');
    expect(composed).toContain('A: Use /colors/grayscale and /colors/color');
    expect(composed).toContain('Q: Should GET /colors remain as-is?');
    expect(composed).toContain('A: Keep it as-is');
  });
});

describe('OutcomePlanPanel drafting state and polling', () => {
  it('treats a 404 for getOutcomeSpec as pending drafting and surfaces no error to the user', async () => {
    vi.mocked(apiClient.getOutcomeSpec).mockRejectedValue(new ApiError(404, 'not found'));

    const { queryByText } = render(
      <Wrapper>
        <OutcomePlanPanel runId="run-1" events={[]} streamStatus="streaming" />
      </Wrapper>,
    );

    await waitFor(() => expect(vi.mocked(apiClient.getOutcomeSpec)).toHaveBeenCalled());

    expect(screen.getByText(/Drafting the Outcome plan/i)).toBeTruthy();
    expect(queryByText(/API error/i)).toBeNull();
    expect(queryByText(/404/)).toBeNull();
  });

  it('polls after a 404 until the drafted Outcome plan is available', async () => {
    vi.mocked(apiClient.getOutcomeSpec)
      .mockRejectedValueOnce(new ApiError(404, 'not found'))
      .mockResolvedValue(awaitingSpec);

    render(
      <Wrapper>
        <OutcomePlanPanel runId="run-1" events={[]} streamStatus="streaming" />
      </Wrapper>,
    );

    await waitFor(() => expect(vi.mocked(apiClient.getOutcomeSpec)).toHaveBeenCalledTimes(2), { timeout: 3500 });
    expect(screen.getByText('Ship the feature')).toBeTruthy();
  });

  it('shows a terminal error instead of an infinite spinner if the run fails before drafting', async () => {
    vi.mocked(apiClient.getOutcomeSpec).mockRejectedValue(new ApiError(404, 'not found'));

    render(
      <Wrapper>
        <OutcomePlanPanel runId="run-1" events={[]} streamStatus="done" runStatus="failed" />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByText(/failed before the Outcome plan could be drafted/i)).toBeTruthy());
    expect(screen.queryByText(/Drafting the Outcome plan/i)).toBeNull();
  });
});

describe('OutcomePlanPanel terminal REST status precedence', () => {
  it('shows declined from the REST snapshot even when the latest SSE spec event is awaiting confirmation', async () => {
    vi.mocked(apiClient.getOutcomeSpec).mockResolvedValue({ ...awaitingSpec, status: 'declined' });

    render(
      <Wrapper>
        <OutcomePlanPanel runId="run-1" events={[staleAwaitingEvent]} streamStatus="streaming" />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByText('Declined')).toBeTruthy());
    expect(screen.getByText(/Outcome plan declined/i)).toBeTruthy();
  });
});

describe('OutcomePlanPanel Break into tasks visibility', () => {
  it('shows "Break into tasks" for a confirmed spec during pre-dispatch authoring', async () => {
    vi.mocked(apiClient.getOutcomeSpec).mockResolvedValue(confirmedSpec);

    render(
      <Wrapper>
        <OutcomePlanPanel runId="run-1" projectId="proj-1" events={[]} streamStatus="streaming" />
      </Wrapper>,
    );

    expect(await screen.findByRole('button', { name: /break into tasks/i })).toBeTruthy();
  });

  it('hides "Break into tasks" once the run has been decomposed / dispatched', async () => {
    vi.mocked(apiClient.getOutcomeSpec).mockResolvedValue(confirmedSpec);

    render(
      <Wrapper>
        <OutcomePlanPanel runId="run-1" projectId="proj-1" events={[]} streamStatus="streaming" dispatched />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByText(/Outcome plan confirmed/i)).toBeTruthy());
    expect(screen.queryByRole('button', { name: /break into tasks/i })).toBeNull();
    // The read-only plan content stays available.
    expect(screen.getByText('Ship the feature')).toBeTruthy();
  });
});

