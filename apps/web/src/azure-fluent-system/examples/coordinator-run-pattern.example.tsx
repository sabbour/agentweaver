import { useState } from 'react';
import { CoordinatorRunPattern } from '..';

export function CoordinatorRunPatternExample() {
  const [steering, setSteering] = useState('Prioritize the East US remediation and hold the rest until it clears.');

  return (
    <CoordinatorRunPattern
      title="Coordinator · rollout-remediation-run"
      subtitle="Multi-agent run · 3 of 4 steps complete"
      runActions={[
        { id: 'pause', label: 'Pause run', onClick: () => undefined },
        { id: 'logs', label: 'View logs', appearance: 'subtle', onClick: () => undefined },
      ]}
      copilotActions={[{ id: 'summarize', label: 'Summarize run', onClick: () => undefined }]}
      reasoning={{
        title: 'Run reasoning',
        subtitle: '3 artifacts created',
        steps: [
          {
            id: 'context',
            title: 'Collect current rollout context',
            body: 'Gathered deployment summaries, recent incidents, and artifact links for the review.',
            status: 'complete',
            badge: { label: 'Approved by user', tone: 'success' },
          },
          {
            id: 'draft',
            title: 'Draft remediation plan',
            body: 'Preparing the ordered remediation steps and rollback notes.',
            status: 'running',
          },
          {
            id: 'approve',
            title: 'Requesting approval to modify resources',
            body: "I need approval to modify resources in the 'Contoso Production' subscription before applying fixes.",
            disclaimer: 'Denying will immediately stop reasoning, and it can’t be restarted. Continuing may incur costs.',
            approveLabel: 'Approve modifications',
            denyLabel: 'Deny modifications',
            needsInput: true,
            status: 'warning',
            defaultOpen: true,
          },
        ],
        artifacts: [
          { id: 'summary', title: 'rollout-summary.md', type: 'markdown file', size: '4KB', onOpen: () => undefined },
          { id: 'policy', title: 'policy-findings.json', type: 'json file', size: '1KB', onOpen: () => undefined },
          { id: 'packet', title: 'approval-packet.pdf', type: 'pdf file', size: '212KB', onOpen: () => undefined },
        ],
        onApprove: () => undefined,
        onDeny: () => undefined,
      }}
      response={{
        parts: [
          { id: 'user', type: 'text', author: 'user', content: 'Summarize the rollout failures and attach the Kusto query.' },
          {
            id: 'summary',
            type: 'text',
            title: 'Copilot',
            content: 'Telemetry drift was isolated to the East US cluster and two dependent workbooks.',
            supportingText: '1 request left',
          },
        ],
      }}
      composer={{
        value: steering,
        onChange: setSteering,
        onSend: () => undefined,
        attachments: [{ id: 'run', name: 'run-context.md' }],
        placeholder: 'Steer the coordinator run…',
      }}
    />
  );
}
