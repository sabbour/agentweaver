# components/ui/copilot — Native Copilot Chat Surface

Native `@fluentui/react-components` chat surface styled to the Agentweaver warm-monochrome theme. Zero `@1js` imports in shipped code — the @1js packages were used **as design reference only** (reading `.d.ts` type definitions to mirror component anatomy).

## @1js anatomy mirrored

| Native component | @1js reference |
|---|---|
| `Composer` | `@1js/fai-react-chat-input` `ChatInput` + `SendButton` |
| `CopilotMessage` | `@1js/fai-react-copilot-chat` `CopilotMessage` |
| `UserMessage` | `@1js/fai-react-copilot-chat` `UserMessage` |
| `CopilotChat` | `@1js/fai-react-copilot-chat` `CopilotChat` |
| `OutputCard` | `@1js/fai-react-output-card` `OutputCard` |
| `FeedbackButtons` | `@1js/fai-react-feedback-buttons` `FeedbackButtons` |
| `Attachment` / `AttachmentList` | `@1js/fai-react-attachments` `Attachment` |

---

## Components

### `Composer`

Pill-shaped chat input. Mirrors `ChatInput` slot anatomy exactly.

```tsx
<Composer
  value={text}
  onChange={setText}
  onSubmit={(ev, { value }) => send(value)}
  onStop={() => stopGeneration()}
  isSending={isGenerating}
  hideSendWhenEmpty
  placeholder="Ask anything…"
  attachments={[{ id: "f1", name: "design.pdf", onRemove: () => {} }]}
  banner={<>You are in read-only mode</>}
  contentBefore={<ModelDropdown />}
  actions={<VoiceButton />}
/>
```

**Slots** (mirrored from `ChatInputSlots`):
- `banner` — above attachments, for warnings/notices
- `attachments` — file/agent chips above the editor
- `contentBefore` — left of editor (model selector, attach icon)
- `editor` — the textarea element
- `actions` — right of editor, before send
- `send` — SendButton with animated send↔stop
- `errorMessage` — character limit exceeded
- `contentBelow` — below the shell (suggestions)

**Props**:
```ts
value?: string
onChange?: (v: string) => void
onSubmit?: (ev, { value }) => void   // mirrors ChatInputProps.onSubmit
onStop?: (ev) => void                // mirrors ChatInputProps.onStop
isSending?: boolean                  // mirrors ChatInputProps.isSending
disableSend?: boolean
hideSendWhenEmpty?: boolean
maxLength?: number
appearance?: "auto" | "single" | "multi"
disabled?: boolean
banner?: ReactNode
attachments?: AttachmentProps[]
contentBefore?: ReactNode
actions?: ReactNode
contentBelow?: ReactNode
```

---

### `CopilotMessage`

Structured assistant message. Mirrors `CopilotMessage` slot anatomy.

```tsx
<CopilotMessage
  name="Coordinator"
  loadingState="streaming"
  disclaimer="AI-generated content may be inaccurate"
  actions={<FeedbackButtons onFeedback={setFeedback} />}
  footnote="Sources: 3 files"
>
  <OutputCard isLoading>
    <AgentStepList steps={steps} />
  </OutputCard>
</CopilotMessage>
```

**Slots**: `avatar`, `name`, `disclaimer`, `content`, `progress`, `footnote`, `actions`

**Props**:
```ts
loadingState?: "loading" | "streaming" | "none"  // mirrors CopilotMessageProps
name?: string
avatar?: ReactNode
disclaimer?: ReactNode
footnote?: ReactNode
actions?: ReactNode
announcement?: string  // aria-live
```

---

### `UserMessage`

Right-aligned user bubble. Mirrors `UserMessage` slot anatomy.

```tsx
<UserMessage timestamp="2:34 PM" actionBar={<CopyButton />}>
  Hello, can you help me build a workflow?
</UserMessage>
```

**Slots**: `message`, `timestamp`, `actionBar`, `topContent`

---

### `OutputCard`

Content container with streaming ProgressBar. Mirrors `OutputCard`.

```tsx
<OutputCard isLoading={isStreaming} mode="canvas">
  <MarkdownContent content={text} />
</OutputCard>
```

**Props**:
```ts
isLoading?: boolean      // mirrors OutputCard.isLoading
mode?: "canvas" | "sidecar"
showProgress?: boolean   // opt-in ProgressBar (auto when isLoading)
```

---

### `FeedbackButtons`

Controlled thumbs up/down. Mirrors `FeedbackButtons`.

```tsx
<FeedbackButtons
  selected={feedback}  // "positive" | "negative" | undefined
  onFeedback={setFeedback}
  disabled={isStreaming}
/>
```

---

### `Attachment` / `AttachmentList`

File/reference chip for the `Composer` attachments slot. Mirrors `Attachment`.

```tsx
<AttachmentList
  attachments={[
    { id: "1", name: "design.pdf", onRemove: () => remove("1"), onOpen: () => open("1") }
  ]}
/>
```

---

### `CopilotChat`

Scrollable feed container (`role="feed"`). Mirrors `CopilotChat`.

```tsx
<CopilotChat label="Run conversation">
  <UserMessage>...</UserMessage>
  <CopilotMessage ...>...</CopilotMessage>
</CopilotChat>
```

---

## Composition with `components/ui/agentic/`

The copilot surface composes directly with agentic pieces:

```tsx
<CopilotMessage loadingState="streaming" actions={<FeedbackButtons />}>
  <OutputCard isLoading>
    {/* Agentic progress inside assistant response */}
    <AgentStepList steps={steps} />
    <ToolCallRow call={call} />
  </OutputCard>
</CopilotMessage>
```

The `ApprovalGate` renders inline inside a `CopilotMessage` content area for human-in-the-loop approvals.

---

## Demo

`CopilotSurface` — a dev-only demo wiring all pieces. Not in app routes.
