// Composer — chat input (pill-shaped, auto-growing textarea)
export { Composer } from './Composer';
export type { ComposerProps } from './Composer';

// MessageBubble / MessageList — user + assistant messages
export { MessageBubble, MessageList } from './MessageBubble';
export type { MessageBubbleProps, MessageListProps, MessageRole } from './MessageBubble';

// OutputCard — assistant response container with streaming + feedback
export { OutputCard } from './OutputCard';
export type { OutputCardProps, FeedbackValue } from './OutputCard';

// CopilotSurface is dev-only — import directly for local review:
//   import { CopilotSurface } from 'components/ui/copilot/CopilotSurface';
