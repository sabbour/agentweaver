/**
 * Project Orchestrations in the 0.x layout: header with Refresh, a status
 * summary, then Active and Recent run sections. Runs come from the service run
 * list. Active holds open runs and Recent holds finished runs. Each section
 * loads its own pages with Load more.
 */
import { Button, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { useParams } from 'react-router-dom';
import { EmptyState, ErrorState } from '../components/ui/States';
import { Loading, PageHeading } from './pageParts';
import { RunList } from './RunList';
import { runListErrorMessage, useProjectRunList } from './runListData';
import { runState } from './runState';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXL },
  statusSurface: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    boxShadow: tokens.shadow2,
  },
  pills: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  pill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    minHeight: '28px',
    padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalS}`,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    fontVariantNumeric: 'tabular-nums',
  },
  pillValue: { color: tokens.colorNeutralForeground1, fontWeight: tokens.fontWeightSemibold },
});

/** Formats a loaded-row count. A "+" shows that more rows exist on the service. */
function countLabel(count: number, hasMore: boolean): string {
  return `${count}${hasMore ? '+' : ''}`;
}

/** Renders one project's runs in Active and Recent sections. */
export function OrchestrationsPage() {
  const styles = useStyles();
  const { projectId = '' } = useParams();
  // Two filtered lists, like 0.x: open runs (idle, active, blocked) and
  // finished runs (completed, failed, cancelled, indeterminate).
  const active = useProjectRunList(projectId, { states: ['open'] });
  const recent = useProjectRunList(projectId, { states: ['terminal'] });
  const refresh = () => { active.reload(); recent.reload(); };
  const loading = active.loading || recent.loading;
  // Show one error. Both lists use the same access, so they usually fail together.
  const error = active.error ?? recent.error;
  const total = active.items.length + recent.items.length;
  // Count loaded runs per state label for the status pills. A Map keeps the
  // order in which labels first appear.
  const counts = new Map<string, number>();
  for (const item of [...active.items, ...recent.items]) {
    const label = runState(item.executionState).label;
    counts.set(label, (counts.get(label) ?? 0) + 1);
  }

  return (
    <div className={styles.root}>
      <PageHeading
        title="Orchestrations"
        description="Coordinator runs for this project."
        page="Orchestrations"
        actions={<Button appearance="secondary" icon={<ArrowSyncRegular />} disabled={loading} onClick={refresh}>Refresh</Button>}
      />
      {error != null && (
        <ErrorState title="Couldn't load orchestrations" message={runListErrorMessage(error)} onRetry={refresh} />
      )}
      {total > 0 && (
        <div className={styles.statusSurface} aria-label="Orchestration status summary">
          <div className={styles.pills}>
            <span className={styles.pill}><span className={styles.pillValue}>{countLabel(active.items.length, active.hasMore)}</span> active</span>
            <span className={styles.pill}><span className={styles.pillValue}>{countLabel(recent.items.length, recent.hasMore)}</span> recent</span>
            {[...counts].map(([label, count]) => (
              <span key={label} className={styles.pill}><span className={styles.pillValue}>{count}</span> {label}</span>
            ))}
          </div>
        </div>
      )}
      {/* Show the loader only when no rows are on screen. A refresh keeps the old rows. */}
      {loading && total === 0 && <Loading label="Loading orchestrations" />}
      {!loading && error == null && total === 0 && (
        <EmptyState title="No orchestrations yet" description="Runs started in this project appear here." />
      )}
      {active.items.length > 0 && (
        <RunList
          title="Active"
          description="Runs that are working, open, or waiting for input."
          data={active}
          emptyText="No active runs."
        />
      )}
      {recent.items.length > 0 && (
        <RunList
          title="Recent"
          description="Finished runs: done, failed, cancelled, or with an unknown outcome."
          data={recent}
          emptyText="No finished runs."
        />
      )}
    </div>
  );
}
