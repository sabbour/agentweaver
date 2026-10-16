import { makeStyles, mergeClasses, ProgressBar, Text, tokens } from '@fluentui/react-components';
import { InfoRegular } from '@fluentui/react-icons';
import { AgentIdentity } from '../AgentIdentity';
import { costChipLabel } from '../costChipFormat';
import { Body, EmptyState, TitleText } from '../ui';
import type { AgentUsageBreakdownDto, RunAgentTokenBreakdownDto } from '../../api/types';

const useStyles = makeStyles({
  panel: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: tokens.spacingVerticalL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
  panelPlain: {
    // Chrome-free variant for embedding inside another surface (e.g. the AiCredits popover) where a
    // bordered/filled panel would nest a card inside a card. Strip the border, fill and padding.
    backgroundColor: 'transparent',
    border: 'none',
    padding: 0,
  },
  header: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  note: {
    display: 'inline-flex',
    alignItems: 'flex-start',
    gap: tokens.spacingHorizontalXXS,
    color: tokens.colorNeutralForeground3,
    fontSize: '14px',
    lineHeight: '20px',
  },
  noteIcon: {
    fontSize: '14px',
    flexShrink: 0,
    marginTop: '2px',
  },
  rowHead: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
    alignItems: 'flex-start',
  },
  identity: {
    flex: 1,
    minWidth: 0,
  },
  metric: {
    whiteSpace: 'nowrap',
  },
  progress: {
    maxWidth: 'none',
  },
  usageList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  usageItem: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
});

function usageValue(entry: AgentUsageBreakdownDto): number {
  return entry.totalTokens > 0 ? entry.totalTokens : entry.totalNanoAiu;
}

export function AgentTokenBreakdown({
  data,
  title = 'Agent token breakdown',
  subtitle = 'Per-agent usage for this orchestration run.',
  roleByAgent,
  plain = false,
  showHeader = true,
}: {
  data: RunAgentTokenBreakdownDto | null;
  title?: string;
  subtitle?: string;
  roleByAgent?: Record<string, string>;
  /** Strip the bordered/filled panel chrome so the breakdown can embed inside another surface
   *  (e.g. the AiCredits popover) without nesting a card. */
  plain?: boolean;
  /** Show the title/subtitle header. Off for embedded contexts that already provide a heading. */
  showHeader?: boolean;
}) {
  const styles = useStyles();
  const rows = data?.breakdown ?? [];
  const max = Math.max(1, ...rows.map(usageValue));
  const hasFallbackTotal = !data?.hasAgentData && ((data?.totalTokens ?? 0) > 0 || (data?.totalNanoAiu ?? 0) > 0);

  return (
    <div className={mergeClasses(styles.panel, plain && styles.panelPlain)}>
      {showHeader && (
        <div className={styles.header}>
          <TitleText as="h2">{title}</TitleText>
          <Body tone="muted">{subtitle}</Body>
        </div>
      )}

      {!data ? (
        <EmptyState title="Loading usage…" />
      ) : rows.length === 0 && !hasFallbackTotal ? (
        <EmptyState title="No agent usage data yet." />
      ) : (
        <div className={styles.usageList}>
          {/* TODO(ai-credits): rows kept as plain AIC text — this breakdown is rendered as the `detail`
              inside the AiCredits popover, so nesting another hover control here would nest popovers. */}
          {rows.map((entry) => (
            <div key={entry.agentName} className={styles.usageItem}>
              <div className={styles.rowHead}>
                <AgentIdentity label={entry.agentName} roleByAgent={roleByAgent} className={styles.identity} />
                <Text className={styles.metric}>{costChipLabel(entry.totalNanoAiu, entry.totalTokens) ?? `${entry.invocationCount} turns`}</Text>
              </div>
              <ProgressBar
                className={styles.progress}
                value={Math.max(0.08, usageValue(entry) / max)}
                max={1}
                thickness="large"
              />
            </div>
          ))}
          {hasFallbackTotal && (
            <div className={styles.usageItem}>
              <div className={styles.rowHead}>
                <Text>Total run usage</Text>
                <Text>{costChipLabel(data.totalNanoAiu, data.totalTokens) ?? '—'}</Text>
              </div>
              <ProgressBar className={styles.progress} value={1} max={1} thickness="large" />
            </div>
          )}
        </div>
      )}

      {data?.source === 'events' && (
        <span className={mergeClasses(styles.note)}>
          <InfoRegular className={styles.noteIcon} aria-hidden="true" />
          <Text>Showing persisted turn-usage events because agent dimension data is not yet available for this run.</Text>
        </span>
      )}
    </div>
  );
}
