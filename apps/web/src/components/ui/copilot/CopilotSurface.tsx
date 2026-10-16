/**
 * CopilotSurface — dev-only demo wiring all copilot/ + agentic/ pieces.
 * Not wired into app routes.
 */
import React, { useState } from "react";
import { tokens } from "@fluentui/react-components";
import { Composer } from "./Composer";
import { OutputCard } from "./OutputCard";
import { FeedbackButtons } from "./FeedbackButtons";
import type { FeedbackValue } from "./FeedbackButtons";
import { CopilotChat, CopilotMessage, UserMessage } from "./Message";
import { AgentStepList } from "../agentic";
import type { AgentStep } from "../agentic";

const DEMO_STEPS: AgentStep[] = [
  {
    id: "s1",
    title: "Reading repository files",
    status: "complete",
    body: "Scanned 24 files across src/",
  },
  {
    id: "s2",
    title: "Generating implementation plan",
    status: "running",
    body: "Drafting component tree and style tokens…",
  },
  {
    id: "s3",
    title: "Awaiting your review",
    status: "running",
    needsInput: true,
    body: "The plan is ready. Approve to proceed.",
    riskText: "This will create 6 new files.",
  },
];

export function CopilotSurface() {
  const [input, setInput] = useState("");
  const [messages, setMessages] = useState<Array<{ role: "user" | "assistant"; text: string; id: string }>>([
    { id: "m0", role: "assistant", text: "Hi! I'm the Coordinator. Describe what you'd like to build." },
  ]);
  const [sending, setSending] = useState(false);
  const [feedback, setFeedback] = useState<FeedbackValue | undefined>(undefined);

  const handleSubmit = (_ev: React.SyntheticEvent, { value }: { value: string }) => {
    if (!value.trim()) return;
    const newMessages = [
      ...messages,
      { id: `u${Date.now()}`, role: "user" as const, text: value },
    ];
    setMessages(newMessages);
    setInput("");
    setSending(true);
    setTimeout(() => {
      setMessages([
        ...newMessages,
        { id: `a${Date.now()}`, role: "assistant" as const, text: "Working on it! Here's what I found so far." },
      ]);
      setSending(false);
    }, 1500);
  };

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        height: "100vh",
        maxWidth: "760px",
        margin: "0 auto",
        backgroundColor: tokens.colorNeutralBackground2,
      }}
    >
      <CopilotChat style={{ flex: "1 1 auto", overflowY: "auto" } as React.CSSProperties}>
        {messages.map((m) =>
          m.role === "user" ? (
            <UserMessage key={m.id}>{m.text}</UserMessage>
          ) : (
            <CopilotMessage
              key={m.id}
              loadingState={sending && m.id === messages[messages.length - 1].id ? "streaming" : "none"}
              actions={<FeedbackButtons selected={feedback} onFeedback={setFeedback} />}
            >
              <OutputCard
                isLoading={sending}
                mode="sidecar"
              >
                <p style={{ margin: 0 }}>{m.text}</p>
                {m.id === messages[messages.length - 1].id && !sending && (
                  <AgentStepList steps={DEMO_STEPS} />
                )}
              </OutputCard>
            </CopilotMessage>
          )
        )}
      </CopilotChat>

      <div style={{ padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}` }}>
        <Composer
          value={input}
          onChange={setInput}
          onSubmit={handleSubmit}
          onStop={() => setSending(false)}
          isSending={sending}
          hideSendWhenEmpty
          placeholder="Ask the Coordinator anything…"
        />
      </div>
    </div>
  );
}
