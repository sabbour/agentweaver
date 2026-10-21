/**
 * Shared run list for Orchestrations, Sessions, Dashboard, and Overview, in the
 * 0.x row layout. Rows come from a `RunListData` adapter that reads the
 * service run list.
 */
import { Badge, Button, Text, makeStyles, tokens } from '@fluentui/react-components';
import { Link } from 'react-router-dom';
import type { RunListData } from './runListData';
import { isRunFinished, runState } from './runState';

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
  footer: { display: 'flex', justifyContent: 'center' },
});

/** Formats an ISO time for list rows. */
function formatTime(value: string): string {
  const parsed = new Date(value);
  // Show the raw text if the value is not a valid date, rather than "Invalid Date".
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

/**
 * Renders a run list in the 0.x Orchestrations row layout: state, run, time,
 * and an Open link to the run page. `projectNames` adds the project name when
 * the list spans projects. `limit` shows only the first rows and hides Load
 * more, for summary cards that link to the full list.
 */
export function RunList({ title, description, data, emptyText, projectNames, limit }: {
  title: string;
  description?: string;
  data: RunListData;
  emptyText: string;
  projectNames?: ReadonlyMap<string, string>;
  limit?: number;
}) {
  const styles = useStyles();
  const items = limit ? data.items.slice(0, limit) : data.items;
  const count = data.items.length;
  // A "+" shows that the service has more runs than the ones loaded.
  const countText = `${count}${data.hasMore ? '+' : ''} ${count === 1 && !data.hasMore ? 'run' : 'runs'}`;
  return (
    <section className={styles.section} aria-label={title}>
      <div className={styles.sectionHeader}>
        <div className={styles.sectionTitleGroup}>
          <Text as="h2" className={styles.sectionTitle}>{title}</Text>
          {description && <Text className={styles.sectionDescription}>{description}</Text>}
        </div>
        {count > 0 && <Badge appearance="outline">{countText}</Badge>}
      </div>
      <div className={styles.list}>
        {items.length === 0 ? <Text className={styles.empty}>{emptyText}</Text> : items.map((item) => {
          const state = runState(item.executionState);
          // Encode both IDs; they come from the service and may hold URL characters.
          const path = `/projects/${encodeURIComponent(item.projectId)}/runs/${encodeURIComponent(item.runId)}`;
          return (
            <div key={`${item.projectId}/${item.runId}`} className={styles.row}>
              <div className={styles.rowStatus}>
                <Badge appearance="tint" color={state.color}>{state.label}</Badge>
                <Text className={styles.meta}>{isRunFinished(item.executionState) ? 'Finished' : 'Live run'}</Text>
              </div>
              <div className={styles.rowMain}>
                <Text className={styles.task} title={item.label}>{item.label}</Text>
                <Text className={styles.meta}>
                  {projectNames ? `${projectNames.get(item.projectId) ?? item.projectId} · ` : ''}
                  {item.time.label} {formatTime(item.time.value)}
                </Text>
              </div>
              <div className={styles.actions}>
                <Link className="v1-link-button" to={path} aria-label={`Open run ${item.label}`}>Open</Link>
              </div>
            </div>
          );
        })}
      </div>
      {/* Load more appears only on full lists, and only when the service has another page. */}
      {!limit && data.hasMore && (
        <div className={styles.footer}>
          <Button appearance="secondary" disabled={data.loadingMore} onClick={data.loadMore}>
            {data.loadingMore ? 'Loading more runs…' : 'Load more'}
          </Button>
        </div>
      )}
    </section>
  );
}
