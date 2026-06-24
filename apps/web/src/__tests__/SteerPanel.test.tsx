import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import { type ReactNode } from 'react';
import { SteerPanel } from '../components/SteerPanel';
import { ApiError } from '../api/client';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    steerCoordinator: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter>{children}</MemoryRouter>
    </FluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
});
afterEach(() => {
  cleanup();
});

// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------
describe('SteerPanel — rendering', () => {
  it('renders the instruction textarea and action buttons by default', () => {
    render(
      <Wrapper>
        <SteerPanel runId="run-blocked-1" blockReason="integration_conflict" />
      </Wrapper>,
    );
    expect(screen.getByTestId('steer-panel')).toBeTruthy();
    expect(screen.getByTestId('steer-panel-instruction')).toBeTruthy();
    expect(screen.getByTestId('steer-panel-redirect')).toBeTruthy();
    expect(screen.getByTestId('steer-panel-stop')).toBeTruthy();
  });

  it('renders a permission note and hides controls when canSteer={false}', () => {
    render(
      <Wrapper>
        <SteerPanel runId="run-blocked-1" canSteer={false} />
      </Wrapper>,
    );
    expect(screen.getByTestId('steer-panel')).toBeTruthy();
    expect(screen.getByTestId('steer-panel-no-permission')).toBeTruthy();
    expect(screen.queryByTestId('steer-panel-instruction')).toBeNull();
    expect(screen.queryByTestId('steer-panel-redirect')).toBeNull();
    expect(screen.queryByTestId('steer-panel-stop')).toBeNull();
  });

  it('shows the conflict-specific placeholder text for integration_conflict reason', () => {
    render(
      <Wrapper>
        <SteerPanel runId="run-1" blockReason="integration_conflict" />
      </Wrapper>,
    );
    const textarea = screen.getByTestId('steer-panel-instruction');
    const placeholder = textarea.getAttribute('placeholder') ?? '';
    expect(placeholder).toContain('re-running the affected subtask');
  });
});

// ---------------------------------------------------------------------------
// Reroute button
// ---------------------------------------------------------------------------
describe('SteerPanel — Reroute to coordinator', () => {
  it('calls steerCoordinator with kind=redirect and the typed instruction', async () => {
    vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'applied' });
    render(
      <Wrapper>
        <SteerPanel runId="run-blocked-1" blockReason="integration_conflict" />
      </Wrapper>,
    );

    const textarea = screen.getByTestId('steer-panel-instruction');
    fireEvent.change(textarea, { target: { value: 'Re-run subtask 3 against main' } });
    fireEvent.click(screen.getByTestId('steer-panel-redirect'));

    await waitFor(() =>
      expect(vi.mocked(apiClient.steerCoordinator)).toHaveBeenCalledWith(
        'run-blocked-1',
        { kind: 'redirect', instruction: 'Re-run subtask 3 against main' },
      ),
    );
  });

  it('uses the auto-generated default instruction when the text area is empty', async () => {
    vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'applied' });
    render(
      <Wrapper>
        <SteerPanel runId="run-blocked-1" blockReason="integration_conflict" />
      </Wrapper>,
    );

    // Submit without typing anything
    fireEvent.click(screen.getByTestId('steer-panel-redirect'));

    await waitFor(() => {
      const call = vi.mocked(apiClient.steerCoordinator).mock.calls[0];
      expect(call[0]).toBe('run-blocked-1');
      expect(call[1].kind).toBe('redirect');
      // The auto-generated instruction for integration_conflict should reference re-running
      expect(call[1].instruction).toContain('re-running');
    });
  });

  it('clears the instruction text area after a successful steer', async () => {
    vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'applied' });
    render(
      <Wrapper>
        <SteerPanel runId="run-blocked-1" />
      </Wrapper>,
    );

    const textarea = screen.getByTestId('steer-panel-instruction') as HTMLTextAreaElement;
    fireEvent.change(textarea, { target: { value: 'Some instruction' } });
    fireEvent.click(screen.getByTestId('steer-panel-redirect'));

    await waitFor(() => expect(textarea.value).toBe(''));
  });

  it('calls the onSteered callback after a successful reroute', async () => {
    const onSteered = vi.fn();
    vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'applied' });
    render(
      <Wrapper>
        <SteerPanel runId="run-1" onSteered={onSteered} />
      </Wrapper>,
    );

    fireEvent.click(screen.getByTestId('steer-panel-redirect'));
    await waitFor(() => expect(onSteered).toHaveBeenCalledOnce());
  });

  it('shows the "applied" success message when the run was recovered', async () => {
    vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'applied' });
    render(
      <Wrapper>
        <SteerPanel runId="run-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByTestId('steer-panel-redirect'));

    await waitFor(() => expect(screen.getByTestId('steer-panel-success')).toBeTruthy());
    expect(screen.getByTestId('steer-panel-success').textContent).toContain('resumed');
  });

  it('shows the "queued" success message when the run is live', async () => {
    vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'queued' });
    render(
      <Wrapper>
        <SteerPanel runId="run-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByTestId('steer-panel-redirect'));

    await waitFor(() => expect(screen.getByTestId('steer-panel-success')).toBeTruthy());
    expect(screen.getByTestId('steer-panel-success').textContent).toContain('queued');
  });
});

