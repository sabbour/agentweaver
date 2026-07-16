import {
  Badge,
  makeStyles,
  Text,
  tokens,
} from '@fluentui/react-components';
import { useAppVersion } from '../../hooks/useAppVersion';
import { GitHubSignIn } from '../GitHubSignIn';
import { StartOrchestrationFab } from '../StartOrchestrationFab';
import { ProjectSwitcher } from './ProjectSwitcher';
import { StatusDot } from './StatusDot';
// Top bar. Carries the project switcher, the API-reachability status dot,
// and the GitHub sign-in. The brand mark lives in the left nav rail header.

const useStyles = makeStyles({
  topBar: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    height: '48px',
    paddingLeft: tokens.spacingHorizontalL,
    paddingRight: tokens.spacingHorizontalL,
    backgroundColor: tokens.colorNeutralBackgroundInverted,
    flexShrink: 0,
  },
  start: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
  },
  brand: {
    display: 'flex',
    flexDirection: 'column',
    marginRight: tokens.spacingHorizontalM,
  },
  product: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    color: tokens.colorNeutralForegroundOnBrand,
  },
  area: {
    fontSize: tokens.fontSizeBase100,
    lineHeight: tokens.lineHeightBase100,
    color: tokens.colorNeutralForegroundOnBrand,
    opacity: 0.7,
  },
  end: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexShrink: 0,
  },
});

export interface TopBarProps {
  projectId: string | undefined;
  pathname: string;
  isFallbackProject?: boolean;
  onFallbackProjectMissing?: () => void;
}

export function TopBar({
  projectId,
  pathname,
  isFallbackProject,
  onFallbackProjectMissing,
}: TopBarProps) {
  const styles = useStyles();
  const version = useAppVersion();
  const pageHasStartTaskAction = /^\/projects\/[^/]+(?:\/board)?\/?$/.test(pathname);
  return (
    <header className={styles.topBar} aria-label="Application toolbar">
      <div className={styles.start}>
        <div className={styles.brand}>
          <Text className={styles.product}>Agentweaver</Text>
          <Text className={styles.area}>Copilot work orchestration</Text>
        </div>
        <Badge appearance="outline" color="warning" title="Agentweaver is alpha software under active development.">
          Alpha{version ? ` v${version}` : ''}
        </Badge>
        <ProjectSwitcher
          projectId={projectId}
          pathname={pathname}
          isFallbackProject={isFallbackProject}
          onFallbackProjectMissing={onFallbackProjectMissing}
        />
      </div>
      <div className={styles.end}>
        {!pageHasStartTaskAction && <StartOrchestrationFab currentProjectId={projectId} />}
        <StatusDot />
        <GitHubSignIn />
      </div>
    </header>
  );
}
