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
  kind?: 'graph';
  title: string;
  alt: string;
  direction?: 'TB' | 'LR';
  groups?: GraphGroup[];
  nodes: GraphNode[];
  edges: GraphEdge[];
}

export interface SequenceParticipant {
  id: string;
  label: string;
  subLabel?: string;
  icon: IconKind;
  badge: { text: string; tone: BadgeTone };
}

export interface SequenceMessage {
  type: 'message';
  from: string;
  to: string;
  label: string;
  line?: 'solid' | 'dashed';
  arrow?: 'filled' | 'open' | 'cross';
}

export interface SequenceActivation {
  type: 'activation';
  participant: string;
  action: 'start' | 'end';
}

export interface SequenceNote {
  type: 'note';
  over: string[];
  label: string;
}

export interface SequenceFragmentSection {
  label?: string;
  steps: SequenceStep[];
}

export interface SequenceFragment {
  type: 'fragment';
  operator: 'alt' | 'opt' | 'loop';
  label?: string;
  sections: SequenceFragmentSection[];
}

export type SequenceStep =
  | SequenceMessage
  | SequenceActivation
  | SequenceNote
  | SequenceFragment;

export interface SequenceSpec {
  kind: 'sequence';
  title: string;
  alt: string;
  autonumber?: boolean;
  participants: SequenceParticipant[];
  steps: SequenceStep[];
}

export type DiagramSpec = GraphSpec | SequenceSpec;
