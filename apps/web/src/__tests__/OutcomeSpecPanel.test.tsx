import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import { OutcomeSpecPanel } from '../components/OutcomeSpecPanel';
import { ApiError } from '../api/client';
import type { OutcomeSpec } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getOutcomeSpec: vi.fn(),
    confirmOutcomeSpec: vi.fn(),
    reviseOutcomeSpec: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
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

// A spec whose clarifying questions arrive crammed into one string ("1. … 2. …").
const specWithQuestions: OutcomeSpec = {
  status: 'awaiting_confirmation',
  goal: 'Add color endpoints',
  desiredOutcome: 'Two endpoints',
  clarifyingQuestions: ['1. What should the exact URL paths be? 2. Should GET /colors remain as-is?'],
};

const gateArmingError = () =>
  new ApiError(409, JSON.stringify({ error: 'no_pending_gate', message: 'The outcome spec is not awaiting confirmation.' }));

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getOutcomeSpec).mockResolvedValue(awaitingSpec);
});

afterEach(() => cleanup());

describe('OutcomeSpecPanel confirm retry', () => {
  it('retries a 409 no_pending_gate gate-arming race then confirms without surfacing the error', async () => {
    // The gate may still be arming after a revise re-draft: first two confirms 409, third succeeds.
    vi.mocked(apiClient.confirmOutcomeSpec)
      .mockRejectedValueOnce(gateArmingError())
      .mockRejectedValueOnce(gateArmingError())
      .mockResolvedValueOnce(confirmedSpec);

    render(
      <Wrapper>
        <OutcomeSpecPanel runId="run-1" events={[]} streamStatus="streaming" />
      </Wrapper>,
    );

    const confirmButton = await screen.findByRole('button', { name: /confirm/i });
    await userEvent.click(confirmButton);

    await waitFor(
      () => expect(document.body.textContent).toContain('Outcome spec confirmed'),
      { timeout: 4000 },
    );

    expect(vi.mocked(apiClient.confirmOutcomeSpec)).toHaveBeenCalledTimes(3);
    expect(document.body.textContent).not.toContain('no_pending_gate');
    expect(document.body.textContent).not.toContain('API error 409');
  });

  it('keeps the REST confirmed status when stale SSE still says awaiting confirmation after confirm', async () => {
    vi.mocked(apiClient.confirmOutcomeSpec).mockResolvedValue(confirmedSpec);
    const onReconnect = vi.fn();

    render(
      <Wrapper>
        <OutcomeSpecPanel
          runId="run-1"
          events={[staleAwaitingEvent]}
          streamStatus="streaming"
          onReconnect={onReconnect}
        />
      </Wrapper>,
    );

    await userEvent.click(await screen.findByRole('button', { name: /^confirm$/i }));

    await waitFor(() => expect(screen.getByText('Confirmed')).toBeTruthy());
    expect(screen.getByText(/Outcome spec confirmed by Ahmed/i)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^confirm$/i })).toBeNull();
    expect(onReconnect).toHaveBeenCalledTimes(1);
  });
});

describe('OutcomeSpecPanel clarify dialog', () => {
  it('splits crammed clarifying questions into separate answer fields and composes Q/A feedback', async () => {
    vi.mocked(apiClient.getOutcomeSpec).mockResolvedValue(specWithQuestions);
    vi.mocked(apiClient.reviseOutcomeSpec).mockResolvedValue({ ...specWithQuestions, status: 'drafting' });

    render(
      <Wrapper>
        <OutcomeSpecPanel runId="run-1" events={[]} streamStatus="streaming" />
      </Wrapper>,
    );

    const openBtn = await screen.findByRole('button', { name: /clarify and request changes/i });
    await userEvent.click(openBtn);

    // Each clarifying question gets its own answer box (2) plus the additional-feedback box (1).
    const boxes = await screen.findAllByRole('textbox');
    expect(boxes.length).toBe(3);

    await userEvent.type(boxes[0], 'Use /colors/grayscale and /colors/color');
    await userEvent.type(boxes[1], 'Keep it as-is');

    await userEvent.click(screen.getByRole('button', { name: /^send$/i }));

    await waitFor(() => expect(vi.mocked(apiClient.reviseOutcomeSpec)).toHaveBeenCalledTimes(1));
    const composed = vi.mocked(apiClient.reviseOutcomeSpec).mock.calls[0][1] as string;
    expect(composed).toContain('Q: What should the exact URL paths be?');
    expect(composed).toContain('A: Use /colors/grayscale and /colors/color');
    expect(composed).toContain('Q: Should GET /colors remain as-is?');
    expect(composed).toContain('A: Keep it as-is');
  });
});

describe('OutcomeSpecPanel — 404 suppression', () => {
  it('treats a 404 for getOutcomeSpec as expected absence and surfaces no error to the user', async () => {
    // A 404 before the coordinator drafts the spec is expected — the stream will fill in later.
    // The component must not display an error message for this case.
    vi.mocked(apiClient.getOutcomeSpec).mockRejectedValue(new ApiError(404, 'not found'));

    const { queryByText } = render(
      <Wrapper>
        <OutcomeSpecPanel runId="run-1" events={[]} streamStatus="streaming" />
      </Wrapper>,
    );

    await waitFor(() => expect(vi.mocked(apiClient.getOutcomeSpec)).toHaveBeenCalled());

    // No API error text should appear in the UI — 404 is a silent expected absence.
    expect(queryByText(/API error/i)).toBeNull();
    expect(queryByText(/404/)).toBeNull();
  });

  it('does not retry getOutcomeSpec when streamStatus becomes done after a 404', async () => {
    // If the initial fetch returned 404, the component must not re-fetch when the SSE stream
    // closes (streamStatus=done). Repeated fetches would produce repeated 404 network errors
    // in the browser console without delivering any new information.
    vi.mocked(apiClient.getOutcomeSpec).mockRejectedValue(new ApiError(404, 'not found'));

    const { rerender } = render(
      <Wrapper>
        <OutcomeSpecPanel runId="run-1" events={[]} streamStatus="streaming" />
      </Wrapper>,
    );

    // Wait for the initial mount fetch to complete.
    await waitFor(() => expect(vi.mocked(apiClient.getOutcomeSpec)).toHaveBeenCalledTimes(1));

    // Simulate the SSE stream closing (streamStatus changes to 'done').
    rerender(
      <Wrapper>
        <OutcomeSpecPanel runId="run-1" events={[]} streamStatus="done" />
      </Wrapper>,
    );

    // Allow any pending effects to settle.
    await new Promise((resolve) => setTimeout(resolve, 100));

    // getOutcomeSpec must NOT be called again — the 404 flag prevents the streamStatus=done retry.
    expect(vi.mocked(apiClient.getOutcomeSpec)).toHaveBeenCalledTimes(1);
  });
});

describe('OutcomeSpecPanel terminal REST status precedence', () => {
  it('shows declined from the REST snapshot even when the latest SSE spec event is awaiting confirmation', async () => {
    vi.mocked(apiClient.getOutcomeSpec).mockResolvedValue({ ...awaitingSpec, status: 'declined' });

    render(
      <Wrapper>
        <OutcomeSpecPanel runId="run-1" events={[staleAwaitingEvent]} streamStatus="streaming" />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByText('Declined')).toBeTruthy());
    expect(screen.getByText(/Outcome spec declined/i)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^confirm$/i })).toBeNull();
  });
});
