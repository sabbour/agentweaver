import { memo, useState } from 'react';
import {
  Badge,
  Spinner,
  Text,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import {
  CheckmarkCircleFilled,
  ChevronDownRegular,
  ChevronRightRegular,
  ErrorCircleFilled,
  WarningFilled,
  WrenchRegular,
} from '@fluentui/react-icons';
import type { ToolCallItem } from '../timeline/types';
import type { StreamStatus } from '../api/sse';

/** Characters displayed per content block before truncation (Y-1). */
const BLOCK_MAX = 50_000;

const useStyles = makeStyles({
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    paddingTop: '1px',
    paddingBottom: '1px',
    cursor: 'pointer',
    background: 'none',
    border: 'none',
    padding: 0,
    width: '100%',
    textAlign: 'left',
    ':hover': {
      opacity: 0.75,
    },
  },
  rowError: {
    color: tokens.colorPaletteRedForeground1,
  },
  rowSandbox: {
    color: tokens.colorPaletteYellowForeground1,
  },
  chevron: {
    color: tokens.colorNeutralForeground3,
    flexShrink: 0,
    fontSize: '10px',
  },
  icon: {
    color: tokens.colorNeutralForeground3,
    flexShrink: 0,
  },
  statusIcon: {
    flexShrink: 0,
  },
  successIcon: {
    color: tokens.colorPaletteGreenForeground1,
  },
  errorIcon: {
    color: tokens.colorPaletteRedForeground1,
  },
  sandboxIcon: {
    color: tokens.colorPaletteYellowForeground1,
  },
  title: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    flexGrow: 1,
  },
  badge: {
    marginLeft: tokens.spacingHorizontalXS,
    flexShrink: 0,
  },
  detail: {
    marginLeft: '20px',
    paddingLeft: tokens.spacingHorizontalS,
    borderLeft: `2px solid ${tokens.colorNeutralStroke2}`,
    marginBottom: '2px',
  },
  block: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase100,
    paddingTop: '2px',
    paddingBottom: '2px',
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-all',
    color: tokens.colorNeutralForeground1,
  },
  blockLabel: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    marginRight: tokens.spacingHorizontalXS,
  },
  truncatedNote: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    display: 'block',
  },
});

interface ToolCallCardProps {
  item: ToolCallItem;
  streamStatus: StreamStatus;
}

function truncate(text: string) {
  const truncated = text.length > BLOCK_MAX;
  return { display: truncated ? text.slice(0, BLOCK_MAX) : text, truncated, total: text.length };
}

export const ToolCallCard = memo(function ToolCallCard({ item, streamStatus }: ToolCallCardProps) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(item.error?.isSandboxViolation ?? false);

  const isSandbox = item.error?.isSandboxViolation ?? false;
  const isError = item.error && !isSandbox;

  // Detect non-zero exit code from run_command result text (e.g. "exit_code: -1\n...")
  const exitCodeMatch = item.result?.content?.match(/^exit_code:\s*(-?\d+)/m);
  const exitCode = exitCodeMatch ? parseInt(exitCodeMatch[1], 10) : 0;
  const isNonZeroExit = item.toolName === 'run_command' && item.settled && !item.error && exitCode !== 0;

  function StatusIcon() {
    if (!item.settled) {
      return streamStatus === 'error' ? (
        <WarningFilled className={mergeClasses(styles.statusIcon, styles.sandboxIcon)} aria-label="Result not received" />
      ) : (
        <Spinner size="extra-tiny" aria-label="Pending" />
      );
    }
    if (isSandbox) return <WarningFilled className={mergeClasses(styles.statusIcon, styles.sandboxIcon)} aria-hidden="true" />;
    if (item.error) return <ErrorCircleFilled className={mergeClasses(styles.statusIcon, styles.errorIcon)} aria-hidden="true" />;
    if (isNonZeroExit) return <WarningFilled className={mergeClasses(styles.statusIcon, styles.sandboxIcon)} aria-label={`Exit code ${exitCode}`} />;
    return <CheckmarkCircleFilled className={mergeClasses(styles.statusIcon, styles.successIcon)} aria-hidden="true" />;
  }

  // "ok" is a no-content acknowledgement — nothing useful to expand.
  const hasDetail = !!(
    (item.result && item.result.content.trim() !== 'ok') ||
    item.error
  );

  return (
    <div>
      {/* SECURITY (Y-3): all user-controlled strings rendered as text nodes */}
      <button
        className={mergeClasses(
          styles.row,
          isSandbox ? styles.rowSandbox : undefined,
          isError ? styles.rowError : undefined,
        )}
        onClick={() => hasDetail && setExpanded(e => !e)}
        aria-expanded={hasDetail ? expanded : undefined}
        aria-label={`${item.humanTitle} — ${!item.settled ? 'pending' : isSandbox ? 'sandbox violation' : item.error ? 'error' : 'ok'}`}
      >
        {hasDetail
          ? (expanded
              ? <ChevronDownRegular className={styles.chevron} aria-hidden="true" />
              : <ChevronRightRegular className={styles.chevron} aria-hidden="true" />)
          : <span style={{ width: 10, display: 'inline-block', flexShrink: 0 }} aria-hidden="true" />
        }
        <WrenchRegular className={styles.icon} aria-hidden="true" />
        <StatusIcon />
        <Text className={styles.title}>{item.humanTitle}</Text>
        {isSandbox && <Badge className={styles.badge} color="warning" shape="rounded" size="small">sandbox</Badge>}
        {isError && <Badge className={styles.badge} color="danger" shape="rounded" size="small">error</Badge>}
      </button>

      {expanded && hasDetail && (
        <div className={styles.detail}>
          {item.result && (() => {
            const { display, truncated, total } = truncate(item.result.content);
            return (
              <div className={styles.block}>
                <Text as="pre" style={{ margin: 0, fontFamily: 'inherit', fontSize: 'inherit' }}>
                  {display}
                </Text>
                {truncated && <Text as="span" className={styles.truncatedNote}>[Truncated — {total.toLocaleString()} chars]</Text>}
              </div>
            );
          })()}

          {item.error && (() => {
            const { display, truncated, total } = truncate(item.error.errorMessage);
            return (
              <div className={styles.block}>
                <Text as="span" className={styles.blockLabel}>{isSandbox ? 'violation' : 'error'}</Text>
                <Text as="pre" style={{ margin: 0, fontFamily: 'inherit', fontSize: 'inherit', display: 'inline', color: isSandbox ? tokens.colorPaletteYellowForeground1 : tokens.colorPaletteRedForeground1 }}>
                  {display}
                </Text>
                {truncated && <Text as="span" className={styles.truncatedNote}>[Truncated — {total.toLocaleString()} chars]</Text>}
              </div>
            );
          })()}
        </div>
      )}
    </div>
  );
});