# components/ui/copilot

`@1js/fluentai` + `@1js/fai-react-chat-input` wiring for Agentweaver chat surfaces. Must be rendered **inside** the app's `<FluentProvider theme={agentweaverLightTheme}>`. Do NOT add another `FluentProvider`.

## `AgentweaverCopilotProvider`

Wraps `CopilotProvider` from `@1js/fluentai` with the warm-monochrome `themeExtension` (no blue flair). Sets the Copilot mode and inherits the parent Fluent theme.

```tsx
import { AgentweaverCopilotProvider } from 'components/ui/copilot';

// Docked Console — compact sidecar layout
<AgentweaverCopilotProvider mode="sidecar">
  {/* Composer + message list */}
</AgentweaverCopilotProvider>

// Full-page run/chat — generous canvas layout
<AgentweaverCopilotProvider mode="canvas">
  {/* Composer + transcript */}
</AgentweaverCopilotProvider>
```

Props:

| Prop | Type | Default | Notes |
|------|------|---------|-------|
| `mode` | `"sidecar" \| "canvas"` | `"canvas"` | "sidecar" for the docked Console; "canvas" for a full-page surface |
| `children` | `ReactNode` | required | Content to render inside the provider |

## `Composer`

Thin wrapper around `@1js/fai-react-chat-input` `ChatInput`. Manages submit/stop callbacks with a clean prop surface.

```tsx
import { Composer } from 'components/ui/copilot';

<Composer
  placeholder="Ask the coordinator…"
  onSubmit={(value, ev) => sendMessage(value)}
  onStop={() => stopStream()}
  isSending={isStreaming}
/>
```

Props:

| Prop | Type | Default | Notes |
|------|------|---------|-------|
| `placeholder` | `string` | `"Message…"` | |
| `onSubmit` | `(value: string, ev: ChatInputSubmitEvents) => void` | — | Called when user presses Enter or the send button |
| `onStop` | `(ev: ChatInputSubmitEvents) => void` | — | Called when user clicks the stop button while `isSending` |
| `isSending` | `boolean` | — | Shows a stop button instead of send |
| `disabled` | `boolean` | — | |
| `contentBefore` | `ChatInputProps["contentBefore"]` | — | Slot rendered before the editor |
| `actions` | `ChatInputProps["actions"]` | — | Slot rendered in the actions area |

## `OutputBubble`

Wrapper around `@1js/fluentai` `OutputCard`. Renders streamed assistant responses in the Copilot-branded card surface.

```tsx
import { OutputBubble } from 'components/ui/copilot';

<OutputBubble isLoading={isStreaming} mode="canvas">
  <p>{assistantText}</p>
</OutputBubble>
```

Props:

| Prop | Type | Default | Notes |
|------|------|---------|-------|
| `children` | `ReactNode` | required | Content rendered inside the card |
| `isLoading` | `boolean` | `false` | Shows animated progress bar while streaming |
| `mode` | `"canvas" \| "sidecar"` | — | Inherits from `AgentweaverCopilotProvider` when omitted |

## `CopilotProof` (dev only)

Isolated proof-of-concept that renders `CopilotProvider` + `ChatInput` + `OutputCard` under React 19 + our theme. **Not included in the main index export.** Import directly for local testing:

```tsx
import { CopilotProof } from 'components/ui/copilot/CopilotProof';
```

## Feed note

`@1js/fluentai` and `@1js/fai-react-chat-input` are published to the 1JS Azure Artifacts feed (configured in `apps/web/.npmrc`). If a build fails with a feed/auth error, run:

```sh
npx vsts-npm-auth -config apps/web/.npmrc -F
```

then `npm install --prefix apps/web`.
