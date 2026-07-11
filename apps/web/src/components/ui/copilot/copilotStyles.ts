import { makeStyles, tokens } from '@fluentui/react-components';

export const useCopilotStyles = makeStyles({
  // ─── Composer ───────────────────────────────────────────────────────────────
  composerShell: {
    display: 'flex',
    flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusCircular,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalXS} ${tokens.spacingVerticalXS} ${tokens.spacingHorizontalL}`,
    transition: 'border-color 150ms ease-out, box-shadow 150ms ease-out',
    '@media (prefers-reduced-motion: reduce)': {
      transition: 'none',
    },
    ':focus-within': {
      border: `1px solid ${tokens.colorNeutralStroke1Hover}`,
    },
  },
  composerRow: {
    display: 'flex',
    alignItems: 'flex-end',
    gap: tokens.spacingHorizontalXS,
  },
  composerTextarea: {
    flex: '1',
    border: 'none',
    outline: 'none',
    backgroundColor: 'transparent',
    resize: 'none',
    fontFamily: tokens.fontFamilyBase,
    fontSize: tokens.fontSizeBase300,
    lineHeight: '1.5',
    color: tokens.colorNeutralForeground1,
    paddingTop: '6px',
    paddingBottom: '6px',
    minHeight: '28px',
    maxHeight: '200px',
    overflowY: 'auto',
    '::placeholder': {
      color: tokens.colorNeutralForeground3,
    },
    // Scrollbar styling (thin warm scrollbar)
    scrollbarWidth: 'thin',
    scrollbarColor: `${tokens.colorNeutralStroke1} transparent`,
  },
  composerLeftSlot: {
    display: 'flex',
    alignItems: 'center',
    alignSelf: 'flex-end',
    paddingBottom: '4px',
    flexShrink: 0,
  },
  composerActions: {
    display: 'flex',
    alignItems: 'center',
    alignSelf: 'flex-end',
    gap: '2px',
    paddingBottom: '4px',
    flexShrink: 0,
  },
  // Send button: circular, ink when active
  sendButtonActive: {
    backgroundColor: tokens.colorNeutralForeground1,
    color: tokens.colorNeutralForegroundOnBrand,
    borderRadius: tokens.borderRadiusCircular,
    ':hover': {
      backgroundColor: tokens.colorNeutralForeground2,
      color: tokens.colorNeutralForegroundOnBrand,
    },
    ':active': {
      backgroundColor: tokens.colorNeutralForeground1Pressed,
      color: tokens.colorNeutralForegroundOnBrand,
    },
  },
  sendButtonIdle: {
    borderRadius: tokens.borderRadiusCircular,
    color: tokens.colorNeutralForeground3,
  },
  stopButton: {
    borderRadius: tokens.borderRadiusCircular,
    color: tokens.colorNeutralForeground2,
  },

  // ─── MessageList ────────────────────────────────────────────────────────────
  messageList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    overflowY: 'auto',
    flex: '1',
    // Thin scrollbar
    scrollbarWidth: 'thin',
    scrollbarColor: `${tokens.colorNeutralStroke1} transparent`,
  },

  // ─── MessageBubble ──────────────────────────────────────────────────────────
  messageBubbleWrapper: {
    display: 'flex',
    flexDirection: 'column',
    maxWidth: '80%',
    gap: tokens.spacingVerticalXS,
  },
  messageBubbleUser: {
    alignSelf: 'flex-end',
    alignItems: 'flex-end',
  },
  messageBubbleAssistant: {
    alignSelf: 'flex-start',
    alignItems: 'flex-start',
    maxWidth: '90%',
  },
  messageBubbleContent: {
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    fontSize: tokens.fontSizeBase300,
    lineHeight: '1.5',
    wordBreak: 'break-word',
  },
  messageBubbleContentUser: {
    backgroundColor: tokens.colorNeutralForeground1,
    color: tokens.colorNeutralForegroundOnBrand,
    // Rounded pill except bottom-right corner — matches copilot.com user bubble shape
    borderRadius: `${tokens.borderRadiusXLarge} ${tokens.borderRadiusXLarge} ${tokens.borderRadiusSmall} ${tokens.borderRadiusXLarge}`,
  },
  messageBubbleContentAssistant: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground1,
    // Rounded pill except top-left corner
    borderRadius: `${tokens.borderRadiusSmall} ${tokens.borderRadiusXLarge} ${tokens.borderRadiusXLarge} ${tokens.borderRadiusXLarge}`,
  },
  messageBubbleMeta: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground4,
    paddingLeft: tokens.spacingHorizontalXS,
    paddingRight: tokens.spacingHorizontalXS,
  },
  messageBubbleSenderName: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
    paddingLeft: tokens.spacingHorizontalXS,
  },

  // ─── OutputCard ─────────────────────────────────────────────────────────────
  outputCard: {
    display: 'flex',
    flexDirection: 'column',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    overflow: 'hidden',
    alignSelf: 'flex-start',
    maxWidth: '90%',
  },
  outputCardProgress: {
    // ProgressBar sits flush at the top, full width
    borderRadius: '0',
  },
  outputCardBody: {
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    fontSize: tokens.fontSizeBase300,
    lineHeight: '1.5',
    color: tokens.colorNeutralForeground1,
  },
  outputCardFooter: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  outputCardFooterSpacer: {
    flex: '1',
  },
  outputCardFeedbackLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
});
