import {
  resolveAgentIdentity } from '../utils/agentIdentity';
import { AgentAvatar } from './AgentAvatar';
import { makeStyles,
  mergeClasses,
  tokens,
} from '../copilot-fluent-system';
import {
  BotRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  MergeRegular,
  NotebookRegular,
  PersonRegular,
  ShieldRegular,
} from '../copilot-fluent-system';
import type { ResolvedAgentIdentity } from '../utils/agentIdentity';
import type { FluentIcon } from '../copilot-fluent-system';

const ROLE_ICON_BY_KEY: Readonly<Record<string, FluentIcon>> = {
  agent: BotRegular,
  rai: ShieldRegular,
  review: PersonRegular,
  merge: MergeRegular,
  scribe: NotebookRegular,
  coordinator: BotRegular,
  assembly: CheckmarkCircleRegular,
};

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
  return ROLE_ICON_BY_KEY[roleKey] ?? CircleRegular;
}

function renderRoleIcon(roleKey: string, fontSize: number) {
  switch (roleKey) {
    case 'agent':
    case 'coordinator':
      return <BotRegular fontSize={fontSize} />;
    case 'rai':
      return <ShieldRegular fontSize={fontSize} />;
    case 'review':
      return <PersonRegular fontSize={fontSize} />;
    case 'merge':
      return <MergeRegular fontSize={fontSize} />;
    case 'scribe':
      return <NotebookRegular fontSize={fontSize} />;
    case 'assembly':
      return <CheckmarkCircleRegular fontSize={fontSize} />;
    default:
      return <CircleRegular fontSize={fontSize} />;
  }
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
  const avatar = renderAvatar(identity, avatarSize);

  return (
    <div className={mergeClasses(styles.root, className)} title={`${identity.displayName} — ${identity.roleTitle}`}>
      {avatar ?? (
        <span className={styles.iconAvatar} aria-hidden="true">
          {renderRoleIcon(identity.roleKey, 16)}
        </span>
      )}
      <div className={styles.meta}>
        <span className={styles.name}>{identity.displayName}</span>
        <span className={styles.role}>{identity.roleTitle}</span>
      </div>
    </div>
  );
}
