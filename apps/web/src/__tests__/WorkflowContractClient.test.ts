import { AgentweaverApiClient } from '../api/client';
import type { WorkflowGrammar } from '../api/types';
import { afterEach, describe, expect, it, vi } from 'vitest';

describe('workflow public contract client', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('requests the published workflow grammar', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: vi.fn().mockResolvedValue('{}'),
    });

    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('https://agentweaver.example', 'token');

    await client.getWorkflowGrammar();

    expect(fetchMock).toHaveBeenCalledWith(
      'https://agentweaver.example/api/workflows/grammar',
      expect.objectContaining({ method: 'GET' }),
    );
  });

  it('models the explicit authoring contract and legacy-loading boundary', () => {
    const grammar = {
      grammar_version: '1.1',
      format: 'yaml',
      root: {
        required_fields: ['id', 'name', 'start', 'nodes'],
        optional_fields: [],
        maximum_document_characters: 262144,
        maximum_nodes: 128,
        maximum_edges: 512,
        maximum_triggers: 16,
      },
      node_fields: {
        required_fields: ['id', 'type'],
        optional_fields: ['gate_kind'],
        maximum_prompt_characters: 16384,
        maximum_charter_characters: 8192,
      },
      node_types: [{
        yaml_type: 'check',
        api_type: 'check',
        label: 'Check / gate',
        authorable: true,
        runtime_bindable: true,
        runtime_kinds: ['rai', 'human-review', 'rubberduck'],
        required_fields: ['branches', 'gate_kind'],
        allowed_gate_kinds: ['rai', 'human-review', 'rubberduck'],
      }],
      edge: {
        required_fields: ['from', 'to'],
        optional_fields: ['when'],
        conditions_are_case_sensitive: false,
        transitions: [],
      },
      triggers: {
        types: [],
        schedule_intervals: [],
        review_states: [],
        ref_match_modes: [],
        predicate_types: [],
      },
      compatibility: {
        legacy_loading_only: true,
        check_gate_id_matching: 'case-insensitive',
        check_gate_id_fallbacks: { review: 'human-review' },
      },
    } satisfies WorkflowGrammar;

    expect(grammar.node_types[0].required_fields).toContain('gate_kind');
    expect(grammar.compatibility.check_gate_id_fallbacks.review).toBe('human-review');
  });

  it('serializes supported run-event filters', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: vi.fn().mockResolvedValue('[]'),
    });
    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('https://agentweaver.example', 'token');

    await client.getRunEvents('run id', { type: 'run.failed', after: 7, limit: 25 });

    expect(fetchMock).toHaveBeenCalledWith(
      'https://agentweaver.example/api/runs/run%20id/events?type=run.failed&after=7&limit=25',
      expect.objectContaining({ method: 'GET' }),
    );
  });
});
