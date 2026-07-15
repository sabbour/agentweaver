import type { RunStreamEvent } from '../api/sse';
import { deriveHumanTitle, extractCallId, stripPathPrefix } from './reducer';
import { isSerializedWorkPlan, parseOutcomeSpecMessage, formatOutcomeSpecMessage } from './coordinatorPlanFilter';

/**
 * Count the subtask drafts inside the decompose agent's serialized work-plan JSON array, so the
 * illegible raw dump can be replaced with a short friendly line ("Decomposed the work into N
 * subtasks."). Returns null when the count can't be recovered.
 */
function serializedWorkPlanSubtaskCount(content: string): number | null {
  const start = content.indexOf('[');
  const end = content.lastIndexOf(']');
  if (start < 0 || end <= start) return null;
  try {
    const parsed = JSON.parse(content.slice(start, end + 1));
    return Array.isArray(parsed) ? parsed.length : null;
  } catch {
    return null;
  }
}

/**
 * Intent-driven Timeline model.
 *
 * Unlike the turn-grouping timelineReducer (which groups by agent.turn), the run
 * Timeline groups the stream by the agent's REPORTED INTENTS: every `agent.intent`
 * event opens a step, and every tool call / agent message emitted afterwards (until
 * the next intent or the turn ends) is nested UNDER that intent. This mirrors the
 * Copilot "chain of thought" reading order: intent → the things that ran for it.
 *
 * Tool-call correlation reuses the reducer's callId logic (extractCallId) and its
 * human-title / sandbox-violation derivation so the two surfaces stay consistent.
 */

export type RunTimelineStepStatus = 'pending' | 'running' | 'complete' | 'warning';

/**
 * Coarse category derived from a tool name, driving the row icon + how the result
 * meta is summarised (lines vs results vs diff). Mirrors the GitHub Copilot CLI
 * activity-log grouping: terminal / file-read / search / edit / web / generic.
 */
export type RunTimelineToolCategory = 'command' | 'read' | 'search' | 'edit' | 'web' | 'other';

export interface RunTimelineTool {
  callId: string;
  toolName: string;
  category: RunTimelineToolCategory;
  /** Primary, human title: verb + target (e.g. "View app.ts:1-30", "Searched foo", "Edit app.ts", "Run command"). */
  title: string;
  /** Muted secondary argument shown after the title (e.g. the shell command for a run). */
  titleSecondary?: string;
  status: 'running' | 'complete' | 'error';
  /** Right-aligned result meta derived from the result (e.g. "7 lines", "4 results", "+3 -1"). */
  resultMeta?: string;
  /** Full (capped) result content, kept for expandable rows. */
  resultContent?: string;
  /** Unified diff text for edit rows — powers the expandable diff card. May be capped (see truncated). */
  diff?: string;
  /** True when the stored `diff` was capped by the line/char budget (see DIFF_MAX_*). */
  truncated?: boolean;
  /** Number of diff lines hidden by the cap, for the "… N more lines" note. */
  diffHiddenLines?: number;
  /** True when the row can be expanded to reveal a diff / detail card. */
  expandable?: boolean;
  resultSummary?: string;
  errorMessage?: string;
  isSandboxViolation?: boolean;
}

export interface RunTimelineMessage {
  messageId: string;
  text: string;
  streaming: boolean;
  /**
   * Message role from the server event payload. "user" messages are the operator's echoed
   * input turns; "assistant" (or missing) are the agent's replies. Used by renderers to
   * apply different visual styling (e.g. user bubble vs assistant bubble).
   * When absent, the renderer treats the message as an assistant message.
   */
  role?: 'user' | 'assistant';
  /**
   * Best-known message time in epoch-ms for relative-time rendering in the production
   * RunTimeline surface. Prefer a server/event payload timestamp when available;
   * otherwise fall back to the client-side receipt time captured while folding.
   */
  timestamp: number;
}

