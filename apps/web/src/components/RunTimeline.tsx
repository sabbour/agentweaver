import { ChainOfThought, ChainOfThoughtItem } from '@1js/fluentai';
import {
  Button,
  makeStyles,
  mergeClasses,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import {
  CheckmarkCircleFilled,
  CheckmarkRegular,
  ChevronDownRegular,
  ChevronRightRegular,
  CircleRegular,
  CloudRegular,
  DismissCircleFilled,
  DocumentEditRegular,
  DocumentRegular,
  SearchRegular,
  WarningFilled,
  WindowConsoleRegular,
  WrenchRegular,
} from '@fluentui/react-icons';
import { SafeMarkdown } from './SafeMarkdown';
import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { AgentweaverCopilotProvider } from './ui/copilot/AgentweaverCopilotProvider';
import { Body, EmptyState, Label } from './ui';
import type {
  RunTimelineMessage,
  RunTimelineStep,
  RunTimelineStepStatus,
  RunTimelineTool,
  RunTimelineToolCategory,
} from '../timeline/runTimelineSteps';
import { formatAbsoluteTime, formatRelativeTime } from '../utils/relativeTime';

/** Single place to rename the surface. */
export const TIMELINE_LABEL = 'Messages';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minHeight: 0,
    // Strip the embedded @1js ChainOfThought's own bordered/scrolling Card chrome so the thread
    // grows naturally and the parent scroll region (AgentSessionPanel's tabBody) is the ONLY
    // scroller. Without this the messages are squeezed into a short inner card (~208px max-height)
    // with its own scrollbar and dead space below.
    '& .fai-ChainOfThought__card': {
      border: 'none',
      boxShadow: 'none',
      backgroundColor: 'transparent',
      padding: 0,
    },
    '& .fai-ChainOfThought__card::after': {
      display: 'none',
    },
    '& .fai-ChainOfThought__activitiesPanel': {
      maxHeight: 'none',
      overflow: 'visible',
      padding: 0,
    },
  },
  cotHeaderText: {
    display: 'inline-flex',
    alignItems: 'baseline',
    gap: tokens.spacingHorizontalXS,
  },
  cotHeaderSub: {
    color: tokens.colorNeutralForeground3,
    fontWeight: tokens.fontWeightRegular,
  },
  statusIndicator: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '16px',
    height: '16px',
  },
  iconComplete: { color: tokens.colorNeutralForeground2 },
  iconWarning: { color: '#8a4b01' },
  iconPending: { color: tokens.colorNeutralForeground4 },
  content: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    paddingTop: tokens.spacingVerticalXS,
  },
  toolGroup: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  // Overrides the library's ChainOfThoughtItem headerText, which hard-codes
  // `white-space: nowrap; text-overflow: ellipsis` for a single-line clamp — fine for short
  // reported intents, but long ones (a full sentence summarizing what happened) get silently
  // cut off with no way to read the rest. Let it wrap across lines instead.
  stepHeaderText: {
    whiteSpace: 'normal',
    overflow: 'visible',
    textOverflow: 'clip',
    textAlign: 'left',
  },
  toolGroupHeader: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    minWidth: 'auto',
    justifyContent: 'flex-start',
    fontWeight: tokens.fontWeightRegular,
    color: tokens.colorNeutralForeground2,
    paddingLeft: tokens.spacingHorizontalXXS,
  },
  toolList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    paddingLeft: tokens.spacingHorizontalL,
  },
  toolRow: {
    display: 'grid',
    gridTemplateColumns: '16px minmax(0, auto) minmax(0, 1fr) auto',
    alignItems: 'center',
    columnGap: tokens.spacingHorizontalS,
    paddingTop: tokens.spacingVerticalXXS,
    paddingBottom: tokens.spacingVerticalXXS,
    borderRadius: tokens.borderRadiusSmall,
  },
  toolRowButton: {
    border: 'none',
    background: 'none',
    padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalXXS}`,
    margin: 0,
    textAlign: 'left',
    cursor: 'pointer',
    width: '100%',
    font: 'inherit',
    color: 'inherit',
    ':hover': { backgroundColor: tokens.colorNeutralBackground2 },
  },
  toolIcon: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: tokens.colorNeutralForeground3,
  },
  toolTitle: {
    color: tokens.colorNeutralForeground1,
    fontSize: tokens.fontSizeBase200,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  toolTitleError: {
    color: '#a62147',
  },
  toolSecondary: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    fontFamily: tokens.fontFamilyMonospace,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    minWidth: 0,
  },
  toolMeta: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'nowrap',
    justifySelf: 'end',
  },
  toolExpandChevron: {
    display: 'inline-flex',
    alignItems: 'center',
    color: tokens.colorNeutralForeground3,
  },
  toolState: {
    display: 'inline-flex',
    alignItems: 'center',
    color: tokens.colorNeutralForeground3,
  },
  toolStateError: { color: '#a62147' },
  toolStateComplete: { color: tokens.colorNeutralForeground2 },
  toolError: {
    color: '#a62147',
    fontSize: tokens.fontSizeBase200,
    paddingLeft: `calc(16px + ${tokens.spacingHorizontalS})`,
  },
  diffCard: {
    marginLeft: `calc(16px + ${tokens.spacingHorizontalS})`,
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    overflow: 'hidden',
    backgroundColor: tokens.colorNeutralBackground1,
  },
  diffHeader: {
    padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalS}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    display: 'flex',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
  },
  diffHeaderCounts: {
    flexShrink: 0,
    display: 'inline-flex',
    gap: tokens.spacingHorizontalXS,
  },
  diffAdded: { color: tokens.colorPaletteGreenForeground1 },
  diffRemoved: { color: tokens.colorPaletteRedForeground1 },
  diffScroll: {
    maxHeight: '260px',
    overflow: 'auto',
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  diffLine: {
    display: 'grid',
    gridTemplateColumns: '16px 1fr',
    columnGap: tokens.spacingHorizontalXS,
    paddingLeft: tokens.spacingHorizontalXS,
    paddingRight: tokens.spacingHorizontalXS,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  diffLineSign: {
    userSelect: 'none',
    textAlign: 'center',
    color: tokens.colorNeutralForeground4,
  },
  diffLineAdded: {
    backgroundColor: tokens.colorPaletteGreenBackground1,
  },
  diffLineRemoved: {
    backgroundColor: tokens.colorPaletteRedBackground1,
  },
  diffLineHunk: {
    color: tokens.colorNeutralForeground3,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  diffTruncated: {
    color: tokens.colorNeutralForeground3,
    fontFamily: tokens.fontFamilyBase,
    fontSize: tokens.fontSizeBase200,
    paddingTop: tokens.spacingVerticalXS,
    paddingLeft: tokens.spacingHorizontalXS,
    fontStyle: 'italic',
  },
  outputScroll: {
    maxHeight: '260px',
    overflow: 'auto',
    margin: 0,
    padding: tokens.spacingVerticalS,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  message: {
    color: tokens.colorNeutralForeground1,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    '& p': { margin: `0 0 ${tokens.spacingVerticalS} 0` },
    '& p:last-child': { marginBottom: 0 },
    '& pre': {
      backgroundColor: tokens.colorNeutralBackground3,
      padding: tokens.spacingVerticalS,
      borderRadius: tokens.borderRadiusMedium,
      overflowX: 'auto',
      fontSize: tokens.fontSizeBase200,
    },
    '& code': { fontFamily: tokens.fontFamilyMonospace },
  },
  messageUser: {
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    color: tokens.colorNeutralForeground1,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  messageUserLabel: {
    display: 'block',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    marginBottom: tokens.spacingVerticalXXS,
  },
  messageStreaming: {
    color: tokens.colorNeutralForeground2,
    whiteSpace: 'pre-wrap',
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
  },
  messageTimestamp: {
    display: 'block',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    marginBottom: tokens.spacingVerticalXXS,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
});

function StepStatusIcon({ status }: { status: RunTimelineStepStatus }) {
  const styles = useStyles();
  if (status === 'running') {
    return <Spinner size="extra-tiny" aria-label="Running" />;
  }
  if (status === 'complete') {
    return <CheckmarkCircleFilled className={styles.iconComplete} aria-label="Complete" />;
  }
  if (status === 'warning') {
    return <WarningFilled className={styles.iconWarning} aria-label="Needs attention" />;
  }
  return <CircleRegular className={styles.iconPending} aria-label="Pending" />;
}

function toolIconFor(category: RunTimelineToolCategory): ReactNode {
  switch (category) {
    case 'command':
      return <WindowConsoleRegular />;
    case 'read':
      return <DocumentRegular />;
    case 'edit':
      return <DocumentEditRegular />;
    case 'search':
      return <SearchRegular />;
    case 'web':
      return <CloudRegular />;
    case 'other':
    default:
      return <WrenchRegular />;
  }
}

function ToolResultState({ status }: { status: RunTimelineTool['status'] }) {
  const styles = useStyles();
  return (
    <span
      className={mergeClasses(
        styles.toolState,
        status === 'complete' && styles.toolStateComplete,
        status === 'error' && styles.toolStateError,
      )}
    >
      {status === 'running' && <Spinner size="extra-tiny" aria-label="Running" />}
      {status === 'complete' && <CheckmarkRegular aria-label="Done" />}
      {status === 'error' && <DismissCircleFilled aria-label="Failed" />}
      {/* Defensive fallback (#item-7): any status other than the three known terminal/
          in-progress values should still read as "in progress", not a static dead icon. */}
      {status !== 'running' && status !== 'complete' && status !== 'error' && (
        <Spinner size="extra-tiny" aria-label="Running" />
      )}
    </span>
  );
}

/** Character cap for the inline "Arguments"/"Result"/"Error" text blocks before a
 *  "Show more" toggle takes over — keeps huge payloads from making the row unusably tall
 *  by default while still letting the full (already-capped-server-side) text be read. */
const INLINE_TRUNCATE_CHARS = 800;

/** Collapsible text block used for the Arguments/Result/Error sections of an expanded
 *  tool row (#item-3). Long content stays collapsed to a short preview until "Show more"
 *  is clicked, so huge tool payloads don't dominate the transcript. */
function TruncatedTextBlock({ text, testId }: { text: string; testId?: string }) {
  const styles = useStyles();
  const [showAll, setShowAll] = useState(false);
  const isLong = text.length > INLINE_TRUNCATE_CHARS;
  const shown = !isLong || showAll ? text : `${text.slice(0, INLINE_TRUNCATE_CHARS)}\u2026`;
  return (
    <>
      <pre className={styles.outputScroll} data-testid={testId}>{shown}</pre>
      {isLong && (
        <Button
          appearance="transparent"
          size="small"
          onClick={() => setShowAll((v) => !v)}
          data-testid={testId ? `${testId}-toggle` : undefined}
        >
          {showAll ? 'Show less' : 'Show more'}
        </Button>
      )}
    </>
  );
}

/** Arguments section shared by both branches of ToolDiffCard below — the raw call
 *  arguments should be visible whether the row expands into a diff, a plain output card,
 *  or (deriveEditDiff/tool.resultContent both empty) neither (#item-3). */
function ToolArgumentsSection({ tool }: { tool: RunTimelineTool }) {
  const styles = useStyles();
  if (!tool.argumentsJson) return null;
  return (
    <div>
      <div className={styles.diffHeader}><span>Arguments</span></div>
      <TruncatedTextBlock text={tool.argumentsJson} testId="timeline-tool-arguments" />
    </div>
  );
}

/** Read-only inline card for an expanded tool row: unified diff for edits, or raw output text
 *  for any other tool call that returned content (#299). Warm-monochrome, no side stripes. */
function ToolDiffCard({ tool }: { tool: RunTimelineTool }) {
  const styles = useStyles();
  if (!tool.diff) {
    if (!tool.resultContent && !tool.argumentsJson && !tool.errorMessage) return null;
    return (
      <div className={styles.diffCard} data-testid="timeline-tool-output">
        <div className={styles.diffHeader}>
          <span>{tool.title}</span>
        </div>
        <ToolArgumentsSection tool={tool} />
        {tool.resultContent && (
          <div>
            <div className={styles.diffHeader}><span>Result</span></div>
            <TruncatedTextBlock text={tool.resultContent} testId="timeline-tool-result" />
          </div>
        )}
        {tool.errorMessage && (
          <div>
            <div className={styles.diffHeader}><span>Error</span></div>
            <TruncatedTextBlock text={tool.errorMessage} testId="timeline-tool-error-detail" />
          </div>
        )}
      </div>
    );
  }
  const lines = tool.diff.split('\n');
  let added = 0;
  let removed = 0;
  for (const l of lines) {
    if (l.startsWith('+') && !l.startsWith('+++')) added += 1;
    else if (l.startsWith('-') && !l.startsWith('---')) removed += 1;
  }
  return (
    <div className={styles.diffCard} data-testid="timeline-tool-diff">
      <div className={styles.diffHeader}>
        <span>{tool.title}</span>
        <span className={styles.diffHeaderCounts}>
          {added > 0 && <span className={styles.diffAdded}>{`+${added}`}</span>}
          {removed > 0 && <span className={styles.diffRemoved}>{`-${removed}`}</span>}
        </span>
      </div>
      <ToolArgumentsSection tool={tool} />
      <div className={styles.diffScroll}>
        {lines.map((raw, i) => {
          const isHeader = raw.startsWith('+++') || raw.startsWith('---') || raw.startsWith('diff ') || raw.startsWith('index ');
          if (isHeader) return null;
          const isAdded = raw.startsWith('+');
          const isRemoved = raw.startsWith('-');
          const isHunk = raw.startsWith('@@');
          const sign = isAdded ? '+' : isRemoved ? '-' : '';
          const content = isAdded || isRemoved ? raw.slice(1) : raw;
          return (
            <div
              key={i}
              className={mergeClasses(
                styles.diffLine,
                isAdded && styles.diffLineAdded,
                isRemoved && styles.diffLineRemoved,
                isHunk && styles.diffLineHunk,
              )}
            >
              <span className={styles.diffLineSign} aria-hidden>{sign}</span>
              <span>{content || ' '}</span>
            </div>
          );
        })}
        {tool.truncated && (
          <div className={styles.diffTruncated} data-testid="timeline-diff-truncated">
            {tool.diffHiddenLines
              ? `\u2026 ${tool.diffHiddenLines} more ${tool.diffHiddenLines === 1 ? 'line' : 'lines'} (truncated)`
              : '\u2026 truncated'}
          </div>
        )}
      </div>
    </div>
  );
}

function ToolRow({ tool }: { tool: RunTimelineTool }) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);
  const canExpand = Boolean(
    tool.expandable && (tool.diff || tool.resultContent || tool.argumentsJson || tool.errorMessage),
  );

  const rowInner = (
    <>
      <span className={styles.toolIcon} aria-hidden>{toolIconFor(tool.category)}</span>
      <span
        className={mergeClasses(styles.toolTitle, tool.status === 'error' && styles.toolTitleError)}
        title={tool.title}
      >
        {tool.title}
      </span>
      {tool.titleSecondary ? (
        <span className={styles.toolSecondary} title={tool.titleSecondary}>
          {tool.titleSecondary}
        </span>
      ) : (
        <span />
      )}
      <span className={styles.toolMeta}>
        {canExpand && (
          <span className={styles.toolExpandChevron} aria-hidden>
            {expanded ? <ChevronDownRegular /> : <ChevronRightRegular />}
          </span>
        )}
        {tool.resultMeta && <span>{tool.resultMeta}</span>}
        <ToolResultState status={tool.status} />
      </span>
    </>
  );

  return (
    <>
      {canExpand ? (
        <button
          type="button"
          className={mergeClasses(styles.toolRow, styles.toolRowButton)}
          data-testid="timeline-tool-row"
          data-tool-status={tool.status}
          data-tool-category={tool.category}
          aria-expanded={expanded}
          onClick={() => setExpanded((v) => !v)}
        >
          {rowInner}
        </button>
      ) : (
        <div
          className={styles.toolRow}
          data-testid="timeline-tool-row"
          data-tool-status={tool.status}
          data-tool-category={tool.category}
        >
          {rowInner}
        </div>
      )}
      {tool.status === 'error' && tool.errorMessage && (
        <div className={styles.toolError}>
          {tool.isSandboxViolation ? 'Blocked by sandbox policy: ' : ''}
          {tool.errorMessage}
        </div>
      )}
      {canExpand && expanded && <ToolDiffCard tool={tool} />}
    </>
  );
}

function ToolGroup({ tools }: { tools: RunTimelineTool[] }) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  return (
    <div className={styles.toolGroup}>
      <Button
        appearance="transparent"
        size="small"
        className={styles.toolGroupHeader}
        icon={open ? <ChevronDownRegular /> : <ChevronRightRegular />}
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        data-testid="timeline-tool-group"
      >
        {`Used ${tools.length} ${tools.length === 1 ? 'tool' : 'tools'}`}
      </Button>
      {open && (
        <div className={styles.toolList}>
          {tools.map((tool) => (
            <ToolRow key={tool.callId} tool={tool} />
          ))}
        </div>
      )}
    </div>
  );
}

function MessageBlock({ message }: { message: RunTimelineMessage }) {
  const styles = useStyles();
  const timestamp = (
    <span
      className={styles.messageTimestamp}
      title={formatAbsoluteTime(message.timestamp)}
    >
      {formatRelativeTime(message.timestamp)}
    </span>
  );
  if (message.role === 'user') {
    return (
      <div data-testid="timeline-message" data-role="user">
        {timestamp}
        <span className={styles.messageUserLabel}>You</span>
        <div className={styles.messageUser}>{message.text}</div>
      </div>
    );
  }
  // role === 'assistant' or absent — render as agent output.
  if (message.streaming || message.text.length === 0) {
    return (
      <div data-testid="timeline-message">
        {timestamp}
        <div className={styles.messageStreaming}>{message.text}</div>
      </div>
    );
  }
  return (
    <div data-testid="timeline-message">
      {timestamp}
      <div className={styles.message}>
        <SafeMarkdown>{message.text}</SafeMarkdown>
      </div>
    </div>
  );
}

/**
 * Render a step's ordered children: assistant messages interleave BETWEEN tool groups in
 * the sequence they occurred (message → tools → message → tools). Consecutive tools cluster
 * under one "Used N tools" group; each message is a first-class markdown block.
 */
function StepBody({ step }: { step: RunTimelineStep }) {
  const styles = useStyles();
  if (step.children.length === 0) {
    return <Label className={styles.empty}>No activity recorded yet for this step.</Label>;
  }
  const blocks: ReactNode[] = [];
  let toolRun: RunTimelineTool[] = [];
  const flushTools = (key: string) => {
    if (toolRun.length > 0) {
      blocks.push(<ToolGroup key={`tools-${key}`} tools={toolRun} />);
      toolRun = [];
    }
  };
  step.children.forEach((child, index) => {
    if (child.kind === 'tool') {
      toolRun.push(child.tool);
    } else {
      flushTools(`before-${index}`);
      blocks.push(<MessageBlock key={`msg-${child.message.messageId}-${index}`} message={child.message} />);
    }
  });
  flushTools('end');
  return <div className={styles.content}>{blocks}</div>;
}

export interface RunTimelineProps {
  steps: RunTimelineStep[];
  running: boolean;
  /** Optional label override for the empty state. */
  emptyHint?: string;
  /**
   * When embedded inside another surface (e.g. the session Messages panel that already
   * has its own scope header), the ChainOfThought toggle shows only the step count —
   * the "Messages" word lives once in the tab, not repeated here.
   */
  embedded?: boolean;
  /**
   * Skip the ChainOfThought/step accordion entirely and render every step's children
   * (messages + inline "Used N tools" tool-group disclosures) as a flat, concatenated
   * list — no outer "N step(s)" header, no per-step status icon, no accordion toggle.
   *
   * Steps make sense for genuinely multi-phase/multi-turn coordinator-style runs where
   * "what phase is this activity part of" is meaningful information. For a turn-by-turn
   * chat surface (the Assistant page) every user/assistant exchange was still being
   * wrapped in a single meaningless step (e.g. "1 step ⌃" containing one "Step 1 ·
   * Working" row) that doesn't map to anything the user can reason about — so the
   * Assistant page renders `flat` instead (see AssistantRunPage.tsx).
   */
  flat?: boolean;
}

export function RunTimeline({
  steps, running, emptyHint, embedded = false, flat = false,
}: RunTimelineProps) {
  const styles = useStyles();
  const stepLabel = `${steps.length} ${steps.length === 1 ? 'step' : 'steps'}`;
  const [, setRelativeTimeTick] = useState(0);

  useEffect(() => {
    const intervalId = window.setInterval(() => setRelativeTimeTick((tick) => tick + 1), 5_000);
    return () => window.clearInterval(intervalId);
  }, []);

  // `defaultOpenItems` on the underlying Accordion only applies at first mount. Steps stream in
  // asynchronously (SSE / history load), so by the time later steps arrive the Accordion has
  // already locked in its initial (often empty) open set and new steps render collapsed. Track
  // open items ourselves and auto-open any step id we haven't seen before, while still letting a
  // user manually collapse a step (we only ever ADD ids here, never remove one the user closed).
  const [openItems, setOpenItems] = useState<string[]>(() => steps.map((s) => s.id));
  const knownStepIds = useRef(new Set(steps.map((s) => s.id)));
  const newIds = steps.map((s) => s.id).filter((id) => !knownStepIds.current.has(id));
  if (newIds.length > 0) {
    newIds.forEach((id) => knownStepIds.current.add(id));
    setOpenItems((prev) => [...prev, ...newIds]);
  }

  return (
    <AgentweaverCopilotProvider>
      <div className={styles.root} data-testid="run-timeline">
        {steps.length === 0 ? (
          <EmptyState
            title="No steps yet"
            description={emptyHint ?? 'Reported intents, tool calls, and messages will appear here as the run progresses.'}
          />
        ) : flat ? (
          // No ChainOfThought/accordion wrapper — just each step's messages and inline
          // "Used N tools" disclosures, concatenated in order. See the `flat` prop doc above.
          <div className={styles.content} data-testid="run-timeline-flat">
            {steps.map((step) => <StepBody key={step.id} step={step} />)}
          </div>
        ) : (
          <ChainOfThought
            // The library's own `cardHeader` slot defaults to a hard-coded "Activity" label
            // rendered ABOVE the accordion, entirely separate from our `headerText` toggle
            // below it — the two together read as two competing, unexplained headers
            // ("1 step" then "Activity" on the next line). We fold that word into our own
            // single headerText line instead and suppress the library's duplicate.
            cardHeader={null}
            headerText={
              embedded ? (
                <span className={styles.cotHeaderSub}>{stepLabel}</span>
              ) : (
                <span className={styles.cotHeaderText}>
                  {TIMELINE_LABEL}
                  <span className={styles.cotHeaderSub}>{`\u00b7 ${stepLabel}`}</span>
                </span>
              )
            }
            defaultExpanded
            progressState={running ? 'loading' : 'finished'}
            progressMessage={running ? 'Run in progress' : 'Run finished'}
            activitiesAccordion={{
              multiple: true,
              collapsible: true,
              openItems,
              onToggle: (_event, data) => setOpenItems(data.openItems as string[]),
            }}
          >
            {steps.map((step, index) => (
              <ChainOfThoughtItem
                key={step.id}
                value={step.id}
                active={step.status === 'running'}
                statusIndicator={{
                  children: (
                    <span className={styles.statusIndicator}>
                      <StepStatusIcon status={step.status} />
                    </span>
                  ),
                }}
                // Prefix with the step's ordinal so a lone status word like "Working" or
                // "Complete" reads as "Step 1 · Working" instead of floating with no context
                // between the section header above and the transcript below.
                headerText={{
                  className: styles.stepHeaderText,
                  children: (
                    <Body>
                      <span className={styles.cotHeaderSub}>{`Step ${index + 1} \u00b7 `}</span>
                      {step.intent}
                    </Body>
                  ),
                }}
              >
                <StepBody step={step} />
              </ChainOfThoughtItem>
            ))}
          </ChainOfThought>
        )}
      </div>
    </AgentweaverCopilotProvider>
  );
}
