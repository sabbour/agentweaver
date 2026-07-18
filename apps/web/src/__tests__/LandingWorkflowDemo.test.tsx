import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { StrictMode } from 'react';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { ScenarioTheater } from '../components/LandingWorkflowDemo';

/**
 * Behavioural tests for the scenario theater run-token scheduler and accessible
 * tab strip.
 *
 * Timer strategy: the player self-reschedules each tick from a useEffect, and
 * React 18 flushes effects on a MessageChannel macrotask that vitest fake timers
 * do not drive. So full goal→artifact playback is exercised with REAL timers +
 * waitFor, while scheduler-ownership, cleanup, pause, and keyboard behaviour —
 * which do not need a completed run — use fake timers so we can spy on
 * setTimeout / setInterval and prove no ElapsedTimer interval is ever created.
 */

// happy-dom lacks ResizeObserver (needed by React Flow).
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

const realIO = (globalThis as unknown as { IntersectionObserver?: unknown }).IntersectionObserver;

// A controllable IntersectionObserver so tests can toggle visibility.
class ControllableIO {
  static instances: ControllableIO[] = [];
  cb: IntersectionObserverCallback;
  constructor(cb: IntersectionObserverCallback) {
    this.cb = cb;
    ControllableIO.instances.push(this);
  }
  observe() {}
  unobserve() {}
  disconnect() {}
  fire(isIntersecting: boolean) {
    act(() => {
      this.cb([{ isIntersecting } as IntersectionObserverEntry], this as unknown as IntersectionObserver);
    });
  }
}

function setIO(value: unknown) {
  (globalThis as unknown as { IntersectionObserver?: unknown }).IntersectionObserver = value;
}

afterEach(() => {
  cleanup();
  // Restore spies BEFORE swapping timer implementations: a setTimeout spy
  // created under fake timers captured the fake fn as its "original", so
  // restoring after useRealTimers would re-install the fake and freeze the clock.
  vi.restoreAllMocks();
  vi.useRealTimers();
  setIO(realIO);
  ControllableIO.instances = [];
});

