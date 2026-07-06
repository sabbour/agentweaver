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
  CodeRegular,
  DeleteRegular,
  DocumentArrowDownRegular,
  DocumentEditRegular,
  DocumentRegular,
  ErrorCircleFilled,
  FolderRegular,
  InfoRegular,
  SearchRegular,
  WarningFilled,
  WrenchRegular,
} from '@fluentui/react-icons';
import type { FluentIcon } from '@fluentui/react-icons';
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
    fontSize: tokens.fontSizeBase100,
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
    color: tokens.colorNeutralForeground2,
    flexShrink: 1,
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  meta: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground4,
    flexShrink: 0,
    whiteSpace: 'nowrap',
  },
  spacer: {
    flexGrow: 1,
  },
  badge: {
    marginLeft: tokens.spacingHorizontalXS,
    flexShrink: 0,
  },
  detail: {
    marginLeft: tokens.spacingHorizontalXL,
    paddingLeft: tokens.spacingHorizontalS,
    borderLeft: `2px solid ${tokens.colorNeutralStroke2}`,
    marginBottom: tokens.spacingVerticalXXS,
  },
  block: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase100,
    paddingTop: tokens.spacingVerticalXXS,
    paddingBottom: tokens.spacingVerticalXXS,
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
  /** When true (report_intent only), the following tool cluster had failures — show ⚠ instead of ✅. */
  hasFollowingErrors?: boolean;
}

function truncate(text: string) {
  const truncated = text.length > BLOCK_MAX;
  return { display: truncated ? text.slice(0, BLOCK_MAX) : text, truncated, total: text.length };
}

/** Tool-name groups used to pick a calm, action-appropriate leading icon. */
const READ_TOOLS = new Set(['read_file', 'read', 'view', 'open', 'cat', 'get_file_contents']);
const SEARCH_TOOLS = new Set(['search_files', 'grep_search', 'grep', 'search', 'search_code', 'ripgrep']);
const FIND_TOOLS = new Set(['file_search', 'find_files', 'glob', 'find']);
const EDIT_TOOLS = new Set([
  'write_file', 'create_file', 'create', 'edit_file', 'edit',
  'str_replace_editor', 'apply_patch', 'move_file',
]);
const DELETE_TOOLS = new Set(['delete_file', 'delete', 'remove_file']);
const LIST_TOOLS = new Set(['list_directory', 'list_dir', 'ls']);
const CODE_EXT = /\.(tsx?|jsx?|mjs|cjs|py|go|rs|java|kt|rb|cs|cpp|cc|c|h|hpp|swift|php|scala)$/i;

/**
 * Choose a leading action icon that varies by the tool's action type, matching a
 * calm CLI/Copilot transcript: read = document/down-arrow, code file = code glyph,
 * search = magnifier, edit/write = document-edit, list = folder, delete = trash,
 * info = info circle. Constitution VIII: FluentUI icons only.
 */
function leadingIcon(item: ToolCallItem): FluentIcon {
  const name = item.toolName;
  if (name === 'report_intent' || name === 'report_outcome') return InfoRegular;
  if (LIST_TOOLS.has(name)) return FolderRegular;
  if (DELETE_TOOLS.has(name)) return DeleteRegular;
  if (SEARCH_TOOLS.has(name)) return SearchRegular;
  if (FIND_TOOLS.has(name)) return SearchRegular;
  if (name === 'run_command') return CodeRegular;
  const path = pathArgOf(item);
  if (READ_TOOLS.has(name)) return path && CODE_EXT.test(path) ? CodeRegular : DocumentArrowDownRegular;
  if (EDIT_TOOLS.has(name)) return DocumentEditRegular;
  if (path && CODE_EXT.test(path)) return CodeRegular;
  if (path) return DocumentRegular;
  return WrenchRegular;
}

function pathArgOf(item: ToolCallItem): string | null {
  const a = item.args ?? {};
  const p = a['path'] ?? a['file'] ?? a['dir'] ?? a['filename'];
  return p != null ? String(p) : null;
}

/** Count non-empty lines in a block of text. */
function countLines(text: string): number {
  const trimmed = text.replace(/\n+$/, '');
  if (trimmed === '') return 0;
  return trimmed.split('\n').length;
}

/**
 * Derive a short, MUTED secondary metadata label shown after the primary title
 * (e.g. "220 lines", "28 matches", "6 results"). Returns null when nothing
 * meaningful can be derived, so ordinary rows stay clean.
 */
function deriveMeta(item: ToolCallItem): string | null {
  if (!item.settled || item.error || !item.result) return null;
  const content = item.result.content;
  if (!content || content.trim() === '' || content.trim() === 'ok') return null;
  const name = item.toolName;
  const n = countLines(content);
  if (n === 0) return null;
  if (SEARCH_TOOLS.has(name)) return `${n} match${n === 1 ? '' : 'es'}`;
  if (FIND_TOOLS.has(name)) return `${n} result${n === 1 ? '' : 's'}`;
  if (LIST_TOOLS.has(name)) return `${n} item${n === 1 ? '' : 's'}`;
  if (READ_TOOLS.has(name)) return `${n} line${n === 1 ? '' : 's'}`;
  return null;
}

export const ToolCallCard = memo(function ToolCallCard({ item, streamStatus, hasFollowingErrors }: ToolCallCardProps) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);

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
    if (hasFollowingErrors) return <WarningFilled className={mergeClasses(styles.statusIcon, styles.sandboxIcon)} aria-label="Intent not fulfilled" />;
    return <CheckmarkCircleFilled className={mergeClasses(styles.statusIcon, styles.successIcon)} aria-hidden="true" />;
  }

  // Always expandable — args are always worth showing for debugging.
  const hasArgs = item.args && Object.keys(item.args).length > 0;
  const hasDetail = !!(
    hasArgs ||
    (item.result && item.result.content.trim() !== 'ok') ||
    item.error
  );

  const LeadIcon = leadingIcon(item);
  const meta = deriveMeta(item);

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
        <LeadIcon className={styles.icon} aria-hidden="true" />
        <StatusIcon />
        <Text className={styles.title}>{item.humanTitle}</Text>
        {meta && <Text className={styles.meta} aria-hidden="true">{meta}</Text>}
        <span className={styles.spacer} aria-hidden="true" />
        {isSandbox && <Badge className={styles.badge} color="warning" shape="rounded" size="small">sandbox</Badge>}
        {isError && <Badge className={styles.badge} color="danger" shape="rounded" size="small">error</Badge>}
      </button>

      {expanded && hasDetail && (
        <div className={styles.detail}>
          {/* Args block — always shown first so the literal tool call is visible */}
          {hasArgs && (() => {
            const argsJson = JSON.stringify(item.args, null, 2);
            const { display, truncated, total } = truncate(argsJson);
            return (
              <div className={styles.block}>
                <Text as="span" className={styles.blockLabel}>args</Text>
                <Text as="pre" style={{ margin: 0, fontFamily: 'inherit', fontSize: 'inherit', display: 'inline' }}>
                  {display}
                </Text>
                {truncated && <Text as="span" className={styles.truncatedNote}>[Truncated — {total.toLocaleString()} chars]</Text>}
              </div>
            );
          })()}

          {item.result && item.result.content.trim() !== 'ok' && (() => {
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