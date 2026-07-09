import { useState } from 'react';
import { CopilotWorkspacePattern } from '..';

export function CopilotWorkspaceExample() {
  const [prompt, setPrompt] = useState('Draft the rollout status comment for the production review.');

  return (
    <CopilotWorkspacePattern
      title="Copilot workspace"
      serviceMenuGroups={[{ id: 'copilot', label: 'Copilot', items: [{ id: 'chat', label: 'Workspace chat' }, { id: 'artifacts', label: 'Artifacts' }] }]}
      selectedMenuId="chat"
      response={{ parts: [{ id: 'summary', type: 'text', content: 'Two clusters need follow-up before the next rollout.' }] }}
      composer={{
        value: prompt,
        onChange: setPrompt,
        onSend: () => undefined,
        attachments: [{ id: 'summary', name: 'rollout-summary.md' }],
      }}
    />
  );
}
