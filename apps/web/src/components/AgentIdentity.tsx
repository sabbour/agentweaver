import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import type { FluentIcon } from '@fluentui/react-icons';
import {
  BotRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  MergeRegular,
  NotebookRegular,
  PersonRegular,
  ShieldRegular,
} from '@fluentui/react-icons';
import { AgentAvatar } from './AgentAvatar';
import { resolveAgentIdentity, type ResolvedAgentIdentity } from '../utils/agentIdentity';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
  },
  meta: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  name: {
    color: tokens.colorNeutralForeground1,
    fontWeight: tokens.fontWeightSemibold,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  role: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    lineHeight: tokens.lineHeightBase200,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  iconAvatar: {
    width: '28px',
    height: '28px',
    minWidth: '28px',
    borderRadius: tokens.borderRadiusCircular,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    flexShrink: 0,
  },
});

function iconForRoleKey(roleKey: string): FluentIcon {
  const map: Record<string, FluentIcon> = {
    agent: BotRegular,
    rai: ShieldRegular,
    review: PersonRegular,
    merge: MergeRegular,
    scribe: NotebookRegular,
    coordinator: BotRegular,
    assembly: CheckmarkCircleRegular,
  };
  return map[roleKey] ?? CircleRegular;
}

function renderAvatar(identity: ResolvedAgentIdentity, avatarSize: number) {
  const Icon = iconForRoleKey(identity.roleKey);
  if (identity.isNamedAgent && !identity.isModelFallback) {
    return (
      <AgentAvatar
        name={identity.displayName}
        size={avatarSize}
        circle
        badgeIcon={Icon}
        badgeTitle={identity.roleTitle}
      />
    );
  }
  return null;
}

export function AgentIdentity({
  label,
  roleByAgent,
  avatarSize = 28,
  className,
}: {
  label: string | null | undefined;
  roleByAgent?: Record<string, string>;
  avatarSize?: number;
  className?: string;
}) {
  const styles = useStyles();
  const identity = resolveAgentIdentity(label, roleByAgent);
  const Icon = iconForRoleKey(identity.roleKey);
  const avatar = renderAvatar(identity, avatarSize);

  return (
    <div className={mergeClasses(styles.root, className)} title={`${identity.displayName} — ${identity.roleTitle}`}>
      {avatar ?? (
        <span className={styles.iconAvatar} aria-hidden="true">
          <Icon fontSize={16} />
        </span>
      )}
      <div className={styles.meta}>
        <span className={styles.name}>{identity.displayName}</span>
        <span className={styles.role}>{identity.roleTitle}</span>
      </div>
    </div>
  );
}
