import { memo, type ComponentProps } from 'react';
import { Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { BotRegular } from '@fluentui/react-icons';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize, { defaultSchema } from 'rehype-sanitize';

// SECURITY: sanitize with the default schema (no raw HTML passthrough).
// rehype-raw is intentionally NOT included — raw HTML in agent text is neutralised.
// This schema strips <script>, event-handler attributes, and any tag not on the allowlist.
const SANITIZE_SCHEMA = defaultSchema;

const useStyles = makeStyles({
  wrapper: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'flex-start',
    paddingTop: tokens.spacingVerticalXS,
    paddingBottom: tokens.spacingVerticalXS,
  },
  icon: {
    color: tokens.colorNeutralForeground3,
    flexShrink: 0,
    marginTop: '2px',
  },
  bubble: {
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusLarge,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    maxWidth: '80%',
    wordBreak: 'break-word',
  },
  // Plain-text streaming view preserves whitespace (pre-wrap), markdown view does not need it.
  plainText: {
    whiteSpace: 'pre-wrap',
  },
  // Streaming cursor — only applied when streaming && isLiveRun (§4.2)
  // Respects prefers-reduced-motion: the animation is disabled but the static
  // cursor block remains visible (§6.5).
  cursorAfter: {
    '::after': {
      content: '""',
      display: 'inline-block',
      width: '2px',
      height: '1em',
      backgroundColor: tokens.colorBrandForeground1,
      marginLeft: tokens.spacingHorizontalXS,
      verticalAlign: 'text-bottom',
      animationName: {
        '0%, 100%': { opacity: '1' },
        '50%': { opacity: '0' },
      },
      animationDuration: '1s',
      animationIterationCount: 'infinite',
      animationTimingFunction: 'step-start',
    },
    '@media (prefers-reduced-motion: reduce)': {
      '::after': {
        animationName: 'none',
      },
    },
  },
  placeholder: {
    minHeight: '1em',
  },
  truncated: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    marginTop: tokens.spacingVerticalXS,
  },
  // Markdown body — resets margins so the bubble padding alone controls spacing.
  markdownBody: {
    '& > *:first-child': { marginTop: '0' },
    '& > *:last-child': { marginBottom: '0' },
    '& p': {
      marginTop: tokens.spacingVerticalXS,
      marginBottom: tokens.spacingVerticalXS,
    },
    '& ul, & ol': {
      paddingLeft: tokens.spacingHorizontalL,
      marginTop: tokens.spacingVerticalXS,
      marginBottom: tokens.spacingVerticalXS,
    },
    '& h1, & h2, & h3, & h4, & h5, & h6': {
      marginTop: tokens.spacingVerticalM,
      marginBottom: tokens.spacingVerticalXS,
    },
    '& h1': {
      fontSize: tokens.fontSizeBase500,
      lineHeight: tokens.lineHeightBase500,
      fontWeight: tokens.fontWeightSemibold,
    },
    '& h2': {
      fontSize: tokens.fontSizeBase400,
      lineHeight: tokens.lineHeightBase400,
      fontWeight: tokens.fontWeightSemibold,
    },
    '& h3': {
      fontSize: tokens.fontSizeBase300,
      lineHeight: tokens.lineHeightBase300,
      fontWeight: tokens.fontWeightBold,
    },
    '& h4, & h5, & h6': {
      fontSize: tokens.fontSizeBase300,
      lineHeight: tokens.lineHeightBase300,
      fontWeight: tokens.fontWeightSemibold,
    },
    // Scoped color and display ensure Fluent-consistent code styling regardless of browser defaults.
    '& code': {
      fontFamily: tokens.fontFamilyMonospace,
      backgroundColor: tokens.colorNeutralBackground2,
      borderRadius: tokens.borderRadiusSmall,
      padding: `1px ${tokens.spacingHorizontalXS}`,
      fontSize: tokens.fontSizeBase200,
      color: tokens.colorNeutralForeground1,
      display: 'inline',
    },
    '& pre': {
      fontFamily: tokens.fontFamilyMonospace,
      backgroundColor: tokens.colorNeutralBackground2,
      borderRadius: tokens.borderRadiusMedium,
      padding: tokens.spacingVerticalS,
      overflowX: 'auto',
      fontSize: tokens.fontSizeBase200,
      '& code': {
        backgroundColor: 'transparent',
        padding: '0',
        borderRadius: '0',
        color: tokens.colorNeutralForeground1,
      },
    },
    '& blockquote': {
      borderLeft: `3px solid ${tokens.colorNeutralStroke1}`,
      paddingLeft: tokens.spacingHorizontalM,
      marginLeft: '0',
      color: tokens.colorNeutralForeground3,
    },
    '& a': {
      color: tokens.colorBrandForeground1,
    },
    '& table': {
      borderCollapse: 'collapse',
      width: '100%',
    },
    '& th, & td': {
      border: `1px solid ${tokens.colorNeutralStroke1}`,
      padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
      textAlign: 'left',
    },
    '& th': {
      backgroundColor: tokens.colorNeutralBackground2,
      fontWeight: tokens.fontWeightSemibold,
    },
  },
});

