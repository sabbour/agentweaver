/**
 * Message — UserMessage + CopilotMessage + CopilotChat
 *
 * Mirrors @1js/fai-react-copilot-chat anatomy:
 *
 * CopilotChat — mirrors CopilotChat:
 *   root (div, role="feed", scrollable)
 *
 * UserMessage — mirrors UserMessage + BebopUserMessage:
 *   Slots: root, topContent, message (bubble), timestamp, actionBar (hover Toolbar)
 *
 * CopilotMessage — mirrors CopilotMessage + BebopCopilotMessage:
 *   Slots: root, avatar (icon circle), name (label), disclaimer,
 *          content, progress (ProgressBar), footnote, actions
 *   Props: loadingState: "loading" | "streaming" | "none"
 *          announcement (aria-live)
 */
import React from "react";
import { mergeClasses, ProgressBar } from "@fluentui/react-components";
import { SparkleRegular, CopyRegular } from "@fluentui/react-icons";
import {
  useCopilotChatStyles,
  useUserMessageStyles,
  useCopilotMessageStyles,
} from "./copilotStyles";

// ─── CopilotChat ──────────────────────────────────────────────────────────────

export interface CopilotChatProps {
  children?: React.ReactNode;
  className?: string;
  style?: React.CSSProperties;
  /** aria-label for the feed region */
  label?: string;
}

export function CopilotChat({
  children,
  className,
  style,
  label = "Conversation",
}: CopilotChatProps) {
  const styles = useCopilotChatStyles();
  return (
    <div
      className={mergeClasses(styles.root, className)}
      style={style}
      role="feed"
      aria-label={label}
    >
      {children}
    </div>
  );
}

// ─── UserMessage ──────────────────────────────────────────────────────────────

export interface UserMessageProps {
  children?: React.ReactNode;
  /** slot: topContent — optional header above message bubble */
  topContent?: React.ReactNode;
  /** slot: timestamp — shown below the bubble */
  timestamp?: string;
  /** slot: actionBar — hover toolbar (copy, edit); shown on hover */
  actionBar?: React.ReactNode;
  className?: string;
  /** aria: accessible heading for screen readers */
  accessibleHeading?: string;
}

export function UserMessage({
  children,
  topContent,
  timestamp,
  actionBar,
  className,
  accessibleHeading,
}: UserMessageProps) {
  const styles = useUserMessageStyles();
  const [hovered, setHovered] = React.useState(false);

  return (
    <div
      className={mergeClasses(styles.root, className)}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      role="article"
      aria-label={accessibleHeading}
    >
      {accessibleHeading && (
        <h5 style={{ position: "absolute", width: 1, height: 1, overflow: "hidden", clip: "rect(0,0,0,0)" }}>
          {accessibleHeading}
        </h5>
      )}

      {/* slot: topContent */}
      {topContent && <div className={styles.topContent}>{topContent}</div>}

      {/* slot: actionBar — hover toolbar, renders above message when hovered */}
      {actionBar ? (
        <div className={mergeClasses(styles.actionBar, hovered ? styles.actionBarVisible : undefined)}>
          {actionBar}
        </div>
      ) : (
        <div className={mergeClasses(styles.actionBar, hovered ? styles.actionBarVisible : undefined)}>
          <CopyActionButton />
        </div>
      )}

      {/* slot: message */}
      <div className={styles.message}>{children}</div>

      {/* slot: timestamp */}
      {timestamp && <span className={styles.timestamp}>{timestamp}</span>}
    </div>
  );
}

function CopyActionButton() {
  return (
    <button
      type="button"
      aria-label="Copy message"
      style={{
        background: "none",
        border: "none",
        padding: "4px",
        borderRadius: "6px",
        cursor: "pointer",
        display: "flex",
        alignItems: "center",
        color: "var(--colorNeutralForeground3)",
      }}
    >
      <CopyRegular fontSize={14} />
    </button>
  );
}

// ─── CopilotMessage ───────────────────────────────────────────────────────────

export type CopilotLoadingState = "loading" | "streaming" | "none";

export interface CopilotMessageProps {
  children?: React.ReactNode;
  /** slot: avatar — icon circle; defaults to SparkleRegular */
  avatar?: React.ReactNode;
  /** slot: name — agent display label */
  name?: string;
  /** slot: disclaimer — shown after name */
  disclaimer?: React.ReactNode;
  /** slot: content — explicit content override (otherwise children) */
  content?: React.ReactNode;
  /** slot: progress — ProgressBar; auto-shown when loadingState != "none" */
  progress?: React.ReactNode;
  /** slot: footnote — info footer (citations) */
  footnote?: React.ReactNode;
  /** slot: actions — footer actions (FeedbackButtons, copy) */
  actions?: React.ReactNode;
  /**
   * Mirrors CopilotMessageProps.loadingState.
   * "loading" — skeleton placeholder
   * "streaming" — ProgressBar + content being written
   * "none" — complete
   */
  loadingState?: CopilotLoadingState;
  /** aria-live announcement for screen readers */
  announcement?: string;
  className?: string;
  /** accessible heading (h6) for screen readers */
  accessibleHeading?: string;
}

export function CopilotMessage({
  children,
  avatar,
  name = "Coordinator",
  disclaimer,
  content,
  progress,
  footnote,
  actions,
  loadingState = "none",
  announcement,
  className,
  accessibleHeading,
}: CopilotMessageProps) {
  const styles = useCopilotMessageStyles();

  return (
    <div
      className={mergeClasses(styles.root, className)}
      role="article"
      aria-label={accessibleHeading ?? name}
    >
      {/* Screen reader heading */}
      {accessibleHeading && (
        <h6 style={{ position: "absolute", width: 1, height: 1, overflow: "hidden", clip: "rect(0,0,0,0)" }}>
          {accessibleHeading}
        </h6>
      )}

      {/* aria-live announcement */}
      {announcement && (
        <div aria-live="polite" aria-atomic="true" style={{ position: "absolute", width: 1, height: 1, overflow: "hidden", clip: "rect(0,0,0,0)" }}>
          {announcement}
        </div>
      )}

      {/* header row: slot: avatar + name + disclaimer */}
      <div className={styles.header}>
        <div className={styles.avatar} aria-hidden>
          {avatar ?? <SparkleRegular fontSize={14} />}
        </div>
        <span className={styles.name}>{name}</span>
        {disclaimer && (
          <span className={styles.disclaimer}>{disclaimer}</span>
        )}
      </div>

      {/* slot: progress — ProgressBar when streaming */}
      {(loadingState === "streaming") && (
        <div className={styles.progress}>
          {progress ?? <ProgressBar thickness="medium" />}
        </div>
      )}

      {/* slot: content or loading skeleton */}
      {loadingState === "loading" ? (
        <div className={styles.loadingPulse} aria-busy="true">
          <div className={styles.loadingBar} style={{ width: "80%" }} />
          <div className={mergeClasses(styles.loadingBar, styles.loadingBarShort)} />
          <div className={styles.loadingBar} style={{ width: "70%" }} />
        </div>
      ) : (
        <div className={styles.content}>
          {content ?? children}
        </div>
      )}

      {/* slot: footnote */}
      {footnote && loadingState === "none" && (
        <div className={styles.footnote}>{footnote}</div>
      )}

      {/* slot: actions — only when done */}
      {actions && loadingState === "none" && (
        <div className={styles.actions}>{actions}</div>
      )}
    </div>
  );
}
