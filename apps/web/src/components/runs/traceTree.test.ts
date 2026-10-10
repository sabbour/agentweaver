import { describe, expect, it } from 'vitest';
import { buildTraceTree } from './traceTree';
import type { RunTraceSpanDto } from '../../api/types';

describe('buildTraceTree', () => {
  it('renders a child-run tool below its child agent without altering the distributed parent link', () => {
    const spans: RunTraceSpanDto[] = [
      {
        id: 'coordinator-turn',
        name: 'Coordinator turn',
        spanType: 'invoke-agent',
        timestamp: '2026-09-11T16:00:00.000Z',
        durationMs: 5_000,
        success: true,
        agentName: 'Coordinator',
        attributes: { runId: 'coordinator-run' },
      },
      {
        id: 'alia-turn',
        name: 'Alia turn',
        spanType: 'invoke-agent',
        timestamp: '2026-09-11T16:00:01.000Z',
        durationMs: 3_000,
        success: true,
        agentName: 'Alia',
        attributes: { runId: 'alia-run', parentRunId: 'coordinator-run' },
      },
      {
        id: 'alia-tool',
        parentId: 'coordinator-turn',
        name: 'Search repository',
        spanType: 'tool',
        timestamp: '2026-09-11T16:00:02.000Z',
        durationMs: 100,
        success: true,
        agentName: 'Alia',
        toolName: 'search',
        attributes: { runId: 'alia-run', parentRunId: 'coordinator-run' },
      },
    ];

    const tree = buildTraceTree(spans);

    expect(spans[2].parentId).toBe('coordinator-turn');
    expect(tree).toHaveLength(1);
    expect(tree[0].key).toBe('coordinator-turn');
    expect(tree[0].children.map((node) => node.key)).toEqual(['alia-turn']);
    expect(tree[0].children[0].children.map((node) => node.key)).toEqual(['alia-tool']);
  });

  it('keeps same-run Activity parenting unchanged', () => {
    const spans: RunTraceSpanDto[] = [
      {
        id: 'alia-turn',
        name: 'Alia turn',
        spanType: 'invoke-agent',
        timestamp: '2026-09-11T16:00:01.000Z',
        durationMs: 3_000,
        success: true,
        agentName: 'Alia',
        attributes: { runId: 'alia-run', parentRunId: 'coordinator-run' },
      },
      {
        id: 'alia-tool',
        parentId: 'alia-turn',
        name: 'Search repository',
        spanType: 'tool',
        timestamp: '2026-09-11T16:00:02.000Z',
        durationMs: 100,
        success: true,
        agentName: 'Alia',
        toolName: 'search',
        attributes: { runId: 'alia-run', parentRunId: 'coordinator-run' },
      },
    ];

    const tree = buildTraceTree(spans);

    expect(tree).toHaveLength(1);
    expect(tree[0].key).toBe('alia-turn');
    expect(tree[0].children.map((node) => node.key)).toEqual(['alia-tool']);
  });
});
