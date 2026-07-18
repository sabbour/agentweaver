import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { ScenarioTheater } from '../components/LandingWorkflowDemo';

/**
 * Full goal→OutcomeSpec→plan→dispatch→artifact playback, driven by the player's
 * real self-scheduling timers.
 *
 * This lives in its own file (with no fake timers anywhere) on purpose: React 18
 * flushes effects on a MessageChannel macrotask that vitest fake timers do not
 * drive, and mixing fake-timer spies with real-timer playback in one module
 * leaks a frozen clock across tests. A dedicated real-timer module keeps the
 * self-rescheduling run progressing deterministically.
 */

// React 18 flushes effects on a MessageChannel macrotask, so testing-library's
// waitFor (which wraps polls in async-act) starves the run. This raw poll yields
// to the macrotask queue between checks so the scheduler chain advances.
async function waitForText(text: string | RegExp, timeout = 20000) {
  const start = Date.now();
  while (Date.now() - start < timeout) {
    if (screen.queryByText(text)) return;
    await new Promise((r) => setTimeout(r, 100));
  }
  throw new Error(`Timed out after ${timeout}ms waiting for text: ${String(text)}`);
}

// happy-dom lacks ResizeObserver (needed by React Flow).
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

const realIO = (globalThis as unknown as { IntersectionObserver?: unknown }).IntersectionObserver;

// With no IntersectionObserver the player treats itself as in view and autoplays.
function disableIO() {
  (globalThis as unknown as { IntersectionObserver?: unknown }).IntersectionObserver = undefined;
}

afterEach(() => {
  cleanup();
  (globalThis as unknown as { IntersectionObserver?: unknown }).IntersectionObserver = realIO;
});

describe('ScenarioTheater playback', () => {
  it('autoplays through goal typing to the pull-request artifact', async () => {
    disableIO();
    render(<ScenarioTheater />);

    await waitForText('Illustrative output');
    expect(screen.getByText('Pull request preview')).toBeTruthy();
    expect(screen.queryByText(/appears here once the run/i)).toBeNull();
  }, 25000);

  it('resets to a fresh run and replays when another tab is selected', async () => {
    disableIO();
    render(<ScenarioTheater />);

    await waitForText('Pull request preview');

    fireEvent.click(screen.getByRole('tab', { name: /Marketing site/i }));

    // The prior artifact is gone immediately and the new scenario resets to typing.
    expect(screen.queryByText('Pull request preview')).toBeNull();
    expect(screen.getByText('Design a marketing launch page')).toBeTruthy();
    expect(screen.getByText(/appears here once the run/i)).toBeTruthy();

    // The fresh run plays through to the marketing artifact.
    await waitForText('Landing page preview');
  }, 45000);

  it('Replay restarts a completed run', async () => {
    disableIO();
    render(<ScenarioTheater />);

    await waitForText('Pull request preview');

    fireEvent.click(screen.getByRole('button', { name: /Replay/i }));
    // Replay clears the artifact and restarts from typing.
    expect(screen.queryByText('Pull request preview')).toBeNull();
    expect(screen.getByText(/appears here once the run/i)).toBeTruthy();

    await waitForText('Pull request preview');
  }, 45000);
});
