/**
 * Composer — mirrors @1js/fai-react-chat-input ChatInput anatomy.
 *
 * Slots mirrored: root, banner, attachments, inputWrapper, contentBefore,
 *   editor (textarea), actions, send (SendButton), errorMessage, contentBelow.
 *
 * Props mirrored from ChatInputProps:
 *   - isSending, onSubmit, onStop
 *   - disableSend, hideSendWhenEmpty
 *   - maxLength (enables character count/error)
 *   - appearance: "auto" | "single" | "multi"
 *   - attachments: AttachmentProps[]
 *
 * SendButton mirrors @1js/fai-react-send-button:
 *   - isSending=false: send icon (active or idle)
 *   - isSending=true: stop icon with circular affordance
 *   - Animated cross-fade between send ↔ stop icons
 */
import React, { useCallback, useRef, useEffect } from "react";
import { mergeClasses, Tooltip } from "@fluentui/react-components";
import {
  SendRegular,
  StopRegular,
  AttachRegular,
  LockClosedRegular,
} from "@fluentui/react-icons";
import { useComposerStyles, useSendButtonStyles } from "./copilotStyles";
import { AttachmentList } from "./Attachment";
import type { AttachmentProps } from "./Attachment";

export type ComposerAppearance = "auto" | "single" | "multi";

export interface ComposerSubmitData {
  /** Current editor value at time of submit */
  value: string;
}

export interface ComposerProps {
  value?: string;
  onChange?: (value: string) => void;
  /** Mirrors ChatInputProps.onSubmit(ev, { value }) */
  onSubmit?: (ev: React.SyntheticEvent, data: ComposerSubmitData) => void;
  /** Mirrors ChatInputProps.onStop(ev) — called when stop button pressed */
  onStop?: (ev: React.MouseEvent<HTMLButtonElement>) => void;
  placeholder?: string;
  disabled?: boolean;
  /** Mirrors ChatInputProps.isSending — animates send → stop */
  isSending?: boolean;
  /** Mirrors ChatInputProps.disableSend */
  disableSend?: boolean;
  /** Mirrors ChatInputProps.hideSendWhenEmpty */
  hideSendWhenEmpty?: boolean;
  /** Mirrors ChatInputProps.maxLength — enables character count + error */
  maxLength?: number;
  /** Mirrors ChatInputProps.appearance */
  appearance?: ComposerAppearance;
  /** slot: banner — rendered above attachments + inputWrapper */
  banner?: React.ReactNode;
  /** slot: attachments — file/agent chips above the editor */
  attachments?: AttachmentProps[];
  /** slot: contentBefore — left zone (model selector, attach affordance) */
  contentBefore?: React.ReactNode;
  /** slot: actions — right of editor, before send button */
  actions?: React.ReactNode;
  /** slot: contentBelow — below the input shell (suggestions, etc.) */
  contentBelow?: React.ReactNode;
  /**
   * When true, the composer is replaced by a read-only notice — the user can
   * see the conversation but cannot send messages directly. Use in run views
   * where the selected agent can only be steered via the Coordinator.
   */
  readOnly?: boolean;
  /**
   * The text shown in the read-only notice.
   * Defaults to "Viewing this agent — steer via the Coordinator".
   */
  readOnlyNote?: string;
  className?: string;
}

function SendButton({
  isSending,
  canSend,
  onSend,
  onStop,
  hidden,
}: {
  isSending: boolean;
  canSend: boolean;
  onSend: (ev: React.MouseEvent<HTMLButtonElement>) => void;
  onStop: (ev: React.MouseEvent<HTMLButtonElement>) => void;
  hidden: boolean;
}) {
  const styles = useSendButtonStyles();

  if (hidden) return null;

  if (isSending) {
    return (
      <Tooltip content="Stop" relationship="label" withArrow>
        <button
          type="button"
          className={mergeClasses(styles.root, styles.stopping)}
          onClick={onStop}
          aria-label="Stop"
        >
          {/* slot: stopIcon */}
          <span className={mergeClasses(styles.stopIcon, styles.iconVisible)}>
            <StopRegular fontSize={14} />
          </span>
        </button>
      </Tooltip>
    );
  }

  return (
    <Tooltip content="Send" relationship="label" withArrow>
      <button
        type="button"
        className={mergeClasses(
          styles.root,
          canSend ? styles.active : styles.idle
        )}
        onClick={onSend}
        disabled={!canSend}
        aria-label="Send"
      >
        {/* slot: sendIcon */}
        <span className={mergeClasses(styles.sendIcon, styles.iconVisible)}>
          <SendRegular fontSize={14} />
        </span>
      </button>
    </Tooltip>
  );
}