/** Characters shown before the "show more" truncation affordance (Y-1). */
const DISPLAY_MAX = 50_000;

// SECURITY: custom link renderer forces safe external-link attributes.
// This prevents target="_blank" without rel="noopener noreferrer" (reverse tabnapping).
function SafeLink({ href, children }: ComponentProps<'a'>) {
  return (
    <a href={href} target="_blank" rel="noopener noreferrer">
      {children}
    </a>
  );
}

interface AgentMessageBubbleProps {
  content: string;
  streaming: boolean;
  isLiveRun: boolean;
}

export const AgentMessageBubble = memo(function AgentMessageBubble({
  content,
  streaming,
  isLiveRun,
}: AgentMessageBubbleProps) {
  const styles = useStyles();
  // Cursor shown only while actively streaming a live run (§4.2).
  const showCursor = streaming && isLiveRun;
  // Markdown rendered only for fully settled messages to avoid broken partial fences/lists.
  const renderMarkdown = !showCursor;

  // Suppress render for empty settled messages (§7.3)
  if (!streaming && content.trim() === '') return null;

  const isTruncated = content.length >= DISPLAY_MAX;
  const displayContent = isTruncated ? content.slice(0, DISPLAY_MAX) : content;

  return (
    // aria-label on wrapper; aria-live scoped to inner text only when streaming (§6.3, fix #6)
    <div className={styles.wrapper} aria-label="Agent message">
      {/* aria-hidden — icon is decorative, text is the accessible label (§6.4) */}
      <BotRegular className={styles.icon} aria-hidden="true" />
      <div className={mergeClasses(styles.bubble, showCursor && styles.cursorAfter, !renderMarkdown && styles.plainText)}>
        {renderMarkdown ? (
          // SECURITY: react-markdown builds a React element tree — no dangerouslySetInnerHTML.
          // rehype-sanitize (defaultSchema) strips <script>, onerror, and all non-allowlisted HTML.
          // rehype-raw is intentionally omitted so raw HTML in agent text is never interpreted.
          <div
            className={styles.markdownBody}
            aria-live={undefined}
          >
            <ReactMarkdown
              remarkPlugins={[remarkGfm]}
              rehypePlugins={[[rehypeSanitize, SANITIZE_SCHEMA]]}
              components={{ a: SafeLink }}
            >
              {displayContent}
            </ReactMarkdown>
          </div>
        ) : (
          // Streaming: plain escaped React text node + blinking cursor (§4.2).
          // SECURITY (Y-3): no HTML interpretation — content is a React text node.
          <Text
            as="span"
            className={content === '' ? styles.placeholder : undefined}
            aria-live={streaming && isLiveRun ? 'polite' : undefined}
          >
            {displayContent}
          </Text>
        )}
        {isTruncated && (
          <div className={styles.truncated}>
            [Content truncated — {content.length.toLocaleString()} chars total]
          </div>
        )}
      </div>
    </div>
  );
});