/**
 * One ordered item inside a step's `children` array. Messages and tool rows are kept
 * in the SEQUENCE they occurred so the Timeline can interleave assistant narration
 * BETWEEN tool groups (message → tools → message → tools), matching reading order.
 */
export type RunTimelineChild =
  | { kind: 'message'; message: RunTimelineMessage }
  | { kind: 'tool'; tool: RunTimelineTool };

export interface RunTimelineStep {
  id: string;
  /** The reported intent text — the step title. */
  intent: string;
  status: RunTimelineStepStatus;
  /** True while this intent's turn is still streaming. */
  active: boolean;
  /**
   * True when the step was NOT opened by an explicit agent.intent — i.e. a tool call
   * or message arrived with no reported intent, so we synthesised a "Working" step.
   * Message-only synthetic steps render as plain narration (no chain-of-thought chrome).
   */
  synthetic: boolean;
  tools: RunTimelineTool[];
  messages: RunTimelineMessage[];
  /**
   * Ordered children (messages + tools) in the sequence they occurred. Render uses this
   * to interleave narration between tool groups; `tools`/`messages` remain for status
   * derivation and back-compat.
   */
  children: RunTimelineChild[];
  /** Sequence of the opening agent.intent event (ordering key). */
  sequence: number;
}

export interface RunTimelineModel {
  steps: RunTimelineStep[];
  /** Total number of raw events folded into the timeline (for the "{N} steps" subline we use steps.length). */
  eventCount: number;
  /** True while any step is still actively streaming. */
  running: boolean;
}

const READ_RESULT_MAX = 160;

function summariseResult(content: string): string {
  const trimmed = content.trim().replace(/\s+/g, ' ');
  if (trimmed.length <= READ_RESULT_MAX) return trimmed;
  return `${trimmed.slice(0, READ_RESULT_MAX)}\u2026`;
}

/** Cap the full result content kept for expandable rows so replays stay bounded. */
const EXPAND_CONTENT_MAX = 8000;

const asStrOpt = (v: unknown): string | undefined => (v == null ? undefined : String(v));

/** Map a raw tool name to a coarse activity category (icon + result-meta behaviour). */
export function categorizeTool(toolName: string): RunTimelineToolCategory {
  const n = toolName.toLowerCase();
  if (
    n === 'run_command' || n === 'run' || n.includes('powershell') || n.includes('bash') ||
    n.includes('shell') || n.includes('terminal') || n.includes('console') || n.includes('exec')
  ) {
    return 'command';
  }
  if (
    n.includes('edit') || n.includes('str_replace') || n.includes('replace') || n.includes('write') ||
    n.includes('create') || n.includes('patch') || n.includes('apply') || n.includes('delete') ||
    n.includes('move') || n.includes('insert')
  ) {
    return 'edit';
  }
  if (
    n.includes('search') || n.includes('grep') || n.includes('glob') || n.includes('find') || n.includes('list')
  ) {
    return 'search';
  }
  if (n.includes('read') || n.includes('view') || n.includes('cat') || n.includes('open') || n.includes('file')) {
    return 'read';
  }
  if (
    n.includes('http') || n.includes('web') || n.includes('fetch') || n.includes('url') ||
    n.includes('workiq') || n.includes('email') || n.includes('cloud') || n.includes('api')
  ) {
    return 'web';
  }
  return 'other';
}

/** Derive a "start-end" (or "start") line-range suffix from common range argument shapes. */
function deriveLineRange(args: Record<string, unknown>): string | undefined {
  const vr = args['view_range'] ?? args['range'] ?? args['lineRange'] ?? args['lines'];
  if (Array.isArray(vr) && vr.length >= 1 && vr[0] != null) {
    const start = vr[0];
    const end = vr.length >= 2 ? vr[1] : undefined;
    if (end == null || end === -1) return `${start}`;
    return `${start}-${end}`;
  }
  const start = args['start_line'] ?? args['startLine'] ?? args['start'];
  const end = args['end_line'] ?? args['endLine'] ?? args['end'];
  if (start != null) return end != null ? `${start}-${end}` : `${start}`;
  return undefined;
}

