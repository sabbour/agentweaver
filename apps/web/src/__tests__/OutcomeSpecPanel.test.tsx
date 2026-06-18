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

const awaitingSpec: OutcomeSpec = {
  status: 'awaiting_confirmation',
  goal: 'Ship the feature',
  desiredOutcome: 'A working feature',
};

const confirmedSpec: OutcomeSpec = { ...awaitingSpec, status: 'confirmed', confirmedBy: 'Ahmed' };

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
});
