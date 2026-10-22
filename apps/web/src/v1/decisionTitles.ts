/** Plain-English titles for pending request kinds, shared by decisions and attention lists. */
import type { CoordinationBlockerKind } from './contracts';

/** Title shown for each kind of pending request. */
export const DECISION_TITLES: Record<CoordinationBlockerKind, string> = {
  awaitingInput: 'Question',
  awaitingPlanApproval: 'Approve the work plan',
  awaitingApproval: 'Approval needed',
  awaitingOutcomeConfirmation: 'Confirm the outcome',
};
