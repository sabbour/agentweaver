import type { BadgeTone } from './theme';

export type IconKind =
  | 'globe'
  | 'branch'
  | 'route'
  | 'window'
  | 'server'
  | 'bot'
  | 'database'
  | 'key'
  | 'box';

export interface GraphGroup {
  id: string;
  label: string;
  tier: number;
  parent?: string;
}

export interface GraphNode {
  id: string;
  label: string;
  subLabel?: string;
  meta?: string;
  icon: IconKind;
  badge: { text: string; tone: BadgeTone };
  group?: string;
}

export interface GraphEdge {
  from: string;
  to: string;
  label?: string;
  dashed?: boolean;
  undirected?: boolean;
}

export interface GraphSpec {
  title: string;
  alt: string;
  direction?: 'TB' | 'LR';
  groups?: GraphGroup[];
  nodes: GraphNode[];
  edges: GraphEdge[];
}
