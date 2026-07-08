import { useState } from 'react';
import { CopilotComposer, CopilotResponse, type AzfResponsePart } from '..';

const initialResponse: AzfResponsePart[] = [
  { id: 'summary', type: 'text', content: 'Copilot can summarize telemetry, suggest actions, and ask for confirmation without exposing hidden reasoning.' },
  {
    id: 'confirm',
    type: 'confirmation',
    content: 'Run the resource health check against the selected cluster?',
    confirmLabel: 'Run health check',
    cancelLabel: 'Not now',
    onConfirm: () => undefined,
    onCancel: () => undefined,
  },
];

export function CopilotComposerResponseExample() {
  const [prompt, setPrompt] = useState('Summarize unhealthy resources in the last 24 hours.');
  const [isRunning, setIsRunning] = useState(false);

  return (
    <div>
      <CopilotResponse
        parts={initialResponse}
        actions={[
          { id: 'copy', label: 'Copy summary', onClick: () => undefined },
          { id: 'open-log', label: 'Open activity log', onClick: () => undefined },
        ]}
        loading={isRunning}
      />
      <CopilotComposer
        value={prompt}
        onChange={setPrompt}
        onSend={() => setIsRunning(true)}
        isRunning={isRunning}
        onStop={() => setIsRunning(false)}
        agentMode="Investigate"
        onAgentModeChange={() => undefined}
        attachments={[
          { id: 'cluster', name: 'cluster-health.csv', description: "Yesterday's export", onRemove: () => undefined },
        ]}
        onAddAttachment={() => undefined}
        validationMessage={prompt.length === 0 ? 'Enter a prompt before sending.' : undefined}
      />
    </div>
  );
}