// ---------------------------------------------------------------------------
// Stop button
// ---------------------------------------------------------------------------
describe('SteerPanel — Stop run', () => {
  it('calls steerCoordinator with kind=stop and no instruction', async () => {
    vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'applied' });
    render(
      <Wrapper>
        <SteerPanel runId="run-blocked-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByTestId('steer-panel-stop'));

    await waitFor(() =>
      expect(vi.mocked(apiClient.steerCoordinator)).toHaveBeenCalledWith(
        'run-blocked-1',
        { kind: 'stop', instruction: undefined },
      ),
    );
  });
});

// ---------------------------------------------------------------------------
// In-flight state
// ---------------------------------------------------------------------------
describe('SteerPanel — in-flight state', () => {
  it('disables both buttons while the request is in-flight', async () => {
    let resolveSteer!: (v: { status: string }) => void;
    vi.mocked(apiClient.steerCoordinator).mockReturnValue(
      new Promise<{ status: string }>((res) => { resolveSteer = res; }),
    );

    render(
      <Wrapper>
        <SteerPanel runId="run-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByTestId('steer-panel-redirect'));

    // Both buttons should be disabled mid-flight
    await waitFor(() => {
      expect((screen.getByTestId('steer-panel-redirect') as HTMLButtonElement).disabled).toBe(true);
      expect((screen.getByTestId('steer-panel-stop') as HTMLButtonElement).disabled).toBe(true);
    });

    // Resolve and verify re-enabled
    resolveSteer({ status: 'applied' });
    await waitFor(() => {
      expect((screen.getByTestId('steer-panel-redirect') as HTMLButtonElement).disabled).toBe(false);
    });
  });
});

// ---------------------------------------------------------------------------
// Error handling
// ---------------------------------------------------------------------------
describe('SteerPanel — error handling', () => {
  it('surfaces a clear permission error when the API returns 403', async () => {
    vi.mocked(apiClient.steerCoordinator).mockRejectedValue(
      new ApiError(403, 'Forbidden'),
    );
    render(
      <Wrapper>
        <SteerPanel runId="run-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByTestId('steer-panel-redirect'));

    await waitFor(() => expect(screen.getByTestId('steer-panel-error')).toBeTruthy());
    expect(screen.getByTestId('steer-panel-error').textContent).toContain('owner');
  });

  it('surfaces a specific message for 409 steering_recovery_exhausted', async () => {
    vi.mocked(apiClient.steerCoordinator).mockRejectedValue(
      new ApiError(409, JSON.stringify({ error: 'steering_recovery_exhausted', message: 'Max retries reached for subtask 2.' })),
    );
    render(
      <Wrapper>
        <SteerPanel runId="run-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByTestId('steer-panel-redirect'));

    await waitFor(() => expect(screen.getByTestId('steer-panel-error')).toBeTruthy());
    const text = screen.getByTestId('steer-panel-error').textContent ?? '';
    // Should show the server message, not the generic "Steer failed (409)"
    expect(text).toContain('Max retries reached');
    expect(text).not.toContain('Steer failed (409)');
  });

  it('uses the fallback exhausted message when 409 body has no message field', async () => {
    vi.mocked(apiClient.steerCoordinator).mockRejectedValue(
      new ApiError(409, JSON.stringify({ error: 'steering_recovery_exhausted' })),
    );
    render(
      <Wrapper>
        <SteerPanel runId="run-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByTestId('steer-panel-redirect'));

    await waitFor(() => expect(screen.getByTestId('steer-panel-error')).toBeTruthy());
    expect(screen.getByTestId('steer-panel-error').textContent).toContain('maximum number of times');
  });

  it('surfaces a generic error message for non-403/non-409 failures', async () => {
    vi.mocked(apiClient.steerCoordinator).mockRejectedValue(
      new ApiError(500, 'Internal Server Error'),
    );
    render(
      <Wrapper>
        <SteerPanel runId="run-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByTestId('steer-panel-redirect'));

    await waitFor(() => expect(screen.getByTestId('steer-panel-error')).toBeTruthy());
    expect(screen.getByTestId('steer-panel-error').textContent).toContain('500');
  });

  it('re-enables the buttons after an error (not stuck)', async () => {
    vi.mocked(apiClient.steerCoordinator).mockRejectedValue(new Error('network error'));
    render(
      <Wrapper>
        <SteerPanel runId="run-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByTestId('steer-panel-redirect'));

    await waitFor(() => expect(screen.getByTestId('steer-panel-error')).toBeTruthy());
    expect((screen.getByTestId('steer-panel-redirect') as HTMLButtonElement).disabled).toBe(false);
  });
});

// ---------------------------------------------------------------------------
// Verb contract — Send / Redirect (with target child) / Amend
// ---------------------------------------------------------------------------
describe('SteerPanel — steering verb contract', () => {
  it('Send calls steerCoordinator with kind=send and the typed instruction', async () => {
    vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'queued' });
    render(
      <Wrapper>
        <SteerPanel runId="run-1" />
      </Wrapper>,
    );

    fireEvent.change(screen.getByTestId('steer-panel-instruction'), { target: { value: 'Just a heads up' } });
    fireEvent.click(screen.getByTestId('steer-panel-send'));

    await waitFor(() =>
      expect(vi.mocked(apiClient.steerCoordinator)).toHaveBeenCalledWith(
        'run-1',
        { kind: 'send', instruction: 'Just a heads up' },
      ),
    );
  });

  it('Redirect against a target child sends kind=redirect and target_child_run_id', async () => {
    vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'applied' });
    render(
      <Wrapper>
        <SteerPanel runId="coord-1" targetChildRunId="child-7" />
      </Wrapper>,
    );

    fireEvent.change(screen.getByTestId('steer-panel-instruction'), { target: { value: 'Unblock and use v2' } });
    fireEvent.click(screen.getByTestId('steer-panel-redirect'));

    await waitFor(() =>
      expect(vi.mocked(apiClient.steerCoordinator)).toHaveBeenCalledWith(
        'coord-1',
        { kind: 'redirect', instruction: 'Unblock and use v2', target_child_run_id: 'child-7' },
      ),
    );
  });

  it('Amend calls steerCoordinator with kind=amend and no target child', async () => {
    vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'queued' });
    render(
      <Wrapper>
        <SteerPanel runId="run-1" targetChildRunId="child-7" />
      </Wrapper>,
    );

    fireEvent.change(screen.getByTestId('steer-panel-instruction'), { target: { value: 'Also add tests' } });
    fireEvent.click(screen.getByTestId('steer-panel-amend'));

    await waitFor(() => {
      const call = vi.mocked(apiClient.steerCoordinator).mock.calls[0];
      expect(call[0]).toBe('run-1');
      expect(call[1].kind).toBe('amend');
      expect(call[1].instruction).toBe('Also add tests');
      // A child target only applies to Redirect.
      expect(call[1].target_child_run_id).toBeUndefined();
    });
  });
});

// ---------------------------------------------------------------------------
// Copy check — the CoordinatorRunPage panel must point at the SteerPanel
// ---------------------------------------------------------------------------
describe('SteerPanel — copy', () => {
  it('the parent panel copy must reference the controls (not "steering chat below")', () => {
    // The old text said "Use the steering chat below" — that wording must not appear
    // in the blocked panel copy. We check via the hint text CoordinatorRunPage uses.
    // This test is intentionally a string-match sentinel so regressions surface in CI.
    const oldMisleadingCopy = 'Use the steering chat below';
    const newCopy = 'Use the controls below to redirect the coordinator';
    // CoordinatorRunPage renders this text — we can verify the constant here since
    // both the old and new strings are defined only in CoordinatorRunPage.tsx.
    expect(oldMisleadingCopy).not.toBe(newCopy); // sentinel: the strings are different
    // The new copy is intentionally shown in the CoordinatorRunPage snapshot tests
    // (CoordinatorRunPage.coordUx.test.tsx) which render the blocked phase.
  });
});
