import { memo, useState } from 'react';
import { Badge, Button, Text, Tooltip, makeStyles, tokens } from '@fluentui/react-components';
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
  CopyRegular,
  CheckmarkRegular,
  ShieldLockRegular,
} from '@fluentui/react-icons';
import type { RunStreamEvent } from '../api/sse';
import { API_KEY, API_URL } from '../config';

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
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
    wordBreak: 'break-word',
    flexGrow: 1,
  },
  badge: { flexShrink: 0 },

  // truncated comment text (max 3 lines)
  changesRequestedComment: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    marginLeft: '24px',
    marginTop: tokens.spacingVerticalXS,
    paddingLeft: tokens.spacingHorizontalS,
    borderLeft: `2px solid ${tokens.colorNeutralStroke1}`,
    display: '-webkit-box',
    WebkitBoxOrient: 'vertical',
    WebkitLineClamp: '3',
    overflow: 'hidden',
    wordBreak: 'break-word',
  },
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

  approvalActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    marginTop: tokens.spacingVerticalXS,
  },
  approvalResolved: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },

  // sandbox.warning banner
  sandboxWarning: {
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
  },

  // --- ToolApprovalCard redesign styles ---
  approvalCardRedesign: {
    display: 'flex',
    flexDirection: 'column',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorStatusInformativeBorder1}`,
    backgroundColor: tokens.colorNeutralBackground1,
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
    overflow: 'hidden',
    boxShadow: tokens.shadow4,
  },
  approvalHeaderRedesign: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorStatusInformativeBackground1,
    borderBottom: `1px solid ${tokens.colorStatusInformativeBorder1}`,
  },
  approvalBodyRedesign: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
  },
  approvalToolBadgeRedesign: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase100,
    backgroundColor: tokens.colorNeutralBackground3,
    padding: `2px ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusSmall,
    color: tokens.colorNeutralForeground1,
  },
  approvalUrlRowRedesign: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: tokens.spacingHorizontalS,
  },
  approvalUrlBlockRedesign: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusSmall,
    padding: tokens.spacingHorizontalS,
    flex: '1',
    margin: '0',
    whiteSpace: 'nowrap',
    overflowX: 'auto',
    color: tokens.colorNeutralForeground1,
  },
  approvalCopyBtnRedesign: {
    flexShrink: '0',
  },
  approvalIntentionRedesign: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  approvalActionsRedesign: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    marginTop: tokens.spacingVerticalXS,
    alignItems: 'center',
  },
  approvalResolvedRedesign: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },
  approvalRequestIdRedesign: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    marginTop: tokens.spacingVerticalXS,
  },
});

type BadgeColor = 'success' | 'warning' | 'danger' | 'subtle' | 'informative';

