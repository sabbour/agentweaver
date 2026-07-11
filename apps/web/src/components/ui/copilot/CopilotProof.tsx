/**
 * CopilotProof — isolated proof-of-concept that verifies:
 *   1. @1js/fluentai CopilotProvider renders under React 19 + our theme
 *   2. @1js/fai-react-chat-input ChatInput renders and accepts input
 *   3. @1js/fluentai OutputCard renders with and without the loading state
 *
 * NOT wired into any route. Import from a dev page or a manual test only.
 * This component must not be imported by production pages.
 */

import { useState } from 'react';
import { Text, Divider } from '@fluentui/react-components';
import { AgentweaverCopilotProvider } from './AgentweaverCopilotProvider';
import { Composer, OutputBubble } from './Composer';

export function CopilotProof() {
  const [messages, setMessages] = useState<Array<{ id: string; text: string; loading: boolean }>>([]);
  const [isSending, setIsSending] = useState(false);

  const handleSubmit = (value: string) => {
    if (!value.trim()) return;
    const id = String(Date.now());
    setIsSending(true);
    setMessages((prev) => [...prev, { id, text: value, loading: true }]);
    // Simulate a short streamed response
    setTimeout(() => {
      setMessages((prev) =>
        prev.map((m) =>
          m.id === id
            ? { ...m, text: `Echo: ${value}`, loading: false }
            : m,
        ),
      );
      setIsSending(false);
    }, 1200);
  };

  return (
    <div style={{ padding: '24px', maxWidth: '640px', display: 'flex', flexDirection: 'column', gap: '16px' }}>
      <Text size={500} weight="semibold">Copilot package proof</Text>
      <Text size={200} style={{ color: '#635c57' }}>
        Verifies @1js/fluentai + @1js/fai-react-chat-input render under React 19 and
        the warm-monochrome theme. Not a production surface.
      </Text>
      <Divider />

      {/* Provider wraps both the transcript and the composer */}
      <AgentweaverCopilotProvider mode="canvas">
        <div style={{ display: 'flex', flexDirection: 'column', gap: '12px', minHeight: '120px' }}>
          {messages.length === 0 && (
            <Text size={300} style={{ color: '#746d68' }}>No messages yet — type something below.</Text>
          )}
          {messages.map((msg) => (
            <OutputBubble key={msg.id} isLoading={msg.loading} mode="canvas">
              <Text size={300}>{msg.text}</Text>
            </OutputBubble>
          ))}
        </div>

        <Composer
          placeholder="Send a test message…"
          onSubmit={handleSubmit}
          isSending={isSending}
          onStop={() => setIsSending(false)}
        />
      </AgentweaverCopilotProvider>

      <Divider />
      <Text size={200} style={{ color: '#8c837c' }}>
        Sidecar mode (compact):
      </Text>
      <AgentweaverCopilotProvider mode="sidecar">
        <OutputBubble isLoading={false} mode="sidecar">
          <Text size={300}>Sidecar output bubble — no loading bar.</Text>
        </OutputBubble>
        <Composer placeholder="Sidecar composer…" />
      </AgentweaverCopilotProvider>
    </div>
  );
}
