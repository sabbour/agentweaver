import { useState } from 'react';
import { Button, InlineCopilot, Text, type AzfOption } from '..';

const suggestions: AzfOption[] = [
  { id: 'summarize', label: 'Summarize deployment failures from the last 24 hours' },
  { id: 'next-step', label: 'Suggest the next safe remediation step' },
  { id: 'draft', label: 'Draft a stakeholder update for the incident channel' },
];

export function InlineCopilotExample() {
  const [openPrompt, setOpenPrompt] = useState('');
  const [guidedPrompt, setGuidedPrompt] = useState('Summarize deployment failures from the last 24 hours');
  const [guidedState, setGuidedState] = useState<'empty' | 'loading' | 'error' | 'generated'>('generated');
  const [lastResult, setLastResult] = useState<string | undefined>();

  const submitGuided = () => {
    if (!guidedPrompt.trim()) {
      setGuidedState('error');
      return;
    }

    setGuidedState('generated');
    setLastResult(`Generated guidance for: ${guidedPrompt}`);
  };

  return (
    <div className="azf-stack azf-gap-m">
      <div className="azf-stack azf-gap-xs">
        <Text weight="semibold">Open start</Text>
        <InlineCopilot
          open
          trigger={<Button appearance="secondary">Open inline Copilot</Button>}
          value={openPrompt}
          onChange={setOpenPrompt}
          onSubmit={() => setLastResult(`Queued prompt: ${openPrompt || 'Ask Copilot'}`)}
          placeholder="Ask Copilot to draft, fix, or explain"
        />
      </div>

      <div className="azf-stack azf-gap-xs">
        <Text weight="semibold">Guided start</Text>
        <InlineCopilot
          open
          trigger={<Button appearance="secondary">Summarize with Copilot</Button>}
          title="Summarize with Copilot"
          value={guidedPrompt}
          onChange={(nextValue) => {
            setGuidedPrompt(nextValue);
            setGuidedState(nextValue.trim() ? 'generated' : 'error');
          }}
          onSubmit={submitGuided}
          state={guidedState}
          errorMessage={guidedState === 'error' ? 'Try a concrete prompt such as a summary, fix, or update request.' : undefined}
          suggestions={suggestions}
        />
      </div>

      {lastResult && <Text className="azf-muted">{lastResult}</Text>}
    </div>
  );
}
