import type { RunTraceSpanDto } from '../../api/types';
export type SpanType = 'invoke-agent' | 'llm' | 'tool';

export interface TraceNode {
  key: string;
  span: RunTraceSpanDto;
  type: SpanType;
  /** True when this node is a presentation-only LLM leaf synthesized from an agent span. */
  synthetic: boolean;
  children: TraceNode[];
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
 * Spans whose parent is missing from the set become roots. Each invoke-agent span that carries
 * model/token usage also gets a synthetic LLM leaf child so the tree mirrors the AppInsights
 * "Invoke Agent -> LLM -> Execute Tool" reference structure.
 */
export function buildTraceTree(spans: RunTraceSpanDto[]): TraceNode[] {
  if (!spans.length) return [];

  const nodes = new Map<string, TraceNode>();
  for (const span of spans) {
    nodes.set(span.id, { key: span.id, span, type: normalizeType(span), synthetic: false, children: [] });
  }

  const roots: TraceNode[] = [];
  for (const span of spans) {
    const node = nodes.get(span.id)!;
    const parent = span.parentId ? nodes.get(span.parentId) : undefined;
    if (parent && parent !== node) parent.children.push(node);
    else roots.push(node);
  }

  const sortByTime = (a: TraceNode, b: TraceNode) =>
    new Date(a.span.timestamp).getTime() - new Date(b.span.timestamp).getTime();

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
