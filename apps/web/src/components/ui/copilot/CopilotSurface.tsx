/**
 * CopilotSurface — isolated demo wiring Composer + MessageList + MessageBubble
 * + OutputCard + AgentStepList together into the full run/console surface.
 *
 * NOT wired into any route. Import only for local review:
 *   import { CopilotSurface } from 'components/ui/copilot/CopilotSurface';
 */

import { useState } from 'react';
import { Text } from '@fluentui/react-components';
import { DocumentRegular } from '@fluentui/react-icons';
import { AgentStepList } from '../agentic';
import type { AgentStep } from '../agentic';
import { Composer } from './Composer';
import { MessageBubble, MessageList } from './MessageBubble';
import { OutputCard } from './OutputCard';
import type { FeedbackValue } from './OutputCard';

const demoSteps: AgentStep[] = [
  {
    id: 'read',
    title: 'Read repository context',
    body: 'Scanning recent commits and open issues.',
    status: 'complete',
    artifacts: [{ id: 'ctx', title: 'context.json', type: 'JSON', icon: <DocumentRegular />, onOpen: () => undefined }],
  },
  {
    id: 'plan',
    title: 'Propose changes',
    body: 'The agent will modify two files. Approve to continue.',
    status: 'warning',
    needsInput: true,
    riskText: 'Approving lets the agent write to src/api.ts and tests/api.test.ts. Review the diff before merge.',
    disclaimer: 'Denying stops the run.',
  },
];

interface Message {
  id: string;
  role: 'user' | 'assistant';
  text?: string;
  showSteps?: boolean;
}

export function CopilotSurface() {
  const [messages, setMessages] = useState<Message[]>([
    { id: '1', role: 'user', text: 'Fix the flaky API test and update the handler.' },
    { id: '2', role: 'assistant', showSteps: true },
  ]);
  const [value, setValue] = useState('');
  const [isStreaming, setIsStreaming] = useState(false);
  const [feedback, setFeedback] = useState<FeedbackValue | undefined>();

  const handleSubmit = (v: string) => {
    if (!v.trim()) return;
    const id = String(Date.now());
    setValue('');
    setIsStreaming(true);
    setMessages((prev) => [...prev, { id, role: 'user', text: v }]);
    setTimeout(() => {
      setMessages((prev) => [
        ...prev,
        { id: `${id}-resp`, role: 'assistant', text: `Echo: ${v}` },
      ]);
      setIsStreaming(false);
    }, 1400);
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', gap: '12px', padding: '16px', maxWidth: '680px' }}>
      <Text size={400} weight="semibold">Copilot surface demo</Text>
      <MessageList aria-label="Run conversation">
        {messages.map((msg) =>
          msg.showSteps ? (
            <OutputCard
              key={msg.id}
              showFeedback
              onFeedback={setFeedback}
              feedbackValue={feedback}
            >
              <AgentStepList steps={demoSteps} />
            </OutputCard>
          ) : (
            <MessageBubble key={msg.id} role={msg.role}>
              <Text size={300}>{msg.text}</Text>
            </MessageBubble>
          ),
        )}
        {isStreaming && (
          <OutputCard isStreaming>
            <Text size={300} style={{ color: '#746d68' }}>Generating…</Text>
          </OutputCard>
        )}
      </MessageList>
      <Composer
        value={value}
        onChange={setValue}
        onSubmit={handleSubmit}
        onStop={() => setIsStreaming(false)}
        isStreaming={isStreaming}
        placeholder="Ask the coordinator…"
      />
    </div>
  );
}
