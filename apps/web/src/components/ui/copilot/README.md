# components/ui/copilot

Native `@fluentui/react-components` chat surface components styled to the copilot.com Day look via our warm-monochrome theme. **No `@1js` dependency** — fully open-sourceable.

Composes with `components/ui/agentic/` so a run view = `MessageList` + `OutputCard` + `AgentStepList`/`ToolCallRow` + `Composer`.

## Components

### `Composer`

Auto-growing pill-shaped chat input. Built on a native `<textarea>` styled with Fluent tokens; no internal Fluent form control wrapper so the pill shape is clean.

```tsx
import { Composer } from 'components/ui/copilot';

<Composer
  value={value}
  onChange={setValue}
  onSubmit={(text) => send(text)}
  onStop={() => cancelStream()}
  isStreaming={isStreaming}
  placeholder="Ask the coordinator…"
  leftSlot={<AttachButton />}     // optional: attach affordance, model picker, etc.
  rightSlot={<ModelSelector />}   // optional: rendered between textarea and send
/>
```

Props:

| Prop | Type | Default | Notes |
|------|------|---------|-------|
| `value` | `string` | required | Controlled |
| `onChange` | `(value: string) => void` | required | |
| `onSubmit` | `(value: string) => void` | — | Enter (no Shift) or send button |
| `onStop` | `() => void` | — | Stop button while `isStreaming` |
| `placeholder` | `string` | `"Message…"` | |
| `disabled` | `boolean` | `false` | |
| `isStreaming` | `boolean` | `false` | Shows Stop instead of Send |
| `leftSlot` | `ReactNode` | — | Left of textarea |
| `rightSlot` | `ReactNode` | — | Right of textarea, before Send |
| `aria-label` | `string` | `"Chat composer"` | |

### `MessageBubble`

A single chat message, user or assistant.

```tsx
import { MessageBubble } from 'components/ui/copilot';

// User bubble — right-aligned, near-black background
<MessageBubble role="user">
  <Text size={300}>Fix the flaky API test.</Text>
</MessageBubble>

// Assistant bubble — left-aligned, surface background + border
<MessageBubble role="assistant" senderName="Coordinator">
  <Text size={300}>I'll start by reading the test file.</Text>
</MessageBubble>
```

Props: `role`, `children`, `senderName?`, `timestamp?`, `className?`.

### `MessageList`

Scrollable container for a sequence of messages. Sets `role="log"` + `aria-live="polite"` for screen readers.

```tsx
import { MessageList } from 'components/ui/copilot';

<MessageList aria-label="Run conversation">
  <MessageBubble role="user">…</MessageBubble>
  <OutputCard isStreaming>…</OutputCard>
</MessageList>
```

### `OutputCard`

Assistant response container. Combines a streaming progress bar, body content, and optional feedback buttons. Designed to hold any content — prose, `AgentStepList`, `ToolCallRow`, code, etc.

```tsx
import { OutputCard } from 'components/ui/copilot';

// While streaming
<OutputCard isStreaming>
  <Text size={300}>Generating…</Text>
</OutputCard>

// Complete, with feedback
<OutputCard showFeedback onFeedback={(v) => record(v)} feedbackValue={feedback}>
  <AgentStepList steps={steps} onApprove={onApprove} onDeny={onDeny} />
</OutputCard>

// Complete, with custom footer actions
<OutputCard footerActions={<CopyButton />}>
  <Text size={300}>Here is the result.</Text>
</OutputCard>
```

Props:

| Prop | Type | Default | Notes |
|------|------|---------|-------|
| `children` | `ReactNode` | required | |
| `isStreaming` | `boolean` | `false` | Indeterminate progress bar |
| `showFeedback` | `boolean` | `false` | Thumb up/down in footer |
| `onFeedback` | `(v: "positive"\|"negative") => void` | — | |
| `feedbackValue` | `"positive"\|"negative"` | — | Controlled selection |
| `footerActions` | `ReactNode` | — | Custom footer node |

## Composing with agentic

A full run console surface:

```tsx
import { MessageList, MessageBubble, OutputCard, Composer } from 'components/ui/copilot';
import { AgentStepList, ToolCallRow } from 'components/ui/agentic';

<div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
  <MessageList style={{ flex: 1, minHeight: 0 }}>
    <MessageBubble role="user">Fix the test.</MessageBubble>
    <OutputCard showFeedback onFeedback={recordFeedback}>
      <AgentStepList steps={steps} onApprove={approve} onDeny={deny} />
    </OutputCard>
  </MessageList>
  <Composer value={value} onChange={setValue} onSubmit={send} />
</div>
```

## Design notes

- Warm-monochrome only — no blue, no @1js, fully open-sourceable.
- All colors and spacing from `@fluentui/react-components` tokens.
- User bubbles: `colorNeutralForeground1` bg (near-black), `colorNeutralForegroundOnBrand` text.
- Assistant bubbles / OutputCard: `colorNeutralBackground1` + `colorNeutralStroke2` border.
- Composer: `borderRadiusCircular` pill shell, auto-grow textarea (max 200px), focus ring via `colorStrokeFocus2`.
- All transitions respect `prefers-reduced-motion`.
