import { makeStyles, tokens, Badge } from '@fluentui/react-components';
import { GitHubSignIn } from '../GitHubSignIn';
import { ProjectSwitcher } from './ProjectSwitcher';
import { StatusDot } from './StatusDot';

// Spec 011 — top bar (FR-011..FR-015). Carries the switch-only project switcher,
// the API-reachability status dot, and the existing GitHub sign-in. The brand
// mark lives in the left nav rail header (top-left). Docs / Inbox / Consult are
// intentionally absent (FR-015).

const useStyles = makeStyles({
  topBar: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalXXL}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    flexShrink: 0,
  },
  left: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalL,
    minWidth: 0,
  },
  right: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalL,
    flexShrink: 0,
  },
});

export interface TopBarProps {
  projectId: string | undefined;
  pathname: string;
  // True when projectId is a persisted fallback (route carries no :projectId).
  isFallbackProject?: boolean;
  // Called when the persisted fallback project no longer exists in the project list.
  onFallbackProjectMissing?: () => void;
}

export function TopBar({
  projectId,
  pathname,
  isFallbackProject,
  onFallbackProjectMissing,
}: TopBarProps) {
  const styles = useStyles();
  return (
    <header className={styles.topBar}>
      <div className={styles.left}>
        <Badge appearance="outline" color="warning" title="Agentweaver is alpha software under active development.">
          Alpha
        </Badge>
        <ProjectSwitcher
          projectId={projectId}
          pathname={pathname}
          isFallbackProject={isFallbackProject}
          onFallbackProjectMissing={onFallbackProjectMissing}
        />
      </div>
      <div className={styles.right}>
        <StatusDot />
        <GitHubSignIn />
      </div>
    </header>
  );
}
