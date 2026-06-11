import { memo, useState } from 'react';
import { Badge, Text, makeStyles, tokens } from '@fluentui/react-components';
import {
  CheckmarkCircleFilled,
  ErrorCircleFilled,
  WarningFilled,
  BranchRegular,
  DismissCircleFilled,
  ShieldRegular,
  CodeRegular,
  ChevronDownRegular,
  ChevronRightRegular,
} from '@fluentui/react-icons';
import type { RunStreamEvent } from '../api/sse';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
  },
  successIcon: { color: tokens.colorPaletteGreenForeground1, flexShrink: 0 },
  errorIcon: { color: tokens.colorPaletteRedForeground1, flexShrink: 0 },
  warningIcon: { color: tokens.colorPaletteYellowForeground1, flexShrink: 0 },
  subtleIcon: { color: tokens.colorNeutralForeground3, flexShrink: 0 },
  summary: {
    fontSize: tokens.fontSizeBase200,
    wordBreak: 'break-word',
    flexGrow: 1,
  },
  badge: { flexShrink: 0 },

  // terminal-style output line (tool.output)
  terminalLine: {
    display: 'flex',
    alignItems: 'baseline',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    marginTop: '1px',
    marginBottom: '1px',
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusSmall,
  },
  terminalStderrLine: {
    backgroundColor: tokens.colorPaletteRedBackground1,
  },
  streamPrefix: {
    color: tokens.colorNeutralForeground3,
    flexShrink: 0,
    userSelect: 'none',
  },
  stderrPrefix: {
    color: tokens.colorPaletteRedForeground1,
  },
  terminalContent: {
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-all',
    color: tokens.colorNeutralForeground1,
    flexGrow: 1,
  },
  stderrContent: {
    color: tokens.colorPaletteRedForeground1,
  },

  // shell.approval_required
  approvalCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorPaletteYellowForeground2}`,
    backgroundColor: tokens.colorPaletteYellowBackground1,
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
  },
  approvalHeading: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
  },
  approvalMeta: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  approvalCommand: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    backgroundColor: tokens.colorNeutralBackground3,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusSmall,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-all',
  },

  // sandbox.warning banner
  sandboxWarning: {
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
  },
});

type BadgeColor = 'success' | 'warning' | 'danger' | 'subtle' | 'informative';

function lifecycleProps(event: RunStreamEvent): {
  icon: React.ReactNode;
  label: string;
  summary: string;
  badgeColor: BadgeColor;
} {
  // SECURITY (Y-3): all string values extracted and rendered as React text — no HTML
  const p = event.payload;
  switch (event.type) {
    case 'run.completed':
      return {
        icon: <CheckmarkCircleFilled aria-hidden="true" />,
        label: 'run.completed',
        summary: String(p['summary'] ?? 'Run completed'),
        badgeColor: 'success',
      };
    case 'run.failed':
      return {
        icon: <ErrorCircleFilled aria-hidden="true" />,
        label: 'run.failed',
        summary: String(p['message'] ?? p['summary'] ?? 'Run failed'),
        badgeColor: 'danger',
      };
    case 'review.requested':
      return {
        icon: <WarningFilled aria-hidden="true" />,
        label: 'review.requested',
        summary: `Tree ${String(p['tree_hash'] ?? '')}`,
        badgeColor: 'warning',
      };
    case 'review.approved':
      return {
        icon: <CheckmarkCircleFilled aria-hidden="true" />,
        label: 'review.approved',
        summary: `Approved by ${String(p['approved_by'] ?? '')}`,
        badgeColor: 'success',
      };
    case 'review.declined':
      return {
        icon: <DismissCircleFilled aria-hidden="true" />,
        label: 'review.declined',
        summary: `Declined by ${String(p['declined_by'] ?? '')}`,
        badgeColor: 'subtle',
      };
    case 'merge.completed':
      return {
        icon: <BranchRegular aria-hidden="true" />,
        label: 'merge.completed',
        summary: `Merged at ${String(p['merged_commit_hash'] ?? '')}`,
        badgeColor: 'success',
      };
    case 'merge.failed':
      return {
        icon: <ErrorCircleFilled aria-hidden="true" />,
        label: 'merge.failed',
        summary: String(p['reason'] ?? 'Merge failed'),
        badgeColor: 'danger',
      };
    case 'sandbox.selected':
      return {
        icon: <ShieldRegular aria-hidden="true" />,
        label: 'sandbox.selected',
        summary: `${String(p['backend'] ?? '')}${p['isRealIsolation'] === false ? ' \u2014 no isolation' : ''}`,
        badgeColor: p['isRealIsolation'] === false ? 'warning' : 'informative',
      };
    case 'agent.system_prompt': {
      const provider = String(p['provider'] ?? 'unknown');
      const prompt = p['prompt'] ? String(p['prompt']) : null;
      const note = p['note'] ? String(p['note']) : null;
      const chars = prompt ? ` (${prompt.length} chars)` : '';
      return {
        icon: <CodeRegular aria-hidden="true" />,
        label: `system_prompt:${provider}`,
        summary: prompt ? `${prompt.slice(0, 120).replace(/\n/g, ' ')}…${chars}` : (note ?? ''),
        badgeColor: 'subtle',
      };
    }
    default:
      return {
        icon: null,
        label: event.type,
        summary: JSON.stringify(p),
        badgeColor: 'subtle',
      };
  }
}

interface LifecycleEventCardProps {
  event: RunStreamEvent;
}

export const LifecycleEventCard = memo(function LifecycleEventCard({ event }: LifecycleEventCardProps) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);

  // sandbox.warning is suppressed — shown inline only if genuinely blocking.
  if (event.type === 'sandbox.warning') {
    return null;
  }

  // --- agent.system_prompt: expandable full-text card ---
  if (event.type === 'agent.system_prompt') {
    const p = event.payload;
    const provider = String(p['provider'] ?? 'unknown');
    const prompt = p['prompt'] ? String(p['prompt']) : null;
    const note = p['note'] ? String(p['note']) : null;
    const preview = prompt
      ? prompt.slice(0, 120).replace(/\n/g, ' ') + `… (${prompt.length} chars)`
      : (note ?? '');
    return (
      <div className={styles.card} style={{ flexDirection: 'column', alignItems: 'flex-start', cursor: prompt ? 'pointer' : undefined }}
        onClick={prompt ? () => setExpanded(e => !e) : undefined}
        role={prompt ? 'button' : undefined}
        aria-expanded={prompt ? expanded : undefined}>
        <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, width: '100%' }}>
          {prompt && (expanded
            ? <ChevronDownRegular style={{ flexShrink: 0, fontSize: '10px', color: tokens.colorNeutralForeground3 }} aria-hidden="true" />
            : <ChevronRightRegular style={{ flexShrink: 0, fontSize: '10px', color: tokens.colorNeutralForeground3 }} aria-hidden="true" />)}
          <CodeRegular className={styles.subtleIcon} aria-hidden="true" />
          <Badge className={styles.badge} color="subtle" shape="rounded" size="small">system_prompt:{provider}</Badge>
          <Text className={styles.summary}>{preview}</Text>
        </div>
        {expanded && prompt && (
          <Text as="pre" style={{ margin: `${tokens.spacingVerticalS} 0 0 24px`, fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase100, whiteSpace: 'pre-wrap', wordBreak: 'break-word', color: tokens.colorNeutralForeground2 }}>
            {prompt}
          </Text>
        )}
      </div>
    );
  }

  // --- terminal output line (tool.output) ---
  if (event.type === 'tool.output') {
    const stream = String(event.payload['stream'] ?? 'stdout');
    const content = String(event.payload['content'] ?? '');
    const isStderr = stream === 'stderr';
    return (
      // SECURITY (Y-3): content rendered as text — no HTML
      <div
        className={`${styles.terminalLine}${isStderr ? ` ${styles.terminalStderrLine}` : ''}`}
        role="log"
        aria-label={`${stream} output`}
      >
        <Text
          as="span"
          className={`${styles.streamPrefix}${isStderr ? ` ${styles.stderrPrefix}` : ''}`}
        >
          [{stream}]
        </Text>
        <Text
          as="pre"
          className={`${styles.terminalContent}${isStderr ? ` ${styles.stderrContent}` : ''}`}
          style={{ margin: 0, fontFamily: 'inherit', fontSize: 'inherit' }}
        >
          {content}
        </Text>
      </div>
    );
  }

  // --- exit code (tool.exec_result) ---
  if (event.type === 'tool.exec_result') {
    const exitCode = Number(event.payload['exitCode'] ?? event.payload['exit_code'] ?? 0);
    const isFailure = exitCode !== 0;
    return (
      <div className={styles.card}>
        <CodeRegular className={styles.subtleIcon} aria-hidden="true" />
        <Badge
          className={styles.badge}
          color={isFailure ? 'danger' : 'success'}
          shape="rounded"
          size="small"
        >
          Exit code: {exitCode}
        </Badge>
      </div>
    );
  }

  // --- shell approval required ---
  if (event.type === 'shell.approval_required') {
    const requestId = String(event.payload['requestId'] ?? event.payload['request_id'] ?? '');
    const command = event.payload['command'] ? String(event.payload['command']) : null;
    return (
      // SECURITY (Y-3): requestId and command rendered as text — no HTML
      <div className={styles.approvalCard} role="alert">
        <div className={styles.approvalHeading}>
          <WarningFilled className={styles.warningIcon} aria-hidden="true" />
          <Text weight="semibold">Shell command requires approval</Text>
        </div>
        {command && (
          <Text as="pre" className={styles.approvalCommand} style={{ margin: 0 }}>
            {command}
          </Text>
        )}
        <Text className={styles.approvalMeta}>Request ID: {requestId}</Text>
      </div>
    );
  }

  // --- default card layout ---
  const { icon, label, summary, badgeColor } = lifecycleProps(event);

  const iconClass =
    badgeColor === 'success' ? styles.successIcon :
    badgeColor === 'danger' ? styles.errorIcon :
    badgeColor === 'warning' ? styles.warningIcon :
    styles.subtleIcon;

  return (
    <div className={styles.card}>
      <span className={iconClass}>{icon}</span>
      <Badge className={styles.badge} color={badgeColor} shape="rounded" size="small">
        {label}
      </Badge>
      {/* SECURITY (Y-3): summary rendered as escaped text — no HTML interpretation */}
      <Text className={styles.summary}>{summary}</Text>
    </div>
  );
});

