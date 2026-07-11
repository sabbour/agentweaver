/**
 * Composer — a Copilot-styled chat input built on native @fluentui/react-components.
 *
 * Shape: rounded-pill warm-white card with an auto-growing textarea, a send
 * button, and optional left/right slots. No @1js dependency.
 *
 * Props:
 *   value        controlled text value
 *   onChange     called on every keystroke
 *   onSubmit     called on Enter (without Shift) or send button click
 *   placeholder  placeholder text (default: "Message…")
 *   disabled     disables input and buttons
 *   leftSlot     node rendered to the left of the textarea (e.g. an attach button)
 *   rightSlot    node rendered between textarea and send (e.g. a model picker)
 *   isStreaming  true while assistant is responding; shows a Stop button
 *   onStop       called when the Stop button is clicked
 */

import {
  type KeyboardEvent,
  type ReactNode,
  useCallback,
  useEffect,
  useRef,
} from 'react';
import { Button, Tooltip, mergeClasses } from '@fluentui/react-components';
import { SendRegular, StopRegular } from '@fluentui/react-icons';
import { useCopilotStyles } from './copilotStyles';

export interface ComposerProps {
  value: string;
  onChange: (value: string) => void;
  onSubmit?: (value: string) => void;
  onStop?: () => void;
  placeholder?: string;
  disabled?: boolean;
  isStreaming?: boolean;
  /** Node rendered to the left of the textarea (e.g. attach button or model label). */
  leftSlot?: ReactNode;
  /** Node rendered to the right of the textarea and before the send button. */
  rightSlot?: ReactNode;
  className?: string;
  'aria-label'?: string;
}

export function Composer({
  value,
  onChange,
  onSubmit,
  onStop,
  placeholder = 'Message…',
  disabled = false,
  isStreaming = false,
  leftSlot,
  rightSlot,
  className,
  'aria-label': ariaLabel,
}: ComposerProps) {
  const styles = useCopilotStyles();
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  // Auto-grow: adjust height on value change
  const adjustHeight = useCallback(() => {
    const ta = textareaRef.current;
    if (!ta) return;
    ta.style.height = 'auto';
    ta.style.height = `${Math.min(ta.scrollHeight, 200)}px`;
  }, []);

  useEffect(() => {
    adjustHeight();
  }, [value, adjustHeight]);

  const handleKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    // Submit on Enter without Shift
    if (e.key === 'Enter' && !e.shiftKey && !disabled && !isStreaming) {
      e.preventDefault();
      if (value.trim()) {
        onSubmit?.(value);
      }
    }
  };

  const handleSend = () => {
    if (value.trim() && !disabled && !isStreaming) {
      onSubmit?.(value);
    }
  };

  const canSend = !disabled && !isStreaming && value.trim().length > 0;

  return (
    <div
      className={mergeClasses(styles.composerShell, className)}
      aria-label={ariaLabel ?? 'Chat composer'}
    >
      <div className={styles.composerRow}>
        {leftSlot && (
          <span className={styles.composerLeftSlot}>{leftSlot}</span>
        )}
        <textarea
          ref={textareaRef}
          className={styles.composerTextarea}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder={placeholder}
          disabled={disabled}
          rows={1}
          aria-label={ariaLabel ?? 'Message'}
          aria-multiline="true"
        />
        <span className={styles.composerActions}>
          {rightSlot}
          {isStreaming ? (
            <Tooltip content="Stop generating" relationship="label">
              <Button
                appearance="subtle"
                shape="circular"
                size="small"
                className={styles.stopButton}
                icon={<StopRegular />}
                onClick={onStop}
                aria-label="Stop generating"
              />
            </Tooltip>
          ) : (
            <Tooltip content={canSend ? 'Send message' : 'Type a message to send'} relationship="label">
              <Button
                appearance="subtle"
                shape="circular"
                size="small"
                className={canSend ? styles.sendButtonActive : styles.sendButtonIdle}
                icon={<SendRegular />}
                onClick={handleSend}
                disabled={!canSend}
                aria-label="Send message"
              />
            </Tooltip>
          )}
        </span>
      </div>
    </div>
  );
}
