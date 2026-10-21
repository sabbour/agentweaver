/**
 * Shared run list for Orchestrations, Overview, and later Sessions and Flow.
 * The data adapter reads the browser's recent runs today. When the service
 * offers a run list, a second adapter can return the same `RunListItem` shape,
 * so callers keep the same layout.
 */
import { Badge, Button, Text, Tooltip, makeStyles, tokens } from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { useNavigate } from 'react-router-dom';
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
  // Show the raw text if the value is not a valid date, rather than "Invalid Date".
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
  // `limit` trims the rows shown. The count badge still shows the full total.
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
      <div className={styles.list}>
        {items.length === 0 ? <Text className={styles.empty}>{emptyText}</Text> : items.map((item) => {
          const state = runState(item.executionState);
          // Encode both IDs; they come from storage and may hold URL characters.
          const path = `/projects/${encodeURIComponent(item.projectId)}/runs/${encodeURIComponent(item.runId)}`;
          return (
            <div key={`${item.projectId}/${item.runId}`} className={styles.row}>
              <div className={styles.rowStatus}>
                <Badge appearance="tint" color={state.color}>{state.label}</Badge>
                {/* The state is a saved copy from the last time the run was
                    opened. It is not live, so the caption says so. */}
                <Text className={styles.meta}>{item.executionState ? 'State when last opened' : 'State not read yet'}</Text>
              </div>
              <div className={styles.rowMain}>
                <Text className={styles.task}>Run {item.runId}</Text>
                <Text className={styles.meta}>
                  {projectNames ? `${projectNames.get(item.projectId) ?? item.projectId} · ` : ''}
                  {item.time.label} {formatTime(item.time.value)}
                </Text>
              </div>
              <div className={styles.actions}>
                <Button appearance="secondary" aria-label={`Open run ${item.runId}`} onClick={() => navigate(path)}>Open</Button>
                {/* Remove appears only when the data source supports it. It
                    forgets the run in this browser and does not touch the run. */}
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
    </section>
  );
}
