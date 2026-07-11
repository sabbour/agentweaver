/**
 * Composer — thin wrapper around @1js/fai-react-chat-input ChatInput.
 *
 * Provides a single, predictable prop surface for the Console and CoordinatorRunPage.
 * The parent must already be inside <AgentweaverCopilotProvider>.
 */

import type { ReactNode } from 'react';
import { ChatInput } from '@1js/fai-react-chat-input';
import type { ChatInputSubmitEvents, ChatInputProps } from '@1js/fai-react-chat-input';
import type { EditorInputValueData } from '@1js/fai-react-editor-input';

export interface ComposerProps {
  /** Placeholder text shown when the editor is empty. */
  placeholder?: string;
  /** Called when the user submits (Enter or send button). */
  onSubmit?: (value: string, ev: ChatInputSubmitEvents) => void;
  /** Called when the stop button is clicked while isSending=true. */
  onStop?: (ev: ChatInputSubmitEvents) => void;
  /** True while a response is being generated; shows a stop button. */
  isSending?: boolean;
  /** Whether the composer is disabled. */
  disabled?: boolean;
  /** Slot rendered before the editor (e.g. attachment pills). */
  contentBefore?: ChatInputProps['contentBefore'];
  /** Slot rendered below the editor (e.g. character count). */
  actions?: ChatInputProps['actions'];
}

export function Composer({
  placeholder,
  onSubmit,
  onStop,
  isSending,
  disabled,
  contentBefore,
  actions,
}: ComposerProps) {
  const handleSubmit = (ev: ChatInputSubmitEvents, data: EditorInputValueData) => {
    onSubmit?.(data.value, ev);
  };

  return (
    <ChatInput
      editor={{ placeholderValue: placeholder ?? 'Message…' }}
      onSubmit={handleSubmit}
      onStop={onStop}
      isSending={isSending}
      disabled={disabled}
      contentBefore={contentBefore}
      actions={actions}
      // CharactersRemainingMessage is required by the type union (NoMaxLengthProps).
      // Pass undefined to opt out of character counting.
      charactersRemainingMessage={undefined}
    />
  );
}

export type { ChatInputSubmitEvents };

/**
 * OutputBubble — light wrapper around @1js/fluentai OutputCard.
 *
 * Renders streamed assistant content inside the Copilot-branded card surface.
 * Pass isLoading=true while the stream is in progress.
 */
import { OutputCard } from '@1js/fluentai';
import type { OutputCardProps } from '@1js/fluentai';

export interface OutputBubbleProps {
  children: ReactNode;
  /** True while the assistant is still streaming. Shows an animated progress bar. */
  isLoading?: boolean;
  mode?: 'canvas' | 'sidecar';
}

export function OutputBubble({ children, isLoading = false, mode }: OutputBubbleProps) {
  const cardProps: OutputCardProps = { isLoading, mode };
  return <OutputCard {...cardProps}>{children}</OutputCard>;
}
