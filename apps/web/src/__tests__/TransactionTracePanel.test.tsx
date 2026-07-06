import { describe, it, expect } from 'vitest';
import { buildTraceTree } from '../components/runs/traceTree';
import type { RunTraceSpanDto } from '../api/types';

function span(partial: Partial<RunTraceSpanDto> & { id: string }): RunTraceSpanDto {
  return {
    name: partial.id,
    timestamp: '2026-07-06T00:00:00.000Z',
    durationMs: 100,
    success: true,
    ...partial,
  } as RunTraceSpanDto;
}

describe('buildTraceTree', () => {
  it('reconstructs a parent/child hierarchy from parentId links', () => {
    const spans: RunTraceSpanDto[] = [
      span({ id: 'agent', spanType: 'invoke-agent', agentName: 'coordinator', timestamp: '2026-07-06T00:00:00.000Z' }),
      span({ id: 'tool-1', parentId: 'agent', spanType: 'tool', toolName: 'mock_search', timestamp: '2026-07-06T00:00:01.000Z' }),
    ];

    const tree = buildTraceTree(spans);
    expect(tree).toHaveLength(1);
    expect(tree[0].type).toBe('invoke-agent');
    const toolChild = tree[0].children.find((c) => c.type === 'tool');
    expect(toolChild?.span.toolName).toBe('mock_search');
  });

  it('synthesizes an LLM leaf under an agent span carrying model/tokens', () => {
    const spans: RunTraceSpanDto[] = [
      span({ id: 'agent', spanType: 'invoke-agent', agentName: 'coordinator', model: 'gpt-4o', inputTokens: 512, outputTokens: 128 }),
    ];

    const tree = buildTraceTree(spans);
    const llm = tree[0].children.find((c) => c.type === 'llm');
    expect(llm).toBeDefined();
    expect(llm?.synthetic).toBe(true);
    expect(llm?.span.model).toBe('gpt-4o');
  });

  it('treats spans with missing parents as roots', () => {
    const spans: RunTraceSpanDto[] = [
      span({ id: 'orphan', parentId: 'not-present', spanType: 'tool', toolName: 'grep' }),
    ];
    const tree = buildTraceTree(spans);
    expect(tree).toHaveLength(1);
    expect(tree[0].span.id).toBe('orphan');
  });

  it('returns an empty forest for no spans', () => {
    expect(buildTraceTree([])).toEqual([]);
  });
});
