import {
  Button,
  Caption1,
  CounterBadge,
  Popover,
  PopoverSurface,
  PopoverTrigger,
  Switch,
  Text,
  Tooltip,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { Alert24Regular, DismissRegular } from '@fluentui/react-icons';
import { useNotifications } from '../../notifications/notificationsContext';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { NotificationTypeBadge } from './NotificationTypeBadge';
// #247 — persistent bell + unread badge, rendered in the left-nav chrome (this app has no
// separate top bar — see LeftNav.tsx's own header comment), so it is visible on every page
// regardless of collapsed state.

const useStyles = makeStyles({
  trigger: {
    position: 'relative',
  },
  badge: {
    position: 'absolute',
    top: '2px',
    right: '2px',
  },
  surface: {
    width: '360px',
    maxWidth: 'calc(100vw - 24px)',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    maxHeight: '360px',
    overflowY: 'auto',
  },
  item: {
    position: 'relative',
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: tokens.spacingVerticalXS,
    paddingRight: '36px',
    borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer',
  },
  dismiss: {
    position: 'absolute',
    top: tokens.spacingVerticalXXS,
    right: tokens.spacingHorizontalXXS,
  },
  empty: {
    padding: tokens.spacingVerticalM,
    textAlign: 'center',
    color: tokens.colorNeutralForeground3,
  },
});

export function NotificationBell() {
  const styles = useStyles();
  const navigate = useNavigate();
  const { notifications, unreadCount, muted, toggleMuted, markAllSeen, dismissNotification } = useNotifications();
  const [open, setOpen] = useState(false);
  const pendingApprovalCount = notifications.filter((notification) => notification.type === 'tool_approval').length;
  const badgeCount = Math.max(unreadCount, pendingApprovalCount);

  const goTo = (path: string) => {
    setOpen(false);
    navigate(path);
  };

  return (
    <Popover
      open={open}
      onOpenChange={(_, data) => {
        setOpen(data.open);
        if (data.open) markAllSeen();
      }}
      positioning="below-end"
    >
      <PopoverTrigger disableButtonEnhancement>
        <Tooltip content="Notifications" relationship="label">
          <Button
            appearance="subtle"
            icon={<Alert24Regular />}
            aria-label={pendingApprovalCount > 0
              ? `Notifications, ${pendingApprovalCount} pending tool approval${pendingApprovalCount === 1 ? '' : 's'}`
              : unreadCount > 0
                ? `Notifications, ${unreadCount} unread`
                : 'Notifications'}
            data-testid="notification-bell"
            className={styles.trigger}
          >
            {badgeCount > 0 && (
              <CounterBadge
                count={badgeCount}
                size="small"
                color="danger"
                className={styles.badge}
                data-testid="notification-bell-badge"
                aria-label={`${badgeCount} action${badgeCount === 1 ? '' : 's'} need attention`}
              />
            )}
          </Button>
        </Tooltip>
      </PopoverTrigger>
      <PopoverSurface className={styles.surface}>
        <div className={styles.header}>
          <Text weight="semibold" size={400}>Notifications</Text>
          <Switch
            checked={!muted}
            onChange={toggleMuted}
            label={muted ? 'Sound off' : 'Sound on'}
            labelPosition="before"
            data-testid="notification-mute-toggle"
          />
        </div>
        <div className={styles.list} role="list" aria-label="Pending review requests">
          {notifications.length === 0 && (
            <Caption1 className={styles.empty}>Nothing needs your attention right now.</Caption1>
          )}
          {notifications.map((notification) => (
            <div
              key={notification.id}
              role="listitem"
              className={styles.item}
              tabIndex={0}
              onClick={() => goTo(notification.cta_path)}
              onKeyDown={(event) => {
                if (event.key === 'Enter' || event.key === ' ') {
                  event.preventDefault();
                  goTo(notification.cta_path);
                }
              }}
            >
              <NotificationTypeBadge type={notification.type} />
              <Text weight="semibold">{notification.title}</Text>
              <Caption1>{notification.project_name ?? 'Unknown project'}</Caption1>
              <Button
                appearance="subtle"
                size="small"
                icon={<DismissRegular />}
                aria-label={`Dismiss notification: ${notification.title}`}
                className={styles.dismiss}
                onClick={(event) => {
                  event.stopPropagation();
                  dismissNotification(notification.id);
                }}
                onKeyDown={(event) => event.stopPropagation()}
              />
            </div>
          ))}
        </div>
      </PopoverSurface>
    </Popover>
  );
}