function lifecycleProps(event: RunStreamEvent, runOutcome?: { achieved: boolean; reason: string }): {
  icon: React.ReactNode;
  label: string;
  summary: string;
  badgeColor: BadgeColor;
} {
  // SECURITY (Y-3): all string values extracted and rendered as React text — no HTML
  const p = event.payload;
  switch (event.type) {
    case 'run.completed':
      if (runOutcome?.achieved === false) {
        return {
          icon: <WarningFilled aria-hidden="true" />,
          label: 'run.completed',
          summary: `Not achieved · ${runOutcome.reason}`,
          badgeColor: 'warning',
        };
      }
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
    case 'run.error':
      return {
        icon: <WarningFilled aria-hidden="true" />,
        label: 'run.error',
        summary: String(p['reason'] ?? p['message'] ?? 'Retryable error — run is awaiting review'),
        badgeColor: 'warning',
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
    case 'review.changes_requested':
      return {
        icon: <WarningFilled aria-hidden="true" />,
        label: 'review.changes_requested',
        summary: String(p['comment'] ?? 'Changes requested'),
        badgeColor: 'warning',
      };
    case 'revision.started':
      return {
        icon: <WarningFilled aria-hidden="true" />,
        label: 'revision.started',
        summary: String(p['message'] ?? 'Revision started'),
        badgeColor: 'informative',
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

interface ToolApprovalCardProps {
  styles: ReturnType<typeof useStyles>;
  requestId: string;
  displayId: string;
  toolName: string;
  url: string | null;
  intention: string | null;
  runId?: string;
  isResolved?: boolean;
  resolvedScope?: string | null;
}

function scopeLabel(scope: string): string {
  switch (scope) {
    case 'once': return 'Allowed (once)';
    case 'run': return 'Allowed (this run)';
    case 'always': return 'Allowed (always, this session)';
    case 'tool': return 'Allowed (all calls to tool)';
    default: return `Allowed (${scope})`;
  }
}
function ToolApprovalCard({ styles, requestId, displayId, toolName, url, intention, runId, isResolved, resolvedScope: resolvedScopeProp }: ToolApprovalCardProps) {
  const [resolvedScope, setResolvedScope] = useState<string | null>(
    isResolved ? (resolvedScopeProp ?? 'once') : null,
  );
  const [busy, setBusy] = useState(false);
  const [copied, setCopied] = useState(false);

  const handleAllow = async (scope: 'once' | 'run' | 'always' | 'tool') => {
    if (!runId || resolvedScope !== null || busy) return;
    setBusy(true);
    const baseUrl = API_URL.replace(/\/+$/, '');
    try {
      const response = await fetch(`${baseUrl}/api/runs/${encodeURIComponent(runId)}/tool-approvals`, {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${API_KEY}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ request_id: requestId, scope }),
      });
      if (!response.ok) {
        console.error('Tool approval request failed:', response.status);
        return;
      }
      setResolvedScope(scope);
    } catch {
      // ignore network errors — user can retry
    } finally {
      setBusy(false);
    }
  };

  const handleDeny = async () => {
    if (!runId || resolvedScope !== null || busy) return;
    setBusy(true);
    const baseUrl = API_URL.replace(/\/+$/, '');
    try {
      const response = await fetch(`${baseUrl}/api/runs/${encodeURIComponent(runId)}/tool-denials`, {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${API_KEY}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ request_id: requestId }),
      });
      if (!response.ok) {
        console.error('Tool denial request failed:', response.status);
        return;
      }
      setResolvedScope('deny');
    } catch {
      // ignore network errors — user can retry
    } finally {
      setBusy(false);
    }
  };

  const handleCopy = () => {
    if (!url) return;
    void navigator.clipboard.writeText(url);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  // Collapsed inline view — shown after action or when pre-resolved from reducer
  if (resolvedScope !== null) {
    const label = resolvedScope === 'deny'
      ? `\u2717 Denied \u00b7 ${toolName}`
      : `\u2713 ${scopeLabel(resolvedScope)} \u00b7 ${toolName}`;
    return (
      <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}` }}>
        <Text size={200} style={{ color: resolvedScope === 'deny' ? tokens.colorStatusDangerForeground1 : tokens.colorStatusSuccessForeground1 }}>
          {label}
        </Text>
      </div>
    );
  }

  return (
    // SECURITY (Y-3): all values rendered as text — no HTML
    <div className={styles.approvalCardRedesign} role="alert">
      <div className={styles.approvalHeaderRedesign}>
        <ShieldLockRegular
          style={{ fontSize: '18px', color: tokens.colorStatusInformativeForeground1 }}
          aria-hidden="true"
        />
        <Text weight="semibold" size={300} style={{ color: tokens.colorStatusInformativeForeground1 }}>
          Tool Approval Required
        </Text>
      </div>

      <div className={styles.approvalBodyRedesign}>
        <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS }}>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>Tool</Text>
          <Badge appearance="filled" color="informative" shape="rounded">
            {toolName}
          </Badge>
        </div>

        {url && (
          <div className={styles.approvalUrlRowRedesign}>
            <pre className={styles.approvalUrlBlockRedesign}>{url}</pre>
            <Tooltip content={copied ? 'Copied!' : 'Copy URL'} relationship="label">
              <Button
                appearance="subtle"
                size="small"
                icon={copied ? <CheckmarkRegular /> : <CopyRegular />}
                onClick={handleCopy}
                aria-label="Copy URL"
              />
            </Tooltip>
          </div>
        )}

        {intention && (
          <Text className={styles.approvalIntentionRedesign}>{intention}</Text>
        )}

        <div className={styles.approvalActionsRedesign}>
          <Button
            appearance="primary"
            size="small"
            disabled={busy || !runId}
            onClick={() => void handleAllow('once')}
          >
            Allow once
          </Button>
          <Button
            appearance="outline"
            size="small"
            disabled={busy || !runId}
            onClick={() => void handleAllow('run')}
          >
            Allow this run
          </Button>
          <Tooltip
            content="Allows all calls to this tool (any URL) for the rest of this run."
            relationship="description"
          >
            <Button
              appearance="outline"
              size="small"
              disabled={busy || !runId}
              onClick={() => void handleAllow('tool')}
            >
              Allow tool
            </Button>
          </Tooltip>
          <Tooltip
            content="Allows this tool+URL for all future requests this session. Resets when the server restarts."
            relationship="description"
          >
            <Button
              appearance="outline"
              size="small"
              disabled={busy || !runId}
              onClick={() => void handleAllow('always')}
            >
              Always allow (session)
            </Button>
          </Tooltip>
          <Button
            appearance="outline"
            size="small"
            disabled={busy || !runId}
            onClick={() => void handleDeny()}
            style={{
              marginLeft: 'auto',
              borderColor: tokens.colorStatusDangerBorder1,
              color: tokens.colorStatusDangerForeground1,
            }}
          >
            Deny
          </Button>
        </div>

        <Text className={styles.approvalRequestIdRedesign}>ID: {displayId}</Text>
      </div>
    </div>
  );
}

interface ShellApprovalCardProps {
  styles: ReturnType<typeof useStyles>;
  requestId: string;
  commandHash: string;
  command: string | null;
  runId?: string;
  isResolved?: boolean;
  resolvedOutcome?: 'approved' | 'denied' | null;
}

function ShellApprovalCard({ styles, requestId, commandHash, command, runId, isResolved, resolvedOutcome: resolvedOutcomeProp }: ShellApprovalCardProps) {
  const [resolvedOutcome, setResolvedOutcome] = useState<'approved' | 'denied' | null>(
    isResolved ? (resolvedOutcomeProp ?? 'approved') : null,
  );
  const [busy, setBusy] = useState(false);

  const handleApprove = async () => {
    if (!runId || resolvedOutcome !== null || busy) return;
    setBusy(true);
    const baseUrl = API_URL.replace(/\/+$/, '');
    try {
      const response = await fetch(`${baseUrl}/api/runs/${encodeURIComponent(runId)}/shell-approvals`, {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${API_KEY}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ command_hash: commandHash }),
      });
      if (!response.ok) {
        console.error('Shell approval request failed:', response.status);
        return;
      }
      setResolvedOutcome('approved');
    } catch {
      // ignore network errors
    } finally {
      setBusy(false);
    }
  };

  const handleDeny = async () => {
    if (!runId || resolvedOutcome !== null || busy) return;
    setBusy(true);
    const baseUrl = API_URL.replace(/\/+$/, '');
    try {
      const response = await fetch(`${baseUrl}/api/runs/${encodeURIComponent(runId)}/shell-denials`, {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${API_KEY}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ command_hash: commandHash }),
      });
      if (!response.ok) {
        console.error('Shell denial request failed:', response.status);
        return;
      }
      setResolvedOutcome('denied');
    } catch {
      // ignore network errors
    } finally {
      setBusy(false);
    }
  };

  if (resolvedOutcome !== null) {
    const label = resolvedOutcome === 'denied'
      ? `\u2717 Denied \u00b7 run_command`
      : `\u2713 Approved \u00b7 run_command`;
    return (
      <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}` }}>
        <Text size={200} style={{ color: resolvedOutcome === 'denied' ? tokens.colorStatusDangerForeground1 : tokens.colorStatusSuccessForeground1 }}>
          {label}
        </Text>
      </div>
    );
  }

  return (
    // SECURITY (Y-3): command and requestId rendered as text — no HTML
    <div className={styles.approvalCard} role="alert">
      <div className={styles.approvalHeading}>
        <WarningFilled className={styles.warningIcon} aria-hidden="true" />
        <Text weight="semibold">Dangerous command — approval required</Text>
      </div>
      {command && (
        <Text as="pre" className={styles.approvalCommand} style={{ margin: 0 }}>
          {command}
        </Text>
      )}
      <Text className={styles.approvalMeta}>Request ID: {requestId}</Text>
      <div className={styles.approvalActions}>
        <Button
          appearance="primary"
          size="small"
          disabled={busy || !runId}
          onClick={() => void handleApprove()}
        >
          Approve
        </Button>
        <Button
          appearance="outline"
          size="small"
          disabled={busy || !runId}
          onClick={() => void handleDeny()}
          style={{ borderColor: tokens.colorStatusDangerBorder1, color: tokens.colorStatusDangerForeground1 }}
        >
          Deny
        </Button>
      </div>
    </div>
  );
}

