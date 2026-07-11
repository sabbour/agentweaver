/**
 * AgenticDemo — isolated story demonstrating AgentStepList, ApprovalGate,
 * ArtifactChip, and ToolCallRow. Not wired into any route; import for local
 * development/review only.
 */

import { useState } from 'react';
import { Text } from '@fluentui/react-components';
import { DocumentRegular, ShieldTaskRegular } from '@fluentui/react-icons';
import { AgentStepList, ToolCallRow } from './components';
import type { AgentStep, ToolCall } from './types';

const initialSteps: AgentStep[] = [
  {
    id: 'collect-context',
    title: 'Collect run context',
    body: 'Read the repository layout, recent commits, and open issues.',
    status: 'complete',
    artifacts: [
      { id: 'context-json', title: 'context.json', type: 'JSON', icon: <DocumentRegular /> },
    ],
  },
  {
    id: 'plan-changes',
    title: 'Plan file changes',
    body: 'The agent will modify three source files. Review the plan before it writes.',
    status: 'warning',
    needsInput: true,
    riskText: 'Approving lets the agent write to src/api/routes.ts, src/api/handlers.ts, and tests/api.test.ts. You can review the diff before merge.',
    disclaimer: 'Denying stops the run; you can restart with a refined prompt.',
    artifacts: [
      { id: 'plan-md', title: 'plan.md', type: 'Plan', icon: <ShieldTaskRegular />, onOpen: () => undefined },
    ],
  },
  {
    id: 'write-files',
    title: 'Write file changes',
    body: 'Waiting for approval to continue.',
    status: 'pending',
  },
];

const toolCalls: ToolCall[] = [
  {
    id: 'read-file-1',
    name: 'read_file',
    inputSummary: 'src/api/routes.ts',
    resultSummary: '248 lines read',
    status: 'complete',
  },
  {
    id: 'grep-1',
    name: 'grep',
    inputSummary: 'pattern: "handleRequest" in src/',
    resultSummary: '3 matches across 2 files',
    status: 'complete',
    artifacts: [{ id: 'grep-out', title: 'grep-results.txt', type: 'Text', onOpen: () => undefined }],
  },
];

export function AgenticDemo() {
  const [steps, setSteps] = useState(initialSteps);
  const [decision, setDecision] = useState<string>();

  const handleApprove = (stepId: string) => {
    setDecision(`Approved step: ${stepId}`);
    setSteps((prev) =>
      prev.map((s) => {
        if (s.id === stepId) return { ...s, needsInput: false, status: 'complete', body: 'Plan approved — writing files now.' };
        if (s.id === 'write-files') return { ...s, status: 'running', body: 'Writing src/api/routes.ts …' };
        return s;
      }),
    );
  };

  const handleDeny = (stepId: string) => {
    setDecision(`Denied step: ${stepId}`);
    setSteps((prev) =>
      prev.map((s) => {
        if (s.id === stepId) return { ...s, needsInput: false, status: 'blocked', body: 'Plan was denied. Refine the prompt and retry.' };
        if (s.id === 'write-files') return { ...s, status: 'blocked', body: 'Blocked pending a new plan approval.' };
        return s;
      }),
    );
  };

  return (
    <div style={{ padding: '24px', maxWidth: '600px', display: 'flex', flexDirection: 'column', gap: '24px' }}>
      <Text size={500} weight="semibold">Agentic progress demo</Text>

      {decision && (
        <Text size={300} style={{ color: '#635c57' }}>Decision: {decision}</Text>
      )}

      <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
        <Text size={300} weight="semibold">Steps</Text>
        <AgentStepList steps={steps} onApprove={handleApprove} onDeny={handleDeny} />
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
        <Text size={300} weight="semibold">Tool calls</Text>
        {toolCalls.map((tc) => (
          <ToolCallRow key={tc.id} toolCall={tc} />
        ))}
      </div>
    </div>
  );
}
