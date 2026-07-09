import { useState } from 'react';
import { CodeSnippet, CopilotComposer, CopilotResponse, type AzfResponsePart } from '..';

const initialResponse: AzfResponsePart[] = [
  { id: 'user', type: 'text', author: 'user', content: 'Summarize unhealthy resources and include the Kusto filter.' },
  {
    id: 'summary',
    type: 'text',
    title: 'Copilot',
    badge: 'AI-generated content may be incorrect',
    content: (
      <div className="azf-stack azf-gap-s">
        <p>Copilot can summarize telemetry, suggest actions, and ask for confirmation without exposing hidden reasoning.</p>
        <CodeSnippet
          title="Kusto"
          lines={[
            { lineNumber: 1, tokens: [{ text: 'securityresources', tone: 'key' }] },
            { lineNumber: 2, tokens: [{ text: '| ', tone: 'operator' }, { text: 'where', tone: 'keyword' }, { text: ' type == ', tone: 'plain' }, { text: '"microsoft.authorization/policyassignments"', tone: 'string' }] },
          ]}
          maxHeight={152}
        />
      </div>
    ),
    supportingText: '1 request left',
  },
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
  const [agentMode, setAgentMode] = useState(true);

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
        agentMode={agentMode}
        onAgentModeChange={setAgentMode}
        attachments={[
          { id: 'cluster', name: 'cluster-health.csv', description: "Yesterday's export", onRemove: () => undefined },
        ]}
        onAddAttachment={() => undefined}
        validationMessage={prompt.length === 0 ? 'Enter a prompt before sending.' : undefined}
      />
    </div>
  );
}
