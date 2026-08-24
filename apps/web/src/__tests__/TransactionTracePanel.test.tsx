import { aggregateNanoAiu, buildToolCallIndex, buildTraceTree, totalNanoAiu } from '../components/runs/traceTree';
import { describe, expect, it } from 'vitest';
import type { PersistedRunEvent, RunTraceSpanDto } from '../api/types';
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

describe('buildToolCallIndex', () => {
  function event(type: string, payload: Record<string, unknown>): PersistedRunEvent {
    return { sequence: 0, type, payload };
  }

  it('pairs tool.call arguments with a matching tool.result content by callId', () => {
    const events: PersistedRunEvent[] = [
      event('tool.call', { callId: 'c1', toolName: 'start_preview_process', arguments: { command: 'python3 -m http.server 9090' } }),
      event('tool.result', { callId: 'c1', content: 'preview_process_started: pid=594' }),
    ];
    const index = buildToolCallIndex(events);
    const detail = index.get('c1');
    expect(detail?.arguments).toEqual({ command: 'python3 -m http.server 9090' });
    expect(detail?.content).toBe('preview_process_started: pid=594');
    expect(detail?.errorMessage).toBeUndefined();
  });

  it('pairs tool.call arguments with a matching tool.error message by callId', () => {
    const events: PersistedRunEvent[] = [
      event('tool.call', { callId: 'c2', toolName: 'observe_bound_port', arguments: { port: 9090 } }),
      event('tool.error', { callId: 'c2', errorMessage: 'Tool execution failed' }),
    ];
    const index = buildToolCallIndex(events);
    const detail = index.get('c2');
    expect(detail?.arguments).toEqual({ port: 9090 });
    expect(detail?.errorMessage).toBe('Tool execution failed');
    expect(detail?.content).toBeUndefined();
  });

  it('ignores events without a callId and ignores unrelated event types', () => {
    const events: PersistedRunEvent[] = [
      event('tool.call', { toolName: 'no_call_id' }),
      event('agent.message', { text: 'hello' }),
    ];
    expect(buildToolCallIndex(events).size).toBe(0);
  });

  it('returns an empty index for no events', () => {
    expect(buildToolCallIndex([]).size).toBe(0);
  });
});

describe('AIC (AI Credit) aggregation', () => {
  it('attributes cost to a real llm node from its own totalNanoAiu', () => {
    const spans: RunTraceSpanDto[] = [
      span({ id: 'llm-1', spanType: 'llm', model: 'gpt-4o', totalNanoAiu: 5_000_000_000 }),
    ];
    const tree = buildTraceTree(spans);
    expect(aggregateNanoAiu(tree[0])).toBe(5_000_000_000);
  });

  it('attributes zero cost to a tool node with no descendants', () => {
    const spans: RunTraceSpanDto[] = [
      span({ id: 'tool-1', spanType: 'tool', toolName: 'grep' }),
    ];
    const tree = buildTraceTree(spans);
    expect(aggregateNanoAiu(tree[0])).toBe(0);
  });

  it('aggregates an invoke-agent node from its synthetic LLM leaf (single turn)', () => {
    const spans: RunTraceSpanDto[] = [
      span({ id: 'agent', spanType: 'invoke-agent', agentName: 'trinity', model: 'gpt-4o', totalNanoAiu: 7_667_650_000 }),
    ];
    const tree = buildTraceTree(spans);
    expect(aggregateNanoAiu(tree[0])).toBe(7_667_650_000);
  });

  it('aggregates an invoke-agent node across multiple nested turns and tool calls', () => {
    const spans: RunTraceSpanDto[] = [
      span({ id: 'agent', spanType: 'invoke-agent', agentName: 'trinity' }),
      span({ id: 'turn-1', parentId: 'agent', spanType: 'llm', model: 'gpt-4o', totalNanoAiu: 2_000_000_000, timestamp: '2026-07-06T00:00:01.000Z' }),
      span({ id: 'tool-1', parentId: 'agent', spanType: 'tool', toolName: 'grep', timestamp: '2026-07-06T00:00:02.000Z' }),
      span({ id: 'turn-2', parentId: 'agent', spanType: 'llm', model: 'gpt-4o', totalNanoAiu: 3_000_000_000, timestamp: '2026-07-06T00:00:03.000Z' }),
    ];
    const tree = buildTraceTree(spans);
    expect(aggregateNanoAiu(tree[0])).toBe(5_000_000_000);
  });

  it('rolls up the run-level total across every root span in the forest', () => {
    const spans: RunTraceSpanDto[] = [
      span({ id: 'agent-1', spanType: 'invoke-agent', agentName: 'trinity', model: 'gpt-4o', totalNanoAiu: 1_000_000_000 }),
      span({ id: 'agent-2', spanType: 'invoke-agent', agentName: 'tank', model: 'gpt-4o', totalNanoAiu: 4_000_000_000, timestamp: '2026-07-06T00:01:00.000Z' }),
    ];
    const tree = buildTraceTree(spans);
    expect(totalNanoAiu(tree)).toBe(5_000_000_000);
  });

  it('returns zero for an empty forest', () => {
    expect(totalNanoAiu([])).toBe(0);
  });
});
