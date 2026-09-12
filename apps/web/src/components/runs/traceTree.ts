import type { PersistedRunEvent, RunTraceSpanDto } from '../../api/types';
export type SpanType = 'invoke-agent' | 'llm' | 'tool';

/** Arguments/output pulled from the persisted tool.call/tool.result/tool.error event log,
 *  keyed by callId, so the trace panel can show what a tool span actually did (issue #850). */
export interface ToolCallDetail {
  arguments?: unknown;
  content?: unknown;
  errorMessage?: unknown;
  outcome: 'pending' | 'succeeded' | 'failed';
}

export interface SafeToolValue {
  state: 'available' | 'redacted' | 'unavailable';
  text?: string;
}

const REDACTED = '***REDACTED***';
const maxStringLength = 8_192;
const maxRenderedLength = 16_384;
export const maxToolErrorDetailLength = 2_048;
const maxCollectionEntries = 100;
const maxDepth = 8;
const sensitiveKey = /(token|authorization|password|secret|credential|connection.?string|api.?key|private.?key|access.?key|bearer|key)/i;
const sensitiveValue = /(?:\bgh[uspor]_[A-Za-z0-9_-]+\b|\bgithub_pat_[A-Za-z0-9_]+\b|-----BEGIN [A-Z0-9 ]+-----|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]*)?|https?:\/\/[^\s/@:]+:[^\s/@]+@|(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{86}==(?=[^A-Za-z0-9+/=]|$))/i;

function hasSensitiveValue(value: string): boolean {
  return sensitiveValue.test(value)
    || (/(?:^|[?&;])\s*(?:sv|ss|sp|se)\s*=/i.test(value)
      && /(?:^|[?&;])\s*sig\s*=/i.test(value))
    || /\b(?:AccountKey|SharedAccessSignature)\s*=/i.test(value);
}

function parseStructuredText(value: string): unknown {
  const trimmed = value.trim();
  if (trimmed.length > 1 && (trimmed.startsWith('{') || trimmed.startsWith('['))) {
    try { return JSON.parse(trimmed); } catch { /* Render source text below. */ }
  }
  return value;
}

function normalizeToolValue(value: unknown, depth: number): { value: unknown; redacted: boolean } {
  if (depth > maxDepth) return { value: '[Nested value omitted]', redacted: false };
  if (value == null || typeof value === 'boolean' || typeof value === 'number') return { value, redacted: false };
  if (typeof value === 'string') {
    if (value === REDACTED || hasSensitiveValue(value)) return { value: REDACTED, redacted: true };
    if (value.length > maxStringLength) return { value: '[Value omitted: exceeds display limit]', redacted: false };
    const parsed = parseStructuredText(value);
    return parsed === value ? { value, redacted: false } : normalizeToolValue(parsed, depth + 1);
  }
  if (Array.isArray(value)) {
    const entries = value.slice(0, maxCollectionEntries).map((entry) => normalizeToolValue(entry, depth + 1));
    return {
      value: value.length > maxCollectionEntries
        ? [...entries.map((entry) => entry.value), `[${value.length - maxCollectionEntries} entries omitted]`]
        : entries.map((entry) => entry.value),
      redacted: entries.some((entry) => entry.redacted),
    };
  }
  if (typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>).slice(0, maxCollectionEntries);
    let redacted = false;
    const normalized: Record<string, unknown> = {};
    for (const [key, entry] of entries) {
      if (sensitiveKey.test(key)) {
        normalized[key] = REDACTED;
        redacted = true;
      } else {
        const next = normalizeToolValue(entry, depth + 1);
        normalized[key] = next.value;
        redacted ||= next.redacted;
      }
    }
    if (Object.keys(value as Record<string, unknown>).length > maxCollectionEntries)
      normalized._omitted = 'Additional fields omitted.';
    return { value: normalized, redacted };
  }
  return { value: '[Unsupported recorded value]', redacted: false };
}

/**
 * Produces a bounded, syntax-readable representation of tool data. This repeats backend
 * redaction defensively so a malformed or legacy event cannot expose credentials in the trace UI.
 */
export function formatSafeToolValue(value: unknown, maximumLength = maxRenderedLength): SafeToolValue {
  if (value === undefined) return { state: 'unavailable' };
  const normalized = normalizeToolValue(value, 0);
  const text = typeof normalized.value === 'string'
    ? normalized.value
    : JSON.stringify(normalized.value, null, 2);
  if (text.length > maximumLength)
    return { state: 'unavailable', text: 'Recorded value exceeds the display limit.' };
  return { state: normalized.redacted ? 'redacted' : 'available', text };
}

