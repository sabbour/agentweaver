import { AgenticApprovalPattern } from '..';

export function AgenticApprovalPatternExample() {
  return (
    <AgenticApprovalPattern
      title="Approve production remediation"
      summary="The coordinator paused for a human decision before modifying live clusters."
      defaultOpenItems={['approve']}
      steps={[
        {
          id: 'collect',
          title: 'Collect current rollout context',
          body: 'Gathering deployment summaries and artifact links.',
          status: 'complete',
          artifacts: [{ id: 'artifact-summary', title: 'Rollout summary', type: 'Markdown', onOpen: () => undefined }],
        },
        {
          id: 'approve',
          title: 'Request production approval',
          body: 'The next step modifies live clusters and may increase spend.',
          needsInput: true,
          status: 'warning',
          riskText: 'Approve to let the run continue, or deny to stop the workflow.',
          artifacts: [{ id: 'approval-packet', title: 'Approval packet', type: 'Artifact', onOpen: () => undefined }],
        },
      ]}
      onApprove={() => undefined}
      onDeny={() => undefined}
    />
  );
}
