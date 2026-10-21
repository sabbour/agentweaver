/**
 * Lists the signed-in user's notifications across all projects. Overview and the
 * notification bell use it; later the attention inbox and Chief of Staff
 * Priorities reuse it. A pending gate opens the run's Approvals tab, where the
 * shared decision view handles the response. A backlog promotion opens the
 * project Board. Each item can be dismissed.
 */
import {
  Button,
  Caption1,
  CounterBadge,
  Popover,
  PopoverSurface,
  PopoverTrigger,
  Text,
  Tooltip,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { Alert24Regular, DismissRegular } from '@fluentui/react-icons';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { formatAbsoluteTime, formatRelativeTime } from '../utils/relativeTime';
import { DECISION_TITLES } from './decisionTitles';
import { errorMessage } from './errors';
import { attentionKey, attentionPath, subtasksCreatedLabel } from './runAttention';
import type { AttentionItem, RunAttention } from './runAttention';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS },
  // Room on the right for the absolutely placed Dismiss button, as in 0.x.
  item: { position: 'relative', paddingRight: '36px', borderRadius: tokens.borderRadiusMedium },
  link: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: tokens.spacingVerticalXS,
    borderRadius: tokens.borderRadiusMedium,
    color: tokens.colorNeutralForeground1,
    textDecorationLine: 'none',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '1px' },
  },
  dismiss: { position: 'absolute', top: tokens.spacingVerticalXXS, right: tokens.spacingHorizontalXXS },
  empty: { padding: tokens.spacingVerticalM, color: tokens.colorNeutralForeground3 },
  trigger: { position: 'relative', display: 'inline-flex', flexShrink: 0 },
  badge: { position: 'absolute', top: '2px', right: '2px', pointerEvents: 'none' },
  surface: {
    width: '360px',
    maxWidth: 'calc(100vw - 24px)',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  scroll: { maxHeight: '360px', overflowY: 'auto' },
});

/** Title shown for an item: the request kind for a gate, the subtask count for a promotion. */
function itemTitle(item: AttentionItem): string {
  return item.source === 'pendingGate' ? DECISION_TITLES[item.kind] : subtasksCreatedLabel(item.taskCount);
}

/** Renders the notifications, or a one-line state when there are none. */
export function AttentionList({
  attention,
  projectNames,
  onNavigate,
}: {
  attention: RunAttention;
  /** Project names by ID, when the caller already has them. IDs are shown otherwise. */
  projectNames?: ReadonlyMap<string, string>;
  onNavigate?: () => void;
}) {
  const styles = useStyles();
  // A failed dismiss is shown above the list or state line.
  const dismissFailure = attention.dismissError
    ? <Caption1 className={styles.empty} role="alert">Could not dismiss the notification. {errorMessage(attention.dismissError)}</Caption1>
    : null;
  // Check states in order: error, loading, empty, then the list.
  if (attention.error) return <Caption1 className={styles.empty} role="alert">Could not load notifications. {errorMessage(attention.error)}</Caption1>;
  if (attention.loading) return <Caption1 className={styles.empty}>Loading notifications…</Caption1>;
  if (attention.items.length === 0) return <>{dismissFailure}<Caption1 className={styles.empty}>Nothing needs your attention.</Caption1></>;
  // The list holds one page. Say so when the service counts more.
  const more = attention.count > attention.items.length;
  return (
    <>
      {dismissFailure}
      <div className={styles.list} role="list" aria-label="Needs your attention">
        {attention.items.map((item) => {
          const title = itemTitle(item);
          return (
            <div key={attentionKey(item)} role="listitem" className={styles.item}>
              <Link
                className={styles.link}
                to={attentionPath(item)}
                // Lets the bell close its popover when the user follows a link.
                onClick={onNavigate}
              >
                <Text weight="semibold">{title}</Text>
                {item.source === 'pendingGate' && item.prompt && <Text>{item.prompt}</Text>}
                <Caption1>{projectNames?.get(item.projectId) ?? item.projectId} · Run {item.runId}</Caption1>
                <Caption1 title={formatAbsoluteTime(item.createdAt)}>{formatRelativeTime(item.createdAt)}</Caption1>
              </Link>
              <Button
                appearance="subtle"
                size="small"
                icon={<DismissRegular />}
                aria-label={`Dismiss notification: ${title}`}
                className={styles.dismiss}
                onClick={() => { void attention.dismiss(item.notificationId); }}
              />
            </div>
          );
        })}
      </div>
      {more && <Caption1 className={styles.empty}>Showing the newest {attention.items.length} of {attention.count}.</Caption1>}
    </>
  );
}

/**
 * 0.x notification bell. The badge shows the service's pending count and is
 * hidden on error. The badge sits outside the icon-only button: button content
 * would give it a text-button minimum width and squeeze the brand in the rail header.
 */
export function AttentionBell({ attention, projectNames }: { attention: RunAttention; projectNames?: ReadonlyMap<string, string> }) {
  const styles = useStyles();
  // The popover is controlled so a link click can close it.
  const [open, setOpen] = useState(false);
  const count = attention.error ? 0 : attention.count;
  return (
    <span className={styles.trigger}>
      <Popover open={open} onOpenChange={(_, data) => setOpen(data.open)} positioning="below-end">
        <PopoverTrigger disableButtonEnhancement>
          <Tooltip content="Notifications" relationship="label">
            <Button
              appearance="subtle"
              icon={<Alert24Regular />}
              // The count is in the accessible name because the visual badge is aria-hidden.
              aria-label={count > 0 ? `Notifications, ${count} need${count === 1 ? 's' : ''} your attention` : 'Notifications'}
              data-testid="notification-bell"
            />
          </Tooltip>
        </PopoverTrigger>
        <PopoverSurface className={styles.surface}>
          <Text weight="semibold" size={400}>Notifications</Text>
          <div className={styles.scroll}>
            <AttentionList attention={attention} projectNames={projectNames} onNavigate={() => setOpen(false)} />
          </div>
        </PopoverSurface>
      </Popover>
      {count > 0 && <CounterBadge count={count} size="small" color="danger" className={styles.badge} aria-hidden />}
    </span>
  );
}