export function Composer({
  value = "",
  onChange,
  onSubmit,
  onStop,
  placeholder = "Ask anything…",
  disabled = false,
  isSending = false,
  disableSend = false,
  hideSendWhenEmpty = false,
  maxLength,
  appearance = "multi",
  banner,
  attachments = [],
  contentBefore,
  actions,
  contentBelow,
  readOnly = false,
  readOnlyNote = "Viewing this agent — steer via the Coordinator",
  className,
}: ComposerProps) {
  // All hooks must be unconditional — declared before any early return.
  const styles = useComposerStyles();
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  const hasValue = value.trim().length > 0;
  const charCount = value.length;
  const isOverLimit = maxLength != null && charCount > maxLength;
  const canSend = !disabled && !disableSend && !isOverLimit && (hasValue || !hideSendWhenEmpty) && !isSending;
  const hideSend = hideSendWhenEmpty && !hasValue && !isSending;

  // Auto-resize textarea (no-op when readOnly since textarea is not rendered)
  useEffect(() => {
    const el = textareaRef.current;
    if (!el || appearance === "single") return;
    el.style.height = "auto";
    el.style.height = `${el.scrollHeight}px`;
  }, [value, appearance]);

  const handleKeyDown = useCallback(
    (ev: React.KeyboardEvent<HTMLTextAreaElement>) => {
      if (ev.key === "Enter" && !ev.shiftKey && !disabled && canSend) {
        ev.preventDefault();
        onSubmit?.(ev, { value });
      }
    },
    [onSubmit, value, disabled, canSend]
  );

  const handleSend = useCallback(
    (ev: React.MouseEvent<HTMLButtonElement>) => {
      if (canSend) onSubmit?.(ev, { value });
    },
    [onSubmit, value, canSend]
  );

  const handleStop = useCallback(
    (ev: React.MouseEvent<HTMLButtonElement>) => {
      onStop?.(ev);
    },
    [onStop]
  );

  // ── Read-only mode: replace composer with a subdued notice ──────────────────
  if (readOnly) {
    return (
      <div className={mergeClasses(styles.readOnly, className)} role="status" aria-label={readOnlyNote}>
        <span className={styles.readOnlyIcon} aria-hidden="true">
          <LockClosedRegular fontSize={14} />
        </span>
        {readOnlyNote}
      </div>
    );
  }
  // ─────────────────────────────────────────────────────────────────────────────

  return (
    <div
      className={mergeClasses(styles.root, className)}
      role="region"
      aria-label="Message input"
    >
      {/* slot: banner */}
      {banner && <div className={styles.banner}>{banner}</div>}

      {/* slot: attachments */}
      {attachments.length > 0 && (
        <div className={styles.attachments}>
          <AttachmentList attachments={attachments} />
        </div>
      )}

      {/* slot: inputWrapper */}
      <div className={styles.inputWrapper}>
        {/* slot: contentBefore — defaults to attach icon if not overridden */}
        {contentBefore !== undefined ? (
          <div className={styles.contentBefore}>{contentBefore}</div>
        ) : (
          <div className={styles.contentBefore}>
            <Tooltip content="Attach" relationship="label" withArrow>
              <button
                type="button"
                aria-label="Attach file"
                style={{
                  background: "none",
                  border: "none",
                  cursor: "pointer",
                  display: "flex",
                  alignItems: "center",
                  padding: "4px",
                  borderRadius: "6px",
                  color: "var(--colorNeutralForeground3)",
                }}
              >
                <AttachRegular fontSize={18} />
              </button>
            </Tooltip>
          </div>
        )}

        {/* slot: editor */}
        <textarea
          ref={textareaRef}
          className={mergeClasses(
            styles.editor,
            appearance === "single" ? styles.editorSingle : undefined
          )}
          value={value}
          onChange={(ev) => onChange?.(ev.target.value)}
          onKeyDown={handleKeyDown}
          placeholder={placeholder}
          disabled={disabled}
          rows={appearance === "single" ? 1 : 1}
          maxLength={maxLength}
          aria-label={placeholder}
          aria-multiline={appearance !== "single"}
        />

        {/* slot: actions */}
        {actions && <div className={styles.actions}>{actions}</div>}

        {/* slot: send */}
        <SendButton
          isSending={isSending}
          canSend={canSend}
          onSend={handleSend}
          onStop={handleStop}
          hidden={hideSend}
        />
      </div>

      {/* slot: errorMessage */}
      {isOverLimit && (
        <div className={styles.errorMessage} role="alert">
          Character limit exceeded ({charCount}/{maxLength})
        </div>
      )}

      {/* slot: contentBelow */}
      {contentBelow && (
        <div className={styles.contentBelow}>{contentBelow}</div>
      )}
    </div>
  );
}
