import { makeStyles, tokens } from "@fluentui/react-components";

// ─── Composer (mirrors @1js/fai-react-chat-input ChatInput slots) ────────────

export const useComposerStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusCircular,
    overflow: "hidden",
    transition: "border 150ms ease",
    ":focus-within": {
      border: `1.5px solid ${tokens.colorNeutralForeground3}`,
    },
  },
  /** slot: banner — top banner (warnings, suggestions, etc.) */
  banner: {
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  /** slot: attachments — attachment pills above the editor row */
  attachments: {
    display: "flex",
    flexWrap: "wrap",
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    paddingBottom: 0,
  },
  /** slot: inputWrapper — row containing contentBefore + editor + actions + send */
  inputWrapper: {
    display: "flex",
    alignItems: "flex-end",
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalS}`,
    paddingLeft: tokens.spacingHorizontalM,
  },
  /** slot: contentBefore — left zone (model selector, attach button) */
  contentBefore: {
    display: "flex",
    alignItems: "center",
    flexShrink: 0,
    gap: tokens.spacingHorizontalXS,
    paddingBottom: tokens.spacingVerticalXS,
  },
  /** slot: editor — the textarea element */
  editor: {
    flex: "1 1 auto",
    resize: "none",
    border: "none",
    outline: "none",
    background: "transparent",
    fontFamily: tokens.fontFamilyBase,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    color: tokens.colorNeutralForeground1,
    minHeight: "22px",
    maxHeight: "200px",
    overflowY: "auto",
    paddingTop: tokens.spacingVerticalXS,
    paddingBottom: tokens.spacingVerticalXS,
    "::placeholder": {
      color: tokens.colorNeutralForeground4,
    },
  },
  editorSingle: {
    maxHeight: "22px",
    overflowY: "hidden",
  },
  /** slot: actions — buttons to the right of editor, before send */
  actions: {
    display: "flex",
    alignItems: "center",
    flexShrink: 0,
    gap: tokens.spacingHorizontalXXS,
    paddingBottom: tokens.spacingVerticalXS,
  },
  /** slot: errorMessage — character limit or other inline error */
  errorMessage: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorPaletteRedForeground1,
  },
  /** slot: contentBelow — below composer (suggestions, etc.) */
  contentBelow: {
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
  },
});

// ─── SendButton (mirrors @1js/fai-react-send-button SendButton) ──────────────

export const useSendButtonStyles = makeStyles({
  root: {
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    width: "32px",
    height: "32px",
    borderRadius: "50%",
    border: "none",
    cursor: "pointer",
    flexShrink: 0,
    position: "relative",
    overflow: "hidden",
    transition: "background-color 150ms ease, opacity 150ms ease",
    "@media (prefers-reduced-motion: reduce)": {
      transition: "none",
    },
  },
  /** isSending=false, value present — active send state */
  active: {
    backgroundColor: tokens.colorNeutralForeground1,
    color: tokens.colorNeutralForegroundOnBrand,
    ":hover": {
      opacity: "0.85",
    },
  },
  /** isSending=false, no value — idle/disabled */
  idle: {
    backgroundColor: tokens.colorNeutralBackground4,
    color: tokens.colorNeutralForeground3,
    cursor: "default",
  },
  /** isSending=true — stop state */
  stopping: {
    backgroundColor: tokens.colorNeutralBackground4,
    color: tokens.colorNeutralForeground1,
    border: `1.5px solid ${tokens.colorNeutralStroke1}`,
    ":hover": {
      backgroundColor: tokens.colorNeutralBackground3,
    },
  },
  /** slot: sendIcon — primary icon */
  sendIcon: {
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    transition: "opacity 150ms ease, transform 150ms ease",
    "@media (prefers-reduced-motion: reduce)": {
      transition: "none",
    },
  },
  /** slot: stopIcon — shown when isSending */
  stopIcon: {
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    transition: "opacity 150ms ease",
    "@media (prefers-reduced-motion: reduce)": {
      transition: "none",
    },
  },
  iconVisible: { opacity: "1" },
  iconHidden: {
    opacity: "0",
    pointerEvents: "none",
    position: "absolute",
  },
});

// ─── Attachment (mirrors @1js/fai-react-attachments Attachment) ──────────────

export const useAttachmentStyles = makeStyles({
  root: {
    display: "inline-flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalXXS,
    backgroundColor: tokens.colorNeutralBackground3,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    padding: `2px ${tokens.spacingHorizontalS}`,
    maxWidth: "180px",
    overflow: "hidden",
  },
  /** slot: media — icon or thumbnail */
  media: {
    display: "flex",
    alignItems: "center",
    color: tokens.colorNeutralForeground3,
    flexShrink: 0,
  },
  /** slot: content — file name */
  content: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
    cursor: "pointer",
    ":hover": {
      color: tokens.colorNeutralForeground1,
    },
  },
  /** slot: dismissButton */
  dismissButton: {
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    background: "transparent",
    border: "none",
    padding: "2px",
    borderRadius: "50%",
    cursor: "pointer",
    color: tokens.colorNeutralForeground3,
    flexShrink: 0,
    ":hover": {
      color: tokens.colorNeutralForeground1,
      backgroundColor: tokens.colorNeutralBackground4,
    },
  },
});

// ─── CopilotChat feed container ───────────────────────────────────────────────

export const useCopilotChatStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalL,
    overflowY: "auto",
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalL}`,
    flex: "1 1 auto",
  },
});

