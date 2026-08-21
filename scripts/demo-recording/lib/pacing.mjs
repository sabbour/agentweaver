function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value));
}

/**
 * Removes timestamps that form a regular-interval pattern (e.g. a progress spinner
 * cycling every ~5 seconds). Events are considered a "spinner run" when at least
 * `minRunLength` consecutive timestamps are spaced `intervalMs ± toleranceMs` apart.
 */
export function filterRegularIntervalEvents(
  timestamps,
  { intervalMs = 5000, toleranceMs = 350, minRunLength = 3 } = {},
) {
  if (timestamps.length < minRunLength) return timestamps;

  const remove = new Set();
  let i = 0;
  while (i < timestamps.length) {
    let runEnd = i;
    for (let j = i + 1; j < timestamps.length; j++) {
      const gap = timestamps[j] - timestamps[j - 1];
      if (Math.abs(gap - intervalMs) <= toleranceMs) {
        runEnd = j;
      } else {
        break;
      }
    }
    const runLength = runEnd - i + 1;
    if (runLength >= minRunLength) {
      for (let k = i; k <= runEnd; k++) remove.add(k);
      i = runEnd + 1;
    } else {
      i++;
    }
  }
  return timestamps.filter((_, idx) => !remove.has(idx));
}

export function normalizeActivityEvents(events = [], durationMs) {
  const max = Math.max(0, Number(durationMs) || 0);
  const times = events
    .map((event) => (typeof event === 'number' ? event : event?.t))
    .filter((value) => Number.isFinite(value))
    .map((value) => clamp(Math.round(value), 0, max))
    .sort((a, b) => a - b);

  const deduped = [];
  for (const time of times) {
    if (deduped[deduped.length - 1] !== time) deduped.push(time);
  }
  if (deduped[0] !== 0) deduped.unshift(0);
  if (deduped[deduped.length - 1] !== max) deduped.push(max);
  return deduped;
}

export function buildKeepSegments({
  durationMs,
  events = [],
  maxStaticMs = 2500,
  retainAfterActivityMs = 900,
  retainBeforeActivityMs = 1200,
  minSegmentMs = 250,
}) {
  const total = Math.max(0, Math.round(durationMs));
  if (!total) return [{ startMs: 0, endMs: 0 }];

  const marks = normalizeActivityEvents(events, total);
  const removals = [];

  for (let index = 0; index < marks.length - 1; index += 1) {
    const current = marks[index];
    const next = marks[index + 1];
    const gap = next - current;
    if (gap <= maxStaticMs) continue;

    let keepAfter = Math.min(retainAfterActivityMs, maxStaticMs);
    let keepBefore = Math.min(retainBeforeActivityMs, maxStaticMs - keepAfter);
    if (keepAfter + keepBefore > maxStaticMs) {
      const scale = maxStaticMs / (keepAfter + keepBefore);
      keepAfter = Math.floor(keepAfter * scale);
      keepBefore = Math.floor(keepBefore * scale);
    }

    const cutStart = current + keepAfter;
    const cutEnd = next - keepBefore;
    if (cutEnd - cutStart >= minSegmentMs) removals.push({ startMs: cutStart, endMs: cutEnd });
  }

  if (!removals.length) {
    return [{ startMs: 0, endMs: total }];
  }

  const segments = [];
  let cursor = 0;
  for (const removal of removals) {
    if (removal.startMs - cursor >= minSegmentMs) segments.push({ startMs: cursor, endMs: removal.startMs });
    cursor = removal.endMs;
  }
  if (total - cursor >= minSegmentMs) segments.push({ startMs: cursor, endMs: total });
  return segments.length ? segments : [{ startMs: 0, endMs: total }];
}

export function summarizeTrim({ durationMs, segments }) {
  const keptMs = segments.reduce((sum, segment) => sum + Math.max(0, segment.endMs - segment.startMs), 0);
  return {
    originalDurationMs: durationMs,
    trimmedDurationMs: keptMs,
    removedMs: Math.max(0, durationMs - keptMs),
    segments,
  };
}
