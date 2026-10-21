/**
 * Project Flow: the project's work grouped by state. Needs input lists the
 * caller's notifications for the project, including dismissed ones, because a
 * dismissed notification only hides it from the bell while its gate is pending.
 * Open, Working, and Done come from the Gateway run list (G2):
 * Open = idle, Working = active or blocked, Done = completed, failed, cancelled,
 * or indeterminate, each with its own label. Pull request state has no source
 * yet, so every run shows it as Unavailable.
 */
import { Button, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { useMemo } from 'react';
import { useParams } from 'react-router-dom';
import { AttentionList } from './AttentionList';
import { PageHeading } from './pageParts';
import { useNotificationAttention } from './runAttention';
import { RunList } from './RunList';
import { PULL_REQUEST_NOTE, flowGroup } from './flowGroups';
import type { FlowGroup } from './flowGroups';
import { useGatewayRunList } from './runListData';
import type { RunListData, RunListItem } from './runListData';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXL },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  title: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
});

/** Renders one project's Flow page. */
export function FlowPage() {
  const styles = useStyles();
  const { projectId = '' } = useParams();
  const attention = useNotificationAttention({ projectId, includeDismissed: true });
  const runs = useGatewayRunList(projectId);

  const groups = useMemo(() => {
    const result: Record<FlowGroup, RunListItem[]> = { open: [], working: [], done: [] };
    for (const item of runs.items) {
      const group = flowGroup(item.executionState);
      if (group) result[group].push({ ...item, notes: [PULL_REQUEST_NOTE] });
    }
    return result;
  }, [runs.items]);

  const view = (items: RunListItem[]): RunListData => ({
    items, reload: runs.reload, stateSource: 'service', loading: runs.loading,
  });
  const refresh = () => { runs.reload(); attention.reload(); };

  return (
    <div className={styles.root}>
      <PageHeading
        title="Flow"
        description="Work in this project by state."
        page="Flow"
        actions={<Button appearance="secondary" icon={<ArrowSyncRegular />} onClick={refresh}>Refresh</Button>}
      />
      <section className={styles.card} aria-labelledby="flow-needs-input">
        <Text as="h2" id="flow-needs-input" className={styles.title}>Needs input</Text>
        <AttentionList attention={attention} />
      </section>
      {runs.error != null && <RunList title="Runs" data={{ ...view([]), error: runs.error, loading: false }} emptyText="Could not read runs." />}
      <RunList title="Open" description="Accepted and not working." data={view(groups.open)} emptyText="No open runs." />
      <RunList title="Working" description="Running, or waiting on a decision." data={view(groups.working)} emptyText="No runs working." />
      <RunList title="Done" description="Ended. Failed, cancelled, and unknown outcomes keep their own labels." data={view(groups.done)} emptyText="No ended runs." />
      {runs.loadMore && <div><Button appearance="secondary" onClick={runs.loadMore}>Load more runs</Button></div>}
    </div>
  );
}