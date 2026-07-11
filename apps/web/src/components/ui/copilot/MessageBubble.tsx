/**
 * MessageBubble — a single chat message, styled for user or assistant.
 *
 * User bubbles: right-aligned, near-black background, warm-white text,
 *   pill shape with a small bottom-right corner (matches copilot.com Day).
 *
 * Assistant bubbles: left-aligned, surface background, subtle border,
 *   pill shape with a small top-left corner.
 *
 * MessageList: scrollable container for a sequence of MessageBubbles/OutputCards.
 */

import type { ReactNode } from 'react';
import { Text, mergeClasses } from '@fluentui/react-components';
import { useCopilotStyles } from './copilotStyles';

// ─── MessageBubble ────────────────────────────────────────────────────────────

export type MessageRole = 'user' | 'assistant';

export interface MessageBubbleProps {
  role: MessageRole;
  children: ReactNode;
  /** Optional sender label shown above the bubble (e.g. "You" or "Coordinator"). */
  senderName?: string;
  /** Optional ISO timestamp string rendered below the bubble. */
  timestamp?: string;
  className?: string;
}

export function MessageBubble({
  role,
  children,
  senderName,
  timestamp,
  className,
}: MessageBubbleProps) {
  const styles = useCopilotStyles();
  const isUser = role === 'user';

  return (
    <div
      className={mergeClasses(
        styles.messageBubbleWrapper,
        isUser ? styles.messageBubbleUser : styles.messageBubbleAssistant,
        className,
      )}
    >
      {senderName && !isUser && (
        <Text className={styles.messageBubbleSenderName}>{senderName}</Text>
      )}
      <div
        className={mergeClasses(
          styles.messageBubbleContent,
          isUser ? styles.messageBubbleContentUser : styles.messageBubbleContentAssistant,
        )}
        role={isUser ? undefined : 'article'}
        aria-label={isUser ? undefined : (senderName ? `${senderName} message` : 'Assistant message')}
      >
        {children}
      </div>
      {timestamp && (
        <Text className={styles.messageBubbleMeta} aria-label={`Sent ${timestamp}`}>
          {timestamp}
        </Text>
      )}
    </div>
  );
}

// ─── MessageList ─────────────────────────────────────────────────────────────

export interface MessageListProps {
  children: ReactNode;
  className?: string;
  'aria-label'?: string;
}

export function MessageList({ children, className, 'aria-label': ariaLabel }: MessageListProps) {
  const styles = useCopilotStyles();
  return (
    <div
      className={mergeClasses(styles.messageList, className)}
      role="log"
      aria-label={ariaLabel ?? 'Conversation'}
      aria-live="polite"
      aria-relevant="additions"
    >
      {children}
    </div>
  );
}
