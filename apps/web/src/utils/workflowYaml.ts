import { Document, isMap, isSeq, parseDocument } from 'yaml';
/**
 * workflowYaml — client-side YAML ↔ execution-graph conversion for the visual
 * workflow editor (Feature 015, US8 / FR-050).
 *
 * The on-disk `.agentweaver/workflows/<id>.yaml` is the single source of truth.
 * This module parses that YAML into a lightweight graph model for rendering and
 * applies surgical edits back onto the YAML document, **preserving unknown /
 * forward-compatible fields and comments** so visual editing never drops
 * definition fields the editor does not model.
 *
 * All authoritative validation/persistence remains server-side; this is only a
 * view-and-edit convenience layer over the canonical YAML text.
 */
/** The canonical node `type` strings the runtime loader accepts (WorkflowDefinitionLoader.TryParseNodeType). */
export const WORKFLOW_NODE_TYPES = [
  'prompt',
  'peer_review',
  'build_test',
  'check',
  'fan_out',
  'fan_in',
  'coordinator_composed',
  'serial',
  'merge',
  'scribe',
  'terminal',
] as const;

export type WorkflowNodeTypeName = (typeof WORKFLOW_NODE_TYPES)[number];

export const AUTHORABLE_WORKFLOW_NODE_TYPES = WORKFLOW_NODE_TYPES.filter(
  (t) => t !== 'merge' && t !== 'scribe',
);

/** Human-friendly labels for the node-type picker. */
export const NODE_TYPE_LABELS: Record<string, string> = {
  prompt: 'Prompt (agent turn)',
  peer_review: 'Peer review',
  build_test: 'Build & Test',
  check: 'Check / gate',
  fan_out: 'Fan-out',
  fan_in: 'Fan-in',
  coordinator_composed: 'Coordinator-composed',
  serial: 'Serial',
  merge: 'Merge',
  scribe: 'Scribe',
  terminal: 'Terminal',
};

export interface WfNode {
  id: string;
  type: string;
  label?: string;
  agent?: string;
  prompt?: string;
  model?: string;
  role?: string;
  kind?: string;
  gate_kind?: string;
  target?: string;
  steps?: string[];
  branches?: string[];
}

export interface WfEdge {
  from: string;
  to: string;
  when?: string;
}

export interface WfModel {
  id: string;
  name: string;
  description: string;
  start: string;
  nodes: WfNode[];
  edges: WfEdge[];
}

export interface ParseResult {
  model: WfModel | null;
  error: string | null;
}

export const WORKFLOW_EVENT_TYPES = [
  'issues',
  'issue_comment',
  'pull_request',
  'pull_request_review',
  'push',
  'release',
  'discussion',
] as const;

export type WorkflowEventType = (typeof WORKFLOW_EVENT_TYPES)[number];

export const WORKFLOW_EVENT_PREDICATE_TYPES = [
  'hasLabel',
  'isNotLabeledWith',
  'baseBranch',
  'reviewState',
  'ref',
  'category',
  'commentMatches',
] as const;

export type WorkflowEventPredicateType = (typeof WORKFLOW_EVENT_PREDICATE_TYPES)[number];

export interface WorkflowEventCondition {
  predicate: WorkflowEventPredicateType;
  values: string[];
  matchAny: boolean;
}

export interface WorkflowEventTrigger {
  event: WorkflowEventType;
  eventName: string;
  conditions: WorkflowEventCondition[];
}

export interface WorkflowScheduleTrigger {
  interval: 'daily' | 'weekly' | 'monthly';
  timeOfDay: string;
  dayOfWeek?: string;
  dayOfMonth?: number;
}