/**
 * Split a tool call into a primary title (verb + target) and an optional muted secondary
 * argument. Reuses stripPathPrefix / deriveHumanTitle so paths/labels stay consistent with
 * the other timeline surface.
 */
export function deriveToolTitle(
  category: RunTimelineToolCategory,
  toolName: string,
  args: Record<string, unknown>,
): { title: string; secondary?: string } {
  const pathArg = asStrOpt(args['path'] ?? args['file'] ?? args['filename'] ?? args['file_path']);
  const display = pathArg ? stripPathPrefix(pathArg) : undefined;
  switch (category) {
    case 'command': {
      const cmd = asStrOpt(args['command'] ?? args['cmd'] ?? args['script']);
      const isGeneric = toolName === 'run_command' || toolName === 'run';
      const title = isGeneric ? 'Run command' : `Running ${toolName}`;
      return { title, secondary: cmd };
    }
    case 'read': {
      const range = deriveLineRange(args);
      if (display) return { title: `View ${display}${range ? `:${range}` : ''}` };
      return { title: 'View file' };
    }
    case 'search': {
      const pattern = asStrOpt(args['pattern'] ?? args['query'] ?? args['glob'] ?? args['q']);
      if (pattern) return { title: `Searched ${pattern}` };
      if (display) return { title: `List ${display}` };
      return { title: 'Search' };
    }
    case 'edit': {
      const n = toolName.toLowerCase();
      const verb = n.includes('create')
        ? 'Create'
        : n.includes('delete')
          ? 'Delete'
          : n.includes('move')
            ? 'Move'
            : n.includes('write')
              ? 'Write'
              : n.includes('patch') || n.includes('apply')
                ? 'Apply patch'
                : 'Edit';
      return { title: display ? `${verb} ${display}` : verb };
    }
    case 'web': {
      const url = asStrOpt(args['url'] ?? args['href']);
      if (url) return { title: `Fetch ${stripPathPrefix(url)}` };
      return { title: deriveHumanTitle(toolName, args) };
    }
    default:
      return { title: deriveHumanTitle(toolName, args) };
  }
}

/** Count added/removed lines in a unified-diff-ish string. */
function diffCounts(diff: string): { added: number; removed: number } {
  let added = 0;
  let removed = 0;
  for (const line of diff.split('\n')) {
    if (line.startsWith('+') && !line.startsWith('+++')) added += 1;
    else if (line.startsWith('-') && !line.startsWith('---')) removed += 1;
  }
  return { added, removed };
}

/** Format a "+N -M" diff delta, or undefined when there is nothing to show. */
function diffMeta(diff: string): string | undefined {
  const { added, removed } = diffCounts(diff);
  const parts: string[] = [];
  if (added > 0) parts.push(`+${added}`);
  if (removed > 0) parts.push(`-${removed}`);
  return parts.length > 0 ? parts.join(' ') : undefined;
}

function looksLikeDiff(s: string): boolean {
  return /^(@@|diff |Index: |--- |\+\+\+ )/m.test(s) || /^[+-][^+-]/m.test(s);
}

/**
 * Derive a diff for an edit row: prefer a diff embedded in the result content, else
 * synthesise one from old/new string arguments (str_replace-style edits).
 */
function deriveEditDiff(args: Record<string, unknown>, resultContent?: string): string | undefined {
  if (resultContent && looksLikeDiff(resultContent)) return resultContent;
  const oldStr = asStrOpt(args['old_str'] ?? args['oldStr'] ?? args['old_string'] ?? args['old']);
  const newStr = asStrOpt(args['new_str'] ?? args['newStr'] ?? args['new_string'] ?? args['new']);
  if (oldStr == null && newStr == null) return undefined;
  const oldLines = oldStr != null && oldStr.length > 0 ? oldStr.split('\n') : [];
  const newLines = newStr != null && newStr.length > 0 ? newStr.split('\n') : [];
  const body = [...oldLines.map((l) => `-${l}`), ...newLines.map((l) => `+${l}`)].join('\n');
  return body.length > 0 ? body : undefined;
}

