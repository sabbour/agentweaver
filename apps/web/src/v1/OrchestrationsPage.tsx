/**
 * Project Orchestrations in the 0.x layout: header with Refresh, status pills,
 * then In progress and Finished run sections. Runs come from this browser's run
 * history because this page does not read the service run list. Starting,
 * stopping, and deleting runs are not supported by the service yet, so they are
 * not shown.
 */
import { Button, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { useParams } from 'react-router-dom';
import { PageHeading } from './pageParts';
import { RunList } from './RunList';
import { useBrowserRunList } from './runListData';
import { isRunFinished, runState } from './runState';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXL },
  pills: { display: 'flex', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  pill: {
    display: 'inline-flex',
    alignItems: 'baseline',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusCircular,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground2,
  },
  pillValue: { fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground1, fontVariantNumeric: 'tabular-nums' },
});

/** Renders one project's runs grouped by their last read state. */
export function OrchestrationsPage() {
  const styles = useStyles();
  const { projectId = '' } = useParams();
  const runs = useBrowserRunList(projectId);
  // Split by the last state seen. A run with no known state counts as in progress,
  // because it has not been seen to finish.
  const inProgress = runs.items.filter((item) => !isRunFinished(item.executionState));
  const finished = runs.items.filter((item) => isRunFinished(item.executionState));
  // Count runs per state label for the summary pills. Map keeps first-seen order.
  const counts = new Map<string, number>();
  for (const item of runs.items) {
    const label = runState(item.executionState).label;
    counts.set(label, (counts.get(label) ?? 0) + 1);
  }

  return (
    <div className={styles.root}>
      <PageHeading
        title="Orchestrations"
        description="Coordinator runs for this project."
        page="Orchestrations"
        actions={<Button appearance="secondary" icon={<ArrowSyncRegular />} onClick={runs.reload}>Refresh</Button>}
      />
      {runs.items.length > 0 && (
        <div className={styles.pills} aria-label="Run status summary">
          {[...counts].map(([label, count]) => (
            <span key={label} className={styles.pill}><span className={styles.pillValue}>{count}</span>{label}</span>
          ))}
        </div>
      )}
      {runs.items.length === 0 ? (
        <RunList title="Recent runs in this browser" data={runs} emptyText="Runs you open appear here." />
      ) : (
        <>
          {inProgress.length > 0 && (
            <RunList
              title="In progress"
              description="Runs that were working, open, or waiting for input when you last opened them."
              data={{ ...runs, items: inProgress }}
              emptyText="Runs you open appear here."
            />
          )}
          {finished.length > 0 && (
            <RunList
              title="Finished"
              description="Runs that were done, failed, or ended with an unknown outcome."
              data={{ ...runs, items: finished }}
              emptyText="Runs you open appear here."
            />
          )}
          <Text size={200}>These runs were opened in this browser. A full run list needs a service update.</Text>
        </>
      )}
    </div>
  );
}