interface LifecycleEventCardProps {
  event: RunStreamEvent;
  runId?: string;
  isResolved?: boolean;
  resolvedScope?: string | null;
  runOutcome?: { achieved: boolean; reason: string };
}

export const LifecycleEventCard = memo(function LifecycleEventCard({ event, runId, isResolved, resolvedScope, runOutcome }: LifecycleEventCardProps) {
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
          <Text as="pre" style={{ margin: `${tokens.spacingVerticalS} 0 0 24px`, fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, whiteSpace: 'pre-wrap', wordBreak: 'break-word', color: tokens.colorNeutralForeground2 }}>
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
    const commandHash = String(event.payload['commandHash'] ?? event.payload['command_hash'] ?? '');
    const command = event.payload['command'] ? String(event.payload['command']) : null;
    return (
      <ShellApprovalCard
        styles={styles}
        requestId={requestId}
        commandHash={commandHash}
        command={command}
        runId={runId}
      />
    );
  }

  // --- tool approval required ---
  if (event.type === 'tool.approval_required') {
    const requestId = String(event.payload['requestId'] ?? event.payload['request_id'] ?? '');
    const displayId = event.payload['displayId'] ? String(event.payload['displayId']) : requestId.slice(0, 8);
    const toolName = String(event.payload['toolName'] ?? event.payload['tool_name'] ?? 'unknown');
    const rawUrl = event.payload['url'] ? String(event.payload['url']) : null;
    const url = rawUrl && rawUrl.length > 80 ? rawUrl.slice(0, 80) + '...' : rawUrl;
    const intention = event.payload['intention'] ? String(event.payload['intention']) : null;

    return (
      <ToolApprovalCard
        styles={styles}
        requestId={requestId}
        displayId={displayId}
        toolName={toolName}
        url={url}
        intention={intention}
        runId={runId}
        isResolved={isResolved}
        resolvedScope={resolvedScope}
      />
    );
  }

  // --- agent.intent: inline intent step (text only, no HTML — Y-3) ---
  if (event.type === 'agent.intent') {
    const intent = event.payload['intent'] ? String(event.payload['intent']) : '';
    if (!intent) return null;
    return (
      <div className={styles.card}>
        <CodeRegular className={styles.subtleIcon} aria-hidden="true" />
        <Badge className={styles.badge} color="subtle" shape="rounded" size="small">intent</Badge>
        {/* SECURITY (Y-3): intent rendered as escaped text — no HTML interpretation */}
        <Text className={styles.summary}>{intent}</Text>
      </div>
    );
  }

  // --- agent.tools: separate flat card listing registered tools ---
  if (event.type === 'agent.tools') {
    const tools = event.payload['tools'] as string[] | undefined;
    if (!tools || tools.length === 0) return null;
    return (
      <div className={styles.card} style={{ flexWrap: 'wrap', alignItems: 'flex-start' }}>
        <CodeRegular className={styles.subtleIcon} aria-hidden="true" style={{ marginTop: '2px' }} />
        <Badge className={styles.badge} color="subtle" shape="rounded" size="small">tools</Badge>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalXS }}>
          {tools.map(tool => (
            <Badge key={tool} appearance="outline" size="small" shape="rounded" style={{ fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase100 }}>{tool}</Badge>
          ))}
        </div>
      </div>
    );
  }

  // --- review.changes_requested: show label + optional comment blockquote ---
  if (event.type === 'review.changes_requested') {
    const comment = event.payload['comment'] ? String(event.payload['comment']) : null;
    return (
      <div className={styles.card} style={{ flexDirection: 'column', alignItems: 'flex-start' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, width: '100%' }}>
          <span className={styles.warningIcon}><WarningFilled aria-hidden="true" /></span>
          <Badge className={styles.badge} color="warning" shape="rounded" size="small">review.changes_requested</Badge>
          <Text className={styles.summary}>Changes requested</Text>
        </div>
        {comment && (
          <Text as="p" className={styles.changesRequestedComment}>{comment}</Text>
        )}
      </div>
    );
  }

  // --- default card layout ---
  const { icon, label, summary, badgeColor } = lifecycleProps(event, runOutcome);

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