/**
 * Correlates persisted `tool.call` / `tool.result` / `tool.error` run events by `callId` so the
 * trace panel can display arguments and output for a tool span even though the AppInsights-backed
 * span itself only carries the tool name/status/duration. Malformed or missing payloads are
 * skipped rather than throwing, since this is best-effort enrichment of the trace UI.
 */
export function buildToolCallIndex(events: PersistedRunEvent[]): Map<string, ToolCallDetail> {
  const index = new Map<string, ToolCallDetail>();
  for (const event of events) {
    const payload = event.payload;
    const callId = typeof payload?.['callId'] === 'string' ? payload['callId'] : undefined;
    if (!callId) continue;
    const entry: ToolCallDetail = index.get(callId) ?? { outcome: 'pending' };
    if (event.type === 'tool.call') {
      if (Object.hasOwn(payload, 'arguments')) entry.arguments = payload['arguments'];
    } else if (event.type === 'tool.result') {
      entry.outcome = 'succeeded';
      if (Object.hasOwn(payload, 'content')) entry.content = payload['content'];
    } else if (event.type === 'tool.error') {
      entry.outcome = 'failed';
      if (Object.hasOwn(payload, 'errorMessage')) entry.errorMessage = payload['errorMessage'];
    } else {
      continue;
    }
    index.set(callId, entry);
  }
  return index;
}

export interface TraceNode {
  key: string;
  span: RunTraceSpanDto;
  type: SpanType;
  /** True when this node is a presentation-only LLM leaf synthesized from an agent span. */
  synthetic: boolean;
  children: TraceNode[];
}

export interface TraceTimeline {
  startedAtMs: number;
  endedAtMs: number;
  durationMs: number;
}

/**
 * Calculates the actual trace window from normalized span timestamps and durations.
 * Invalid timestamps are ignored so a malformed record cannot make every bar disappear.
 */
export function getTraceTimeline(spans: RunTraceSpanDto[]): TraceTimeline | null {
  const timedSpans = spans
    .map((span) => ({
      startedAtMs: new Date(span.timestamp).getTime(),
      durationMs: Math.max(0, span.durationMs),
    }))
    .filter((span) => Number.isFinite(span.startedAtMs));

  if (timedSpans.length === 0) return null;

  const startedAtMs = Math.min(...timedSpans.map((span) => span.startedAtMs));
  const endedAtMs = Math.max(...timedSpans.map((span) => span.startedAtMs + span.durationMs));
  return { startedAtMs, endedAtMs, durationMs: Math.max(0, endedAtMs - startedAtMs) };
}

export function normalizeType(span: RunTraceSpanDto): SpanType {
  const raw = (span.spanType ?? '').toLowerCase();
  if (raw === 'tool' || span.toolName) return 'tool';
  if (raw === 'llm') return 'llm';
  if (raw === 'invoke-agent') return 'invoke-agent';
  // Fall back to attribute hints when the backend did not classify the span.
  if (span.operationName === 'execute_tool') return 'tool';
  if (span.agentName) return 'invoke-agent';
  return 'llm';
}

/**
 * Reconstructs a span forest from the flat AppInsights span list using parentId links.
 * Spans whose parent is missing from the set become roots. Child runs additionally carry a
 * durable parent-run relationship, which is used only for presentation when a distributed
 * trace crosses runs. The original parentId remains intact on every span.
 * Each invoke-agent span that carries model/token usage also gets a synthetic LLM leaf child
 * so the tree mirrors the AppInsights "Invoke Agent -> LLM -> Execute Tool" reference structure.
 */
