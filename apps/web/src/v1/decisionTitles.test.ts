import { describe, expect, it } from 'vitest';
import { DECISION_TITLES } from './decisionTitles';

describe('DECISION_TITLES', () => {
  it('gives each pending request kind a plain title', () => {
    expect(DECISION_TITLES).toEqual({
      awaitingInput: 'Question',
      awaitingPlanApproval: 'Approve the work plan',
      awaitingApproval: 'Approval needed',
      awaitingOutcomeConfirmation: 'Confirm the outcome',
    });
  });
});
