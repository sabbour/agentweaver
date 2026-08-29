import type { AgentMessageItem, TimelineItem, TurnGroupItem } from './types';

/**
 * The outcome-spec drafting turn (coordinator.outcome_spec) streams the drafting agent's raw JSON
 * object onto the run stream before it is confirmed (e.g. `{"desired_outcome":"...","scope":"..."}`).
 * Rendered verbatim this is an illegible JSON blob; once confirmed, the SAME shape gets a friendly
 * "### Outcome plan" rendering (see formatOutcomeSpecMessage below). This recognizes that interim
 * JSON shape wherever a message body might contain it (coordinator scope AND any child/subtask scope
 * that drafts an outcome spec) so it is never shown as raw JSON, matching the confirmed rendering.
 */
export interface OutcomeSpecMessage {
  desiredOutcome?: string;
  scope?: string;
}

const OUTCOME_SPEC_FIRST_KEYS = ['"desired_outcome"', '"desiredOutcome"'];

/**
 * True while a streamed message is still spelling the outcome spec's first JSON key.
 * This deliberately only recognizes the canonical key at the beginning of an object,
 * leaving ordinary natural-language streams untouched.
 */
export function isOutcomeSpecMessagePrefix(content: string): boolean {
  const trimmed = content.trimStart();
  if (!trimmed.startsWith('{')) return false;

  const firstProperty = trimmed.slice(1).trimStart();
  if (!firstProperty) return false;
  return OUTCOME_SPEC_FIRST_KEYS.some(
    (key) => key.startsWith(firstProperty) || firstProperty.startsWith(key),
  );
}

function readOutcomeField(payload: Record<string, unknown>, keys: string[]): string | undefined {
  for (const key of keys) {
    const value = payload[key];
    if (value != null && String(value).trim() !== '') return String(value);
  }
  return undefined;
}

/** True/parsed when a message body is the outcome-spec drafting agent's raw JSON object. */
export function parseOutcomeSpecMessage(content: string): OutcomeSpecMessage | null {
  const trimmed = content.trim();
  if (!trimmed.startsWith('{') || !/"desired_outcome"|"desiredOutcome"/.test(trimmed)) return null;
  try {
    const parsed = JSON.parse(trimmed) as Record<string, unknown>;
    const desiredOutcome = readOutcomeField(parsed, ['desiredOutcome', 'desired_outcome']);
    const scope = readOutcomeField(parsed, ['scope']);
    if (!desiredOutcome && !scope) return null;
    return { desiredOutcome, scope };
  } catch {
    return null;
  }
}

/** Render an outcome spec as the same friendly Markdown used once the spec is confirmed. */
export function formatOutcomeSpecMessage(spec: OutcomeSpecMessage): string {
  return [
    '### Outcome plan',
    spec.desiredOutcome ? `**Desired outcome:**\n\n${spec.desiredOutcome}` : null,
    spec.scope ? `**Scope:**\n\n${spec.scope}` : null,
  ].filter(Boolean).join('\n\n');
}
/**
 * The coordinator decomposition turn streams the planning agent's final assistant message onto the
 * coordinator run stream (CoordinatorOrchestratorExecutor.DecomposeWithModelAsync). That message is
 * the SERIALIZED work plan — a raw JSON array of subtask drafts
 * (e.g. [{"title":...,"scope":...,"role":...,"depends_on":[...]}, ...]). The reused run timeline
 * would otherwise render it verbatim as a giant code/text bubble next to the structured
 * "Decomposed into N subtasks" lifecycle chip (emitted by the coordinator.work_plan event).
 *
 * This recognizes that serialized-plan message and drops it from the coordinator timeline, leaving
 * the structured work-plan affordance (the chip + the work-plan panel/graph) as the single source of
 * truth. It NEVER touches the shared timeline reducer, so normal per-run timelines are unaffected.
 */

/** True when an assistant message body is the decompose agent's serialized work-plan JSON array. */
export function isSerializedWorkPlan(content: string): boolean {
  if (!content) return false;

  // Tolerant extraction: the model is told to emit only the array, but may wrap it in prose or
  // ```json fences. Mirror the backend's ParseDecomposition (first '[' .. last ']').
  const start = content.indexOf('[');
  const end = content.lastIndexOf(']');
  if (start < 0 || end <= start) return false;

  let parsed: unknown;
  try {
    parsed = JSON.parse(content.slice(start, end + 1));
  } catch {
    return false;
  }

  if (!Array.isArray(parsed) || parsed.length === 0) return false;

  // Every element must look like a subtask draft: an object carrying the two REQUIRED backend fields
  // (title + scope). This is specific enough to never match an arbitrary assistant JSON reply.
  return parsed.every(
    (el) =>
      el !== null &&
      typeof el === 'object' &&
      typeof (el as Record<string, unknown>).title === 'string' &&
      typeof (el as Record<string, unknown>).scope === 'string',
  );
}

/**
 * Returns a copy of <paramref name="items"/> with the decompose agent's serialized-plan message
 * removed. Turn groups that become empty (the plan JSON was their only step) are dropped so the
 * timeline does not show a hollow turn bubble.
 */
export function stripSerializedWorkPlanMessages(items: TimelineItem[]): TimelineItem[] {
  const result: TimelineItem[] = [];

  for (const item of items) {
    if (item.kind !== 'turn-group') {
      result.push(item);
      continue;
    }

    const steps = item.steps.filter(
      (step) =>
        !(step.kind === 'agent-message' && isSerializedWorkPlan((step as AgentMessageItem).content)),
    );

    // Drop a turn group only if it had message steps that were ALL serialized plans and nothing
    // else remains; otherwise keep it with the plan message(s) stripped.
    if (steps.length === 0) continue;

    const trimmed: TurnGroupItem = { ...item, steps };
    result.push(trimmed);
  }

  return result;
}
