import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { StrictMode } from 'react';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import {
  ARTIFACT_HOLD_MS,
  ScenarioTheater,
} from '../components/LandingWorkflowDemo';
import {
  initialRunState,
  nextScenarioId,
  runReducer,
  type RunState,
} from '../components/LandingWorkflowDemo.state';
import { SCENARIOS } from '../components/landing/scenarios';

/**
 * Behavioural tests for the scenario theater run-token scheduler, auto-advancing
 * carousel, and accessible tab strip.
 *
 * Timer strategy: the player self-reschedules each tick from a useEffect;
 * scheduler-ownership, cleanup, carousel advance, out-of-view suspension, and
 * keyboard behaviour are driven with fake timers + vi.advanceTimersByTimeAsync
 * (which flushes the effects between ticks). Full real-timer playback lives in
 * LandingWorkflowPlayback.test.tsx.
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
// Pure reducer: deterministic transitions (no DOM, no timers).
// ---------------------------------------------------------------------------
describe('runReducer transitions', () => {
  it('starts idle on scenario 0 and only ever reaches phase idle or running', () => {
    const s0 = initialRunState();
    expect(s0.phase).toBe('idle');
    expect(s0.activeId).toBe(SCENARIOS[0].id);

    const actions = [
      { type: 'PLAY_IF_IDLE' as const },
      { type: 'TYPE_TICK' as const, goalLen: 10 },
      { type: 'ADVANCE' as const },
      { type: 'DISPATCH_TICK' as const },
      { type: 'ADVANCE_SCENARIO' as const },
      { type: 'SELECT' as const, id: SCENARIOS[3].id },
      { type: 'REPLAY' as const },
    ];
    let s: RunState = s0;
    for (const a of actions) {
      s = runReducer(s, a);
      // There is no 'paused' (or 'complete') phase anywhere in the machine.
      expect(['idle', 'running']).toContain(s.phase);
    }
  });

  it('SELECT starts the chosen scenario immediately from typing (running)', () => {
    const s = runReducer(initialRunState(), { type: 'SELECT', id: SCENARIOS[4].id });
    expect(s.activeId).toBe(SCENARIOS[4].id);
    expect(s.phase).toBe('running');
    expect(s.stage).toBe(0);
    expect(s.typedLen).toBe(0);
  });

  it('REPLAY restarts the current scenario from typing and bumps the token', () => {
    const running: RunState = { ...initialRunState(), phase: 'running', stage: 4, typedLen: 40, token: 7 };
    const s = runReducer(running, { type: 'REPLAY' });
    expect(s.activeId).toBe(running.activeId);
    expect(s.stage).toBe(0);
    expect(s.typedLen).toBe(0);
    expect(s.phase).toBe('running');
    expect(s.token).toBe(8);
  });

  it('ADVANCE_SCENARIO advances to the next scenario and wraps 8 → 1', () => {
    const onLast: RunState = { ...initialRunState(), activeId: SCENARIOS[SCENARIOS.length - 1].id };
    const wrapped = runReducer(onLast, { type: 'ADVANCE_SCENARIO' });
    expect(wrapped.activeId).toBe(SCENARIOS[0].id);
    expect(wrapped.phase).toBe('running');
    expect(wrapped.stage).toBe(0);

    const fromFirst = runReducer(initialRunState(), { type: 'ADVANCE_SCENARIO' });
    expect(fromFirst.activeId).toBe(SCENARIOS[1].id);
  });

  it('nextScenarioId wraps across all eight scenarios', () => {
    for (let i = 0; i < SCENARIOS.length; i += 1) {
      const expected = SCENARIOS[(i + 1) % SCENARIOS.length].id;
      expect(nextScenarioId(SCENARIOS[i].id)).toBe(expected);
    }
  });
});

// ---------------------------------------------------------------------------
// Fake-timer group: scheduler ownership, carousel, suspension, keyboard, a11y.
// ---------------------------------------------------------------------------
describe('ScenarioTheater scheduler & carousel (fake timers)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  async function advance(ms: number) {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(ms);
    });
  }

  async function advanceUntil(pred: () => boolean, maxMs = 40000, step = 250) {
    let elapsed = 0;
    while (elapsed < maxMs) {
      await advance(step);
      elapsed += step;
      if (pred()) return;
    }
    throw new Error('advanceUntil: condition never met');
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

  it('auto-advances the carousel to the next scenario after the artifact hold', async () => {
    setIO(undefined);
    render(<ScenarioTheater />);

    // Run scenario 0 through to its full-body artifact takeover.
    await advanceUntil(() => screen.queryByLabelText('Run artifact') !== null);
    expect(screen.getByText('Ship a product feature')).toBeTruthy();

    // Hold, then the carousel advances to scenario 1 and starts it (typing).
    await advance(ARTIFACT_HOLD_MS + 700);
    expect(screen.queryByLabelText('Run artifact')).toBeNull();
    expect(screen.getByText('Design a marketing launch page')).toBeTruthy();
  });

  it('silently suspends scheduling out of view and resumes on re-entry, never showing a paused label', async () => {
    setIO(ControllableIO);
    render(<ScenarioTheater />);

    const io = ControllableIO.instances[0];
    expect(io).toBeTruthy();

    io.fire(true); // enter view → running
    await advance(400); // typing begins

    io.fire(false); // leave view → scheduling suspends (no visible paused state)
    await advance(30000);
    // A suspended run makes no progress: it never reaches its artifact...
    expect(screen.queryByLabelText('Run artifact')).toBeNull();
    // ...and no "paused" affordance is ever shown.
    expect(screen.queryByText(/paused/i)).toBeNull();

    io.fire(true); // re-enter → resumes the same beat
    await advanceUntil(() => screen.queryByLabelText('Run artifact') !== null);
    expect(screen.getByLabelText('Run artifact')).toBeTruthy();
    expect(screen.queryByText(/paused/i)).toBeNull();
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

  it('renders none of the removed playback chrome or forbidden strings', async () => {
    setIO(undefined);
    render(<ScenarioTheater />);
    await advance(1200);

    // The full disclaimer and the per-artifact "Illustrative output" badge are both removed.
    expect(
      screen.queryByText(
        /Illustrative simulated runs\. Outputs are authored examples, not professional advice/i,
      ),
    ).toBeNull();
    // Simulated-playback badge, Paused state, and the visible stepper are gone.
    expect(screen.queryByText(/Simulated playback/i)).toBeNull();
    expect(screen.queryByText(/paused/i)).toBeNull();
    // The graph interaction hint is gone.
    expect(screen.queryByText(/Drag to pan/i)).toBeNull();
    // The phrase "scenario theater" must not appear in any visible copy or ARIA label.
    expect(screen.queryByText(/scenario theater/i)).toBeNull();
    expect(screen.queryByLabelText(/scenario theater/i)).toBeNull();
  });

  it('renders the run graph with no MiniMap, GraphControls, or node/card carousel controls', () => {
    setIO(undefined);
    const { container } = render(<ScenarioTheater />);

    expect(container.querySelector('.react-flow__minimap')).toBeNull();
    expect(container.querySelector('.react-flow__controls')).toBeNull();
    // No previous/next node or card controls survive on the fixed canvas.
    expect(screen.queryByRole('button', { name: /next|previous|zoom|fit/i })).toBeNull();
  });

  it('exposes a correct tablist with roving tabindex and aria-controls', () => {
    setIO(undefined);
    render(<ScenarioTheater />);

    const tablist = screen.getByRole('tablist', { name: /Example runs/i });
    const tabs = within(tablist).getAllByRole('tab');
    expect(tabs).toHaveLength(8);

    const [first, second] = tabs;
    expect(first.getAttribute('aria-selected')).toBe('true');
    expect(first.getAttribute('tabindex')).toBe('0');
    expect(second.getAttribute('tabindex')).toBe('-1');
    expect(first.getAttribute('aria-controls')).toBe('aw-theater-panel');

    expect(screen.getByRole('tabpanel').id).toBe('aw-theater-panel');
  });

  it('manual tab selection starts the chosen scenario immediately', async () => {
    setIO(undefined);
    render(<ScenarioTheater />);
    await advance(200);

    fireEvent.click(screen.getByRole('tab', { name: /Marketing site/i }));
    // The selected scenario's header is shown at once and its run is under way
    // (typing) — no idle wait, no paused state.
    expect(screen.getByText('Design a marketing launch page')).toBeTruthy();
    expect(screen.queryByLabelText('Run artifact')).toBeNull();
    expect(screen.getByLabelText('Run surface')).toBeTruthy();
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
    // The run is actively playing (composer shows the first two characters).
    expect(screen.getByText('Ad')).toBeTruthy();
  });

  it('moves selection with ArrowRight / Home / End (roving focus)', () => {
    setIO(undefined);
    render(<ScenarioTheater />);

    const tablist = screen.getByRole('tablist', { name: /Example runs/i });
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