// ─── UserMessage (mirrors @1js/fai-react-copilot-chat UserMessage) ───────────

export const useUserMessageStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    alignItems: "flex-end",
    gap: tokens.spacingVerticalXS,
    position: "relative",
    ":hover > .actionBarVisible": {
      opacity: "1",
    },
  },
  /** slot: topContent — above the message (optional header) */
  topContent: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    alignSelf: "flex-end",
  },
  /** slot: message — the speech bubble */
  message: {
    maxWidth: "75%",
    backgroundColor: tokens.colorNeutralForeground1,
    color: tokens.colorNeutralForegroundOnBrand,
    borderRadius: `${tokens.borderRadiusXLarge} ${tokens.borderRadiusXLarge} ${tokens.borderRadiusSmall} ${tokens.borderRadiusXLarge}`,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    wordBreak: "break-word",
    whiteSpace: "pre-wrap",
  },
  /** slot: timestamp */
  timestamp: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground4,
  },
  /** slot: actionBar — hover toolbar (copy, edit) */
  actionBar: {
    display: "flex",
    gap: tokens.spacingHorizontalXXS,
    opacity: "0",
    transition: "opacity 100ms ease",
    "@media (prefers-reduced-motion: reduce)": {
      transition: "none",
    },
  },
  actionBarVisible: {
    opacity: "1",
  },
});

// ─── CopilotMessage (mirrors @1js/fai-react-copilot-chat CopilotMessage) ─────

export const useCopilotMessageStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
    maxWidth: "100%",
  },
  /** slot: header row — avatar + name + disclaimer */
  header: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
  },
  /** slot: avatar — branded icon circle */
  avatar: {
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    width: "28px",
    height: "28px",
    borderRadius: "50%",
    backgroundColor: tokens.colorNeutralForeground1,
    color: tokens.colorNeutralForegroundOnBrand,
    flexShrink: 0,
  },
  /** slot: name — agent display label */
  name: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
  },
  /** slot: disclaimer — "AI-generated content may be inaccurate" */
  disclaimer: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground4,
    marginLeft: "auto",
  },
  /** slot: content — main message body (rendered below header) */
  content: {
    paddingLeft: "36px",
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    color: tokens.colorNeutralForeground1,
  },
  /** slot: progress — ProgressBar when streaming */
  progress: {
    paddingLeft: "36px",
    paddingRight: tokens.spacingHorizontalL,
  },
  /** slot: footnote — info footer (citations, references) */
  footnote: {
    paddingLeft: "36px",
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
  },
  /** slot: actions — footer action row (feedback, copy) */
  actions: {
    paddingLeft: "36px",
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalXS,
  },
  /** loadingState=loading — pulsing placeholder */
  loadingPulse: {
    paddingLeft: "36px",
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
  },
  loadingBar: {
    height: "12px",
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground4,
    animationName: {
      "0%": { opacity: "0.4" },
      "50%": { opacity: "0.9" },
      "100%": { opacity: "0.4" },
    },
    animationDuration: "1.5s",
    animationIterationCount: "infinite",
    animationTimingFunction: "ease-in-out",
  },
  loadingBarShort: {
    width: "60%",
  },
  "@media (prefers-reduced-motion: reduce)": {
    loadingBar: {
      animationName: "none",
      opacity: "0.6",
    },
  },
});

// ─── OutputCard (mirrors @1js/fai-react-output-card OutputCard) ──────────────

export const useOutputCardStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusXLarge,
    overflow: "hidden",
    transition: "box-shadow 200ms ease",
  },
  rootCanvas: {
    boxShadow: tokens.shadow4,
  },
  /** slot: progress — opt-in ProgressBar at top of card */
  progress: {
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  body: {
    padding: tokens.spacingHorizontalL,
  },
  /** After isLoading transitions to false: subtle border brightening */
  done: {
    border: `1px solid ${tokens.colorNeutralStroke1}`,
  },
});

// ─── FeedbackButtons (mirrors @1js/fai-react-feedback-buttons FeedbackButtons) 

export const useFeedbackButtonStyles = makeStyles({
  root: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalXXS,
  },
  /** slot: positiveFeedbackButton */
  positiveButton: {
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    background: "transparent",
    border: "none",
    borderRadius: tokens.borderRadiusMedium,
    padding: "4px",
    cursor: "pointer",
    color: tokens.colorNeutralForeground3,
    transition: "color 100ms ease, background-color 100ms ease",
    ":hover": {
      color: tokens.colorNeutralForeground1,
      backgroundColor: tokens.colorNeutralBackground3,
    },
  },
  /** slot: negativeFeedbackButton */
  negativeButton: {
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    background: "transparent",
    border: "none",
    borderRadius: tokens.borderRadiusMedium,
    padding: "4px",
    cursor: "pointer",
    color: tokens.colorNeutralForeground3,
    transition: "color 100ms ease, background-color 100ms ease",
    ":hover": {
      color: tokens.colorNeutralForeground1,
      backgroundColor: tokens.colorNeutralBackground3,
    },
  },
  selectedPositive: {
    color: tokens.colorPaletteGreenForeground1,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  selectedNegative: {
    color: tokens.colorPaletteRedForeground1,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  disabled: {
    opacity: "0.5",
    pointerEvents: "none",
  },
});