export const WORKFLOW_EVENT_PREDICATES_BY_EVENT: Record<WorkflowEventType, WorkflowEventPredicateType[]> = {
  issues: ['hasLabel', 'isNotLabeledWith'],
  issue_comment: ['commentMatches'],
  pull_request: ['hasLabel', 'isNotLabeledWith', 'baseBranch'],
  pull_request_review: ['reviewState'],
  push: ['ref'],
  release: [],
  discussion: ['category'],
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

function asString(v: unknown): string | undefined {
  if (v === null || v === undefined) return undefined;
  return String(v);
}

function asStringArray(v: unknown): string[] | undefined {
  if (Array.isArray(v)) return v.map((x) => String(x));
  return undefined;
}

/** Parse YAML text into the graph model. Returns a structured error when the
 *  document is not parseable so the editor can show the last valid graph + the error. */
export function parseWorkflowYaml(text: string): ParseResult {
  let obj: Record<string, unknown>;
  try {
    const doc = parseDocument(text);
    if (doc.errors.length > 0) {
      return { model: null, error: doc.errors[0].message };
    }
    const js = doc.toJS();
    if (js === null || typeof js !== 'object' || Array.isArray(js)) {
      return { model: null, error: 'Workflow YAML must be a mapping at the top level.' };
    }
    obj = js as Record<string, unknown>;
  } catch (err) {
    return { model: null, error: err instanceof Error ? err.message : String(err) };
  }

  const rawNodes = Array.isArray(obj.nodes) ? (obj.nodes as unknown[]) : [];
  const nodes: WfNode[] = rawNodes
    .filter((n): n is Record<string, unknown> => !!n && typeof n === 'object')
    .map((n) => ({
      id: asString(n.id) ?? '',
      type: asString(n.type) ?? 'prompt',
      label: asString(n.label),
      agent: asString(n.agent),
      prompt: asString(n.prompt),
      model: asString(n.model),
      role: asString(n.role),
      kind: asString(n.kind),
      gate_kind: asString(n.gate_kind),
      target: asString(n.target),
      steps: asStringArray(n.steps),
      branches: asStringArray(n.branches),
    }));

  const rawEdges = Array.isArray(obj.edges) ? (obj.edges as unknown[]) : [];
  const edges: WfEdge[] = rawEdges
    .filter((e): e is Record<string, unknown> => !!e && typeof e === 'object')
    .map((e) => ({
      from: asString(e.from) ?? '',
      to: asString(e.to) ?? '',
      when: asString(e.when),
    }));

  return {
    model: {
      id: asString(obj.id) ?? '',
      name: asString(obj.name) ?? '',
      description: asString(obj.description) ?? '',
      start: asString(obj.start) ?? '',
      nodes,
      edges,
    },
    error: null,
  };
}

// ---------------------------------------------------------------------------
// Mutation helpers — operate on the YAML Document so unknown fields/comments
// survive the round-trip. Each returns the new YAML text.
// ---------------------------------------------------------------------------

function withDoc(text: string, fn: (doc: Document) => void): string {
  const doc = parseDocument(text);
  fn(doc);
  return doc.toString();
}

function nodesSeqItems(doc: Document) {
  const s = doc.get('nodes');
  return isSeq(s) ? s.items : [];
}

function nodeIndexById(doc: Document, id: string): number {
  return nodesSeqItems(doc).findIndex((it) => isMap(it) && it.get('id') === id);
}

function edgesSeqItems(doc: Document) {
  const s = doc.get('edges');
  return isSeq(s) ? s.items : [];
}

/** Set or delete a top-level scalar workflow field (id/name/description/start). */
export function setHeaderField(text: string, field: string, value: string): string {
  return withDoc(text, (doc) => {
    if (value === '') {
      // Keep id/name/start present (required); only description may be cleared.
      if (field === 'description') doc.delete(field);
      else doc.set(field, '');
    } else {
      doc.set(field, value);
    }
  });
}

/** Set or remove a schedule trigger while preserving the rest of the workflow document. */
export function setScheduleTrigger(
  text: string,
  trigger: WorkflowScheduleTrigger | null,
): string {
  return withDoc(text, (doc) => {
    if (!isMap(doc.contents)) return;
    if (trigger === null) {
      writeTriggers(doc, readTriggers(doc).filter((entry) => entry.type !== 'schedule'));
      return;
    }
    const value: Record<string, string | number> = {
      type: 'schedule',
      interval: trigger.interval,
      time_of_day: trigger.timeOfDay,
    };
    if (trigger.interval === 'weekly' && trigger.dayOfWeek) value.day_of_week = trigger.dayOfWeek;
    if (trigger.interval === 'monthly' && trigger.dayOfMonth) value.day_of_month = trigger.dayOfMonth;
    writeTriggers(doc, upsertTrigger(readTriggers(doc), value));
  });
}

function readTriggers(doc: Document): Array<Record<string, unknown>> {
  const root = doc.toJS();
  if (!isRecord(root)) return [];
  if (Array.isArray(root.triggers)) {
    return root.triggers.filter((entry): entry is Record<string, unknown> => isRecord(entry));
  }
  return isRecord(root.trigger) ? [root.trigger] : [];
}

function writeTriggers(doc: Document, triggers: Array<Record<string, unknown>>): void {
  if (!isMap(doc.contents)) return;
  doc.contents.delete('trigger');
  doc.contents.delete('triggers');
  if (triggers.length === 1) {
    doc.contents.set('trigger', triggers[0]);
  } else if (triggers.length > 1) {
    doc.contents.set('triggers', triggers);
  }
}

function upsertTrigger(
  triggers: Array<Record<string, unknown>>,
  replacement: Record<string, unknown>,
): Array<Record<string, unknown>> {
  const index = triggers.findIndex((entry) => entry.type === replacement.type);
  if (index < 0) return [...triggers, replacement];
  return triggers.map((entry, entryIndex) => entryIndex === index ? replacement : entry);
}

/** Read the schedule trigger from either the legacy singular or multi-trigger YAML shape. */
export function getScheduleTrigger(text: string): WorkflowScheduleTrigger | null {
  try {
    const js = parseDocument(text).toJS();
    if (!isRecord(js)) return null;
    const trigger = Array.isArray(js.triggers)
      ? js.triggers.find((entry): entry is Record<string, unknown> => isRecord(entry) && entry.type === 'schedule') ?? null
      : isRecord(js.trigger) ? js.trigger : null;
    if (!trigger || trigger.type !== 'schedule') return null;

    const interval = trigger.interval;
    if (interval !== 'daily' && interval !== 'weekly' && interval !== 'monthly') return null;
    const timeOfDay = asString(trigger.time_of_day);
    if (!timeOfDay) return null;

    const dayOfMonth = Number(trigger.day_of_month);
    return {
      interval,
      timeOfDay,
      dayOfWeek: asString(trigger.day_of_week),
      dayOfMonth: Number.isInteger(dayOfMonth) && dayOfMonth > 0 ? dayOfMonth : undefined,
    };
  } catch {
    return null;
  }
}

export function scheduleTriggerLabel(trigger: WorkflowScheduleTrigger): string {
  return `${trigger.interval} · ${trigger.timeOfDay} UTC`;
}

function normalizeEventName(event: WorkflowEventType): string {
  return `github.${event}`;
}

function parseEventName(eventName: unknown): WorkflowEventType | null {
  if (typeof eventName !== 'string' || !eventName.startsWith('github.')) return null;
  const rawEvent = eventName.slice('github.'.length).split('.')[0];
  return WORKFLOW_EVENT_TYPES.find((event) => event === rawEvent) ?? null;
}

function escapeRegexLiteral(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function exactCommandPattern(value: string): string {
  return `^${escapeRegexLiteral(value)}$`;
}

function exactCommandFromPattern(pattern: string): string {
  if (!pattern.startsWith('^') || !pattern.endsWith('$')) return pattern;
  const body = pattern.slice(1, -1);
  let unescaped = '';
  for (let i = 0; i < body.length; i += 1) {
    const ch = body[i];
    if (ch === '\\' && i + 1 < body.length) {
      unescaped += body[i + 1];
      i += 1;
      continue;
    }
    unescaped += ch;
  }
  return unescaped;
}

function singleValueCondition(
  predicate: WorkflowEventPredicateType,
  value: string | undefined,
): WorkflowEventCondition | null {
  if (!value) return null;
  return {
    predicate,
    values: [value],
    matchAny: false,
  };
}

function parseEventCondition(value: unknown): WorkflowEventCondition | null {
  if (!isRecord(value)) return null;

  if (Array.isArray(value.or)) {
    const children = value.or.map((entry) => parseEventCondition(entry)).filter((entry): entry is WorkflowEventCondition => entry !== null);
    if (children.length === 0) return null;
    const [first, ...rest] = children;
    if (first.matchAny || rest.some((entry) => entry.matchAny || entry.predicate !== first.predicate || entry.values.length !== 1)) {
      return null;
    }
    return {
      predicate: first.predicate,
      values: children.map((entry) => entry.values[0]).filter(Boolean),
      matchAny: true,
    };
  }

  const hasLabel = isRecord(value.hasLabel) ? value.hasLabel : isRecord(value.has_label) ? value.has_label : null;
  if (hasLabel) {
    return singleValueCondition('hasLabel', asString(hasLabel.label));
  }
  const isNotLabeledWith = isRecord(value.isNotLabeledWith)
    ? value.isNotLabeledWith
    : isRecord(value.is_not_labeled_with)
      ? value.is_not_labeled_with
      : null;
  if (isNotLabeledWith) {
    return singleValueCondition('isNotLabeledWith', asString(isNotLabeledWith.label));
  }
  const baseBranch = isRecord(value.baseBranch) ? value.baseBranch : isRecord(value.base_branch) ? value.base_branch : null;
  if (baseBranch) {
    return singleValueCondition('baseBranch', asString(baseBranch.branch));
  }
  const reviewState = isRecord(value.reviewState) ? value.reviewState : isRecord(value.review_state) ? value.review_state : null;
  if (reviewState) {
    return singleValueCondition('reviewState', asString(reviewState.state));
  }
  if (isRecord(value.ref)) {
    return singleValueCondition('ref', asString(value.ref.branch) ?? asString(value.ref.value));
  }
  if (isRecord(value.category)) {
    return singleValueCondition('category', asString(value.category.name));
  }
  const commentMatches = isRecord(value.commentMatches) ? value.commentMatches : isRecord(value.comment_matches) ? value.comment_matches : null;
  if (commentMatches) {
    const pattern = asString(commentMatches.pattern);
    return singleValueCondition('commentMatches', pattern ? exactCommandFromPattern(pattern) : undefined);
  }

  return null;
}

function serializeEventPredicate(predicate: WorkflowEventPredicateType, value: string): Record<string, unknown> {
  switch (predicate) {
    case 'hasLabel':
      return { has_label: { label: value } };
    case 'isNotLabeledWith':
      return { is_not_labeled_with: { label: value } };
    case 'baseBranch':
      return { base_branch: { branch: value } };
    case 'reviewState':
      return { review_state: { state: value } };
    case 'ref':
      return { ref: { branch: value, match_mode: 'equals' } };
    case 'category':
      return { category: { name: value } };
    case 'commentMatches':
      return { comment_matches: { pattern: exactCommandPattern(value) } };
  }
}

export function getEventTrigger(text: string): WorkflowEventTrigger | null {
  let js: Record<string, unknown>;
  try {
    const doc = parseDocument(text);
    if (doc.errors.length > 0) return null;
    const next = doc.toJS();
    if (!isRecord(next)) return null;
    js = next;
  } catch {
    return null;
  }

  const trigger = Array.isArray(js.triggers)
    ? js.triggers.find((entry): entry is Record<string, unknown> => isRecord(entry) && entry.type === 'event') ?? null
    : isRecord(js.trigger) ? js.trigger : null;
  if (!trigger || trigger.type !== 'event') return null;
  const event = parseEventName(trigger.event_name);
  if (!event) return null;

  const rawConditions = Array.isArray(trigger.if) ? trigger.if : [];
  const conditions = rawConditions
    .map((entry) => parseEventCondition(entry))
    .filter((entry): entry is WorkflowEventCondition => entry !== null);

  return {
    event,
    eventName: typeof trigger.event_name === 'string' ? trigger.event_name : normalizeEventName(event),
    conditions,
  };
}

/** Set or remove an event trigger while preserving the rest of the workflow document. */
export function setEventTrigger(text: string, trigger: WorkflowEventTrigger | null): string {
  return withDoc(text, (doc) => {
    if (!isMap(doc.contents)) return;
    if (trigger === null) {
      writeTriggers(doc, readTriggers(doc).filter((entry) => entry.type !== 'event'));
      return;
    }

    const eventName = trigger.eventName.startsWith(`github.${trigger.event}`)
      ? trigger.eventName
      : normalizeEventName(trigger.event);
    const conditions = trigger.conditions
      .map((condition) => ({
        ...condition,
        values: condition.values.map((value) => value.trim()).filter(Boolean),
      }))
      .filter((condition) => condition.values.length > 0);

    const serializedConditions = conditions.map((condition) =>
      condition.matchAny && condition.values.length > 1
        ? { or: condition.values.map((value) => serializeEventPredicate(condition.predicate, value)) }
        : serializeEventPredicate(condition.predicate, condition.values[0]),
    );

    const value: Record<string, unknown> = {
      type: 'event',
      event_name: eventName,
    };
    if (serializedConditions.length > 0) value.if = serializedConditions;
    writeTriggers(doc, upsertTrigger(readTriggers(doc), value));
  });
}

/** Set or delete a scalar field on the node identified by id. */
export function setNodeField(text: string, id: string, field: string, value: string): string {
  return withDoc(text, (doc) => {
    const idx = nodeIndexById(doc, id);
    if (idx < 0) return;
    if (value === '') doc.deleteIn(['nodes', idx, field]);
    else doc.setIn(['nodes', idx, field], value);
  });
}

/** Rename a node id and update every edge endpoint and the workflow start that referenced it. */
export function renameNode(text: string, oldId: string, newId: string): string {
  return withDoc(text, (doc) => {
    const idx = nodeIndexById(doc, oldId);
    if (idx < 0 || newId === '' || newId === oldId) return;
    doc.setIn(['nodes', idx, 'id'], newId);
    edgesSeqItems(doc).forEach((it) => {
      if (!isMap(it)) return;
      if (it.get('from') === oldId) it.set('from', newId);
      if (it.get('to') === oldId) it.set('to', newId);
    });
    if (doc.get('start') === oldId) doc.set('start', newId);
  });
}

/** Append a new typed node with a sensible default label. */
export function addNode(text: string, node: {
  id: string;
  type: string;
  label?: string;
  role?: string;
  kind?: string;
  gate_kind?: string;
  agent?: string;
  branches?: string[];
}): string {
  return withDoc(text, (doc) => {
    if (!isSeq(doc.get('nodes'))) doc.set('nodes', []);
    const record: Record<string, string | string[]> = {
      id: node.id,
      type: node.type,
      label: node.label ?? node.id,
    };
    if (node.role) record.role = node.role;
    if (node.kind) record.kind = node.kind;
    if (node.gate_kind) record.gate_kind = node.gate_kind;
    if (node.agent) record.agent = node.agent;
    if (node.branches) record.branches = node.branches;
    doc.addIn(['nodes'], record);
  });
}

/** Set or delete a string-array field on a node. */
export function setNodeStringArrayField(text: string, id: string, field: string, values: string[]): string {
  return withDoc(text, (doc) => {
    const idx = nodeIndexById(doc, id);
    if (idx < 0) return;
    const cleaned = values.map((v) => v.trim()).filter(Boolean);
    if (cleaned.length === 0) doc.deleteIn(['nodes', idx, field]);
    else doc.setIn(['nodes', idx, field], cleaned);
  });
}

/** Remove a node and every edge that references it. */
export function removeNode(text: string, id: string): string {
  return withDoc(text, (doc) => {
    const idx = nodeIndexById(doc, id);
    if (idx >= 0) doc.deleteIn(['nodes', idx]);
    const edges = doc.get('edges');
    if (isSeq(edges)) {
      // Delete from the end so indices stay valid.
      for (let i = edges.items.length - 1; i >= 0; i--) {
        const it = edges.items[i];
        if (isMap(it) && (it.get('from') === id || it.get('to') === id)) {
          doc.deleteIn(['edges', i]);
        }
      }
    }
  });
}

/** Append a new edge, optionally with a `when` condition. */
export function addEdge(text: string, from: string, to: string, when?: string): string {
  return withDoc(text, (doc) => {
    if (!isSeq(doc.get('edges'))) doc.set('edges', []);
    const edge: Record<string, string> = { from, to };
    if (when) edge.when = when;
    doc.addIn(['edges'], edge);
  });
}

/** Create/update/remove the edge for one branch verdict from a gate-like node. */
export function setBranchTarget(text: string, from: string, when: string, to: string): string {
  return withDoc(text, (doc) => {
    if (!isSeq(doc.get('edges'))) doc.set('edges', []);
    const items = edgesSeqItems(doc);
    const idx = items.findIndex((it) => isMap(it) && it.get('from') === from && it.get('when') === when);
    if (to === '') {
      if (idx >= 0) doc.deleteIn(['edges', idx]);
      return;
    }
    if (idx >= 0) {
      doc.setIn(['edges', idx, 'to'], to);
      return;
    }
    doc.addIn(['edges'], { from, to, when });
  });
}

/** Set or delete a field on the edge at the given index. */
export function setEdgeFieldAt(text: string, index: number, field: string, value: string): string {
  return withDoc(text, (doc) => {
    const items = edgesSeqItems(doc);
    if (index < 0 || index >= items.length) return;
    if (value === '') doc.deleteIn(['edges', index, field]);
    else doc.setIn(['edges', index, field], value);
  });
}

/** Remove the edge at the given index. */
export function removeEdgeAt(text: string, index: number): string {
  return withDoc(text, (doc) => {
    const items = edgesSeqItems(doc);
    if (index < 0 || index >= items.length) return;
    doc.deleteIn(['edges', index]);
  });
}

/** Read the workflow id from YAML text (for routing the PUT), falling back when absent. */
export function readWorkflowId(text: string, fallback: string): string {
  const r = parseWorkflowYaml(text);
  return r.model?.id || fallback;
}
