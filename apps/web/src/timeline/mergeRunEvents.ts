import type { RunStreamEvent } from '../api/sse';

/**
 * Shared run-history seeding helpers (BLOCKING #3 — de-duplication).
 *
 * Previously `mergeRunEvents` was copy-pasted in WorkflowRunPage.tsx and
 * AgentSessionPanel.tsx, and `SEED_STATUSES` lived only in WorkflowRunPage.
 * Both the workflow page, the agent session panel and the browser console TUI
 * need the SAME parked/terminal seeding semantics, so the logic lives here once.
 */

/**
 * Statuses for which the run is finished/parked and its live SSE stream is closed,
 * so the timeline must be seeded from the persisted-events endpoint. Generous on
 * purpose: a child parks at assemble-ready, and listing unknown-but-inactive
 * states here is harmless.
 */
export const SEED_STATUSES: ReadonlySet<string> = new Set([
  'completed', 'failed', 'merged', 'declined', 'merge_failed',
  'parked', 'assemble_ready', 'assembled', 'cancelled', 'stopped',
]);

/**
 * Fold a persisted-events REST seed under live SSE deltas. Seeded events come
 * first in order; a live event is appended only when not already represented.
 * Positive sequences dedupe by sequence. Sequence-0 events are only deduped for
 * true singleton terminal events; repeated review / assembly events must remain
 * visible because they represent distinct gates across revisions.
 *
 * @param opts.sort when true, the merged list is re-ordered by sequence (seq-0
 *   events sort last). The AgentSessionPanel relies on this; the workflow page
 *   and the TUI keep arrival order (sort omitted).
 */
export function mergeRunEvents(
  seed: RunStreamEvent[],
  live: RunStreamEvent[],
  opts: { sort?: boolean } = {},
): RunStreamEvent[] {
  const bySequence = (a: RunStreamEvent, b: RunStreamEvent) =>
    (a.sequence || Number.MAX_SAFE_INTEGER) - (b.sequence || Number.MAX_SAFE_INTEGER);

  if (seed.length === 0) {
    return opts.sort ? [...live].sort(bySequence) : live;
  }
  const merged = [...seed];
  const seenSeq = new Set(seed.filter((e) => e.sequence > 0).map((e) => e.sequence));
  const seqZeroSingletonTypes: ReadonlySet<string> = new Set([
    'run.completed',
    'run.failed',
    'review.approved',
    'review.declined',
    'merge.completed',
    'merge.failed',
  ]);
  const seenSeqZeroSingletonType = new Set(
    seed
      .filter((e) => e.sequence === 0 && seqZeroSingletonTypes.has(e.type))
      .map((e) => e.type),
  );
  for (const evt of live) {
    if (evt.sequence > 0) {
      if (seenSeq.has(evt.sequence)) continue;
      seenSeq.add(evt.sequence);
    } else if (seqZeroSingletonTypes.has(evt.type)) {
      if (seenSeqZeroSingletonType.has(evt.type)) continue;
      seenSeqZeroSingletonType.add(evt.type);
    }
    merged.push(evt);
  }
  return opts.sort ? merged.sort(bySequence) : merged;
}
