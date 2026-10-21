/**
 * Lists pending questions and approvals. The notification bell uses it for the
 * bound run; Overview and Flow use it for the caller's notifications. Each item
 * opens the run's Approvals tab, where the shared decision view handles the
 * response.
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
import { Alert24Regular } from '@fluentui/react-icons';
import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { DECISION_TITLES } from './decisionTitles';
import { errorMessage } from './errors';
import { attentionPath } from './runAttention';
import type { RunAttention } from './runAttention';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS },
  item: {
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
  empty: { padding: tokens.spacingVerticalM, color: tokens.colorNeutralForeground3 },
  row: { display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: tokens.spacingHorizontalS },
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

/**
 * Renders the pending items, or a one-line state when there are none. Run attention
 * needs a bound run; notification attention (`source: 'notifications'`) reads across
 * runs and offers Dismiss when the source supports it.
 */
export function AttentionList({ attention, onNavigate }: { attention: RunAttention; onNavigate?: () => void }) {
  const styles = useStyles();
  const [dismissError, setDismissError] = useState<unknown>(null);
  const notifications = attention.source === 'notifications';
  // Show exactly one state line, checked in order: no run bound, read error,
  // still loading, nothing pending. Only then render the list.
  // Run attention needs a bound run. Notification attention works without one.
  if (!notifications && !attention.binding) return <Caption1 className={styles.empty}>Open a run to see what needs your input.</Caption1>;
  if (attention.error) return (
    <Caption1 className={styles.empty} role="alert">
      {notifications ? 'Could not read notifications.' : 'Could not check the open run.'} {errorMessage(attention.error)}
    </Caption1>
  );
  if (attention.loading) return <Caption1 className={styles.empty}>{notifications ? 'Checking notifications…' : 'Checking the open run…'}</Caption1>;
  if (attention.items.length === 0) return <Caption1 className={styles.empty}>{notifications ? 'Nothing needs your input.' : 'Nothing needs your input in the open run.'}</Caption1>;
  // Copy to a local const so TypeScript keeps it narrowed inside the click handler.
  const dismiss = attention.dismiss;
  return (
    <div className={styles.list} role="list" aria-label="Needs your input">
      {dismissError != null && <Caption1 role="alert">Could not dismiss. {errorMessage(dismissError)}</Caption1>}
      {attention.items.map((item) => {
        const content = (
          <>
            <Text weight="semibold">{DECISION_TITLES[item.kind]}</Text>
            {item.prompt && <Text>{item.prompt}</Text>}
            <Caption1>Run {item.runId}</Caption1>
          </>
        );
        // Session id plus request id is unique across runs.
        const key = `${item.sessionId}/${item.requestId}`;
        // Without a dismiss action (or for a non-notification item), the whole row is one link.
        if (!dismiss || !item.notificationId) return (
          <Link key={key} role="listitem" className={styles.item} to={attentionPath(item)} onClick={onNavigate}>
            {content}
          </Link>
        );
        return (
          <div key={key} role="listitem" className={styles.row}>
            <Link className={styles.item} to={attentionPath(item)} onClick={onNavigate}>{content}</Link>
            <Button
              appearance="subtle"
              size="small"
              aria-label={`Dismiss ${DECISION_TITLES[item.kind]} in run ${item.runId}`}
              onClick={() => {
                // Clear any old error first, then show a new one only if this dismiss fails.
                // On success the list reloads and the item disappears.
                setDismissError(null);
                void dismiss(item).catch((reason: unknown) => setDismissError(reason));
              }}
            >
              Dismiss
            </Button>
          </div>
        );
      })}
    </div>
  );
}

/**
 * 0.x notification bell, fed by the bound run's pending items. The count badge sits
 * outside the icon-only button: button content would give it a text-button minimum
 * width and squeeze the brand in the rail header.
 */
export function AttentionBell({ attention }: { attention: RunAttention }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const count = attention.items.length;
  return (
    <span className={styles.trigger}>
      <Popover open={open} onOpenChange={(_, data) => setOpen(data.open)} positioning="below-end">
        <PopoverTrigger disableButtonEnhancement>
          <Tooltip content="Notifications" relationship="label">
            <Button
              appearance="subtle"
              icon={<Alert24Regular />}
              aria-label={count > 0 ? `Notifications, ${count} need${count === 1 ? 's' : ''} your input` : 'Notifications'}
              data-testid="notification-bell"
            />
          </Tooltip>
        </PopoverTrigger>
        <PopoverSurface className={styles.surface}>
          <Text weight="semibold" size={400}>Notifications</Text>
          <div className={styles.scroll}>
            <AttentionList attention={attention} onNavigate={() => setOpen(false)} />
          </div>
          {attention.binding && (
            <Button appearance="subtle" onClick={() => {
              // Close the popover first so it does not stay open over the run page.
              setOpen(false);
              navigate(`/projects/${encodeURIComponent(attention.binding!.projectId)}/runs/${encodeURIComponent(attention.binding!.runId)}`);
            }}>
              Open run
            </Button>
          )}
        </PopoverSurface>
      </Popover>
      {count > 0 && <CounterBadge count={count} size="small" color="danger" className={styles.badge} aria-hidden />}
    </span>
  );
}
