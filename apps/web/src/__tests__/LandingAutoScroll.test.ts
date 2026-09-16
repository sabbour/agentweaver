import { describe, expect, it, vi } from 'vitest';
import { autoScrollEase, autoScrollTop, runAutoScroll } from '../components/landing/autoScroll';

/**
 * Unit tests for the deterministic artifact auto-scroll math + animator. The
 * animator accepts injectable now/raf/caf so the rAF loop is driven by a fake
 * clock with no real frames — proving both that it travels 0 → max and that
 * cancel() halts the loop and releases the frame request (leak-free teardown).
 */

describe('autoScrollEase', () => {
  it('clamps to [0,1] and pins the endpoints', () => {
    expect(autoScrollEase(-1)).toBe(0);
    expect(autoScrollEase(0)).toBe(0);
    expect(autoScrollEase(1)).toBe(1);
    expect(autoScrollEase(2)).toBe(1);
  });

  it('is monotonically non-decreasing across the range', () => {
    let prev = -Infinity;
    for (let t = 0; t <= 1.0001; t += 0.05) {
      const v = autoScrollEase(t);
      expect(v).toBeGreaterThanOrEqual(prev);
      prev = v;
    }
  });
});

describe('autoScrollTop', () => {
  it('stays at 0 before the start delay and rests at full distance after travel', () => {
    expect(autoScrollTop(0, 1000, 2000, 500)).toBe(0);
    expect(autoScrollTop(400, 1000, 2000, 500)).toBe(0); // still inside the delay
    expect(autoScrollTop(500, 1000, 2000, 500)).toBe(0); // travel just beginning
    expect(autoScrollTop(2500, 1000, 2000, 500)).toBeCloseTo(1000, 5); // settled at bottom
  });

  it('is a no-op when there is nothing to scroll', () => {
    expect(autoScrollTop(1000, 0, 2000, 0)).toBe(0);
    expect(autoScrollTop(1000, -5, 2000, 0)).toBe(0);
  });
});

/** A minimal fake rAF clock so the loop advances deterministically. */
function makeFakeRaf() {
  let time = 0;
  const queue: Array<{ id: number; cb: (t: number) => void }> = [];
  let nextId = 1;
  return {
    now: () => time,
    raf: (cb: (t: number) => void) => {
      const id = nextId++;
      queue.push({ id, cb });
      return id;
    },
    caf: vi.fn((id: number) => {
      const idx = queue.findIndex((q) => q.id === id);
      if (idx >= 0) queue.splice(idx, 1);
    }),
    /** Advance the clock and run any queued frame callbacks once. */
    tick(dt: number) {
      time += dt;
      const pending = queue.splice(0, queue.length);
      for (const { cb } of pending) cb(time);
    },
    pending: () => queue.length,
  };
}

function fakeScrollEl(scrollHeight: number, clientHeight: number): HTMLElement {
  return { scrollHeight, clientHeight, scrollTop: 0 } as unknown as HTMLElement;
}

describe('runAutoScroll', () => {
  it('scrolls the element from top toward its maximum over the duration', () => {
    const clock = makeFakeRaf();
    const el = fakeScrollEl(1000, 400); // distance = 600
    runAutoScroll(el, { durationMs: 1000, startDelayMs: 0, now: clock.now, raf: clock.raf, caf: clock.caf });

    expect(el.scrollTop).toBe(0);
    clock.tick(250);
    const quarter = el.scrollTop;
    expect(quarter).toBeGreaterThan(0);
    clock.tick(750); // reach the end of travel
    expect(el.scrollTop).toBeCloseTo(600, 5);
    expect(el.scrollTop).toBeGreaterThan(quarter);
  });

  it('is inert (never schedules a frame) when the element cannot scroll', () => {
    const clock = makeFakeRaf();
    const el = fakeScrollEl(400, 400); // distance = 0
    const handle = runAutoScroll(el, { durationMs: 1000, now: clock.now, raf: clock.raf, caf: clock.caf });
    expect(clock.pending()).toBe(0);
    expect(el.scrollTop).toBe(0);
    handle.cancel(); // idempotent no-op
  });

  it('cancel() halts the loop and releases the pending frame', () => {
    const clock = makeFakeRaf();
    const el = fakeScrollEl(1000, 200); // distance = 800
    const handle = runAutoScroll(el, { durationMs: 1000, startDelayMs: 0, now: clock.now, raf: clock.raf, caf: clock.caf });

    clock.tick(200);
    const before = el.scrollTop;
    handle.cancel();
    expect(clock.caf).toHaveBeenCalledTimes(1);

    // After cancellation the loop is dead: further ticks never move the element.
    clock.tick(1000);
    expect(el.scrollTop).toBe(before);
    // Cancelling twice is safe.
    expect(() => handle.cancel()).not.toThrow();
  });
});