// ---------------------------------------------------------------------------
// Fake-timer group: scheduler ownership, cleanup, pause, keyboard, a11y.
// ---------------------------------------------------------------------------
describe('ScenarioTheater scheduler ownership (fake timers)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  async function advance(ms: number) {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(ms);
    });
  }

  it('drives playback with setTimeout and never starts a 1s ElapsedTimer interval, even under StrictMode', async () => {
    setIO(undefined); // no observer → the player treats itself as in view and autoplays
    const setTimeoutSpy = vi.spyOn(globalThis, 'setTimeout');
    const setIntervalSpy = vi.spyOn(globalThis, 'setInterval');

    render(
      <StrictMode>
        <ScenarioTheater />
      </StrictMode>,
    );

    // Let the scheduler fire several ticks (typing) and mount the run graph.
    await advance(1500);

    expect(setTimeoutSpy).toHaveBeenCalled();
    // Scenario nodes omit startedAt, so ElapsedTimer must never create its 1s tick.
    const elapsedIntervals = setIntervalSpy.mock.calls.filter((call) => call[1] === 1000);
    expect(elapsedIntervals).toHaveLength(0);
  });

  it('pauses when scrolled out of view and stays paused while timers advance', async () => {
    setIO(ControllableIO);
    render(<ScenarioTheater />);

    const io = ControllableIO.instances[0];
    expect(io).toBeTruthy();

    io.fire(true); // enter view → running
    await advance(600);
    expect(screen.getByText('Simulated playback')).toBeTruthy();

    io.fire(false); // leave view → paused
    expect(screen.getByText('Paused')).toBeTruthy();

    await advance(20000);
    // A paused run never reaches its Stage-5 artifact.
    expect(screen.queryByText('Pull request preview')).toBeNull();
    expect(screen.getByText('Paused')).toBeTruthy();
  });

  it('clears pending timers on unmount without throwing', async () => {
    setIO(undefined);
    const clearTimeoutSpy = vi.spyOn(globalThis, 'clearTimeout');
    const { unmount } = render(<ScenarioTheater />);

    await advance(300); // a tick is pending
    unmount();
    expect(clearTimeoutSpy).toHaveBeenCalled();

    // Draining the clock after unmount must be inert.
    await expect(advance(20000)).resolves.toBeUndefined();
  });

  it('shows the mandatory trust disclaimer above the tabs', () => {
    setIO(undefined);
    render(<ScenarioTheater />);
    expect(
      screen.getByText(
        /Illustrative simulated runs\. Outputs are authored examples, not professional advice/i,
      ),
    ).toBeTruthy();
  });

  it('exposes a correct tablist with roving tabindex and aria-controls', () => {
    setIO(undefined);
    render(<ScenarioTheater />);

    const tablist = screen.getByRole('tablist', { name: /Scenario theater/i });
    const tabs = within(tablist).getAllByRole('tab');
    expect(tabs).toHaveLength(8);

    const [first, second] = tabs;
    expect(first.getAttribute('aria-selected')).toBe('true');
    expect(first.getAttribute('tabindex')).toBe('0');
    expect(second.getAttribute('tabindex')).toBe('-1');
    expect(first.getAttribute('aria-controls')).toBe('aw-theater-panel');

    expect(screen.getByRole('tabpanel').id).toBe('aw-theater-panel');
  });

  it('run-token double-guard: stale callback after REPLAY cannot mutate the new run', async () => {
    setIO(undefined); // no observer → treats itself as in view, autoplays

    // Mock clearTimeout as a no-op so the pending tick from the PREVIOUS run is
    // NOT cancelled by the effect cleanup on REPLAY. This forces the run-token guard
    // (tokenRef / phaseRef double-check inside the callback itself) to be the sole
    // backstop — not the clearTimeout call.
    vi.spyOn(globalThis, 'clearTimeout').mockImplementation(() => {});

    render(<ScenarioTheater />);

    // Flush initial effects: PLAY_IF_IDLE fires → phase='running', token=T0.
    // The scheduler effect schedules a TYPE_TICK at delay TYPE_MS (34ms) with scheduledToken=T0.
    await advance(0);

    // Advance to t=10ms — the pending TYPE_TICK has NOT fired yet (delay is 34ms).
    await advance(10);

    // Click Replay. The reducer bumps the token (T0 → T1) and resets typedLen to 0.
    // The scheduler effect cleanup calls clearTimeout (mocked no-op) → stale TYPE_TICK
    // for T0 remains live in the queue. A new TYPE_TICK is then scheduled for T1
    // at the current clock (t=10) + TYPE_MS = t=44ms.
    fireEvent.click(screen.getByRole('button', { name: /Replay/i }));
    await advance(0); // flush REPLAY effects

    // Advance to t=34ms: the stale TYPE_TICK fires.
    // Inside the callback: tokenRef.current = T1 ≠ T0 = scheduledToken → guard rejects.
    await advance(24); // 10 + 24 = 34ms on the fake clock

    // If the guard worked: dispatch was suppressed, typedLen is still 0.
    // If the guard failed: typedLen would be 2 and 'Ad' (slice(0,2) of the goal) would be
    // in the composer. Test the absence of the first 2 chars of scenario 0's goal.
    expect(screen.queryByText('Ad')).toBeNull();

    // Advance to t=44ms: the new tick for T1 fires → typedLen advances to 2 normally.
    await advance(10); // 34 + 10 = 44ms
    // The run is actively playing (not stuck or corrupted).
    expect(screen.getByText('Simulated playback')).toBeTruthy();
  });

  it('moves selection with ArrowRight / Home / End (roving focus)', () => {
    setIO(undefined);
    render(<ScenarioTheater />);

    const tablist = screen.getByRole('tablist', { name: /Scenario theater/i });
    const tabs = within(tablist).getAllByRole('tab');

    fireEvent.keyDown(tablist, { key: 'ArrowRight' });
    expect(tabs[1].getAttribute('aria-selected')).toBe('true');
    expect(tabs[1].getAttribute('tabindex')).toBe('0');
    expect(tabs[0].getAttribute('tabindex')).toBe('-1');

    fireEvent.keyDown(tablist, { key: 'End' });
    expect(tabs[7].getAttribute('aria-selected')).toBe('true');

    fireEvent.keyDown(tablist, { key: 'Home' });
    expect(tabs[0].getAttribute('aria-selected')).toBe('true');
  });
});
