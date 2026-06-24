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
import { parseDocument, isSeq, isMap, type Document } from 'yaml';

/** The canonical node `type` strings the runtime loader accepts (WorkflowDefinitionLoader.TryParseNodeType). */
export const WORKFLOW_NODE_TYPES = [
  'prompt',
  'peer_review',
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

/** Human-friendly labels for the node-type picker. */
export const NODE_TYPE_LABELS: Record<string, string> = {
  prompt: 'Prompt (agent turn)',
  peer_review: 'Peer review',
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
  triggerType: string;
  start: string;
  nodes: WfNode[];
  edges: WfEdge[];
}

export interface ParseResult {
  model: WfModel | null;
  error: string | null;
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

  const trigger = (obj.trigger ?? {}) as Record<string, unknown>;

  return {
    model: {
      id: asString(obj.id) ?? '',
      name: asString(obj.name) ?? '',
      description: asString(obj.description) ?? '',
      triggerType: asString(trigger.type) ?? 'manual',
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

export function setTriggerType(text: string, value: string): string {
  return withDoc(text, (doc) => {
    doc.setIn(['trigger', 'type'], value);
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
export function addNode(text: string, node: { id: string; type: string }): string {
  return withDoc(text, (doc) => {
    if (!isSeq(doc.get('nodes'))) doc.set('nodes', []);
    doc.addIn(['nodes'], { id: node.id, type: node.type, label: node.id });
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
