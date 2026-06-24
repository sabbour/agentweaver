// Shared, verbatim help copy for the coordinator steering verbs (Send / Redirect /
// Amend). Centralized so the explanation stays consistent across the inline steer
// composer, the steer dialog, and the recovery SteerPanel. These definitions are the
// contract agreed with the backend steering semantics (Morpheus, 2026-06-22).

export const STEERING_HELP = {
  send: 'Message the coordinator without changing the current plan.',
  redirect:
    'Override the current plan and point the coordinator at a new instruction (can unblock a stuck child).',
  amend: 'Add to the outcome/plan without discarding in-flight work.',
} as const;

export interface SteeringVerb {
  kind: 'send' | 'redirect' | 'amend';
  label: string;
  help: string;
}

// Ordered for display in the steering legend.
export const STEERING_VERBS: readonly SteeringVerb[] = [
  { kind: 'send', label: 'Send', help: STEERING_HELP.send },
  { kind: 'redirect', label: 'Redirect', help: STEERING_HELP.redirect },
  { kind: 'amend', label: 'Amend', help: STEERING_HELP.amend },
];
