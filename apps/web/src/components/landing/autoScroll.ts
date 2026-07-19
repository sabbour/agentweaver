/**
 * Simulated artifact auto-scroll.
 *
 * At the artifact beat the landing demo gently scrolls a long result within its
 * own viewport (never the page) so viewers can read past the fold hands-free. The
 * motion is driven by requestAnimationFrame and is intentionally kept SEPARATE
 * from the run-token timeout scheduler: this module owns only the rAF loop and
 * guarantees a single cancel entry-point for every teardown path (scenario
 * change, replay, out-of-view, reduced-motion, unmount).
 *
 * The math (`autoScrollEase`) is a pure function so it can be unit-tested without
 * a DOM, and `runAutoScroll` accepts injectable `now`/`raf`/`caf` so the loop is
 * deterministically testable with a fake clock.
 */

/** easeInOutCubic over a clamped [0,1] progress — calm, non-flashy travel. */
export function autoScrollEase(t: number): number {
  const clamped = t <= 0 ? 0 : t >= 1 ? 1 : t;
  return clamped < 0.5
    ? 4 * clamped * clamped * clamped
    : 1 - Math.pow(-2 * clamped + 2, 3) / 2;
}

/**
 * Scroll distance/progress for a given elapsed time. Pure — returns the target
 * scrollTop in pixels. Before `startDelayMs` it stays pinned at 0; after
 * `startDelayMs + durationMs` it rests at the full distance.
 */
export function autoScrollTop(
  elapsedMs: number,
  distancePx: number,
  durationMs: number,
  startDelayMs: number,
): number {
  if (distancePx <= 0 || durationMs <= 0) return 0;
  const active = elapsedMs - startDelayMs;
  if (active <= 0) return 0;
  return distancePx * autoScrollEase(active / durationMs);
}

export interface AutoScrollHandle {
  /** Idempotent — safe to call from any teardown path, including twice. */
  cancel(): void;
}

export interface AutoScrollOptions {
  /** Travel duration once scrolling begins. */
  durationMs: number;
  /** Quiet hold before travel begins so the top of the result registers first. */
  startDelayMs?: number;
  /** Injectable clock (defaults to performance.now / Date.now). */
  now?: () => number;
  /** Injectable rAF (defaults to window.requestAnimationFrame). */
  raf?: (cb: (t: number) => void) => number;
  /** Injectable cancel (defaults to window.cancelAnimationFrame). */
  caf?: (handle: number) => void;
}

const defaultNow = (): number =>
  typeof performance !== 'undefined' && typeof performance.now === 'function'
    ? performance.now()
    : Date.now();

/**
 * Animate `el.scrollTop` from 0 to its maximum over `durationMs`, easing calmly.
 * No-op (returns an inert handle) when the element cannot scroll. Always returns
 * a handle whose `cancel()` stops the loop and releases the frame request.
 */
export function runAutoScroll(el: HTMLElement, opts: AutoScrollOptions): AutoScrollHandle {
  const now = opts.now ?? defaultNow;
  const raf =
    opts.raf ??
    (typeof requestAnimationFrame === 'function'
      ? requestAnimationFrame
      : (cb: (t: number) => void) => setTimeout(() => cb(now()), 16) as unknown as number);
  const caf =
    opts.caf ??
    (typeof cancelAnimationFrame === 'function'
      ? cancelAnimationFrame
      : (handle: number) => clearTimeout(handle as unknown as ReturnType<typeof setTimeout>));

  const startDelayMs = opts.startDelayMs ?? 0;
  const distance = el.scrollHeight - el.clientHeight;

  // Nothing to reveal — pin to top and report an inert handle.
  if (distance <= 1) {
    el.scrollTop = 0;
    return { cancel() {} };
  }

  el.scrollTop = 0;
  const start = now();
  let frame = 0;
  let cancelled = false;

  const tick = () => {
    if (cancelled) return;
    const elapsed = now() - start;
    el.scrollTop = autoScrollTop(elapsed, distance, opts.durationMs, startDelayMs);
    if (elapsed >= startDelayMs + opts.durationMs) return; // settled at the bottom
    frame = raf(tick);
  };
  frame = raf(tick);

  return {
    cancel() {
      if (cancelled) return;
      cancelled = true;
      caf(frame);
    },
  };
}
