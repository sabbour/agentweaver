/**
 * Shared run list for Orchestrations, Overview, Flow, and later Sessions.
 * The browser adapter reads the browser's recent runs; the Gateway adapter
 * (`useGatewayRunList`) returns the same `RunListData` shape with current states,
 * so callers keep the same layout.
 */
import { Badge, Button, Text, Tooltip, makeStyles, tokens } from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { useNavigate } from 'react-router-dom';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice } from './pageParts';
import type { RunListData } from './runListData';
import { runState } from './runState';

const useStyles = makeStyles({
  section: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  sectionHeader: {
    display: 'flex',
    alignItems: 'flex-end',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
    flexWrap: 'wrap',
  },
  sectionTitleGroup: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, minWidth: 0 },
  sectionTitle: {
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  sectionDescription: { color: tokens.colorNeutralForeground2, lineHeight: tokens.lineHeightBase300, maxWidth: '70ch' },
  list: {
    display: 'flex',
    flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    overflow: 'hidden',
  },
  row: {
    display: 'grid',
    gridTemplateColumns: 'minmax(150px, 0.6fr) minmax(0, 1.4fr) auto',
    alignItems: 'center',
    gap: tokens.spacingHorizontalL,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`,
    ':not(:first-child)': { borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
    '@media (max-width: 760px)': { gridTemplateColumns: '1fr', alignItems: 'start' },
  },
  rowStatus: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: tokens.spacingVerticalXXS, minWidth: 0 },
  rowMain: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, minWidth: 0 },
  task: { fontWeight: tokens.fontWeightSemibold, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  meta: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  empty: {
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalL}`,
    color: tokens.colorNeutralForeground2,
  },
});

/** Formats an ISO time for list rows. */
function formatTime(value: string): string {
  const parsed = new Date(value);
  // If the value is not a valid date, show the raw text rather than "Invalid Date".
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

/**
 * Renders a run list in the 0.x Orchestrations row layout: state, run, time,
 * and an Open action. `projectNames` adds the project name when the list spans
 * projects.
 */
export function RunList({ title, description, data, emptyText, projectNames, limit }: {
  title: string;
  description?: string;
  data: RunListData;
  emptyText: string;
  projectNames?: ReadonlyMap<string, string>;
  limit?: number;
}) {
  const navigate = useNavigate();
  const styles = useStyles();
  // `limit` shows a short preview. The count badge below still uses the full list size,
  // and "Load more" is hidden in preview mode because the full list lives on another page.
  const items = limit ? data.items.slice(0, limit) : data.items;
  return (
    <section className={styles.section} aria-label={title}>
      <div className={styles.sectionHeader}>
        <div className={styles.sectionTitleGroup}>
          <Text as="h2" className={styles.sectionTitle}>{title}</Text>
          {description && <Text className={styles.sectionDescription}>{description}</Text>}
        </div>
        {data.items.length > 0 && <Badge appearance="outline">{data.items.length} {data.items.length === 1 ? 'run' : 'runs'}</Badge>}
      </div>
      {data.error != null && <ErrorNotice code={errorCode(data.error)}>{errorMessage(data.error)}</ErrorNotice>}
      <div className={styles.list}>
        {items.length === 0 ? <Text className={styles.empty}>{data.loading ? 'Loading runs…' : emptyText}</Text> : items.map((item) => {
          const state = runState(item.executionState);
          const path = `/projects/${encodeURIComponent(item.projectId)}/runs/${encodeURIComponent(item.runId)}`;
          // Say where the state came from. Browser rows may be out of date, so the
          // note makes clear it is the state seen when the run was last opened.
          const stateNote = data.stateSource === 'service'
            ? 'Current state'
            : item.executionState ? 'State when last opened' : 'State not read yet';
          return (
            <div key={`${item.projectId}/${item.runId}`} className={styles.row}>
              <div className={styles.rowStatus}>
                <Badge appearance="tint" color={state.color}>{state.label}</Badge>
                <Text className={styles.meta}>{stateNote}</Text>
              </div>
              <div className={styles.rowMain}>
                <Text className={styles.task}>{item.title || `Run ${item.runId}`}</Text>
                <Text className={styles.meta}>
                  {projectNames ? `${projectNames.get(item.projectId) ?? item.projectId} · ` : ''}
                  {item.title ? `Run ${item.runId} · ` : ''}
                  {item.time.label} {formatTime(item.time.value)}
                </Text>
                {item.notes?.map((note) => <Text key={note} className={styles.meta}>{note}</Text>)}
              </div>
              <div className={styles.actions}>
                <Button appearance="secondary" aria-label={`Open run ${item.runId}`} onClick={() => navigate(path)}>Open</Button>
                {data.remove && (
                  <Tooltip content="Remove from this list. The run is not changed." relationship="description">
                    <Button appearance="subtle" icon={<DismissRegular />} aria-label={`Remove run ${item.runId} from this list`} onClick={() => data.remove?.(item)}>
                      Remove
                    </Button>
                  </Tooltip>
                )}
              </div>
            </div>
          );
        })}
      </div>
      {data.loadMore && !limit && (
        <div className={styles.actions}>
          <Button appearance="secondary" onClick={data.loadMore}>Load more</Button>
        </div>
      )}
    </section>
  );
}
