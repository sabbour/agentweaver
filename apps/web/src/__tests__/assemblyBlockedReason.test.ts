import {
  normalizeAssemblyBlockedReason,
  parseIneligibleIdsFromReason,
  readIneligibleSubtasks,
} from '../pages/CoordinatorRunPage';
import { describe, expect, it } from 'vitest';

// #97 — the coordinator must never surface the opaque `ineligible_subtasks [59,60,61,62]` code
// (nor the old "The collective assembly could not complete." fallback). These pure helpers back the
// structured ineligible-subtask surfacing in CoordinatorRunPage.

describe('parseIneligibleIdsFromReason', () => {
  it('extracts bracketed ids from an ineligible_subtasks reason', () => {
    expect(parseIneligibleIdsFromReason('ineligible_subtasks [59,60,61,62]')).toEqual([
      '59', '60', '61', '62',
    ]);
  });

  it('tolerates the assembly_blocked: prefix and whitespace', () => {
    expect(parseIneligibleIdsFromReason('assembly_blocked: ineligible_subtasks [ 7 , 8 ]')).toEqual([
      '7', '8',
    ]);
  });

  it('returns [] for a non-ineligible reason', () => {
    expect(parseIneligibleIdsFromReason('integration_build_error')).toEqual([]);
    expect(parseIneligibleIdsFromReason(undefined)).toEqual([]);
    expect(parseIneligibleIdsFromReason('')).toEqual([]);
  });
});

describe('normalizeAssemblyBlockedReason', () => {
  it('turns the opaque ineligible_subtasks code into readable prose (plural)', () => {
    expect(normalizeAssemblyBlockedReason('ineligible_subtasks [59,60,61,62]')).toBe(
      "Waiting on 4 subtasks that aren't ready to assemble (#59, #60, #61, #62).",
    );
  });

  it('uses the singular form for a single ineligible subtask', () => {
    expect(normalizeAssemblyBlockedReason('assembly_blocked: ineligible_subtasks [59]')).toBe(
      "Waiting on 1 subtask that isn't ready to assemble (#59).",
    );
  });

  it('humanizes other blocked reason codes (strip prefix, underscores to spaces)', () => {
    expect(normalizeAssemblyBlockedReason('assembly_blocked: integration_build_error')).toBe(
      'integration build error',
    );
  });

  it('returns undefined for an empty reason', () => {
    expect(normalizeAssemblyBlockedReason(undefined)).toBeUndefined();
    expect(normalizeAssemblyBlockedReason('   ')).toBeUndefined();
  });
});

describe('readIneligibleSubtasks', () => {
  it('parses the enriched id/title/status/agent detail', () => {
    const result = readIneligibleSubtasks({
      reason: 'ineligible_subtasks',
      ineligibleSubtaskIds: [59, 60],
      ineligibleSubtasks: [
        { id: 59, title: 'Auth API', status: 'failed', agent: 'morpheus' },
        { id: 60, title: 'DB layer', status: 'running', assignedAgent: 'trinity' },
      ],
    });
    expect(result).toEqual([
      { id: '59', title: 'Auth API', status: 'failed', agent: 'morpheus' },
      { id: '60', title: 'DB layer', status: 'running', agent: 'trinity' },
    ]);
  });

  it('falls back to the id-only list when no enriched detail is present', () => {
    expect(readIneligibleSubtasks({ ineligibleSubtaskIds: [7, 8] })).toEqual([
      { id: '7' }, { id: '8' },
    ]);
  });

  it('recovers ids from the reason string when nothing else is present', () => {
    expect(readIneligibleSubtasks({ reason: 'ineligible_subtasks [3,4]' })).toEqual([
      { id: '3' }, { id: '4' },
    ]);
  });

  it('returns [] when the payload carries no ineligible hint', () => {
    expect(readIneligibleSubtasks({ reason: 'integration_build_error' })).toEqual([]);
  });
});
