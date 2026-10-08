import { useMemo, useState } from 'react';
import { DocumentRegular, ShieldTaskRegular } from '@fluentui/react-icons';
import { AgenticProgress, NotificationPattern, type AzfAgentStep } from '..';
const baseSteps: AzfAgentStep[] = [
  {
    id: 'collect-signals',
    title: 'Collect service signals',
    body: 'Gather recent alerts, deployment history, and health probes for the selected workspace.',
    status: 'complete',
    artifacts: [{ id: 'signal-report', title: 'signal-report.json', type: 'JSON', icon: <DocumentRegular /> }],
  },
  {
    id: 'evaluate-risk',
    title: 'Evaluate remediation risk',
    body: 'Copilot found a rollback option but needs an approval before changing live traffic.',
    status: 'warning',
    needsInput: true,
    riskText: 'Rollback swaps traffic back to the previous revision and may interrupt active sessions for up to 30 seconds.',
    artifacts: [{ id: 'rollback-plan', title: 'rollback-plan.md', type: 'Plan', icon: <ShieldTaskRegular /> }],
  },
  {
    id: 'apply-change',
    title: 'Apply approved change',
    body: 'Waiting for approval to continue.',
    status: 'pending',
  },
];

export function AgenticProgressExample() {
  const [steps, setSteps] = useState(baseSteps);
  const [decision, setDecision] = useState<string | undefined>();

  const openItems = useMemo(
    () => steps.filter((step) => step.needsInput || step.status === 'running').map((step) => step.id),
    [steps],
  );

  const completeDecision = (approved: boolean) => {
    setDecision(approved ? 'Approved rollback plan.' : 'Denied rollback plan and requested more investigation.');
    setSteps((current) =>
      current.map((step) => {
        if (step.id === 'evaluate-risk') {
          return {
            ...step,
            needsInput: false,
            status: approved ? 'complete' : 'blocked',
            body: approved
              ? 'Rollback was approved and handed off to the executor.'
              : 'Rollback was denied. Capture more context before retrying.',
          };
        }

        if (step.id === 'apply-change') {
          return {
            ...step,
            status: approved ? 'running' : 'blocked',
            body: approved ? 'Applying the approved rollback now.' : 'Change application is blocked until a new plan is approved.',
          };
        }

        return step;
      }),
    );
  };

  return (
    <div className="azf-stack azf-gap-s">
      {decision && (
        <NotificationPattern
          title="Latest operator decision"
          body={decision}
          intent={decision.startsWith('Approved') ? 'success' : 'warning'}
        />
      )}
      <AgenticProgress
        steps={steps}
        defaultOpenItems={openItems}
        onApprove={() => completeDecision(true)}
        onDeny={() => completeDecision(false)}
      />
    </div>
  );
}
