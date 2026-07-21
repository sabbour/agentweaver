/**
 * Diagram source schema (docs/diagrams/src/*.json).
 *
 * Deliberately plain data — no hand-placed coordinates. `dagre` computes every
 * node/group position at render time (see layout.ts), exactly like the
 * production Coordinator/landing graphs in apps/web lay themselves out.
 */
export interface DiagramGroup {
  id: string;
  label: string;
  /** Parent group id, for nesting (e.g. "core" inside "aks" inside "azure"). */
  parent?: string;
}

export interface DiagramNode {
  id: string;
  /** `\n`-separated lines rendered as stacked <tspan>s — no dynamic wrapping. */
  label: string;
  variant: NodeVariant;
  /** Group id this node belongs to, if any. */
  parent?: string;
}

export interface DiagramEdge {
  source: string;
  target: string;
  label?: string;
  /** "solid" (default, arrowed) | "dashed" (arrowed, dependency/pull) | "plain" (no arrowhead, shared link). */
  style?: 'solid' | 'dashed' | 'plain';
}

export interface DiagramSource {
  title: string;
  direction: 'TB' | 'LR';
  groups?: DiagramGroup[];
  nodes: DiagramNode[];
  edges: DiagramEdge[];
}

/**
 * Node colour categories, carried over 1:1 from the original Mermaid
 * `classDef`s so the rendered diagrams keep the same palette:
 *   client/svc/core/workerStyle/runtime/data/ext.
 */
export type NodeVariant = 'client' | 'svc' | 'core' | 'workerStyle' | 'runtime' | 'data' | 'ext';

export const NODE_VARIANT_STYLES: Record<NodeVariant, { fill: string; stroke: string; strokeWidth: number }> = {
  client: { fill: '#E8EEF9', stroke: '#0F6CBD', strokeWidth: 1 },
  svc: { fill: '#F3F2F1', stroke: '#8A8886', strokeWidth: 1 },
  core: { fill: '#CFE4FA', stroke: '#0F6CBD', strokeWidth: 2 },
  workerStyle: { fill: '#D9EFD9', stroke: '#107C10', strokeWidth: 2 },
  runtime: { fill: '#DDF3DD', stroke: '#107C10', strokeWidth: 1 },
  data: { fill: '#FFF4CE', stroke: '#C19C00', strokeWidth: 1 },
  ext: { fill: '#F0E8F8', stroke: '#8764B8', strokeWidth: 1 },
};

/** Matches WorkflowGraphPanel's neutral edge stroke (`STROKE_MUTED` resolves to this Fluent token). */
export const EDGE_STROKE_MUTED = '#C8C6C4';
export const TEXT_COLOR = '#242424';
export const GROUP_FILL = '#FAF9F8';
export const GROUP_BORDER = '#D2D0CE';
export const FONT_FAMILY = 'Segoe UI, system-ui, -apple-system, sans-serif';