/**
 * Cap a diff so a pathological edit (huge old_str/new_str, or a giant result blob) can
 * never render thousands of lines and freeze the UI. Caps by BOTH line count and total
 * chars; `hiddenLines` counts the lines dropped relative to the original.
 */
const DIFF_MAX_LINES = 200;
const DIFF_MAX_CHARS = 20_000;

function capDiff(diff: string): { diff: string; truncated: boolean; hiddenLines: number } {
  const originalLines = diff.split('\n');
  let capped = diff;
  let truncated = false;
  if (originalLines.length > DIFF_MAX_LINES) {
    capped = originalLines.slice(0, DIFF_MAX_LINES).join('\n');
    truncated = true;
  }
  if (capped.length > DIFF_MAX_CHARS) {
    capped = capped.slice(0, DIFF_MAX_CHARS);
    truncated = true;
  }
  const shownLines = truncated ? capped.split('\n').length : originalLines.length;
  return { diff: capped, truncated, hiddenLines: Math.max(0, originalLines.length - shownLines) };
}

/**
 * Apply a derived edit diff to a tool row: the +A −R delta is counted from the FULL diff
 * (accuracy), while the stored diff text is capped so the expandable card stays bounded.
 */
function applyEditDiff(tool: RunTimelineTool, rawDiff: string): void {
  tool.resultMeta = diffMeta(rawDiff);
  const { diff, truncated, hiddenLines } = capDiff(rawDiff);
  tool.diff = diff;
  tool.expandable = true;
  tool.truncated = truncated;
  tool.diffHiddenLines = truncated ? hiddenLines : undefined;
}

/** Right-aligned result meta from the settled result content, keyed off the tool category. */
function deriveResultMeta(category: RunTimelineToolCategory, content: string): string | undefined {
  const trimmed = content.replace(/\n+$/, '');
  if (trimmed.length === 0) return undefined;
  const lineCount = trimmed.split('\n').length;
  switch (category) {
    case 'read':
    case 'command':
      return `${lineCount} ${lineCount === 1 ? 'line' : 'lines'}`;
    case 'search':
      return `${lineCount} ${lineCount === 1 ? 'result' : 'results'}`;
    default:
      return undefined;
  }
}

/**
 * Run-level terminal events. When one of these arrives every still-active step is
 * closed, so a durable replay of a finished run never shows perpetual running/pending
 * circles. These singleton events often carry sequence 0, so we also sort them LAST
 * (see buildRunTimeline) to guarantee closure happens after all intents/tools/messages.
 */
const RUN_TERMINAL_TYPES = new Set<string>(['run.completed', 'run.failed', 'run.error']);

function readEventTimestamp(evt: RunStreamEvent): number | undefined {
  const raw = evt.payload?.['timestamp_utc'] ?? evt.payload?.['timestampUtc'] ?? evt.payload?.['timestamp'];
  if (raw == null) return undefined;
  const ms = new Date(String(raw)).getTime();
  return Number.isNaN(ms) ? undefined : ms;
}

function captureMessageTimestamp(evt: RunStreamEvent): number {
  return readEventTimestamp(evt) ?? Date.now();
}

/**
 * Close a step: mark it inactive and settle any still-running tool calls to `complete`
 * (unless they errored) — mirroring the reducer's settlePendingCallsInTurn so a missing
 * or mismatched tool.result never leaves a perpetual spinner on a finished step.
 */
function closeStep(step: RunTimelineStep): void {
  step.active = false;
  for (const tool of step.tools) {
    if (tool.status === 'running') tool.status = 'complete';
  }
}

