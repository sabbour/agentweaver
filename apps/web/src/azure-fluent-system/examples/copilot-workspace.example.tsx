import { useState } from 'react';
import { CodeSnippet, CopilotWorkspacePattern } from '..';

export function CopilotWorkspaceExample() {
  const [prompt, setPrompt] = useState('Draft the remediation comment for the deployment review.');

  return (
    <CopilotWorkspacePattern
      className="azf-copilot-workspace-demo"
      title="Copilot workspace"
      serviceMenuGroups={[{ id: 'copilot', label: 'Copilot', items: [{ id: 'chat', label: 'Workspace chat' }, { id: 'artifacts', label: 'Artifacts' }] }]}
      selectedMenuId="chat"
      response={{
        parts: [
          { id: 'user', type: 'text', author: 'user', content: 'Summarize the rollout failures and attach the Kusto query.' },
          {
            id: 'summary',
            type: 'text',
            title: 'Copilot',
            badge: 'AI-generated content may be incorrect',
            content: (
              <div className="azf-stack azf-gap-s">
                <p>Telemetry drift is isolated to the East US cluster and two dependent workbooks.</p>
                <CodeSnippet
                  title="Kusto"
                  lines={[
                    { lineNumber: 1, tokens: [{ text: 'AKSControlPlane', tone: 'key' }] },
                    { lineNumber: 2, tokens: [{ text: '| ', tone: 'operator' }, { text: 'where', tone: 'keyword' }, { text: ' PreciseTimeStamp > ago(2h)' }] },
                    { lineNumber: 3, tokens: [{ text: '| ', tone: 'operator' }, { text: 'where', tone: 'keyword' }, { text: ' Region == ' }, { text: '"eastus"', tone: 'string' }] },
                    { lineNumber: 4, tokens: [{ text: '| ', tone: 'operator' }, { text: 'summarize', tone: 'keyword' }, { text: ' failures=count() by ClusterName' }] },
                  ]}
                  maxHeight={152}
                />
              </div>
            ),
            supportingText: '1 request left',
            footerActions: [
              { id: 'copy', label: 'Copy summary', onClick: () => undefined },
              { id: 'open', label: 'Open workbook', onClick: () => undefined },
            ],
          },
          {
            id: 'confirm',
            type: 'confirmation',
            content: 'Run the remediation script against aks-cluster-sample?',
            confirmLabel: 'Run remediation',
            cancelLabel: 'Review first',
            onConfirm: () => undefined,
            onCancel: () => undefined,
          },
        ],
      }}
      composer={{
        value: prompt,
        onChange: setPrompt,
        onSend: () => undefined,
        attachments: [{ id: 'kusto', name: 'rollout-failures.kql' }],
        placeholder: 'Ask Copilot about this rollout',
      }}
    />
  );
}