export function buildTraceTree(spans: RunTraceSpanDto[]): TraceNode[] {
  if (!spans.length) return [];

  const nodes = new Map<string, TraceNode>();
  const parentByKey = new Map<string, TraceNode | null>();
  for (const span of spans) {
    nodes.set(span.id, { key: span.id, span, type: normalizeType(span), synthetic: false, children: [] });
  }

  const roots: TraceNode[] = [];
  for (const span of spans) {
    const node = nodes.get(span.id)!;
    const parent = span.parentId ? nodes.get(span.parentId) : undefined;
    if (parent && parent !== node) {
      parent.children.push(node);
      parentByKey.set(node.key, parent);
    } else {
      roots.push(node);
      parentByKey.set(node.key, null);
    }
  }

  const sortByTime = (a: TraceNode, b: TraceNode) =>
    new Date(a.span.timestamp).getTime() - new Date(b.span.timestamp).getTime();

  const runId = (node: TraceNode) => node.span.attributes?.runId?.trim() || null;
  const parentRunId = (node: TraceNode) => node.span.attributes?.parentRunId?.trim() || null;
  const invocationByRunId = new Map<string, TraceNode[]>();
  for (const node of nodes.values()) {
    const id = runId(node);
    if (id && node.type === 'invoke-agent') {
      const invocations = invocationByRunId.get(id) ?? [];
      invocations.push(node);
      invocationByRunId.set(id, invocations);
    }
  }
  for (const invocations of invocationByRunId.values())
    invocations.sort(sortByTime);

  const closestInvocation = (invocations: TraceNode[], timestamp: string) => {
    const startedAt = new Date(timestamp).getTime();
    for (let index = invocations.length - 1; index >= 0; index--) {
      if (new Date(invocations[index].span.timestamp).getTime() <= startedAt)
        return invocations[index];
    }
    return invocations[0];
  };

  const move = (node: TraceNode, nextParent: TraceNode) => {
    const currentParent = parentByKey.get(node.key);
    if (currentParent === nextParent || node === nextParent) return;
    if (currentParent) currentParent.children = currentParent.children.filter((child) => child !== node);
    else {
      const rootIndex = roots.indexOf(node);
      if (rootIndex >= 0) roots.splice(rootIndex, 1);
    }
    nextParent.children.push(node);
    parentByKey.set(node.key, nextParent);
  };

  // Activity parent IDs describe the real distributed trace and can cross a run boundary.
  // Render that boundary as an agent invocation instead: a child-run tool must belong to the
  // child agent that executed it, never directly to the coordinator's agent turn.
  for (const node of nodes.values()) {
    const childRunId = runId(node);
    const owningRunId = parentRunId(node);
    if (!childRunId || !owningRunId || node.type === 'invoke-agent') continue;

    const childInvocations = invocationByRunId.get(childRunId);
    if (!childInvocations?.length) continue;
    const currentParent = parentByKey.get(node.key);
    if (currentParent && runId(currentParent) === childRunId) continue;
    move(node, closestInvocation(childInvocations, node.span.timestamp));
  }

  // A child agent can likewise be a root (or physically parented to a non-agent span) when
  // telemetry crosses an async process boundary. Attach its invocation to the parent run's
  // closest agent span while retaining the underlying trace parentId for the inspector/API.
  for (const [childRunId, childInvocations] of invocationByRunId) {
    const owningRunId = parentRunId(childInvocations[0]);
    const parentInvocations = owningRunId ? invocationByRunId.get(owningRunId) : undefined;
    if (!parentInvocations?.length) continue;
    for (const childInvocation of childInvocations) {
      const currentParent = parentByKey.get(childInvocation.key);
      if (currentParent && runId(currentParent) === childRunId) continue;
      move(childInvocation, closestInvocation(parentInvocations, childInvocation.span.timestamp));
    }
  }

  for (const node of nodes.values()) {
    node.children.sort(sortByTime);
    if (node.type === 'invoke-agent'
      && (node.span.model || node.span.inputTokens != null || node.span.outputTokens != null)) {
      const hasRealLlmChild = node.children.some((child) => child.type === 'llm');
      if (!hasRealLlmChild) {
        node.children.unshift({
          key: `${node.span.id}::llm`,
          span: node.span,
          type: 'llm',
          synthetic: true,
          children: [],
        });
      }
    }
  }
  roots.sort(sortByTime);
  return roots;
}

export function collectExpandableKeys(nodes: TraceNode[], acc: Set<string>): Set<string> {
  for (const node of nodes) {
    if (node.children.length) {
      acc.add(node.key);
      collectExpandableKeys(node.children, acc);
    }
  }
  return acc;
}

export function findNode(nodes: TraceNode[], key: string | null): TraceNode | null {
  if (!key) return null;
  for (const node of nodes) {
    if (node.key === key) return node;
    const found = findNode(node.children, key);
    if (found) return found;
  }
  return null;
}

/**
 * AIC (AI Credit) cost attributed to a single node, in nano-AIU (issue #852). Only `llm` nodes
 * (real or the synthetic leaf synthesized from an invoke-agent turn) carry their own cost — every
 * other node type contributes 0 directly and only aggregates its descendants below.
 */
function ownNanoAiu(node: TraceNode): number {
  return node.type === 'llm' ? (node.span.totalNanoAiu ?? 0) : 0;
}

/**
 * Aggregates AIC cost for a node: its own cost (if an LLM turn) plus every descendant's cost,
 * recursively. For an invoke-agent node this sums every model turn nested beneath it; for a root
 * span this sums every turn in the whole run.
 */
export function aggregateNanoAiu(node: TraceNode): number {
  return node.children.reduce((sum, child) => sum + aggregateNanoAiu(child), ownNanoAiu(node));
}

/** Total AIC cost across an entire trace forest — the run-level rollup (issue #852). */
export function totalNanoAiu(nodes: TraceNode[]): number {
  return nodes.reduce((sum, node) => sum + aggregateNanoAiu(node), 0);
}