const CONTINUATION_INTENT_RE = /^(?:now|next|then|after that|from here|meanwhile|finally|lastly|at this point)\b/i;
const CONVERSATIONAL_INTENT_PREFIX_RE = /^(?:(?:now|next|then|after that|from here|meanwhile|finally|lastly|at this point)[,:]?\s*)?(?:(?:let['’]?s|we['’]ll|i['’]ll)\s+)?/i;
const MAX_COLLAPSIBLE_STEP_TOOLS = 4;
const MAX_COLLAPSIBLE_STEP_MESSAGES = 2;
const MAX_COLLAPSIBLE_STEP_MESSAGE_CHARS = 240;
const MAX_COLLAPSIBLE_STEP_CHILDREN = 6;

function isContinuationIntent(intent: string): boolean {
  return CONTINUATION_INTENT_RE.test(intent.trim());
}

function normalizeContinuationIntent(intent: string): string {
  const normalized = intent.replace(CONVERSATIONAL_INTENT_PREFIX_RE, '').trim() || intent.trim();
  return normalized.length > 0
    ? `${normalized.charAt(0).toUpperCase()}${normalized.slice(1)}`
    : intent.trim();
}

function messageCharCount(step: RunTimelineStep): number {
  return step.messages.reduce((total, message) => total + message.text.trim().length, 0);
}

function isCollapsibleNarrationStep(step: RunTimelineStep): boolean {
  if (step.synthetic) return false;
  if (step.tools.length > MAX_COLLAPSIBLE_STEP_TOOLS) return false;
  if (step.messages.length > MAX_COLLAPSIBLE_STEP_MESSAGES) return false;
  if (step.children.length > MAX_COLLAPSIBLE_STEP_CHILDREN) return false;
  if (messageCharCount(step) > MAX_COLLAPSIBLE_STEP_MESSAGE_CHARS) return false;
  if (step.tools.some((tool) => tool.status === 'error')) return false;
  return step.tools.every((tool) => tool.category !== 'command' && tool.category !== 'web');
}

function mergeTimelineSteps(base: RunTimelineStep, next: RunTimelineStep): void {
  base.intent = normalizeContinuationIntent(base.intent);
  base.tools.push(...next.tools);
  base.messages.push(...next.messages);
  base.children.push(...next.children);
  base.active = base.active || next.active;
}

function collapseContinuationNarrationSteps(steps: RunTimelineStep[]): RunTimelineStep[] {
  const collapsed: RunTimelineStep[] = [];
  for (const step of steps) {
    const previous = collapsed[collapsed.length - 1];
    if (
      previous
      && isContinuationIntent(step.intent)
      && isCollapsibleNarrationStep(previous)
      && isCollapsibleNarrationStep(step)
    ) {
      mergeTimelineSteps(previous, step);
      continue;
    }
    collapsed.push(step);
  }
  return collapsed;
}

/**
 * Fold a scope's event stream into intent-grouped Timeline steps.
 * `events` may arrive out of order across reconnects — we sort by `sequence`
 * before grouping. Message timestamps prefer any wire timestamp that does exist on the
 * payload, else fall back to the client-side receipt time captured during folding.
 */
export function buildRunTimeline(
  events: readonly RunStreamEvent[],
  options?: {
    stripSerializedWorkPlan?: boolean;
    /**
     * When true, close every still-open step (settling any 'running' tool to 'complete')
     * even though no agent.turn.end / run.completed|failed|error was ever observed (#299).
     * The run can leave a turn open without one of those events — e.g. a coordinator
     * stall/redispatch, a review gate, or a block — so a tool call started just before
     * that transition would otherwise show a perpetual spinner. Callers should pass the
     * CURRENT run status here (true whenever the run is no longer actively streaming),
     * not just derive it from the events already folded into this call.
     */
    forceCloseIfInactive?: boolean;
  },
): RunTimelineModel {
  // The serialized work-plan replacement is only meaningful on the coordinator run stream, where the
  // decompose agent's raw JSON array actually originates. Defaults on so existing callers/tests keep
  // the summary; child agent scopes pass false so a child's legit JSON output is never rewritten.
  const stripSerializedWorkPlan = options?.stripSerializedWorkPlan ?? true;
  // Sort by sequence, but force run-terminal singletons (often sequence 0) to sort LAST
  // so they close steps only after every intent/tool/message has been placed.
  const sortKey = (e: RunStreamEvent): number =>
    RUN_TERMINAL_TYPES.has(e.type) ? Number.MAX_SAFE_INTEGER : e.sequence;
  const sorted = [...events].sort((a, b) => sortKey(a) - sortKey(b));

  const steps: RunTimelineStep[] = [];
  let current: RunTimelineStep | null = null;

  // Global correlation maps so a tool.result/error settles the right tool even if
  // intents interleave (same approach as the reducer's pendingToolCalls map).
  const toolByCallId = new Map<string, RunTimelineTool>();
  const messageByStep = new Map<string, Map<string, RunTimelineMessage>>();
  // Uncapped arg-derived diff per callId, so the tool.result fallback recounts +A −R from
  // the FULL diff rather than an already-capped copy stored on the tool.
  const rawDiffByCallId = new Map<string, string>();
  // When a message arrives with no messageId, correlate deltas to a single streaming
  // message per step (and let the final agent.message settle it) instead of spawning a
  // new message per delta.
  const streamingNoIdByStep = new Map<string, RunTimelineMessage>();

  const ensureStep = (seq: number): RunTimelineStep => {
    if (current) return current;
    const synthetic: RunTimelineStep = {
      id: `intent-auto-${seq}`,
      intent: 'Working',
      status: 'running',
      active: true,
      synthetic: true,
      tools: [],
      messages: [],
      children: [],
      sequence: seq,
    };
    steps.push(synthetic);
    current = synthetic;
    messageByStep.set(synthetic.id, new Map());
    return synthetic;
  };

  const asStr = (v: unknown): string => (v == null ? '' : String(v));

  for (const evt of sorted) {
    const payload = evt.payload ?? {};
    switch (evt.type) {
      case 'agent.intent': {
        const intent = asStr(payload['intent']).trim() || 'Working';
        // Close the previous intent step before opening a new one, otherwise every
        // earlier step stays active=true and derives to running/pending forever.
        if (current) closeStep(current);
        const step: RunTimelineStep = {
          id: `intent-${evt.sequence}`,
          intent,
          status: 'running',
          active: true,
          synthetic: false,
          tools: [],
          messages: [],
          children: [],
          sequence: evt.sequence,
        };
        steps.push(step);
        current = step;
        messageByStep.set(step.id, new Map());
        break;
      }

      case 'tool.call': {
        const toolName = asStr(payload['toolName']) || 'tool';
        // report_intent IS the intent (already surfaced via agent.intent) — don't
        // duplicate it as a tool row.
        if (toolName === 'report_intent') break;
        const step = ensureStep(evt.sequence);
        const rawCallId = extractCallId(payload);
        const callId = rawCallId == null ? `call-${evt.sequence}` : String(rawCallId);
        const args = (payload['arguments'] as Record<string, unknown>) ?? {};
        const category = categorizeTool(toolName);
        const { title, secondary } = deriveToolTitle(category, toolName, args);
        const tool: RunTimelineTool = {
          callId,
          toolName,
          category,
          title,
          titleSecondary: secondary,
          status: 'running',
        };
        // Edits often carry the change in their arguments (old_str/new_str) — surface the
        // diff + delta immediately, before the result arrives.
        if (category === 'edit') {
          const diff = deriveEditDiff(args);
          if (diff) {
            rawDiffByCallId.set(callId, diff);
            applyEditDiff(tool, diff);
          }
        }
        step.tools.push(tool);
        step.children.push({ kind: 'tool', tool });
        toolByCallId.set(callId, tool);
        break;
      }

      case 'tool.result': {
        const rawCallId = extractCallId(payload);
        if (rawCallId == null) break;
        const tool = toolByCallId.get(String(rawCallId));
        if (!tool) break;
        tool.status = 'complete';
        const content = asStr(payload['content']);
        tool.resultSummary = summariseResult(content);
        tool.resultContent = content.length > EXPAND_CONTENT_MAX
          ? `${content.slice(0, EXPAND_CONTENT_MAX)}\u2026`
          : content;
        if (tool.category === 'edit') {
          // Prefer a diff embedded in the result; keep the uncapped arg-derived diff otherwise.
          const diff = deriveEditDiff({}, content) ?? rawDiffByCallId.get(tool.callId);
          if (diff) applyEditDiff(tool, diff);
        } else {
          tool.resultMeta = deriveResultMeta(tool.category, content);
          // Non-edit tool calls carried a result but had no way to be expanded to view
          // it — only edit rows with a diff were clickable. Any tool with real output
          // is now expandable so its output can be inspected inline (#299).
          if (content.trim().length > 0) tool.expandable = true;
        }
        break;
      }

      case 'tool.error': {
        const rawCallId = extractCallId(payload);
        if (rawCallId == null) break;
        const tool = toolByCallId.get(String(rawCallId));
        if (!tool) break;
        const errorMessage = asStr(payload['errorMessage']);
        const lower = errorMessage.toLowerCase();
        tool.status = 'error';
        tool.errorMessage = errorMessage;
        tool.isSandboxViolation =
          lower.includes('sandbox') ||
          lower.includes('outside the sandbox boundary') ||
          lower.includes('denied');
        break;
      }

      case 'agent.message.delta': {
        const step = ensureStep(evt.sequence);
        const rawId = asStr(payload['messageId']);
        const delta = asStr(payload['delta']);
        const role = (asStr(payload['role']) === 'user' ? 'user' : 'assistant') as 'user' | 'assistant';
        const byId = messageByStep.get(step.id)!;
        if (rawId) {
          const existing = byId.get(rawId);
          if (existing) {
            existing.text += delta;
          } else {
            const msg: RunTimelineMessage = {
              messageId: rawId,
              text: delta,
              streaming: true,
              role,
              timestamp: captureMessageTimestamp(evt),
            };
            byId.set(rawId, msg);
            step.messages.push(msg);
            step.children.push({ kind: 'message', message: msg });
          }
        } else {
          // No messageId — append to the step's current streaming message so a stream of
          // id-less deltas folds into one message instead of one-per-delta.
          const current = streamingNoIdByStep.get(step.id);
          if (current && current.streaming) {
            current.text += delta;
          } else {
            const msg: RunTimelineMessage = {
              messageId: `msg-${evt.sequence}`,
              text: delta,
              streaming: true,
              role,
              timestamp: captureMessageTimestamp(evt),
            };
            streamingNoIdByStep.set(step.id, msg);
            step.messages.push(msg);
            step.children.push({ kind: 'message', message: msg });
          }
        }
        break;
      }

      case 'agent.message': {
        const step = ensureStep(evt.sequence);
        const rawId = asStr(payload['messageId']);
        const content = asStr(payload['content']);
        const role = (asStr(payload['role']) === 'user' ? 'user' : 'assistant') as 'user' | 'assistant';
        const byId = messageByStep.get(step.id)!;
        if (rawId) {
          const existing = byId.get(rawId);
          if (existing) {
            existing.text = content;
            existing.streaming = false;
          } else {
            const msg: RunTimelineMessage = {
              messageId: rawId,
              text: content,
              streaming: false,
              role,
              timestamp: captureMessageTimestamp(evt),
            };
            byId.set(rawId, msg);
            step.messages.push(msg);
            step.children.push({ kind: 'message', message: msg });
          }
        } else {
          // No messageId — settle the step's current streaming message rather than adding a
          // duplicate. Keep accumulated deltas when the final content is empty.
          const current = streamingNoIdByStep.get(step.id);
          if (current) {
            if (content) current.text = content;
            current.streaming = false;
            streamingNoIdByStep.delete(step.id);
          } else {
            const msg: RunTimelineMessage = {
              messageId: `msg-${evt.sequence}`,
              text: content,
              streaming: false,
              role,
              timestamp: captureMessageTimestamp(evt),
            };
            step.messages.push(msg);
            step.children.push({ kind: 'message', message: msg });
          }
        }
        break;
      }

      case 'agent.turn.end': {
        if (current) closeStep(current);
        current = null;
        break;
      }

      case 'run.completed':
      case 'run.failed':
      case 'run.error': {
        // A durable replay of a finished run may never carry agent.turn.end — close the
        // current step AND every still-active step so nothing spins/pends forever.
        for (const step of steps) {
          if (step.active) closeStep(step);
        }
        current = null;
        break;
      }

      default:
        break;
    }
  }

  // The run is no longer actively streaming (parked/blocked/awaiting review/terminal) but
  // no in-stream event closed the last open step — settle it now instead of leaving a
  // perpetual "running" spinner on its tool calls (#299).
  if (options?.forceCloseIfInactive) {
    for (const step of steps) {
      if (step.active) closeStep(step);
    }
  }

  const collapsedSteps = collapseContinuationNarrationSteps(steps);

  // Replace the coordinator decompose agent's serialized work-plan JSON (a giant illegible array)
  // with a short friendly line. The structured work-plan chip + subagents overlay stay the source of
  // truth. Children reference the same message objects, so mutating text covers both surfaces.
  // Only applied on the coordinator scope (stripSerializedWorkPlan) — a child agent may legitimately
  // emit a title/scope JSON array in its own output, which must be left intact.
  if (stripSerializedWorkPlan) {
    for (const step of collapsedSteps) {
      for (const msg of step.messages) {
        if (isSerializedWorkPlan(msg.text)) {
          const n = serializedWorkPlanSubtaskCount(msg.text);
          msg.text = n != null
            ? `Decomposed the work into ${n} subtask${n === 1 ? '' : 's'}.`
            : 'Decomposed the work into subtasks.';
        }
      }
    }
  }

  // Reformat the outcome-spec drafting agent's interim raw JSON (e.g.
  // {"desired_outcome":...,"scope":...}) into the same friendly "### Outcome plan" Markdown
  // used once the spec is confirmed (see AgentSessionPanel's buildTurns). Unlike the
  // serialized-work-plan strip above this always applies — the raw JSON is illegible on
  // ANY scope (coordinator or child/subtask) that streams it (#UI-bug-2).
  for (const step of collapsedSteps) {
    for (const msg of step.messages) {
      const outcomeSpec = parseOutcomeSpecMessage(msg.text);
      if (outcomeSpec) msg.text = formatOutcomeSpecMessage(outcomeSpec);
    }
  }

  // Derive each step's status from its settled work.
  for (const step of collapsedSteps) {
    for (const msg of step.messages) {
      if (!step.active) msg.streaming = false;
    }
    if (step.active) {
      step.status = 'running';
      continue;
    }
    if (step.tools.some((t) => t.status === 'error')) {
      step.status = 'warning';
    } else if (step.tools.length === 0 && step.messages.length === 0) {
      step.status = 'pending';
    } else {
      step.status = 'complete';
    }
  }

  return {
    steps: collapsedSteps,
    eventCount: sorted.length,
    running: collapsedSteps.some((s) => s.active),
  };
}

/** Human-readable tool result path stripping, exported for the row renderer. */
export { stripPathPrefix };
